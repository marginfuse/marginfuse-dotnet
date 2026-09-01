using System.Globalization;
using System.Text.Json;

namespace MarginFuse;

/// <summary>What <see cref="OpenRouter.From(JsonElement?)"/> produced.</summary>
public sealed record OpenRouterMapping
{
    /// <summary>The usage fields, ready for <see cref="TrackParams.Usage"/>.</summary>
    public required Usage Usage { get; init; }

    /// <summary>
    /// The gateway's own cost as a decimal string, or null when the response
    /// carried none. Null lets the event fall through to MarginFuse's own
    /// pricing instead of claiming a $0 charge.
    /// </summary>
    public string? CostUsd { get; init; }
}

/// <summary>
/// OpenRouter helper.
/// </summary>
/// <remarks>
/// <para>OpenRouter returns a <c>usage</c> object carrying the provider-final
/// <c>cost</c>. Forwarding it is what makes an OpenRouter integration exact
/// rather than estimated: MarginFuse cannot know what a gateway charged,
/// because routing, fees and BYOK terms are not visible in a usage event.</para>
/// <para>Two details this helper exists to get right, both of which silently
/// misstate margin when hand-rolled. First, <c>prompt_tokens</c> is the TOTAL
/// input count: cached reads and cache writes are already inside it, and
/// MarginFuse prices those as three separate charges and adds them up, so
/// passing the total through charges every cached token twice at the full
/// uncached rate. Second, <c>cost</c> is a floating point number, and the
/// default numeric formatting renders small ones in exponent notation
/// (<c>1.2E-07</c>), which the API rejects as a decimal string.</para>
/// </remarks>
public static class OpenRouter
{
    /// <summary>Maps an OpenRouter usage object.</summary>
    /// <param name="usage">
    /// The decoded <c>usage</c> object from the response, or null.
    /// </param>
    /// <returns>The usage fields and, when present, the gateway's cost.</returns>
    public static OpenRouterMapping From(JsonElement? usage)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } source)
        {
            return new OpenRouterMapping { Usage = new Usage() };
        }

        JsonElement? details = source.TryGetProperty("prompt_tokens_details", out var d)
            && d.ValueKind == JsonValueKind.Object
                ? d
                : null;

        var cached = ReadInt(details, "cached_tokens");
        var cacheWrites = ReadInt(details, "cache_write_tokens");
        // What is left after the cached parts is what was billed at the full
        // input rate. Clamped at zero so a provider reporting these differently
        // degrades to "no fresh input" rather than a negative charge.
        var fresh = Math.Max(0, ReadInt(source, "prompt_tokens") - cached - cacheWrites);
        var completion = ReadInt(source, "completion_tokens");

        var mapped = new Usage
        {
            InputTokens = fresh > 0 ? fresh : null,
            OutputTokens = completion > 0 ? completion : null,
            CachedInputTokens = cached > 0 ? cached : null,
            CacheCreationTokens = cacheWrites > 0 ? cacheWrites : null,
        };

        if (!source.TryGetProperty("cost", out var costElement)
            || costElement.ValueKind != JsonValueKind.Number
            || !costElement.TryGetDouble(out var cost)
            || double.IsNaN(cost) || double.IsInfinity(cost) || cost < 0)
        {
            return new OpenRouterMapping { Usage = mapped };
        }

        return new OpenRouterMapping { Usage = mapped, CostUsd = CreditsToUsd(cost) };
    }

    private static int ReadInt(JsonElement? source, string name)
    {
        if (source is not { } element
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || double.IsNaN(number) || double.IsInfinity(number) || number <= 0)
        {
            return 0;
        }
        return (int)Math.Round(number, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// OpenRouter credits (1 credit = 1 USD) as a decimal string the API takes.
    /// </summary>
    /// <remarks>
    /// Fixed point to nano precision: the default formatting emits exponent
    /// notation for the small costs cheap models produce, and money below a
    /// nano cannot be represented at all, so it rounds down rather than
    /// pretending otherwise.
    /// </remarks>
    internal static string CreditsToUsd(double cost)
    {
        var quantized = Math.Truncate((decimal)cost * 1_000_000_000m) / 1_000_000_000m;
        var text = quantized.ToString("0.#########", CultureInfo.InvariantCulture);
        return text is "" or "-0" ? "0" : text;
    }
}
