using System.Text;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Api;

public sealed class RunOutputRevisionStoreTests
{
    [Fact]
    public async Task PublishingTwicePreservesIdentityAndBytesThenLinksNextGeneration()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        const string diff = "diff --git a/é b/é\n+😀\n";

        await store.UpdateReviewReadyAsync(id, "tree-a", diff, 1);
        var first = (await store.GetLatestOutputRevisionAsync(id))!;
        await db.Db.EnsureCreatedAsync();
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes(diff));
        first.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes(diff));
        first.DiffSha256.Should().Be(RunOutputRevision.Sha256(Encoding.UTF8.GetBytes(diff)));
        first.ManifestIncomplete.Should().BeTrue();
        first.DiffBytes[0] = 0;
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes(diff));
        await store.UpdateReviewReadyAsync(id, "tree-a", diff, 1);
        (await store.ListOutputRevisionsAsync(id)).Should().ContainSingle();
        (await store.GetLatestOutputRevisionAsync(id))!.RevisionId.Should().Be(first.RevisionId);

        await store.TryTransitionReviewToInProgressAsync(id);
        await store.UpdateReviewReadyAsync(id, "tree-b", "different", 1);
        var latest = (await store.GetLatestOutputRevisionAsync(id))!;
        latest.PredecessorRevisionId.Should().Be(first.RevisionId);
        latest.LifecycleGeneration.Should().Be(first.LifecycleGeneration + 1);
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes(diff));
        await FluentActions.Invoking(() => store.PublishReviewReadyAsync(id, first.LifecycleGeneration, "old", "stale", 1))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BlockedMergeCannotRepublishReviewedOutputAfterRequestChanges()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        var output = new AgentTurnOutput(
            id.ToString(), "old-tree", "old-diff", 1, "worktree", "branch", "repo", "main", false);
        var reviewed = new WorkflowReviewRequest(
            id.ToString(), output.TreeHash, output.Diff, output.StepCount,
            LifecycleGeneration: (await store.GetAsync(id))!.LifecycleGeneration);
        await store.PublishReviewReadyAsync(
            id, reviewed.LifecycleGeneration!.Value, output.TreeHash, output.Diff, output.StepCount);
        var original = (await store.GetLatestOutputRevisionAsync(id))!;

        (await store.TryTransitionReviewToInProgressAsync(id)).Should().BeTrue();
        var blocked = RunWorkflowFactory.RecreateBlockedReviewRequest(output, reviewed);
        blocked.LifecycleGeneration.Should().Be(original.LifecycleGeneration);
        (await store.GetAsync(id))!.LifecycleGeneration.Should().Be(original.LifecycleGeneration + 1);

        await FluentActions.Invoking(() => store.PublishReviewReadyAsync(
                id, blocked.LifecycleGeneration!.Value, blocked.TreeHash, blocked.Diff, blocked.StepCount))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lifecycle generation*");
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().BeNull();
        (await store.ListOutputRevisionsAsync(id)).Should().ContainSingle()
            .Which.RevisionId.Should().Be(original.RevisionId);
    }

    [Fact]
    public void BlockedMergeWithoutCapturedReviewGenerationFailsClosed()
    {
        var output = new AgentTurnOutput(
            RunId.New().ToString(), "tree", "diff", 1, "worktree", "branch", "repo", "main", false);
        var legacy = new WorkflowReviewRequest(output.RunId, output.TreeHash, output.Diff, output.StepCount);

        Action missing = () => RunWorkflowFactory.RecreateBlockedReviewRequest(output, null);
        Action unfenced = () => RunWorkflowFactory.RecreateBlockedReviewRequest(output, legacy);
        missing.Should().Throw<InvalidOperationException>().WithMessage("*generation-bound*");
        unfenced.Should().Throw<InvalidOperationException>().WithMessage("*generation-bound*");
    }

    [Fact]
    public async Task ConflictingAndCancelledPublicationsCannotReplaceRevision()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "first", 0);
        var original = (await store.GetLatestOutputRevisionAsync(id))!;

        await FluentActions.Invoking(() => store.UpdateReviewReadyAsync(id, "tree-a", "second", 0))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => store.UpdateReviewReadyAsync(id, "tree-b", "first", 0))
            .Should().ThrowAsync<InvalidOperationException>();
        (await store.GetLatestOutputRevisionAsync(id))!.RevisionId.Should().Be(original.RevisionId);

        var cancelled = await InsertAsync(store);
        await using (var connection = await db.Db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET status = 'cancelled' WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", cancelled.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await FluentActions.Invoking(() => store.UpdateReviewReadyAsync(cancelled, "tree", "payload", 0))
            .Should().ThrowAsync<InvalidOperationException>();
        (await store.GetLatestOutputRevisionAsync(cancelled)).Should().BeNull();
    }

    [Fact]
    public async Task ApprovalRequiresExactStoredRevisionAndRejectsChangedOutput()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "first", 1);
        var revision = (await store.GetLatestOutputRevisionAsync(id))!;

        (await store.TryStartMergingAsync(id)).Should().BeFalse();
        (await store.TryStartMergingRevisionAsync(id, "stale")).Should().BeFalse();
        (await store.TryStartMergingRevisionAsync(id, revision.RevisionId)).Should().BeTrue();
        (await store.GetAsync(id))!.ApprovedOutputRevisionId.Should().Be(revision.RevisionId);
        await store.RevertMergingAsync(id);

        await using (var connection = await db.Db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET diff = 'changed' WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        (await store.TryStartMergingRevisionAsync(id, revision.RevisionId)).Should().BeFalse();
        (await store.TryTransitionToCommittingRevisionAsync(id, revision.RevisionId)).Should().BeFalse();
    }

    [Fact]
    public async Task CommittingCannotReplaceApprovedTree()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "first", 1);
        var revision = (await store.GetLatestOutputRevisionAsync(id))!;
        (await store.TryTransitionToCommittingRevisionAsync(id, revision.RevisionId)).Should().BeTrue();
        await FluentActions.Invoking(() => store.UpdateTreeHashAfterCommitAsync(id, "tree-b"))
            .Should().ThrowAsync<InvalidOperationException>();
        (await store.TryStartMergingAsync(id)).Should().BeFalse();
        (await store.TryStartMergingRevisionAsync(id, revision.RevisionId)).Should().BeTrue();
        (await store.GetAsync(id))!.ApprovedOutputRevisionId.Should().Be(revision.RevisionId);
    }

    [Fact]
    public async Task MissingPublishedRevisionNeverFallsBackToLegacyApproval()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "first", 1);
        var revision = (await store.GetLatestOutputRevisionAsync(id))!;
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().Be(revision.RevisionId);
        await using (var connection = await db.Db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER trg_run_output_revisions_no_delete;
                DELETE FROM run_output_revisions WHERE revision_id = $revision;
                """;
            command.Parameters.AddWithValue("$revision", revision.RevisionId);
            await command.ExecuteNonQueryAsync();
        }
        (await store.GetLatestOutputRevisionAsync(id)).Should().BeNull();
        (await store.TryStartMergingAsync(id)).Should().BeFalse();
        (await store.TryStartMergingRevisionAsync(id, revision.RevisionId)).Should().BeFalse();
        (await store.TryTransitionToCommittingAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task MissingCurrentRevisionCannotBeReplacedByOlderRevision()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "first", 1);
        var older = (await store.GetLatestOutputRevisionAsync(id))!;
        (await store.TryTransitionReviewToInProgressAsync(id)).Should().BeTrue();
        await store.UpdateReviewReadyAsync(id, "tree-b", "second", 1);
        var current = (await store.GetLatestOutputRevisionAsync(id))!;
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().Be(current.RevisionId);
        await using (var connection = await db.Db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER trg_run_output_revisions_no_delete;
                DELETE FROM run_output_revisions WHERE revision_id = $revision;
                """;
            command.Parameters.AddWithValue("$revision", current.RevisionId);
            await command.ExecuteNonQueryAsync();
        }
        (await store.GetLatestOutputRevisionAsync(id))!.RevisionId.Should().Be(older.RevisionId);
        (await store.TryStartMergingRevisionAsync(id, older.RevisionId)).Should().BeFalse();
        (await store.TryTransitionToCommittingRevisionAsync(id, older.RevisionId)).Should().BeFalse();
        (await store.GetOutputRevisionAsync(id, older.RevisionId))!.DiffBytes
            .Should().Equal(Encoding.UTF8.GetBytes("first"));
    }

    [Fact]
    public async Task MissingCorruptAndUnsupportedContentFailExplicitly()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.UpdateReviewReadyAsync(id, "tree-a", "payload", 0);
        var revision = (await store.GetLatestOutputRevisionAsync(id))!;
        (await store.GetOutputRevisionAsync(id, "absent")).Should().BeNull();

        await using var conn = await db.Db.OpenConnectionAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = "UPDATE run_output_revisions SET tree_hash = 'changed' WHERE revision_id = $id;";
        command.Parameters.AddWithValue("$id", revision.RevisionId);
        await FluentActions.Invoking(() => command.ExecuteNonQueryAsync()).Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        command.CommandText = "DELETE FROM run_output_revisions WHERE revision_id = $id;";
        await FluentActions.Invoking(() => command.ExecuteNonQueryAsync()).Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        command.Parameters.Clear();
        command.CommandText = "DROP TRIGGER trg_run_output_revisions_no_update;";
        await command.ExecuteNonQueryAsync();
        foreach (var (column, value, reason) in new (string, object, string)[]
        {
            ("diff_bytes", DBNull.Value, "missing_content"),
            ("diff_bytes", Encoding.UTF8.GetBytes("tampered"), "corrupt_content"),
            ("schema_version", 99, "unsupported_schema")
        })
        {
            command.CommandText = $"UPDATE run_output_revisions SET {column} = $value WHERE revision_id = $id;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", revision.RevisionId);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync();
            var assertion = await FluentActions.Invoking(() => store.GetOutputRevisionAsync(id, revision.RevisionId))
                .Should().ThrowAsync<RunOutputRevisionUnavailableException>();
            assertion.Which.Reason.Should().Be(reason);
        }
    }

    private static async Task<RunId> InsertAsync(SqliteRunStore store)
    {
        var id = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "output revision",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow
        });
        return id;
    }
}
