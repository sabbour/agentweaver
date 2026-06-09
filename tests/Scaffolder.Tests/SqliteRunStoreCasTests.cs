using FluentAssertions;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

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
    // Helper
    // =========================================================================

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
