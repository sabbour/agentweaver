namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// The supported review-step kinds (Feature 010, FR-026). Each maps onto an existing runtime gate:
/// <list type="bullet">
///   <item><see cref="Rai"/> — the RAI verdict gate (RaiTurnExecutor); enforces content-safety fail-safe (FR-030).</item>
///   <item><see cref="Rubberduck"/> — the request-changes-to-producer review loop.</item>
///   <item><see cref="HumanReview"/> — the human-approval RequestPort gate (opt-in; gates irreversible actions, FR-029).</item>
/// </list>
/// </summary>
public enum ReviewStepKind
{
    /// <summary>Responsible-AI content-safety review gate (FR-026/030). Default-on.</summary>
    Rai,

    /// <summary>Rubber-duck / request-changes-to-producer review loop (FR-026). Default-on.</summary>
    Rubberduck,

    /// <summary>Human approval gate for irreversible actions (FR-026/029). Opt-in only (FR-032).</summary>
    HumanReview,
}

/// <summary>
/// A single review step within a <see cref="ReviewPolicy"/>. Ordered relative to its siblings; the
/// composition transform injects steps in declared order immediately before the merge node (FR-028).
/// </summary>
public sealed record ReviewStep
{
    public required ReviewStepKind Kind { get; init; }

    /// <summary>Human-readable label for rendering (defaults to a kind-derived label).</summary>
    public string? Label { get; init; }
}

/// <summary>
/// A named, per-project review policy composed of ordered review steps (Feature 010, FR-025/026/033).
/// Distinct from any individual workflow definition: a project binds to a policy BY NAME and the
/// policy's steps inject into the project's runs at the pre-merge review point (FR-028). Validated
/// before use; the source of a project's effective review behavior.
/// </summary>
public sealed record ReviewPolicy
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The ordered review steps. At least one; injected in declared order pre-merge.</summary>
    public required IReadOnlyList<ReviewStep> Steps { get; init; }
}
