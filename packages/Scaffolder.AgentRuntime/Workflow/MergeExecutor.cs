using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Executor that performs the merge of the worktree branch into the originating branch.
/// Acquires the per-repository merge lock, performs the CAS transition, and merges.
/// </summary>
public sealed class MergeExecutor : Executor<MergeInput, MergeOutput>
{
    private readonly IWorktreeOperations _worktreeOps;
    private readonly IMergeCoordinator _mergeCoordinator;
    private readonly ILogger<MergeExecutor> _logger;

    public MergeExecutor(
        IWorktreeOperations worktreeOps,
        IMergeCoordinator mergeCoordinator,
        ILogger<MergeExecutor> logger)
        : base("merge")
    {
        _worktreeOps = worktreeOps;
        _mergeCoordinator = mergeCoordinator;
        _logger = logger;
    }

    public override async ValueTask<MergeOutput> HandleAsync(
        MergeInput input, IWorkflowContext context, CancellationToken ct)
    {
        // Acquire per-repo lock + CAS gate (AwaitingReview -> Merging) via the coordinator.
        var lockResult = await _mergeCoordinator.AcquireMergeLockAsync(input.RunId, input.RepositoryPath, ct);
        if (!lockResult.Acquired)
        {
            _logger.LogWarning("Failed to acquire merge lock for run {RunId}: {Reason}", input.RunId, lockResult.Reason);
            return new MergeOutput(input.RunId, "merge_failed", lockResult.Reason);
        }

        try
        {
            var result = _worktreeOps.MergeWorktree(
                input.RepositoryPath,
                input.OriginatingBranch,
                input.WorktreeBranch,
                input.TreeHash);

            switch (result.Kind)
            {
                case MergeResultKind.Merged:
                    var mergeResult = $"merged:{result.CommitHash}";
                    await _mergeCoordinator.CompleteMergeAsync(input.RunId, mergeResult, ct);

                    _logger.LogInformation(
                        "Merge outcome: success. RunId={RunId} CommitHash={CommitHash} MergeMode={MergeMode} " +
                        "PreviousHeadSha={PreviousHeadSha} NewHeadSha={NewHeadSha} WasFastForward={WasFastForward}",
                        input.RunId, result.CommitHash, result.MergeMode,
                        result.PreviousHeadSha, result.NewHeadSha, result.WasFastForward);

                    // Remove worktree on successful merge.
                    try { _worktreeOps.RemoveWorktree(input.RepositoryPath, input.WorktreePath, input.WorktreeBranch); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove worktree for run {RunId} after merge", input.RunId); }

                    return new MergeOutput(input.RunId, "merged", mergeResult);

                case MergeResultKind.Blocked:
                    // Revert Merging -> AwaitingReview. This is a retriable condition.
                    await _mergeCoordinator.RevertMergeAsync(input.RunId, ct);
                    _logger.LogInformation("Merge outcome: blocked. RunId={RunId} Reason={Reason}",
                        input.RunId, SanitizeReason(result.Reason));
                    return new MergeOutput(input.RunId, "merge_failed", result.Reason);

                case MergeResultKind.Conflict:
                    var conflictResult = $"conflict:{result.Reason}";
                    await _mergeCoordinator.FailMergeAsync(input.RunId, conflictResult, ct);
                    _logger.LogInformation("Merge outcome: conflict. RunId={RunId} Details={Details}",
                        input.RunId, SanitizeReason(result.Reason));
                    return new MergeOutput(input.RunId, "merge_failed", conflictResult);

                default:
                    throw new InvalidOperationException($"Unexpected merge result kind: {result.Kind}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Merge operation threw unexpectedly for run {RunId}", input.RunId);
            await _mergeCoordinator.RevertMergeAsync(input.RunId, ct);
            return new MergeOutput(input.RunId, "merge_failed", "unexpected_error");
        }
        finally
        {
            lockResult.Release();
        }
    }

    /// <summary>
    /// Sanitizes merge reason strings for logging: strips absolute paths and truncates
    /// to prevent leaking repository structure into log sinks.
    /// </summary>
    private static string SanitizeReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return string.Empty;
        // Strip segments that look like absolute paths (Windows or Unix).
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            reason, @"[A-Za-z]:\\[^\s""]+|/(?:home|Users|var|tmp|mnt|opt)/[^\s""]+", "[path]");
        const int maxLength = 200;
        return sanitized.Length > maxLength ? sanitized[..maxLength] + "..." : sanitized;
    }
}
