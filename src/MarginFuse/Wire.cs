using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarginFuse;

/// <summary>
/// The strings these enums take on the wire.
/// </summary>
/// <remarks>
/// Mapped explicitly rather than derived from the member names, so renaming a
/// C# member can never silently change what the API receives, and so the file
/// reads as the contract it is. .NET 9 has an attribute for this; net8.0 does
/// not, and net8.0 is what this package targets.
/// </remarks>
internal static class WireNames
{
    internal static string ToWire(this Outcome value) => value switch
    {
        Outcome.Success => "success",
        Outcome.ProviderError => "provider_error",
        Outcome.AppCancelled => "app_cancelled",
        Outcome.Timeout => "timeout",
        _ => "success",
    };

    internal static string ToWire(this Acknowledgment value) => value switch
    {
        Acknowledgment.ProceededAsRequested => "proceeded_as_requested",
        Acknowledgment.UsedDowngradeModel => "used_downgrade_model",
        Acknowledgment.PresentedTopup => "presented_topup",
        Acknowledgment.BlockedBeforeProviderCall => "blocked_before_provider_call",
        Acknowledgment.FailedToApply => "failed_to_apply",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static string ToWire(this DecisionAction value) => value switch
    {
        DecisionAction.Allow => "allow",
        DecisionAction.Downgrade => "downgrade",
        DecisionAction.TopupRequired => "topup_required",
        DecisionAction.Block => "block",
        _ => "allow",
    };

    /// <summary>
    /// Reads a verdict. An action a newer server sends and this version cannot
    /// enforce resolves to Allow: an unrecognised value must never silently
    /// become a block.
    /// </summary>
    internal static DecisionAction ActionFromWire(string? value) => value switch
    {
        "downgrade" => DecisionAction.Downgrade,
        "topup_required" => DecisionAction.TopupRequired,
        "block" => DecisionAction.Block,
        _ => DecisionAction.Allow,
    };
}

/// <summary>Serialises <see cref="DecisionAction"/> using the wire names.</summary>
internal sealed class DecisionActionConverter : JsonConverter<DecisionAction>
{
    public override DecisionAction Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        WireNames.ActionFromWire(reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : null);

    public override void Write(
        Utf8JsonWriter writer,
        DecisionAction value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToWire());
    }
}
