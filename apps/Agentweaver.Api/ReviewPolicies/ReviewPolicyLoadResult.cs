namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The outcome of parsing+validating a single review-policy source (Feature 010, FR-025/026/033). A
/// source that parses and validates yields <see cref="Policy"/> with <see cref="IsValid"/> = true. A
/// malformed or schema-invalid source yields a null policy, <see cref="IsValid"/> = false, and a clear,
/// file-scoped <see cref="Error"/> — it is excluded from the available set but never crashes loading of
/// the other sources.
/// </summary>
public sealed record ReviewPolicyLoadResult
{
    /// <summary>The source file name (e.g. "default.yaml") or "built-in" for the shipped default.</summary>
    public required string Source { get; init; }

    public required bool IsValid { get; init; }

    /// <summary>The validated policy, or null when <see cref="IsValid"/> is false.</summary>
    public ReviewPolicy? Policy { get; init; }

    /// <summary>A specific, actionable message when invalid; null when valid.</summary>
    public string? Error { get; init; }

    /// <summary>True for the built-in shipped default policy (FR-032 fallback).</summary>
    public bool IsBuiltIn { get; init; }

    public static ReviewPolicyLoadResult Valid(string source, ReviewPolicy policy, bool isBuiltIn = false) =>
        new() { Source = source, IsValid = true, Policy = policy, IsBuiltIn = isBuiltIn };

    public static ReviewPolicyLoadResult Invalid(string source, string error) =>
        new() { Source = source, IsValid = false, Error = error };
}
