// The .NET conformance runner.
//
// Reads one scenario as JSON on stdin, drives this SDK against the mock server
// the driver started, and prints one JSON report on stdout. See
// contract/harness/runners/README.md for the contract.
//
// Exits non-zero only if the runner itself broke. An SDK misbehaving is a
// report for the driver to judge, not a crash here.

using System.Text.Json;
using System.Text.Json.Nodes;
using MarginFuse;

var scenario = JsonNode.Parse(await Console.In.ReadToEndAsync())!.AsObject();

var providerCalls = new JsonArray();
var onErrorContexts = new JsonArray();

var options = new MarginFuseOptions
{
    ApiKey = Environment.GetEnvironmentVariable("MARGINFUSE_API_KEY") ?? "",
    BaseUrl = Environment.GetEnvironmentVariable("MARGINFUSE_BASE_URL") ?? "",
    OnError = (_, context) => onErrorContexts.Add(context),
};

if (scenario["options"]?["timeoutMs"] is JsonValue t && t.TryGetValue<int>(out var ms))
{
    options = options with { Timeout = TimeSpan.FromMilliseconds(ms) };
}

var mf = new MarginFuseClient(options);
var p = scenario["params"]?.AsObject() ?? [];
var report = new JsonObject { ["outcome"] = "returned" };

try
{
    switch (scenario["action"]!.GetValue<string>())
    {
        case "decide":
            report["result"] = DecisionJson(await mf.DecideAsync(DecideFrom(p)));
            break;

        case "track":
            mf.Track(TrackFrom(p));
            break;

        case "acknowledge":
            mf.Acknowledge(
                Str(p, "decisionId")!,
                AckFromWire(Str(p, "acknowledgment")!));
            break;

        case "guard":
        {
            var providerSpec = scenario["provider"]?.AsObject();
            var throwsProvider = providerSpec?["throws"]?.GetValue<bool>() ?? false;
            var providerUsage = UsageFrom(providerSpec?["usage"]?.AsObject());

            var outcome = await mf.GuardAsync<string>(DecideFrom(p), decision =>
            {
                providerCalls.Add(new JsonObject
                {
                    ["model"] = decision.Model,
                    ["provider"] = decision.Provider,
                });
                if (throwsProvider) throw new InvalidOperationException("provider exploded");
                return Task.FromResult(new ProviderCall<string>
                {
                    Result = "ok",
                    Usage = providerUsage,
                });
            });

            // Only the discriminant and the decision travel; the application's
            // own result means nothing to another language.
            report["result"] = new JsonObject
            {
                ["kind"] = outcome.Kind switch
                {
                    GuardKind.Completed => "completed",
                    GuardKind.Blocked => "blocked",
                    _ => "topup_required",
                },
                ["decision"] = DecisionJson(outcome.Decision),
            };
            break;
        }

        default:
            await Console.Error.WriteLineAsync($"unknown action {scenario["action"]}");
            return 1;
    }
}
catch (Exception e)
{
    report["outcome"] = "threw";
    report["threw"] = e.Message;
}

// Always flush, including after a throw: the driver asserts on what the SDK
// sent, and guard records the attempt before it rethrows.
await mf.DisposeAsync();

report["providerCalls"] = providerCalls;
report["onErrorContexts"] = onErrorContexts;
Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
return 0;

static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

static int? Int(JsonObject? o, string key) =>
    o?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

static Usage UsageFrom(JsonObject? u) => new()
{
    InputTokens = Int(u, "inputTokens"),
    OutputTokens = Int(u, "outputTokens"),
    CachedInputTokens = Int(u, "cachedInputTokens"),
    CacheCreationTokens = Int(u, "cacheCreationTokens"),
    Images = Int(u, "images"),
    AudioSeconds = u?["audioSeconds"] is JsonValue a && a.TryGetValue<double>(out var d) ? d : null,
};

static DecideParams DecideFrom(JsonObject p) => new()
{
    CustomerId = Str(p, "customerId")!,
    Provider = Str(p, "provider")!,
    Model = Str(p, "model")!,
    Feature = Str(p, "feature"),
    ExpectedUsage = p["expectedUsage"] is JsonObject e ? UsageFrom(e) : null,
};

static TrackParams TrackFrom(JsonObject p) => new()
{
    CustomerId = Str(p, "customerId")!,
    Provider = Str(p, "provider")!,
    Model = Str(p, "model")!,
    EventId = Str(p, "eventId"),
    Feature = Str(p, "feature"),
    RequestedModel = Str(p, "requestedModel"),
    Usage = p["usage"] is JsonObject u ? UsageFrom(u) : null,
    CostUsd = Str(p, "costUsd"),
    DecisionId = Str(p, "decisionId"),
    Outcome = Str(p, "outcome") switch
    {
        "provider_error" => Outcome.ProviderError,
        "app_cancelled" => Outcome.AppCancelled,
        "timeout" => Outcome.Timeout,
        _ => Outcome.Success,
    },
};

static Acknowledgment AckFromWire(string wire) => wire switch
{
    "used_downgrade_model" => Acknowledgment.UsedDowngradeModel,
    "presented_topup" => Acknowledgment.PresentedTopup,
    "blocked_before_provider_call" => Acknowledgment.BlockedBeforeProviderCall,
    "failed_to_apply" => Acknowledgment.FailedToApply,
    _ => Acknowledgment.ProceededAsRequested,
};

static JsonObject DecisionJson(Decision d) => new()
{
    ["id"] = d.Id,
    ["action"] = d.Action switch
    {
        DecisionAction.Downgrade => "downgrade",
        DecisionAction.TopupRequired => "topup_required",
        DecisionAction.Block => "block",
        _ => "allow",
    },
    ["model"] = d.Model,
    ["provider"] = d.Provider,
    ["topupContext"] = d.TopupContext,
    ["degraded"] = d.Degraded,
    ["degradedReason"] = d.DegradedReason,
};
