namespace Agentweaver.Api.Git;

/// <summary>
/// Outcome of <see cref="WorktreeManager.BuildIntegrationBranch"/> (Phase 3, D1): the COMBINED
/// output of all eligible children, assembled as a git integration branch derived from the
/// coordinator's originating branch with every eligible child branch merged in topological order.
/// </summary>
public sealed record IntegrationBranchResult
{
    public IntegrationBranchOutcome Outcome { get; private init; }

    /// <summary>The integration branch name (always set).</summary>
    public string IntegrationBranch { get; private init; } = string.Empty;

    /// <summary>Aggregate tree hash of the integration branch tip (success only).</summary>
    public string? TreeHash { get; private init; }

    /// <summary>Aggregate unified diff of the integration branch vs the originating branch (success only).</summary>
    public string? Diff { get; private init; }

    /// <summary>Whether the aggregate has any changes vs the originating branch (success only).</summary>
    public bool HasChanges { get; private init; }

    /// <summary>Files auto-resolved by accepting the child branch version during integration build.</summary>
    public IReadOnlyList<(string Branch, IReadOnlyList<string> Files)> AutoResolutions { get; private init; } = [];

    /// <summary>The child branch that conflicted while merging (conflict only).</summary>
    public string? ConflictingBranch { get; private init; }

    /// <summary>Repo-relative conflicting file paths (conflict only).</summary>
    public IReadOnlyList<string> ConflictingFiles { get; private init; } = [];

    /// <summary>Human-readable reason (conflict/error only; sanitized of absolute paths by callers/logging).</summary>
    public string? Reason { get; private init; }

    public static IntegrationBranchResult Success(
        string integrationBranch,
        string treeHash,
        string diff,
        IReadOnlyList<(string Branch, IReadOnlyList<string> Files)>? autoResolutions = null) => new()
    {
        Outcome = IntegrationBranchOutcome.Built,
        IntegrationBranch = integrationBranch,
        TreeHash = treeHash,
        Diff = diff,
        HasChanges = !string.IsNullOrEmpty(diff),
        AutoResolutions = autoResolutions ?? [],
    };

    public static IntegrationBranchResult Conflict(
        string integrationBranch, string conflictingBranch, IReadOnlyList<string> conflictingFiles, string reason) => new()
    {
        Outcome = IntegrationBranchOutcome.Conflict,
        IntegrationBranch = integrationBranch,
        ConflictingBranch = conflictingBranch,
        ConflictingFiles = conflictingFiles,
        Reason = reason,
    };
}

public enum IntegrationBranchOutcome
{
    /// <summary>The integration branch was built; all eligible child branches merged cleanly.</summary>
    Built,

    /// <summary>Merging an eligible child branch into the integration branch conflicted (NO partial assembly).</summary>
    Conflict,
}
