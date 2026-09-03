using System.Text.Json.Serialization;

namespace MarginFuse;

/// <summary>What happened to a provider call.</summary>
public enum Outcome
{
    /// <summary>The call succeeded.</summary>
    Success,

    /// <summary>The provider returned an error. It may still have charged.</summary>
    ProviderError,

    /// <summary>The application abandoned the call.</summary>
    AppCancelled,

    /// <summary>The call timed out.</summary>
    Timeout,
}

/// <summary>A verdict. Enforce on this alone.</summary>
public enum DecisionAction
{
    /// <summary>Proceed as asked.</summary>
    Allow,

    /// <summary>Proceed, but on the model the decision carries.</summary>
    Downgrade,

    /// <summary>Do not call the provider. The customer needs to pay first.</summary>
    TopupRequired,

    /// <summary>Do not call the provider.</summary>
    Block,
}

/// <summary>What the application actually did with a decision.</summary>
public enum Acknowledgment
{
    /// <summary>The call ran as requested.</summary>
    ProceededAsRequested,

    /// <summary>The call ran on the downgraded model.</summary>
    UsedDowngradeModel,

    /// <summary>A top-up path was shown. Not evidence the call was avoided.</summary>
    PresentedTopup,

    /// <summary>The call did not reach the provider.</summary>
    BlockedBeforeProviderCall,

    /// <summary>The application could not apply the verdict.</summary>
    FailedToApply,
}

/// <summary>
/// What a provider call consumed.
/// </summary>
/// <remarks>
/// Every property is nullable and null means <em>not reported</em>, not "used
/// none": a null is left off the request entirely, because claiming a call used
/// zero input tokens is a different statement from not knowing what it used.
/// </remarks>
public sealed record Usage
{
    /// <summary>Tokens billed at the full input rate, excluding cached ones.</summary>
    [JsonPropertyName("inputTokens")] public int? InputTokens { get; init; }

    /// <summary>Tokens the model generated.</summary>
    [JsonPropertyName("outputTokens")] public int? OutputTokens { get; init; }

    /// <summary>Input tokens served from the provider's cache.</summary>
    [JsonPropertyName("cachedInputTokens")] public int? CachedInputTokens { get; init; }

    /// <summary>Input tokens written to the provider's cache.</summary>
    [JsonPropertyName("cacheCreationTokens")] public int? CacheCreationTokens { get; init; }

    /// <summary>Images processed.</summary>
    [JsonPropertyName("images")] public int? Images { get; init; }

    /// <summary>Audio processed, in seconds.</summary>
    [JsonPropertyName("audioSeconds")] public double? AudioSeconds { get; init; }
}

/// <summary>
/// A verdict from MarginFuse.
/// </summary>
/// <remarks>
/// <see cref="Degraded"/> is true when MarginFuse could not reach a verdict and
/// the request was allowed through unprotected. <see cref="Id"/> is null in that
/// case, which is exactly why enforcement must depend on <see cref="Action"/>
/// alone.
/// </remarks>
public sealed record Decision
{
    /// <summary>Present when the server produced the decision.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    /// <summary>The verdict. Enforce on this alone.</summary>
    [JsonPropertyName("action")]
    [JsonConverter(typeof(DecisionActionConverter))]
    public DecisionAction Action { get; init; }

    /// <summary>The model to actually call. A downgrade changes it.</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = "";

    /// <summary>The provider to call.</summary>
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";

    /// <summary>Pass-through context configured on the policy.</summary>
    [JsonPropertyName("topupContext")] public string? TopupContext { get; init; }

    /// <summary>True when MarginFuse could not be reached or evaluated.</summary>
    [JsonPropertyName("degraded")] public bool Degraded { get; init; }

    /// <summary>Why the decision is degraded.</summary>
    [JsonPropertyName("degradedReason")] public string? DegradedReason { get; init; }
}

/// <summary>Asks about the call you are about to make.</summary>
public sealed record DecideParams
{
    /// <summary>Your id for the end customer, or their Stripe customer id.</summary>
    public required string CustomerId { get; init; }

    /// <summary>The provider you intend to call.</summary>
    public required string Provider { get; init; }

    /// <summary>The model you intend to call.</summary>
    public required string Model { get; init; }

    /// <summary>A stable feature key, such as "ai_chat".</summary>
    public string? Feature { get; init; }

    /// <summary>
    /// The key of a plan declared in MarginFuse Settings, when you know it here.
    /// </summary>
    /// <remarks>
    /// A hint, not an assignment: a key that does not resolve is ignored rather
    /// than failing the call, because a decision must never be lost to a plan
    /// note. Use <see cref="MarginFuseClient.IdentifyAsync"/> when the
    /// assignment itself has to be recorded.
    /// </remarks>
    public string? Plan { get; init; }

    /// <summary>Optional expected usage, for a better pre-request estimate.</summary>
    public Usage? ExpectedUsage { get; init; }
}

/// <summary>
/// Reports a call that already happened.
/// </summary>
/// <remarks>
/// <see cref="EventId"/> is the idempotency key. Leave it null and one is
/// generated; set it yourself when you already have an id you can safely retry
/// with.
/// </remarks>
public sealed record TrackParams
{
    /// <summary>Your id for the end customer, or their Stripe customer id.</summary>
    public required string CustomerId { get; init; }

    /// <summary>The provider that was called.</summary>
    public required string Provider { get; init; }

    /// <summary>The model that was called.</summary>
    public required string Model { get; init; }

    /// <summary>Your idempotency key. Generated when null.</summary>
    public string? EventId { get; init; }

    /// <summary>A stable feature key, such as "ai_chat".</summary>
    public string? Feature { get; init; }

    /// <summary>
    /// The key of a plan declared in MarginFuse Settings, when you know it here.
    /// </summary>
    /// <remarks>
    /// A hint, not an assignment: a key that does not resolve is ignored rather
    /// than failing the event, because usage must never be lost to a plan note.
    /// Use <see cref="MarginFuseClient.IdentifyAsync"/> when the assignment
    /// itself has to be recorded.
    /// </remarks>
    public string? Plan { get; init; }

    /// <summary>The model originally asked for, when a downgrade changed it.</summary>
    public string? RequestedModel { get; init; }

    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>The real charge as a decimal string, when the provider reports one.</summary>
    public string? CostUsd { get; init; }

    /// <summary>When the call happened. Defaults to now.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>What happened to the call.</summary>
    public Outcome Outcome { get; init; } = Outcome.Success;

    /// <summary>Links this event to a prior decision.</summary>
    public string? DecisionId { get; init; }

    /// <summary>The event this one retries.</summary>
    public string? RetryOfEventId { get; init; }

    /// <summary>The event this one corrects.</summary>
    public string? CorrectsEventId { get; init; }
}

/// <summary>
/// What your callback did, handed back to guard so it can be reported.
/// </summary>
/// <typeparam name="T">Your own result type.</typeparam>
/// <remarks>
/// <see cref="CostUsd"/> is a decimal string, not a double: money that
/// round-trips through a floating point number stops being what the provider
/// charged.
/// </remarks>
public sealed record ProviderCall<T>
{
    /// <summary>What the call consumed.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Your own return value.</summary>
    public T? Result { get; init; }

    /// <summary>The real charge as a decimal string, when you have one.</summary>
    public string? CostUsd { get; init; }

    /// <summary>What happened to the call.</summary>
    public Outcome Outcome { get; init; } = Outcome.Success;
}

/// <summary>
/// Tells MarginFuse who a customer is and what plan they are on.
/// </summary>
/// <remarks>
/// <see cref="Plan"/> is the key of a plan declared in MarginFuse Settings, not
/// a Stripe price id. From its price MarginFuse derives the customer's revenue
/// per period, which is what makes margin work with no revenue source
/// connected.
/// </remarks>
public sealed record IdentifyParams
{
    /// <summary>Your id for the end customer, or their Stripe customer id.</summary>
    public required string CustomerId { get; init; }

    /// <summary>
    /// The declared plan to put this customer on. Omit to leave the plan alone;
    /// sending the plan they are already on changes nothing.
    /// </summary>
    public string? Plan { get; init; }

    /// <summary>Takes the customer off declared plans. Cannot be combined with Plan.</summary>
    public bool ClearPlan { get; init; }

    /// <summary>When the current cycle started, if earlier than now.</summary>
    public DateTimeOffset? PeriodStart { get; init; }

    /// <summary>Display name shown in the MarginFuse dashboard.</summary>
    public string? Name { get; init; }

    /// <summary>Contact address shown in the MarginFuse dashboard.</summary>
    public string? Email { get; init; }

    /// <summary>Short labels segment policies can match on, such as tier=legacy.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// What MarginFuse recorded for a customer, or why it could not.
/// </summary>
/// <remarks>
/// Unlike a decision, this reports failure rather than falling back: a wrong
/// plan is a wrong margin, and "I could not record what this customer pays" has
/// no safe default. Check <see cref="Ok"/>. It is still never thrown.
/// </remarks>
public sealed record Identity
{
    /// <summary>True when MarginFuse recorded the identity.</summary>
    [JsonIgnore] public bool Ok { get; init; }

    /// <summary>MarginFuse's id for this customer, stable across calls.</summary>
    [JsonPropertyName("customerId")] public string? CustomerId { get; init; }

    /// <summary>The declared plan now in force, or null when on none.</summary>
    [JsonPropertyName("plan")] public string? Plan { get; init; }

    /// <summary>Start of the current declared cycle, when there is one.</summary>
    [JsonPropertyName("periodStart")] public string? PeriodStart { get; init; }

    /// <summary>End of the current declared cycle, when there is one.</summary>
    [JsonPropertyName("periodEnd")] public string? PeriodEnd { get; init; }

    /// <summary>Why it failed. Null when <see cref="Ok"/> is true.</summary>
    [JsonIgnore] public string? Error { get; init; }
}

/// <summary>What guard did.</summary>
public enum GuardKind
{
    /// <summary>The call ran.</summary>
    Completed,

    /// <summary>The call was blocked and never reached the provider.</summary>
    Blocked,

    /// <summary>The customer must top up first. The provider was not called.</summary>
    TopupRequired,
}

/// <summary>The result of the whole guard loop.</summary>
/// <typeparam name="T">Your own result type.</typeparam>
public sealed record GuardOutcome<T>
{
    /// <summary>What guard did.</summary>
    public required GuardKind Kind { get; init; }

    /// <summary>The verdict guard acted on.</summary>
    public required Decision Decision { get; init; }

    /// <summary>Your callback's return value. Null unless Kind is Completed.</summary>
    public T? Result { get; init; }
}
