using Scaffolder.Api.Git;

namespace Scaffolder.Api.Coordinator;

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

    /// <summary>Runs the collective RAI review over the aggregate diff. Returns whether RAI flagged a
    /// safety concern (advisory — never hard-blocks; it informs the human reviewer).</summary>
    Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct);

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
public sealed record CollectiveRaiRequest(
    string CoordinatorRunId,
    string RepositoryPath,
    string AggregateDiff);

/// <summary>Outcome of the collective RAI review.</summary>
public sealed record CollectiveRaiResult(bool SafetyFlagged);

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
    string RepositoryPath,
    string ModelSource,
    string? ModelId,
    DateTimeOffset RunStartedAt);
