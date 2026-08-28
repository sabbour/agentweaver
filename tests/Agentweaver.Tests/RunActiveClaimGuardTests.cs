using System.Collections.Concurrent;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for <see cref="RunActiveClaimGuard"/> and <see cref="RunActiveClaimGuardedRunStore"/>
/// -- the in-process mutual-exclusion mechanism PR #972 finding #3 introduces so a SQLite-backed
/// active-run check can be made atomic with durable tool-approval policy persistence, even though
/// the run store (SqliteRunStore) and the RunEvents/policy store (EF MemoryDbContext) are two
/// separate SQLite database files that cannot share one ACID transaction.
/// </summary>
public sealed class RunActiveClaimGuardTests
{
    [Fact]
    public async Task AcquireAsync_SerializesConcurrentClaimsForTheSameRunId()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var order = new ConcurrentQueue<string>();
        var firstHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            await using var claim = await guard.AcquireAsync(runId, CancellationToken.None);
            order.Enqueue("first-acquired");
            firstHeld.SetResult();
            await releaseFirst.Task;
            order.Enqueue("first-released");
        });

        await firstHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = Task.Run(async () =>
        {
            await using var claim = await guard.AcquireAsync(runId, CancellationToken.None);
            order.Enqueue("second-acquired");
        });

        // The second attempt must not be able to acquire the same run's claim while the first
        // still holds it -- this is the exact property finding #3 requires between the SQLite
        // active-run read and the durable policy commit.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        second.IsCompleted.Should().BeFalse("a concurrent claim for the same run must wait");

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        order.Should().Equal("first-acquired", "first-released", "second-acquired");
    }

    [Fact]
    public async Task AcquireAsync_DoesNotSerializeDifferentRunIds()
    {
        var guard = new RunActiveClaimGuard();
        var runA = RunId.New();
        var runB = RunId.New();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claimATask = Task.Run(async () =>
        {
            await using var claim = await guard.AcquireAsync(runA, CancellationToken.None);
            aHeld.SetResult();
            await releaseA.Task;
        });

        await aHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var claimBTask = guard.AcquireAsync(runB, CancellationToken.None);
        var completed = await Task.WhenAny(claimBTask, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(claimBTask, "an unrelated run id must not be blocked by another run's claim");

        var claimB = await claimBTask;
        await claimB.DisposeAsync();
        releaseA.SetResult();
        await claimATask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireAsync_RemovesEntryAfterTheFinalClaimIsReleased()
    {
        var guard = new RunActiveClaimGuard();

        await using (await guard.AcquireAsync(RunId.New(), CancellationToken.None))
            guard.EntryCount.Should().Be(1);

        guard.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_KeepsEntryUntilAWaitingClaimIsReleased()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var first = await guard.AcquireAsync(runId, CancellationToken.None);
        var secondTask = guard.AcquireAsync(runId, CancellationToken.None);

        guard.EntryCount.Should().Be(1);

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        guard.EntryCount.Should().Be(1, "the acquired waiter still owns the registry entry");

        await second.DisposeAsync();
        guard.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_CancelledWaiterReleasesItsEntryReference()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var holder = await guard.AcquireAsync(runId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiter = guard.AcquireAsync(runId, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        guard.EntryCount.Should().Be(1, "the active holder still requires the entry");

        await holder.DisposeAsync();
        guard.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_WaiterAndNewAcquisitionShareTheSameEntryAcrossFinalRelease()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var first = await guard.AcquireAsync(runId, CancellationToken.None);
        var waiterTask = guard.AcquireAsync(runId, CancellationToken.None);

        await first.DisposeAsync();
        var waiter = await waiterTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The waiter acquired after the original holder released. A concurrent later acquisition
        // must use that same entry and wait, rather than observe an entry removed too early and
        // create a second semaphore for this run.
        var laterTask = guard.AcquireAsync(runId, CancellationToken.None);
        laterTask.IsCompleted.Should().BeFalse();

        await waiter.DisposeAsync();
        var later = await laterTask.WaitAsync(TimeSpan.FromSeconds(5));
        await later.DisposeAsync();
        guard.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task GuardedRunStore_TrySetTerminalStatusAsync_WaitsForExternallyHeldActiveClaim()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var inner = new RecordingRunStore();
        var store = new RunActiveClaimGuardedRunStore(inner, guard);

        // Simulate DurableToolApprovalGate.ResolveAndPersistAsync holding the claim across its
        // read-then-commit critical section, exactly as it now does for every non-once scope.
        var claim = await guard.AcquireAsync(runId, CancellationToken.None);
        var terminalizeTask = store.TrySetTerminalStatusAsync(
            runId, RunStatus.Failed, DateTimeOffset.UtcNow, "race", CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        terminalizeTask.IsCompleted.Should().BeFalse(
            "a guarded status transition must wait for the in-flight approval-scope claim to release");
        inner.TerminalizeCalls.Should().Be(0, "the inner store must not observe the call until the claim is free");

        await claim.DisposeAsync();

        (await terminalizeTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        inner.TerminalizeCalls.Should().Be(1);
    }

    [Fact]
    public async Task GuardedRunStore_UpdateReviewReadyAsync_WaitsForExternallyHeldActiveClaim()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var inner = new RecordingRunStore();
        var store = new RunActiveClaimGuardedRunStore(inner, guard);

        await using var claim = await guard.AcquireAsync(runId, CancellationToken.None);
        var reviewReadyTask = store.UpdateReviewReadyAsync(
            runId, "tree", "diff", 1, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        reviewReadyTask.IsCompleted.Should().BeFalse(
            "marking review ready transitions an InProgress run and must not overlap a durable scope grant");
        inner.ReviewReadyCalls.Should().Be(0, "the inner store must not observe the call until the claim is free");

        await claim.DisposeAsync();

        await reviewReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        inner.ReviewReadyCalls.Should().Be(1);
    }

    [Fact]
    public async Task GuardedRunStore_ReadOnlyMembers_ArePurelyPassThroughAndNeverGated()
    {
        var guard = new RunActiveClaimGuard();
        var runId = RunId.New();
        var inner = new RecordingRunStore();
        var store = new RunActiveClaimGuardedRunStore(inner, guard);

        // Hold the claim; a pass-through read (GetAsync is not one of the guarded members)
        // must still complete immediately -- guarding every read would be an unnecessary and
        // incorrect over-serialization, not required by finding #3.
        await using var claim = await guard.AcquireAsync(runId, CancellationToken.None);
        var read = await store.GetAsync(runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        read.Should().BeNull();
        inner.GetAsyncCalls.Should().Be(1);
    }

    private sealed class RecordingRunStore : IRunStore
    {
        public int TerminalizeCalls;
        public int ReviewReadyCalls;
        public int GetAsyncCalls;

        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetAsyncCalls);
            return Task.FromResult<Run?>(null);
        }

        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref TerminalizeCalls);
            return Task.FromResult(true);
        }

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null)
        {
            Interlocked.Increment(ref ReviewReadyCalls);
            return Task.CompletedTask;
        }
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
