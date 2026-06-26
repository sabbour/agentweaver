namespace Agentweaver.Api.Workflows;

/// <summary>
/// The outcome of parsing+validating a single workflow source (Feature 010, FR-002/003/004). A source
/// that parses and validates yields <see cref="Definition"/> with <see cref="IsValid"/> = true. A
/// malformed or schema-invalid source yields a null definition, <see cref="IsValid"/> = false, and a
/// clear, file-scoped <see cref="Error"/> — it is excluded from the available set but never crashes
/// loading of the other sources.
/// </summary>
public sealed record WorkflowLoadResult
{
    /// <summary>The source file name (e.g. "default.yaml") or "built-in" for the shipped default.</summary>
    public required string Source { get; init; }

    public required bool IsValid { get; init; }

    /// <summary>The validated definition, or null when <see cref="IsValid"/> is false.</summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>A specific, actionable message when invalid; null when valid.</summary>
    public string? Error { get; init; }

    /// <summary>Non-fatal validation warnings callers can surface. Invalid results may also carry warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True for the built-in shipped default workflow (FR-005 fallback).</summary>
    public bool IsBuiltIn { get; init; }

    public static WorkflowLoadResult Valid(
        string source,
        WorkflowDefinition definition,
        bool isBuiltIn = false,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Source = source,
            IsValid = true,
            Definition = definition,
            IsBuiltIn = isBuiltIn,
            Warnings = warnings ?? [],
        };

    public static WorkflowLoadResult Invalid(
        string source,
        string error,
        WorkflowDefinition? definition = null,
        bool isBuiltIn = false,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Source = source,
            IsValid = false,
            Definition = definition,
            Error = error,
            IsBuiltIn = isBuiltIn,
            Warnings = warnings ?? [],
        };
}
