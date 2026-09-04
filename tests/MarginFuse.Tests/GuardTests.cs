using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MarginFuse.Tests;

/// <summary>
/// What guard reports about a downgrade it ran.
/// </summary>
/// <remarks>
/// A downgrade can cross vendors: the server is free to answer an OpenAI
/// request with an Anthropic model. Everything guard sends afterwards has to
/// describe the call that actually ran, or the event is priced from the wrong
/// vendor's catalogue, attributed to the wrong vendor, and the saving the
/// downgrade exists to prove is measured against the wrong basis.
/// </remarks>
public class GuardTests
{
    private const string CrossProviderDowngrade =
        """{"id":"dec_1","action":"downgrade","model":"claude-haiku-4.5","provider":"anthropic"}""";

    private const string Allowed =
        """{"id":"dec_2","action":"allow","model":"gpt-4.1","provider":"openai"}""";

    /// <summary>Answers the decision, and keeps everything the SDK sent after it.</summary>
    private sealed class Server : HttpMessageHandler
    {
        private readonly object _gate = new();

        /// <summary>The body answered to /v1/decisions.</summary>
        public required string DecisionBody { get; init; }

        public List<(string Path, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                Sent.Add((path, body));
            }

            // The media type is load-bearing: a body not announced as JSON is
            // refused on the way in, and the decision would fail open instead.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    path == "/v1/decisions" ? DecisionBody : "{}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static MarginFuseClient ClientOn(HttpClient http) => new(new MarginFuseOptions
    {
        ApiKey = "mf_test",
        BaseUrl = "https://example.invalid",
        HttpClient = http,
    });

    private static DecideParams AskingOpenAi() => new()
    {
        CustomerId = "cus_test",
        Provider = "openai",
        Model = "gpt-4.1",
    };

    /// <summary>The single usage event guard sent.</summary>
    private static JsonElement UsageEvent(Server server)
    {
        var body = Assert.Single(server.Sent, s => s.Path == "/v1/events").Body;
        return JsonDocument.Parse(body).RootElement.GetProperty("events")[0].Clone();
    }

    /// <summary>The single acknowledgment guard sent, in its wire name.</summary>
    private static string AckSent(Server server)
    {
        var body = Assert.Single(
            server.Sent, s => s.Path.EndsWith("/ack", StringComparison.Ordinal)).Body;
        return JsonDocument.Parse(body).RootElement.GetProperty("acknowledgment").GetString()!;
    }

    [Fact]
    public async Task ACrossProviderDowngradeIsBilledToTheVendorThatRan()
    {
        var server = new Server { DecisionBody = CrossProviderDowngrade };
        using var http = new HttpClient(server);
        await using var mf = ClientOn(http);

        var outcome = await mf.GuardAsync<string>(AskingOpenAi(), decision =>
        {
            Assert.Equal("anthropic", decision.Provider);
            return Task.FromResult(new ProviderCall<string>
            {
                Result = "ok",
                Usage = new Usage { InputTokens = 10, OutputTokens = 20 },
            });
        });
        await mf.FlushAsync();

        Assert.Equal(GuardKind.Completed, outcome.Kind);

        var evt = UsageEvent(server);
        Assert.Equal("anthropic", evt.GetProperty("provider").GetString());
        Assert.Equal("claude-haiku-4.5", evt.GetProperty("model").GetString());

        // The requested side of the pair is untouched. That is what the saving
        // is measured against.
        Assert.Equal("gpt-4.1", evt.GetProperty("requestedModel").GetString());
        Assert.Equal("used_downgrade_model", AckSent(server));
    }

    [Fact]
    public async Task ADowngradeThatFailsStillAcknowledgesTheDowngrade()
    {
        var server = new Server { DecisionBody = CrossProviderDowngrade };
        using var http = new HttpClient(server);
        await using var mf = ClientOn(http);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mf.GuardAsync<string>(
                AskingOpenAi(),
                _ => throw new InvalidOperationException("provider exploded")));
        await mf.FlushAsync();

        // The application's own error, propagated unchanged. Guard reports
        // around it rather than in place of it.
        Assert.Equal("provider exploded", thrown.Message);

        var evt = UsageEvent(server);
        Assert.Equal("provider_error", evt.GetProperty("outcome").GetString());
        Assert.Equal("anthropic", evt.GetProperty("provider").GetString());
        Assert.Equal("claude-haiku-4.5", evt.GetProperty("model").GetString());
        Assert.Equal("gpt-4.1", evt.GetProperty("requestedModel").GetString());

        // The cheaper model is what ran. That the call then failed does not
        // turn it back into a request that proceeded as asked.
        Assert.Equal("used_downgrade_model", AckSent(server));
    }

    [Fact]
    public async Task WithoutADowngradeTheVendorThatRanIsTheCallersOwn()
    {
        var server = new Server { DecisionBody = Allowed };
        using var http = new HttpClient(server);
        await using var mf = ClientOn(http);

        await mf.GuardAsync<string>(
            AskingOpenAi(),
            _ => Task.FromResult(new ProviderCall<string> { Result = "ok" }));
        await mf.FlushAsync();

        var evt = UsageEvent(server);
        Assert.Equal("openai", evt.GetProperty("provider").GetString());
        Assert.Equal("gpt-4.1", evt.GetProperty("model").GetString());
        Assert.Equal("proceeded_as_requested", AckSent(server));
    }
}
