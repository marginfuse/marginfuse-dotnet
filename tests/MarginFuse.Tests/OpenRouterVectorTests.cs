using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace MarginFuse.Tests;

/// <summary>
/// Driven entirely by contract/conformance/gateway-vectors.json, which every
/// SDK in every language reads.
///
/// Assertions written here instead would be a second copy of the truth, and
/// this SDK would slowly stop agreeing with the others. To add a case, edit the
/// vector file, not this test.
/// </summary>
public sealed class OpenRouterVectorTests
{
    // The decimal-string pattern from the API's own schema. Exponent notation
    // is the failure this guards, and it is silent everywhere else.
    private static readonly Regex Decimal = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private static JsonElement Vectors()
    {
        var path = Path.Combine(RepoRoot(), "contract", "conformance", "gateway-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contract")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var kase in Cases())
        {
            data.Add(kase.GetProperty("name").GetString()!);
        }
        return data;
    }

    private static List<JsonElement> Cases()
    {
        var adapter = Vectors().GetProperty("adapters").GetProperty("fromOpenRouter");
        var cases = adapter.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    private static OpenRouterMapping Run(JsonElement kase)
    {
        var omit = kase.TryGetProperty("omitInput", out var o)
            && o.ValueKind == JsonValueKind.True;
        if (omit) return OpenRouter.From(null);

        var input = kase.GetProperty("input");
        return OpenRouter.From(input.ValueKind == JsonValueKind.Null ? null : input);
    }

    /// <summary>Only the fields the adapter actually set, in the wire names.</summary>
    private static Dictionary<string, double> Produced(Usage usage)
    {
        var produced = new Dictionary<string, double>();
        if (usage.InputTokens is { } i) produced["inputTokens"] = i;
        if (usage.OutputTokens is { } o) produced["outputTokens"] = o;
        if (usage.CachedInputTokens is { } c) produced["cachedInputTokens"] = c;
        if (usage.CacheCreationTokens is { } w) produced["cacheCreationTokens"] = w;
        if (usage.Images is { } im) produced["images"] = im;
        if (usage.AudioSeconds is { } a) produced["audioSeconds"] = a;
        return produced;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GatewayVector(string name)
    {
        var kase = Cases().Single(c => c.GetProperty("name").GetString() == name);
        var mapped = Run(kase);

        var expected = kase.GetProperty("expected");
        var wantUsage = expected.GetProperty("usage");
        var gotUsage = Produced(mapped.Usage);

        var wantCount = wantUsage.EnumerateObject().Count();
        Assert.True(
            gotUsage.Count == wantCount,
            $"usage fields: got {string.Join(",", gotUsage.Keys)}, want {wantCount}");
        foreach (var field in wantUsage.EnumerateObject())
        {
            Assert.True(gotUsage.ContainsKey(field.Name), $"usage.{field.Name} missing");
            Assert.Equal(field.Value.GetDouble(), gotUsage[field.Name]);
        }

        if (expected.TryGetProperty("costUsd", out var wantCost)
            && wantCost.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(wantCost.GetString(), mapped.CostUsd);
        }
        else
        {
            // Absent must mean absent, not present-and-zero: omitting the cost
            // lets MarginFuse price the call, where "0" would claim it was free.
            Assert.Null(mapped.CostUsd);
        }
    }

    [Fact]
    public void NeverProducesACostTheApiWouldReject()
    {
        foreach (var kase in Cases())
        {
            var cost = Run(kase).CostUsd;
            if (cost is not null)
            {
                Assert.True(
                    Decimal.IsMatch(cost),
                    $"{kase.GetProperty("name").GetString()}: {cost} is not a decimal string");
            }
        }
    }

    [Fact]
    public void ContractVersionMatchesThePinnedContract()
    {
        var path = Path.Combine(RepoRoot(), "contract", "conformance", "behavior-scenarios.json");
        var pinned = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(Contract.Version, pinned.GetProperty("version").GetInt32());
    }
}
