using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Provider-neutral run store interface. Both SqliteRunStore and EfRunStore implement this.
/// Register the correct implementation via Database:Provider in DI.
/// </summary>
public interface IRunStore
{
    Task InsertAsync(Run run, CancellationToken ct = default);
    Task<Run?> GetAsync(RunId runId, CancellationToken ct = default);
    Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default);
    Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default);
    Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default);
    Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default);
    Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null);
    Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null);
    Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default);
    Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default);
    Task<bool> TrySetTerminalStatusAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default);
    Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default);
    Task DeleteAsync(RunId runId, CancellationToken ct = default);
    Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default);
    Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default);
    Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default);
    Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default);
    Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default);
    Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default);
    Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default);
    Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default);
    Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default);
    Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Returns the non-archived runs submitted by <paramref name="submittingUser"/>, newest-first
    /// (by StartedAt), optionally restricted to a single <paramref name="agentName"/> (e.g. the
    /// operator-assistant sentinel) and capped at <paramref name="limit"/>. Used to list a caller's
    /// own conversations without leaking other users' runs. The default implementation throws so the
    /// two production stores (SQLite, EF) must provide it; test fakes that never call it are exempt.
    /// </summary>
    Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement GetRunsBySubmittingUserAsync.");
}
