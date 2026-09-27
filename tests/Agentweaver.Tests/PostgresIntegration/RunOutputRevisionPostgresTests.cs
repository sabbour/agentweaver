using System.Text;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain;
using FluentAssertions;
using Npgsql;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RunOutputRevisionPostgresTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task MigrationAndPublicationPreserveBytesAndPreventMutation()
    {
        var store = new EfRunStore(pg.Factory);
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
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        await store.PublishReviewReadyAsync(id, generation, "tree-a", "héllo\n", 1);
        var first = (await store.GetLatestOutputRevisionAsync(id))!;
        first.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes("héllo\n"));
        await store.PublishReviewReadyAsync(id, generation, "tree-a", "héllo\n", 1);
        (await store.GetLatestOutputRevisionAsync(id))!.RevisionId.Should().Be(first.RevisionId);
        await FluentActions.Invoking(() => store.PublishReviewReadyAsync(id, generation, "tree-a", "different", 1))
            .Should().ThrowAsync<InvalidOperationException>();

        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE run_output_revisions SET tree_hash = 'tampered' WHERE revision_id = @id";
            command.Parameters.AddWithValue("id", first.RevisionId);
            await FluentActions.Invoking(() => command.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        }
        await store.TryTransitionReviewToInProgressAsync(id);
        await FluentActions.Invoking(() => store.PublishReviewReadyAsync(id, generation, "tree-old", "stale", 1))
            .Should().ThrowAsync<InvalidOperationException>();
        await store.PublishReviewReadyAsync(id, generation + 1, "tree-b", "new", 1);
        var second = (await store.GetLatestOutputRevisionAsync(id))!;
        second.PredecessorRevisionId.Should().Be(first.RevisionId);
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes("héllo\n"));
    }
}
