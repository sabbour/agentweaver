using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.SandboxFs;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Implements IMergeCoordinator by coordinating RepositoryMergeLock + IRunStore CAS transitions.
/// </summary>
public sealed class MergeCoordinator : IMergeCoordinator
{
    private readonly IRunStore _runStore;
    private readonly RepositoryMergeLock _mergeLock;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<MergeCoordinator> _logger;

    public MergeCoordinator(IRunStore runStore, RepositoryMergeLock mergeLock, IWorktreeOperations worktreeOps, ILogger<MergeCoordinator> logger)
    {
        _runStore = runStore;
        _mergeLock = mergeLock;
        _worktreeOps = worktreeOps;
        _logger = logger;
    }

    public Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct) =>
        AcquireMergeLockAsync(runId, repositoryPath, ct, reviewer: null);

    private async Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct, string? reviewer)
    {
        string canonicalPath;
        try { canonicalPath = Path.GetFullPath(repositoryPath); }
        catch { return MergeLockResult.Failed("invalid_repository_path"); }

        if (!Directory.Exists(canonicalPath))
            return MergeLockResult.Failed("repository_path_not_found");

        string lockPath;
        try { lockPath = RealPath.Resolve(canonicalPath); }
        catch { return MergeLockResult.Failed("repository_path_not_found"); }

        var lockHandle = await _mergeLock.TryAcquireAsync(lockPath, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (lockHandle is null)
            return MergeLockResult.Failed("repository_busy");

        var casSucceeded = await _runStore.TryStartMergingAsync(RunId.Parse(runId), reviewer, ct: CancellationToken.None).ConfigureAwait(false);
        if (!casSucceeded)
        {
            var run = await _runStore.GetAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
            if (run?.Status != RunStatus.Merging)
            {
                lockHandle.Dispose();
                return MergeLockResult.Failed("already_merging");
            }
        }

        return MergeLockResult.Success(lockHandle);
    }

    public async Task<bool> CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct)
    {
        return await _runStore.CompleteMergingAsync(
            RunId.Parse(runId), RunStatus.Merged, DateTimeOffset.UtcNow, mergeResult, null, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RevertMergeAsync(string runId, CancellationToken ct)
    {
        await _runStore.RevertMergingAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<bool> FailMergeAsync(string runId, string mergeResult, string? mergeConflictsJson, CancellationToken ct)
    {
        return await _runStore.CompleteMergingAsync(
            RunId.Parse(runId), RunStatus.MergeFailed, DateTimeOffset.UtcNow, mergeResult, mergeConflictsJson, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MergeExecutionResult> ExecuteMergeAsync(MergeInput input, CancellationToken ct)
    {
        var lockResult = await AcquireMergeLockAsync(input.RunId, input.RepositoryPath, ct, input.ReviewedBy).ConfigureAwait(false);
        if (!lockResult.Acquired)
        {
            _logger.LogWarning("Failed to acquire merge lock for run {RunId}: {Reason}", input.RunId, lockResult.Reason);
            return new MergeExecutionResult
            {
                Outcome = MergeExecutionOutcome.LockFailed,
                LockFailureReason = lockResult.Reason
            };
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
                    var completedMerge = await _runStore.CompleteMergingAsync(
                        RunId.Parse(input.RunId), RunStatus.Merged, DateTimeOffset.UtcNow, mergeResult,
                        null, CancellationToken.None, result.CommitHash).ConfigureAwait(false);
                    if (!completedMerge)
                        _logger.LogWarning("CompleteMergeAsync CAS returned false for run {RunId} — possible concurrency conflict", input.RunId);

                    _logger.LogInformation(
                        "Merge outcome: success. RunId={RunId} CommitHash={CommitHash} MergeMode={MergeMode} " +
                        "PreviousHeadSha={PreviousHeadSha} NewHeadSha={NewHeadSha} WasFastForward={WasFastForward}",
                        input.RunId, result.CommitHash, result.MergeMode,
                        result.PreviousHeadSha, result.NewHeadSha, result.WasFastForward);

                    try { _worktreeOps.RemoveWorktree(input.RepositoryPath, input.WorktreePath, input.WorktreeBranch); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove worktree for run {RunId} after merge", input.RunId); }

                    return new MergeExecutionResult
                    {
                        Outcome = MergeExecutionOutcome.Merged,
                        MergeResult = mergeResult,
                        CommitHash = result.CommitHash,
                        MergeMode = result.MergeMode,
                        PreviousHeadSha = result.PreviousHeadSha
                    };

                case MergeResultKind.Blocked:
                    await RevertMergeAsync(input.RunId, ct).ConfigureAwait(false);
                    _logger.LogInformation("Merge outcome: blocked. RunId={RunId} Reason={Reason}",
                        input.RunId, SanitizeReason(result.Reason));
                    return new MergeExecutionResult
                    {
                        Outcome = MergeExecutionOutcome.Blocked,
                        Reason = result.Reason
                    };

                case MergeResultKind.Conflict:
                    var conflictingFiles = result.ConflictingFiles ?? [];
                    var mergeConflictsJson = conflictingFiles.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(conflictingFiles)
                        : null;
                    var conflictResult = $"conflict:{result.Reason}";
                    var failedMerge = await FailMergeAsync(input.RunId, conflictResult, mergeConflictsJson, ct).ConfigureAwait(false);
                    if (!failedMerge)
                        _logger.LogWarning("FailMergeAsync CAS returned false for run {RunId} — possible concurrency conflict", input.RunId);
                    _logger.LogInformation("Merge outcome: conflict. RunId={RunId} Details={Details}",
                        input.RunId, SanitizeReason(result.Reason));

                    // Preserve the worktree on conflict so the user can inspect and manually resolve.
                    // The worktree path is recorded in the run's worktree_path column for UI access.
                    _logger.LogInformation(
                        "Merge conflict for run {RunId}: preserving worktree at {WorktreePath} for manual inspection",
                        input.RunId, input.WorktreePath);

                    return new MergeExecutionResult
                    {
                        Outcome = MergeExecutionOutcome.Conflict,
                        MergeResult = conflictResult,
                        Reason = result.Reason,
                        ConflictingFiles = conflictingFiles
                    };

                default:
                    throw new InvalidOperationException($"Unexpected merge result kind: {result.Kind}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Merge operation threw unexpectedly for run {RunId}", input.RunId);
            await RevertMergeAsync(input.RunId, ct).ConfigureAwait(false);
            return new MergeExecutionResult
            {
                Outcome = MergeExecutionOutcome.InternalError,
                Reason = "unexpected_error"
            };
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
        var sanitized = Regex.Replace(
            reason, @"[A-Za-z]:\\[^\s""]+|/(?:home|Users|var|tmp|mnt|opt)/[^\s""]+", "[path]");
        const int maxLength = 200;
        return sanitized.Length > maxLength ? sanitized[..maxLength] + "..." : sanitized;
    }
}
