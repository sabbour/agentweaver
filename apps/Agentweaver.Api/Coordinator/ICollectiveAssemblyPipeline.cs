using Agentweaver.Api.Git;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Side-effecting seam for the Phase 3 collective-assembly pipeline (D3). Separates the orchestration
/// logic in <see cref="CoordinatorAssemblyService"/> (CAS, eligibility, events, node-flip, HITL,
/// rejection inference — all unit-testable) from the heavy git + live-agent operations (integration
/// branch build, collective RAI, collective merge, collective scribe), so the service can be driven
/// end-to-end in tests with a fake pipeline. The production implementation
/// (<see cref="CollectiveAssemblyPipeline"/>) REUSES the existing executors/coordinator:
/// <see cref="WorktreeManager"/> for git, <c>RaiTurnExecutor</c>/<c>ScribeTurnExecutor</c> for the
/// agent turns, and <c>WorktreeManager.MergeWorktree</c>/<c>RepositoryMergeLock</c> for the merge.
/// </summary>
public interface ICollectiveAssemblyPipeline
{
    /// <summary>Builds the COMBINED integration branch (D1) — pure git, no agent.</summary>
    IntegrationBranchResult BuildIntegrationBranch(CollectiveIntegrationRequest request);

    /// <summary>Best-effort retry preparation for a failed integration-branch build.</summary>
    void PrepareIntegrationBranchRetry(CollectiveIntegrationRequest request);

    /// <summary>Runs the collective RAI review over the aggregate diff. Returns whether RAI flagged a
    /// safety concern (advisory — never hard-blocks; it informs the human reviewer).</summary>
    Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct);

    /// <summary>Runs the collective rubber-duck review over the aggregate diff.</summary>
    Task<CollectiveGateDecision> RunRubberduckAsync(CollectiveRubberduckRequest request, CancellationToken ct);

    /// <summary>Runs the collective Build & Test gate over the assembled integration branch.</summary>
    Task<CollectiveGateDecision> RunBuildTestAsync(CollectiveBuildTestRequest request, CancellationToken ct);

    /// <summary>
    /// Provisions (or destructively recreates) the detached reviewer worktree for the assembly gates so
    /// the collective RAI + rubber-duck reviewers can READ the assembled integration files host-side —
    /// raw bytes, line endings, integration state — rather than only the aggregate diff text (#236).
    /// Reuses the deterministic Build/Test worktree name: Build/Test destructively recreates the same
    /// worktree when it runs (no reviewer-write bleed into Build/Test) and the existing
    /// <see cref="CleanupBuildTestResourcesAsync"/> path tears it down (no extra cleanup wiring).
    /// Returns the absolute worktree path. Callers should only invoke this when the integration has
    /// changes; empty-diff assemblies never need a worktree.
    /// </summary>
    string PrepareReviewerWorktree(string coordinatorRunId, string repositoryPath, string integrationBranch);

    /// <summary>Releases any coordinator-scoped Build/Test pod and detached worktree.</summary>
    Task CleanupBuildTestResourcesAsync(
        string coordinatorRunId,
        string repositoryPath,
        CancellationToken ct = default);

    /// <summary>
    /// Absolute path of the coordinator's detached Build/Test worktree (spec-006 decouple-preview).
    /// The deterministic <c>PreviewStep</c> uses it as its command-discovery root and process cwd.
    /// The path is deterministic from the coordinator run id and is valid from the moment
    /// <see cref="RunBuildTestAsync"/> creates the worktree until <see cref="CleanupBuildTestResourcesAsync"/>.
    /// </summary>
    string GetBuildTestWorktreePath(string coordinatorRunId);

    /// <summary>Performs the ONE collective merge of the integration branch into the originating branch.</summary>
    Task<CollectiveMergeResult> MergeAsync(CollectiveMergeRequest request, CancellationToken ct);

    /// <summary>Runs the ONE collective scribe pass after a successful merge.</summary>
    Task RunScribeAsync(CollectiveScribeRequest request, CancellationToken ct);
}

/// <summary>Inputs to build the integration branch: eligible child branches in dependency order.</summary>
public sealed record CollectiveIntegrationRequest(
    string RepositoryPath,
    string OriginatingBranch,
    string IntegrationBranch,
    IReadOnlyList<string> ChildBranchesInOrder);

/// <summary>Inputs to the collective RAI review of the aggregate diff.</summary>
/// <param name="WorktreePath">
/// #236 — absolute path of a checked-out worktree at the assembled integration branch, so the reviewer
/// can read the integration files host-side. Empty ⇒ diff-text-only (empty-diff assemblies).</param>
public sealed record CollectiveRaiRequest(
    string CoordinatorRunId,
    string RepositoryPath,
    string AggregateDiff,
    string SubmittingUser,
    string WorktreePath = "");

/// <summary>Outcome of the collective RAI review.</summary>
public sealed record CollectiveRaiResult(bool SafetyFlagged);

/// <summary>Inputs to the collective rubber-duck review of the aggregate diff.</summary>
/// <param name="WorktreePath">
/// #236 — absolute path of a checked-out worktree at the assembled integration branch, so the reviewer
/// can read the integration files host-side. Empty ⇒ diff-text-only (empty-diff assemblies).</param>
public sealed record CollectiveRubberduckRequest(
    string CoordinatorRunId,
    string RepositoryPath,
    string AggregateDiff,
    string SubmittingUser,
    string? GateNodeId = null,
    string? DisplayLabel = null,
    string WorktreePath = "");

/// <summary>Inputs to the collective Build & Test gate.</summary>
public sealed record CollectiveBuildTestRequest(
    string CoordinatorRunId,
    string? ProjectId,
    string RepositoryPath,
    string IntegrationBranch,
    string AggregateTreeHash,
    string AggregateDiff,
    string SubmittingUser,
    string? GateNodeId = null,
    string? DisplayLabel = null,
    string? AgentId = null);

/// <summary>Normalized pass/revise decision from an authored collective assembly gate.</summary>
/// <param name="TargetFiles">
/// #223 — the reviewer's OPTIONAL structured implicated-file hint (repo-relative diff paths it
/// actually saw). Reverse-mapped to implicated subtasks deterministically by the coordinator; never
/// inferred from prose. Null/empty ⇒ the coordinator fails safe to the whole contributor set.</param>
public sealed record CollectiveGateDecision(
    bool Approved,
    bool RequestChanges,
    string? Feedback,
    IReadOnlyList<string>? TargetFiles = null);

public sealed class CollectiveBuildTestInfrastructureException : Exception
{
    public string Reason { get; }
    public bool Retryable { get; }

    public CollectiveBuildTestInfrastructureException(
        string reason,
        string message,
        bool retryable = true,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "build_test_infrastructure_failure" : reason;
        Retryable = retryable;
    }
}

/// <summary>Inputs to the single collective merge of the integration branch into origin.</summary>
public sealed record CollectiveMergeRequest(
    string CoordinatorRunId,
    string RepositoryPath,
    string OriginatingBranch,
    string IntegrationBranch,
    string TreeHash);

/// <summary>Outcome of the single collective merge.</summary>
public sealed record CollectiveMergeResult
{
    public CollectiveMergeOutcome Outcome { get; init; }
    public string? CommitHash { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> ConflictingFiles { get; init; } = [];

    public static CollectiveMergeResult Merged(string? commitHash) =>
        new() { Outcome = CollectiveMergeOutcome.Merged, CommitHash = commitHash };

    public static CollectiveMergeResult Conflict(IReadOnlyList<string> files, string? reason) =>
        new() { Outcome = CollectiveMergeOutcome.Conflict, ConflictingFiles = files, Reason = reason };

    public static CollectiveMergeResult Failed(string? reason) =>
        new() { Outcome = CollectiveMergeOutcome.Failed, Reason = reason };
}

public enum CollectiveMergeOutcome { Merged, Conflict, Failed }

/// <summary>Inputs to the single collective scribe pass.</summary>
public sealed record CollectiveScribeRequest(
    string CoordinatorRunId,
    string? ProjectId,
    string AgentName,
    string SubmittingUser,
    string RepositoryPath,
    string ModelSource,
    string? ModelId,
    DateTimeOffset RunStartedAt,
    string? TerminalStatus = null,
    string? MergeResult = null);
