using System.Text;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
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

    [PostgresFact]
    public async Task CollectiveAssemblyWritesRejectSupersededLeaseToken()
    {
        var store = new EfRunStore(pg.Factory);
        var leases = new PostgresRunLeaseStore(pg.Factory);
        var id = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "fenced collective assembly",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var tree = RunOutputTree.Encode([
            new RunOutputTree.File("artifact.txt", 33188, [1, 2, 3]),
        ]);

        var firstClaim = await leases.TryClaimAsync(
            id.ToString(), "pod-a:assembly:first", TimeSpan.FromMinutes(1));
        firstClaim.Claimed.Should().BeTrue();
        var first = new RunLeaseClaim(
            "pod-a:assembly:first", firstClaim.FencingToken, generation);
        (await store.TryUpdateAssemblyArtifactsAsync(id, "tree-one", "first", first))
            .Should().BeTrue();
        var firstCandidate = await store.PublishCollectiveCandidateAsync(
            id, generation, "7", "tree-one", "first", tree, default, first);

        await leases.ReleaseAsync(id.ToString(), first.OwnerId, first.FencingToken);
        var secondClaim = await leases.TryClaimAsync(
            id.ToString(), "pod-b:assembly:second", TimeSpan.FromMinutes(1));
        secondClaim.Claimed.Should().BeTrue();
        secondClaim.FencingToken.Should().BeGreaterThan(first.FencingToken);
        var second = new RunLeaseClaim(
            "pod-b:assembly:second", secondClaim.FencingToken, generation);

        (await store.TryUpdateAssemblyArtifactsAsync(id, "stale-tree", "stale", first))
            .Should().BeFalse();
        var stalePublish = () => store.PublishCollectiveCandidateAsync(
            id, generation, "7", "tree-one", "first", tree, default, first);
        await stalePublish.Should().ThrowAsync<RunOutputRevisionUnavailableException>()
            .WithMessage("*stale_collective_candidate*");
        (await store.ApproveCollectiveCandidateAsync(
            id, generation, firstCandidate.RevisionId, default, first)).Should().BeFalse();
        (await store.TryMutateTerminalOutcomeAsync(
            id,
            new TerminalRunMutation(
                TerminalRunOutcome.Create(
                    RunStatus.Failed,
                    EventTypes.RunFailed,
                    new { reason = "stale" },
                    DateTimeOffset.UtcNow,
                    generation),
                "stale",
                RequiredLease: new RunLeaseFence(
                    first.OwnerId,
                    first.FencingToken,
                    first.LifecycleGeneration)))).Should().BeFalse();

        (await store.TryUpdateAssemblyArtifactsAsync(id, "tree-two", "second", second))
            .Should().BeTrue();
        var secondCandidate = await store.PublishCollectiveCandidateAsync(
            id, generation, "7", "tree-two", "second", tree, default, second);
        (await store.ApproveCollectiveCandidateAsync(
            id, generation, secondCandidate.RevisionId, default, second)).Should().BeTrue();
    }
}
