using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Unit tests for <see cref="SqliteRunStore"/> CAS (compare-and-swap) methods
/// that guard the intermediate Merging state introduced in Story 4.
///
/// Each test uses an isolated SqliteDb (via TestSqliteDb) and exercises the
/// real SQLite behaviour with no mocks.
/// </summary>
public sealed class SqliteRunStoreCasTests
{
    // =========================================================================
    // HM-10a: TryStartMergingAsync returns true on the first call and false on
    // a second call, because the second call finds the run in Merging (not
    // AwaitingReview) and the conditional UPDATE matches zero rows.
    // =========================================================================
    [Fact]
    public async Task TryStartMerging_ReturnsTrue_OnFirstCall_False_OnSecond()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertAwaitingReviewRunAsync(store);

        var first = await store.TryStartMergingAsync(runId);
        first.Should().BeTrue(
            "first CAS must succeed when the run is in awaiting_review");

        var runAfterFirst = await store.GetAsync(runId);
        runAfterFirst!.Status.Should().Be(RunStatus.Merging,
            "TryStartMerging must atomically advance awaiting_review to merging");

        var second = await store.TryStartMergingAsync(runId);
        second.Should().BeFalse(
            "second CAS must fail because the run is already in merging, not awaiting_review");

        var runAfterSecond = await store.GetAsync(runId);
        runAfterSecond!.Status.Should().Be(RunStatus.Merging,
            "the status must remain merging after a failed second CAS");
    }

    // =========================================================================
    // HM-10b: RevertMergingAsync transitions Merging → AwaitingReview so that
    // the run is re-approvable after a Blocked outcome or process failure.
    // =========================================================================
    [Fact]
    public async Task RevertMerging_TransitionsMergingBackToAwaitingReview()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertAwaitingReviewRunAsync(store);

        await store.TryStartMergingAsync(runId);

        await store.RevertMergingAsync(runId);

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.AwaitingReview,
            "RevertMerging must return the run to awaiting_review so it can be re-approved");
    }

    // =========================================================================
    // HM-10c: CompleteMergingAsync transitions Merging → Merged and persists
    // the merge result and ended_at timestamp.
    // =========================================================================
    [Fact]
    public async Task CompleteMerging_TransitionsMergingToMerged_PersistsResultAndEndedAt()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertAwaitingReviewRunAsync(store);

        await store.TryStartMergingAsync(runId);

        var endedAt     = DateTimeOffset.UtcNow;
        var mergeResult = "merged:abc1234deadbeef0123456789abcdef01234567";
        await store.CompleteMergingAsync(runId, RunStatus.Merged, endedAt, mergeResult);

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.Merged,
            "CompleteMerging must advance merging to merged");
        run.Result.Should().Be(mergeResult,
            "CompleteMerging must persist the merge result string");
        run.EndedAt.Should().NotBeNull(
            "CompleteMerging must set the ended_at timestamp");
    }

    // =========================================================================
    // Fix 3 regression: when SendResponseAsync throws after TryStartMergingAsync
    // succeeds, the recovery path must be able to transition Merging -> Failed via
    // TrySetTerminalStatusAsync so the run is never permanently stranded.
    // =========================================================================
    [Fact]
    public async Task ApproveSendResponseFailure_MergingToFailed_RecoverySucceeds()
    {
        // Arrange: run at awaiting_review (the state just before the approve CAS).
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertAwaitingReviewRunAsync(store);

        // Act step 1: CAS succeeds — run enters Merging, as the approve endpoint does.
        var casWon = await store.TryStartMergingAsync(runId);
        casWon.Should().BeTrue("CAS must succeed on an awaiting_review run");

        // Act step 2: SendResponseAsync (simulated) throws. The catch block calls
        // TrySetTerminalStatusAsync to deterministically set the run to Failed.
        var recovered = await store.TrySetTerminalStatusAsync(
            runId, RunStatus.Failed, DateTimeOffset.UtcNow, "send_response_failed");

        // Assert: recovery must succeed because Merging is a non-terminal status.
        recovered.Should().BeTrue(
            "TrySetTerminalStatusAsync must succeed on a Merging run — it is non-terminal");

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.Failed,
            "the run must end in Failed when SendResponseAsync throws after the CAS");
        run.Result.Should().Be("send_response_failed",
            "the recovery reason must be persisted so operators can diagnose the failure");
    }

    // =========================================================================
    // Fix 3 stream half: stream must carry run.failed and be marked completed
    // when the SendResponseAsync catch block fires.
    // =========================================================================
    [Fact]
    public void ApproveSendResponseFailure_StreamCompletesWithRunFailedEvent()
    {
        var streamStore = new RunStreamStore();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, "user");

        // Simulate the events the approve endpoint emits before SendResponseAsync.
        entry.RecordNext(EventTypes.MergeStarted, new { tree_hash = "abc123" });

        // Simulate the catch block: emit run.failed and complete the stream.
        entry.RecordNext(EventTypes.RunFailed, new { reason = "send_response_failed" });
        streamStore.Complete(runId);

        var retrieved = streamStore.Get(runId);
        retrieved.Should().NotBeNull();
        retrieved!.IsCompleted.Should().BeTrue(
            "the stream must be marked completed by the catch block");

        var snapshot = retrieved.GetSnapshotSince(0);
        snapshot.IsCompleted.Should().BeTrue();
        snapshot.Events.Should().ContainSingle(e => e.Type == EventTypes.RunFailed,
            "run.failed must be emitted to the stream when SendResponseAsync throws");
        snapshot.Events.First(e => e.Type == EventTypes.RunFailed)
            .Payload.GetType().GetProperty("reason")!.GetValue(
                snapshot.Events.First(e => e.Type == EventTypes.RunFailed).Payload)
            !.ToString()
            .Should().Be("send_response_failed");
    }

    // =========================================================================
    // Dormancy CAS (HITL resumability v2): TryTransitionToIdleAsync parks an
    // InProgress run to the non-terminal Idle state, single-winner across
    // replicas, WITHOUT setting ended_at (an Idle run is paused, not ended).
    // =========================================================================
    [Fact]
    public async Task TryTransitionToIdle_ParksInProgressRun_SingleWinner_NoEndedAt()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertInProgressRunAsync(store);

        var first = await store.TryTransitionToIdleAsync(runId);
        first.Should().BeTrue("first CAS must park an in_progress run dormant");

        var parked = await store.GetAsync(runId);
        parked!.Status.Should().Be(RunStatus.Idle, "the run must be parked dormant, not terminal");
        parked.EndedAt.Should().BeNull("a dormant run is paused, not ended — ended_at must stay null");

        var second = await store.TryTransitionToIdleAsync(runId);
        second.Should().BeFalse("second CAS must be a no-op: the run is already idle, not in_progress");
    }

    [Fact]
    public async Task TryTransitionToIdle_FailsWhenNotInProgress()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertAwaitingReviewRunAsync(store);

        var parked = await store.TryTransitionToIdleAsync(runId);
        parked.Should().BeFalse("only an in_progress run may be parked dormant");

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.AwaitingReview, "a non-in_progress run must be left untouched");
    }

    [Fact]
    public async Task TryWakeFromIdle_WakesDormantRun_SingleWinner()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var runId = await InsertInProgressRunAsync(store);
        (await store.TryTransitionToIdleAsync(runId)).Should().BeTrue();

        var first = await store.TryWakeFromIdleAsync(runId);
        first.Should().BeTrue("first waker wins the CAS");

        var woken = await store.GetAsync(runId);
        woken!.Status.Should().Be(RunStatus.InProgress, "waking must return a dormant run to in_progress");

        var second = await store.TryWakeFromIdleAsync(runId);
        second.Should().BeFalse("second waker must be a no-op: the run is already in_progress — no double-wake");
    }

    // =========================================================================
    // Helper
    // =========================================================================

    private static async Task<RunId> InsertInProgressRunAsync(SqliteRunStore store)
    {
        var runId = RunId.New();
        await store.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "cas idle unit test task",
            SubmittingUser    = "cas-test-user",
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        });
        return runId;
    }

    private static async Task<RunId> InsertAwaitingReviewRunAsync(SqliteRunStore store)
    {
        var runId = RunId.New();
        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "cas unit test task",
            SubmittingUser    = "cas-test-user",
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);
        await store.UpdateReviewReadyAsync(runId, "test-tree-hash", "test-diff", 0);
        return runId;
    }
}
