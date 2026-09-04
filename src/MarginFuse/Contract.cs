namespace MarginFuse;

/// <summary>What this build was verified against.</summary>
public static class Contract
{
    /// <summary>
    /// The version of the shared SDK contract this build passed.
    /// </summary>
    /// <remarks>
    /// Package versions differ per language, because each tracks its own
    /// breaking changes: a rename in Python must not tell .NET users something
    /// broke. What makes the SDKs interchangeable is this, not the package
    /// version. Two SDKs reporting the same contract version have passed the
    /// same scenarios and the same vectors.
    /// See github.com/marginfuse/sdk-contract
    /// </remarks>
    public const int Version = 2;
}
