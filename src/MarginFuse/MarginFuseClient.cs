using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarginFuse;

/// <summary>How to reach MarginFuse. Only <see cref="ApiKey"/> is required.</summary>
public sealed record MarginFuseOptions
{
    /// <summary>Your project API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Point at your own deployment in development.</summary>
    public string BaseUrl { get; init; } = "https://api.marginfuse.com";

    /// <summary>How long DecideAsync waits before failing open.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Receives transport failures the SDK swallowed, with a context string.
    /// Without it they are silent by design: this SDK is in your request path
    /// and must not become your outage.
    /// </summary>
    public Action<Exception, string>? OnError { get; init; }

    /// <summary>Replaces the default client. Useful for proxies and test doubles.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Server-side SDK for MarginFuse: profitability guardrails for AI SaaS.
/// </summary>
/// <remarks>
/// <para>Reliability contract: this SDK never throws into application code and
/// never blocks a request on MarginFuse availability. <see cref="DecideAsync"/>
/// fails open to <see cref="DecisionAction.Allow"/> on any timeout or error;
/// <see cref="Track"/> and <see cref="Acknowledge"/> retry in the background and
/// surface problems only through <see cref="MarginFuseOptions.OnError"/>.</para>
/// <para>Server side only: it carries a secret API key.</para>
/// </remarks>
public sealed class MarginFuseClient : IAsyncDisposable, IDisposable
{
    private const int TrackRetries = 3;
    /// <summary>
    /// The released version of this library, as sent in the user-agent.
    /// </summary>
    /// <remarks>
    /// Checked against the assembly version by the test suite. A literal
    /// nobody compares to anything drifts, which is how the Node SDK came to
    /// ship two releases still reporting 0.1.0.
    /// </remarks>
    public const string Version = "0.1.0";

    private const string UserAgent = "marginfuse-dotnet/" + Version;

    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MarginFuseOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly List<Task> _pending = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Configuration. Only the API key is required.</param>
    public MarginFuseClient(MarginFuseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new ArgumentException("MarginFuse: ApiKey is required", nameof(options));
        }

        _options = options with { BaseUrl = options.BaseUrl.TrimEnd('/') };
        _ownsHttp = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient();
    }

    /// <summary>
    /// Asks whether the next call should run. Always returns a verdict.
    /// </summary>
    /// <remarks>
    /// This never throws and never returns null. A failed decision is not a
    /// condition to branch on: it is an allow with
    /// <see cref="Decision.Degraded"/> set, because MarginFuse being
    /// unreachable must never become your outage.
    /// </remarks>
    /// <param name="parameters">What you are about to call.</param>
    /// <param name="cancellationToken">Cancels the decision, not your request.</param>
    /// <returns>A verdict. Enforce on <see cref="Decision.Action"/> alone.</returns>
    public async Task<Decision> DecideAsync(
        DecideParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Decision FailOpen(string reason) => new()
        {
            Action = DecisionAction.Allow,
            Model = parameters.Model,
            Provider = parameters.Provider,
            Degraded = true,
            DegradedReason = reason,
        };

        var body = new Dictionary<string, object?>
        {
            ["customerId"] = parameters.CustomerId,
            ["feature"] = parameters.Feature,
            ["provider"] = parameters.Provider,
            ["model"] = parameters.Model,
            ["expectedUsage"] = parameters.ExpectedUsage,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await PostAsync("/v1/decisions", body, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Report(new HttpRequestException($"decide: HTTP {(int)response.StatusCode}"), "decide");
                return FailOpen($"server responded {(int)response.StatusCode}");
            }

            var decision = await response.Content
                .ReadFromJsonAsync<Decision>(Wire, timeout.Token)
                .ConfigureAwait(false);
            if (decision is null) return FailOpen("empty response");

            return decision with
            {
                Model = string.IsNullOrEmpty(decision.Model) ? parameters.Model : decision.Model,
                Provider = string.IsNullOrEmpty(decision.Provider)
                    ? parameters.Provider
                    : decision.Provider,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Report(new TimeoutException("decide timed out"), "decide");
            return FailOpen("timeout");
        }
        catch (Exception e)
        {
            Report(e, "decide");
            return FailOpen("unreachable");
        }
    }

    /// <summary>
    /// Reports a call that already happened. Returns immediately and sends in
    /// the background with retries.
    /// </summary>
    /// <remarks>
    /// Call <see cref="FlushAsync"/> before the process exits, or the last
    /// events go with it. Disposing the client flushes.
    /// </remarks>
    /// <param name="parameters">What the call consumed.</param>
    public void Track(TrackParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var occurredAt = parameters.OccurredAt ?? DateTimeOffset.UtcNow;
        var evt = new Dictionary<string, object?>
        {
            ["eventId"] = parameters.EventId ?? $"evt_{Guid.NewGuid()}",
            ["customerId"] = parameters.CustomerId,
            ["feature"] = parameters.Feature,
            ["provider"] = parameters.Provider,
            ["model"] = parameters.Model,
            ["requestedModel"] = parameters.RequestedModel,
            ["usage"] = parameters.Usage ?? new Usage(),
            ["costUsd"] = parameters.CostUsd,
            ["occurredAt"] = occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture),
            ["outcome"] = parameters.Outcome.ToWire(),
            ["decisionId"] = parameters.DecisionId,
            ["retryOfEventId"] = parameters.RetryOfEventId,
            ["correctsEventId"] = parameters.CorrectsEventId,
        };
        var body = new Dictionary<string, object?> { ["events"] = new[] { evt } };

        Background(async token =>
        {
            Exception? last = null;
            for (var attempt = 0; attempt < TrackRetries; attempt++)
            {
                try
                {
                    using var response = await PostAsync("/v1/events", body, token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return;

                    var status = (int)response.StatusCode;
                    if (status is >= 400 and < 500 && status != 429)
                    {
                        // A malformed event is malformed on every attempt.
                        var text = await response.Content.ReadAsStringAsync(token)
                            .ConfigureAwait(false);
                        Report(
                            new HttpRequestException(
                                $"track: HTTP {status} {text[..Math.Min(200, text.Length)]}"),
                            "track");
                        return;
                    }
                    last = new HttpRequestException($"track: HTTP {status}");
                }
                catch (Exception e)
                {
                    last = e;
                }

                try
                {
                    await Task.Delay(250 * (1 << attempt), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (last is not null) Report(last, "track");
        });
    }

    /// <summary>Track for jobs and scripts that must not exit early.</summary>
    /// <param name="parameters">What the call consumed.</param>
    public async Task TrackAndWaitAsync(TrackParams parameters)
    {
        Track(parameters);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Tells MarginFuse what your application did with a decision.</summary>
    /// <param name="decisionId">The id from a prior decision.</param>
    /// <param name="acknowledgment">What the application did.</param>
    public void Acknowledge(string decisionId, Acknowledgment acknowledgment)
    {
        ArgumentException.ThrowIfNullOrEmpty(decisionId);
        var body = new Dictionary<string, object?> { ["acknowledgment"] = acknowledgment.ToWire() };

        Background(async token =>
        {
            try
            {
                using var response = await PostAsync(
                    $"/v1/decisions/{Uri.EscapeDataString(decisionId)}/ack", body, token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Report(
                        new HttpRequestException($"ack: HTTP {(int)response.StatusCode}"),
                        "acknowledge");
                }
            }
            catch (Exception e)
            {
                Report(e, "acknowledge");
            }
        });
    }

    /// <summary>
    /// Runs the whole loop: ask, run, report, acknowledge.
    /// </summary>
    /// <remarks>
    /// Takes a callback rather than returning a decision for you to act on,
    /// because enforcement must not depend on the caller remembering to check
    /// anything. When the verdict is block, the callback is never invoked.
    /// An exception from the callback propagates unchanged: your error handling
    /// owns provider failures. The attempt is recorded first, because the
    /// provider may still have charged for it.
    /// </remarks>
    /// <typeparam name="T">Your own result type.</typeparam>
    /// <param name="parameters">What you are about to call.</param>
    /// <param name="run">Your provider call. Use the decision's model.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>What guard did, and your result when it completed.</returns>
    public async Task<GuardOutcome<T>> GuardAsync<T>(
        DecideParams parameters,
        Func<Decision, Task<ProviderCall<T>>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(run);

        var decision = await DecideAsync(parameters, cancellationToken).ConfigureAwait(false);

        // Enforcement depends on the ACTION alone. A missing id costs an
        // acknowledgment; it must never turn a block into a provider call.
        if (decision.Action == DecisionAction.Block)
        {
            if (decision.Id is not null)
            {
                Acknowledge(decision.Id, Acknowledgment.BlockedBeforeProviderCall);
            }
            return new GuardOutcome<T> { Kind = GuardKind.Blocked, Decision = decision };
        }
        if (decision.Action == DecisionAction.TopupRequired)
        {
            if (decision.Id is not null)
            {
                Acknowledge(decision.Id, Acknowledgment.PresentedTopup);
            }
            return new GuardOutcome<T> { Kind = GuardKind.TopupRequired, Decision = decision };
        }

        var modelUsed = decision.Action == DecisionAction.Downgrade
            ? decision.Model
            : parameters.Model;

        ProviderCall<T> call;
        try
        {
            call = await run(decision).ConfigureAwait(false);
        }
        catch
        {
            Track(new TrackParams
            {
                CustomerId = parameters.CustomerId,
                Feature = parameters.Feature,
                Provider = parameters.Provider,
                Model = modelUsed,
                RequestedModel = parameters.Model,
                Outcome = Outcome.ProviderError,
                DecisionId = decision.Id,
            });
            if (decision.Id is not null)
            {
                Acknowledge(decision.Id, Acknowledgment.ProceededAsRequested);
            }
            throw;
        }

        Track(new TrackParams
        {
            CustomerId = parameters.CustomerId,
            Feature = parameters.Feature,
            Provider = parameters.Provider,
            Model = modelUsed,
            RequestedModel = parameters.Model,
            Usage = call.Usage,
            CostUsd = call.CostUsd,
            Outcome = call.Outcome,
            DecisionId = decision.Id,
        });
        if (decision.Id is not null)
        {
            Acknowledge(decision.Id, decision.Action == DecisionAction.Downgrade
                ? Acknowledgment.UsedDowngradeModel
                : Acknowledgment.ProceededAsRequested);
        }

        return new GuardOutcome<T>
        {
            Kind = GuardKind.Completed,
            Decision = decision,
            Result = call.Result,
        };
    }

    /// <summary>Waits for queued events and acknowledgments. Never throws.</summary>
    public async Task FlushAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
        }
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // already surfaced through OnError
            }
        }
        lock (_gate)
        {
            _pending.RemoveAll(t => t.IsCompleted);
        }
    }

    /// <summary>Flushes, then releases resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await FlushAsync().ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Flushes, then releases resources.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ------------------------------------------------------------- internals

    private void Background(Func<CancellationToken, Task> work)
    {
        if (_disposed) return;
        var task = Task.Run(() => work(_shutdown.Token), CancellationToken.None);
        lock (_gate)
        {
            _pending.RemoveAll(t => t.IsCompleted);
            _pending.Add(task);
        }
    }

    private void Report(Exception error, string context)
    {
        if (_options.OnError is null) return;
        try
        {
            _options.OnError(error, context);
        }
        catch
        {
            // a broken handler is not our failure mode
        }
    }

    private Task<HttpResponseMessage> PostAsync(
        string path,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl + path)
        {
            Content = JsonContent.Create(body, options: Wire),
        };
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_options.ApiKey}");
        request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
        return _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }
}
