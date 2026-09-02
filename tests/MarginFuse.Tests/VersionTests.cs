using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MarginFuse.Tests;

/// <summary>
/// The version the SDK reports has to be the version that was packed.
/// </summary>
/// <remarks>
/// The user-agent is how a support conversation starts: someone reports odd
/// behaviour and the first question is which version sent the request. The
/// Node SDK answered that with "0.1.0" across three releases, because the
/// string was written once and nothing ever compared it to the build.
///
/// MSBuild writes the csproj's Version into the assembly, so that attribute is
/// the published version as far as the compiled code can see it.
/// </remarks>
public class VersionTests
{
    /// <summary>Captures the requests the SDK makes, and answers them.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"dec_1","action":"allow","model":"gpt-4.1","provider":"openai"}"""),
            });
        }
    }

    private static string PackedVersion()
    {
        var informational = typeof(MarginFuseClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational), "assembly carries no informational version");

        // Source-link builds append "+<commit>", which is build metadata rather
        // than part of the version.
        var plus = informational!.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    [Fact]
    public void ReportedVersionIsThePackedVersion()
    {
        Assert.Equal(MarginFuseClient.Version, PackedVersion());
    }

    [Fact]
    public async Task TheWireCarriesThatVersion()
    {
        var recorder = new Recorder();
        using var http = new HttpClient(recorder);
        await using var mf = new MarginFuseClient(new MarginFuseOptions
        {
            ApiKey = "mf_test",
            BaseUrl = "https://example.invalid",
            HttpClient = http,
        });

        await mf.DecideAsync(new DecideParams
        {
            CustomerId = "cus_test",
            Provider = "openai",
            Model = "gpt-4.1",
        });

        var request = Assert.Single(recorder.Requests);
        var sent = string.Join("", request.Headers.GetValues("user-agent"));
        Assert.Equal("marginfuse-dotnet/" + PackedVersion(), sent);
    }
}
