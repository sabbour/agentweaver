using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Implements IMergeCoordinator by coordinating RepositoryMergeLock + SqliteRunStore CAS transitions.
/// </summary>
public sealed class MergeCoordinator : IMergeCoordinator
{
    private readonly SqliteRunStore _runStore;
    private readonly RepositoryMergeLock _mergeLock;

    public MergeCoordinator(SqliteRunStore runStore, RepositoryMergeLock mergeLock)
    {
        _runStore = runStore;
        _mergeLock = mergeLock;
    }

    public async Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct)
    {
        string canonicalPath;
        try { canonicalPath = Path.GetFullPath(repositoryPath); }
        catch { return MergeLockResult.Failed("invalid_repository_path"); }

        if (!Directory.Exists(canonicalPath))
            return MergeLockResult.Failed("repository_path_not_found");

        var lockHandle = await _mergeLock.TryAcquireAsync(canonicalPath, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (lockHandle is null)
            return MergeLockResult.Failed("repository_busy");

        var casSucceeded = await _runStore.TryStartMergingAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
        if (!casSucceeded)
        {
            lockHandle.Dispose();
            return MergeLockResult.Failed("already_merging");
        }

        return MergeLockResult.Success(lockHandle);
    }

    public async Task CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct)
    {
        await _runStore.CompleteMergingAsync(
            RunId.Parse(runId), RunStatus.Merged, DateTimeOffset.UtcNow, mergeResult, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RevertMergeAsync(string runId, CancellationToken ct)
    {
        await _runStore.RevertMergingAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task FailMergeAsync(string runId, string mergeResult, CancellationToken ct)
    {
        await _runStore.CompleteMergingAsync(
            RunId.Parse(runId), RunStatus.MergeFailed, DateTimeOffset.UtcNow, mergeResult, CancellationToken.None).ConfigureAwait(false);
    }
}
