using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Covers the preview-publication lease (#1315). Publishing can spend the configured Gateway
/// convergence window before its final <c>preview_ready</c> batch commits while the run is active.
/// The renewable lease keeps the run active for publication, and every terminal transition defers
/// until the lease clears.
/// </summary>
public class PreviewPublicationLeaseRunStoreTests
{
    private static readonly RunId Run = RunId.New();

    private static PreviewPublicationLeaseRunStore Decorate(LeaseRunStore inner) =>
        new(inner, pollInterval: TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task TerminalTransition_WaitsUntilPublicationReleasesTheLease()
    {
        var inner = new LeaseRunStore();
        var store = Decorate(inner);

        (await store.TryBeginPreviewPublicationAsync(Run, DateTimeOffset.UtcNow.AddMinutes(3)))
            .Should().BeTrue();

        var terminal = Task.Run(() => store.TrySetTerminalStatusAsync(
            Run, RunStatus.Completed, DateTimeOffset.UtcNow, "done"));

        // The transition must still be parked while the publication holds the lease.
        await Task.Delay(100);
        terminal.IsCompleted.Should().BeFalse();
        inner.TerminalCalls.Should().Be(0);

        await store.EndPreviewPublicationAsync(Run);

        (await terminal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        inner.TerminalCalls.Should().Be(1);
    }

    [Fact]
    public async Task TerminalTransition_ProceedsWhenTheLeaseExpires()
    {
        var inner = new LeaseRunStore();
        var store = Decorate(inner);

        // A replica that crashes mid-publication never releases the lease. Expiry is the backstop.
        await store.TryBeginPreviewPublicationAsync(Run, DateTimeOffset.UtcNow.AddMilliseconds(300));

        await store.TrySetTerminalStatusAsync(Run, RunStatus.Completed, DateTimeOffset.UtcNow, "done")
            .WaitAsync(TimeSpan.FromSeconds(5));

        inner.TerminalCalls.Should().Be(1);
    }

    [Fact]
    public async Task NonTerminalTransition_DoesNotWaitForTheLease()
    {
        var inner = new LeaseRunStore();
        var store = Decorate(inner);

        await store.TryBeginPreviewPublicationAsync(Run, DateTimeOffset.UtcNow.AddHours(1));

        // InProgress is not terminal, so it must pass through with no deferral at all.
        await store.UpdateStatusAsync(Run, RunStatus.InProgress, null)
            .WaitAsync(TimeSpan.FromSeconds(1));

        inner.StatusCalls.Should().Be(1);
    }

    [Fact]
    public async Task Lease_IsRefusedForATerminalRun()
    {
        var inner = new LeaseRunStore { Terminal = true };
        var store = Decorate(inner);

        // Publication must abort: a preview URL cannot be published for a run that has ended.
        (await store.TryBeginPreviewPublicationAsync(Run, DateTimeOffset.UtcNow.AddMinutes(3)))
            .Should().BeFalse();
    }

    [Fact]
    public void RunStoreChain_FindsAStoreThroughDecorators()
    {
        var guard = new RunActiveClaimGuardedRunStore(new LeaseRunStore(), new RunActiveClaimGuard());
        var outer = new PreviewPublicationLeaseRunStore(guard);

        // RunStreamStore and SqliteRunEventStream need the guarded store, which is no longer outermost.
        RunStoreChain.Find<RunActiveClaimGuardedRunStore>(outer).Should().BeSameAs(guard);
        RunStoreChain.Find<LeaseRunStore>(outer).Should().NotBeNull();
        RunStoreChain.Find<PreviewPublicationLeaseRunStore>(guard).Should().BeNull();
    }

    /// <summary>Minimal store that records the lease in memory, the way a database row would.</summary>
    internal sealed class LeaseRunStore : IRunStore
    {
        private DateTimeOffset? _leaseUntil;

        public volatile bool Terminal;
        public int TerminalCalls;
        public int StatusCalls;
        public List<DateTimeOffset> LeaseExpirations { get; } = [];

        public Task<bool> TryBeginPreviewPublicationAsync(RunId runId, DateTimeOffset leaseUntil, CancellationToken ct = default)
        {
            if (Terminal)
                return Task.FromResult(false);
            _leaseUntil = leaseUntil;
            LeaseExpirations.Add(leaseUntil);
            return Task.FromResult(true);
        }

        public Task EndPreviewPublicationAsync(RunId runId, CancellationToken ct = default)
        {
            _leaseUntil = null;
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetPreviewPublicationLeaseAsync(RunId runId, CancellationToken ct = default) =>
            Task.FromResult(_leaseUntil);

        public Task<bool> TrySetTerminalStatusAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref TerminalCalls);
            return Task.FromResult(true);
        }

        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref StatusCalls);
            return Task.CompletedTask;
        }

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => Task.FromResult<Run?>(null);
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
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
