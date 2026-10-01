using System.Text;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RunOutputRevisionPostgresTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task FanProjectionRowLocks_SerializeSuppressionAndParentTerminal()
    {
        var store = new EfRunStore(pg.Factory);
        var parentId = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = parentId,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "parked fan parent",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        await store.UpdateStatusAsync(parentId, RunStatus.AwaitingReview, null);
        await using var db = await pg.Factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var spec = new OutcomeSpec
        {
            ProjectId = "fan-guard-test",
            CoordinatorRunId = RunId.New().ToString(),
            Goal = "Hold fan projection",
            DesiredOutcome = "Project exact artifacts",
            Scope = "Isolated parent",
            Assumptions = "Source verified",
            Status = "confirmed",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = spec.ProjectId,
            CoordinatorRunId = spec.CoordinatorRunId,
            ParentRunId = parentId.ToString(),
            ParentWorkflowNodeId = "fan",
            ParentResumeState = "waiting",
            Status = WorkPlanStatus.Complete,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkPlans.Add(plan);
        var pending = new PendingRequestRecord
        {
            RunId = parentId.ToString(),
            RequestJson = "{}",
            RequestId = "fan-request",
            OwnerUser = "test",
            DeliveryKind = PendingRequestDeliveryKinds.WorkflowChildWork,
            DecisionIdentity = "fan-decision",
            DeliveryState = PendingRequestDeliveryStates.Delivering,
            DeliveryClaimOwner = "fence-owner",
            DeliveryClaimedAt = now,
            CreatedAt = now,
        };
        db.PendingRequests.Add(pending);
        await db.SaveChangesAsync();

        await using var fence = await db.Database.BeginTransactionAsync();
        (await db.WorkPlans.Where(row => row.Id == plan.Id)
            .ExecuteUpdateAsync(updates => updates.SetProperty(row => row.UpdatedAt,
                row => row.UpdatedAt))).Should().Be(1);
        (await db.Runs.Where(row => row.RunId == parentId.ToString())
            .ExecuteUpdateAsync(updates => updates.SetProperty(row => row.TreeHash,
                row => row.TreeHash))).Should().Be(1);
        (await db.PendingRequests.Where(row => row.Id == pending.Id
                && row.DeliveryState == PendingRequestDeliveryStates.Delivering
                && row.DeliveryClaimOwner == "fence-owner"
                && row.DeliveryClaimedAt == now)
            .ExecuteUpdateAsync(updates => updates.SetProperty(row => row.DeliveryClaimOwner,
                row => row.DeliveryClaimOwner))).Should().Be(1);

        var suppressionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takeoverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suppression = Task.Run(async () =>
        {
            await using var contender = await pg.Factory.CreateDbContextAsync();
            suppressionStarted.SetResult();
            return await contender.WorkPlans.Where(row => row.Id == plan.Id)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(row => row.Status, WorkPlanStatus.Cancelled)
                    .SetProperty(row => row.ParentResumeState, "suppressed"));
        });
        var terminal = Task.Run(async () =>
        {
            terminalStarted.SetResult();
            return await store.TrySetTerminalOutcomeAsync(parentId,
                TerminalRunOutcome.Create(RunStatus.Failed, EventTypes.RunFailed,
                    new { reason = "cancelled" }, DateTimeOffset.UtcNow,
                    (await store.GetAsync(parentId))!.LifecycleGeneration), "cancelled");
        });
        var takeover = Task.Run(async () =>
        {
            await using var contender = await pg.Factory.CreateDbContextAsync();
            takeoverStarted.SetResult();
            return await contender.PendingRequests.Where(row => row.Id == pending.Id
                    && row.DeliveryState == PendingRequestDeliveryStates.Delivering)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(row => row.DeliveryClaimOwner, "takeover-owner")
                    .SetProperty(row => row.DeliveryClaimedAt, DateTimeOffset.UtcNow));
        });
        await Task.WhenAll(suppressionStarted.Task, terminalStarted.Task, takeoverStarted.Task);
        await Task.Delay(150);
        suppression.IsCompleted.Should().BeFalse("the projection owns the plan row");
        terminal.IsCompleted.Should().BeFalse("the projection owns the parent run row");
        takeover.IsCompleted.Should().BeFalse("the projection owns the pending delivery row through Git apply and receipt");

        await fence.CommitAsync();
        (await suppression).Should().Be(1);
        (await terminal).Should().BeTrue();
        (await takeover).Should().Be(1);
        (await db.WorkPlans.AsNoTracking().SingleAsync(row => row.Id == plan.Id))
            .ParentResumeState.Should().Be("suppressed");
    }

    [PostgresFact]
    public async Task FanChildRevision_AndTerminalWinner_AreLeaseFencedAndAtomic()
    {
        var store = new EfRunStore(pg.Factory);
        var parentId = RunId.New();
        var childId = RunId.New();
        foreach (var (id, parent) in new[]
        {
            (parentId, (string?)null), (childId, parentId.ToString()),
        })
        {
            await store.InsertAsync(new Run
            {
                Id = id,
                ParentRunId = parent,
                RepositoryPath = "repo",
                OriginatingBranch = "main",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "fan child",
                SubmittingUser = "test",
                Status = RunStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
            });
        }
        var generation = (await store.GetAsync(childId))!.LifecycleGeneration;
        var leases = new PostgresRunLeaseStore(pg.Factory);
        var claimed = await leases.TryClaimAsync(childId.ToString(), "fan-owner", TimeSpan.FromMinutes(1));
        claimed.Claimed.Should().BeTrue();
        var content = RunOutputTree.Encode([
            new RunOutputTree.File("demo/checklist.md", 33188, "retained bytes"u8.ToArray()),
        ]);
        var publication = new FanDeclaredFilesPublication("42",
            new string('a', 40), new string('b', 40), content);
        var mutation = new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.AssembleReady, EventTypes.RunAssembleReady,
                new { treeHash = publication.TreeHash }, DateTimeOffset.UtcNow, generation),
            null, TreeHash: publication.TreeHash, WorktreeBranch: "agentweaver/child",
            FanDeclaredFiles: publication,
            ExpectedParentLifecycleGeneration: (await store.GetAsync(parentId))!.LifecycleGeneration,
            RequiredLease: new RunLeaseFence("fan-owner", claimed.FencingToken, generation));

        (await store.TryMutateTerminalOutcomeAsync(childId,
            mutation with { RequiredLease = new RunLeaseFence("stale-owner", claimed.FencingToken, generation) }))
            .Should().BeFalse();
        (await store.GetLatestOutputRevisionAsync(childId)).Should().BeNull();
        (await store.TryMutateTerminalOutcomeAsync(childId, mutation)).Should().BeTrue();
        (await store.TryMutateTerminalOutcomeAsync(childId, mutation)).Should().BeFalse();
        var revision = await ((IRunStore)store).ResolveOutputRevisionAsync(childId,
            (await store.GetAsync(childId))!.CurrentOutputRevisionId!);
        revision.SchemaVersion.Should().Be(RunOutputRevision.FanDeclaredFilesSchemaVersion);
        revision.ResolveFile("demo/checklist.md").Bytes.Should().Equal("retained bytes"u8.ToArray());
        revision.DiffBytes.Should().BeEmpty();
        (await store.ListOutputRevisionsAsync(childId)).Should().ContainSingle();
        var parentBranch = "agentweaver/" + parentId;
        await store.UpdateWorktreeAsync(parentId, "parent-worktree", parentBranch);
        await store.UpdateStatusAsync(parentId, RunStatus.AwaitingReview, null);
        (await store.TryRecordFanInputProjectionAsync(parentId, 1,
            "base-tree", "projected-tree", parentBranch)).Should().BeTrue();
        (await store.TryRecordFanInputProjectionAsync(parentId, 1,
            "base-tree", "projected-tree", parentBranch, "reattached-parent-worktree")).Should().BeTrue();
        (await store.GetAsync(parentId))!.WorktreePath.Should().Be("reattached-parent-worktree");
        (await store.TryRecordFanInputProjectionAsync(parentId, 1,
            "base-tree", "different-tree", parentBranch)).Should().BeFalse();
        (await store.GetAsync(parentId))!.TreeHash.Should().Be("projected-tree");
    }

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

    [PostgresFact]
    public async Task DigestlessCollectiveCandidateAfterCorrections_HasResolvableFinalManifest()
    {
        var store = new EfRunStore(pg.Factory);
        var id = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "revise collective assembly",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var retained = new RunOutputTree.File("app.js", 33188, "retained"u8.ToArray());
        RunOutputRevision? prior = null;
        for (var round = 1; round <= 3; round++)
        {
            var treeHash = $"tree-{round}";
            var diff = $"revision-{round}";
            var tree = RunOutputTree.Encode([retained,
                new RunOutputTree.File($"round-{round}.txt", 33188, [(byte)round])]);
            await store.UpdateAssemblyArtifactsAsync(id, treeHash, diff);
            var candidate = await store.PublishCollectiveCandidateAsync(
                id, generation, "7", treeHash, diff, tree);
            candidate.PredecessorRevisionId.Should().Be(prior?.RevisionId);
            candidate.WorkflowDigest.Should().BeNull();
            candidate.ManifestIncomplete.Should().BeFalse();
            (await ((IRunStore)store).ResolveOutputRevisionAsync(id, candidate.RevisionId))
                .ResolveFile("app.js").Bytes.Should().Equal("retained"u8.ToArray());
            prior = candidate;
            if (round != 3) continue;

            (await store.ApproveCollectiveCandidateAsync(id, generation, candidate.RevisionId))
                .Should().BeTrue();
            (await store.TryMutateTerminalOutcomeAsync(id, new TerminalRunMutation(
                TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                    new { result = "assembly_complete" }, DateTimeOffset.UtcNow, generation),
                "assembly_complete", TreeHash: treeHash, MergedCommitHash: "commit-three",
                CollectiveOutput: new CollectiveOutputPublication(
                    "7", "effect-three", "commit-three", treeHash, false, tree),
                ApprovedCollectiveRevisionId: candidate.RevisionId))).Should().BeTrue();
            var final = (await store.GetLatestOutputRevisionAsync(id))!;
            final.ManifestIncomplete.Should().BeFalse();
            final.ResolveFile("app.js").Bytes.Should().Equal("retained"u8.ToArray());
        }
    }
}
