using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Backlog;
using Agentweaver.Tests.Helpers;
using Agentweaver.Tests.Casting;
using FluentAssertions;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using LibGit2Sharp;

namespace Agentweaver.Tests.Coordinator;

public sealed class ClaimedPrerequisiteResolverTests
{
    [Fact]
    public async Task Resolves_every_claim_to_retained_bytes_independent_of_branch_and_workspace()
    {
        var first = Revision("first", RunId.New());
        var second = Revision("second", RunId.New());
        var revisions = new[] { first, second }.ToDictionary(revision => revision.RunId);
        var resolved = new List<RunId>();
        var workspace = Path.Combine(Path.GetTempPath(), $"claimed-inputs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var mutableFile = Path.Combine(workspace, "output.txt");
        try
        {
            await File.WriteAllTextAsync(mutableFile, "first");

            var inputs = await ClaimedPrerequisiteResolver.ResolveAsync(
                JsonSerializer.Serialize(new[] { Claim(first), Claim(second) }),
                (runId, revisionId, _) =>
                {
                    resolved.Add(runId);
                    revisionId.Should().Be(revisions[runId].RevisionId);
                    return Task.FromResult(revisions[runId]);
                },
                CancellationToken.None);

            await File.WriteAllTextAsync(mutableFile, "changed after claim");
            (await File.ReadAllTextAsync(mutableFile)).Should().Be("changed after claim");
            Directory.Delete(workspace, recursive: true);
            Directory.Exists(workspace).Should().BeFalse();
            resolved.Should().Equal(first.RunId, second.RunId);
            inputs.Select(input => Encoding.UTF8.GetString(input.Files.Single().Bytes))
                .Should().Equal("first", "second");
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing_content")]
    [InlineData("corrupt_content")]
    [InlineData("expired_content")]
    public async Task Unavailable_retained_revision_propagates_explicit_reason(string reason)
    {
        var revision = Revision("original", RunId.New());
        var action = () => ClaimedPrerequisiteResolver.ResolveAsync(
            JsonSerializer.Serialize(new[] { Claim(revision) }),
            (_, _, _) => Task.FromException<RunOutputRevision>(
                new RunOutputRevisionUnavailableException(reason)),
            CancellationToken.None);
        (await action.Should().ThrowAsync<RunOutputRevisionUnavailableException>())
            .Which.Reason.Should().Be(reason);
    }

    [Fact]
    public async Task Changed_revision_generation_or_missing_pin_never_resolves_against_current_branch()
    {
        var revision = Revision("original", RunId.New());
        var calls = 0;
        Task<RunOutputRevision> Resolve(RunId runId, string revisionId, CancellationToken token)
        {
            calls++;
            return Task.FromResult(revision);
        }

        var mismatched = () => ClaimedPrerequisiteResolver.ResolveAsync(
            JsonSerializer.Serialize(new[] { Claim(revision) with { LifecycleGeneration = 2 } }),
            Resolve, CancellationToken.None);
        (await mismatched.Should().ThrowAsync<RunOutputRevisionUnavailableException>())
            .Which.Reason.Should().Be("claim_revision_mismatch");
        var unpinned = () => ClaimedPrerequisiteResolver.ResolveAsync(
            JsonSerializer.Serialize(new[] { Claim(revision) with { OutputRevisionId = null } }),
            Resolve, CancellationToken.None);
        (await unpinned.Should().ThrowAsync<RunOutputRevisionUnavailableException>())
            .Which.Reason.Should().Be("invalid_claimed_prerequisites");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Accepted_no_change_claim_cannot_resolve_an_integrated_output_revision()
    {
        var revision = Revision("original", RunId.New());
        var action = () => ClaimedPrerequisiteResolver.ResolveAsync(
            JsonSerializer.Serialize(new[] { Claim(revision) with { Outcome = "accepted_no_change" } }),
            (_, _, _) => Task.FromResult(revision), CancellationToken.None);

        (await action.Should().ThrowAsync<RunOutputRevisionUnavailableException>())
            .Which.Reason.Should().Be("claim_revision_mismatch");
    }

    [Fact]
    public void Retained_nonoverlapping_outputs_compose_in_claim_order_without_workspace_reads()
    {
        var source = RunOutputTree.Encode([new RunOutputTree.File("base.txt", 33188, "base"u8.ToArray())]);
        var first = ChangedRevision("a.txt", "first", RunId.New());
        var second = ChangedRevision("b.txt", "second", RunId.New());
        var claims = new[] { (Claim(first), first), (Claim(second), second) };
        var initial = ImmutableExecutionInputPlan.Compose("source-commit", "source-tree", source, claims);
        var repeated = ImmutableExecutionInputPlan.Compose("source-commit", "source-tree", source, claims);

        repeated.CompositeId.Should().Be(initial.CompositeId);
        initial.RevisionIds.Should().Equal(first.RevisionId, second.RevisionId);
        RunOutputTree.Decode(initial.TreeContent)
            .ToDictionary(file => file.Path, file => Encoding.UTF8.GetString(file.Bytes))
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["base.txt"] = "base", ["a.txt"] = "first", ["b.txt"] = "second",
            });
        ImmutableExecutionInputPlan.Compose("source-commit", "source-tree", source,
            [(Claim(second), second), (Claim(first), first)]).CompositeId.Should().NotBe(initial.CompositeId);
    }

    [Fact]
    public void Divergent_overlap_blocks_instead_of_choosing_a_revision()
    {
        var source = RunOutputTree.Encode(
            [new RunOutputTree.File("base.txt", 33188, "base"u8.ToArray())]);
        var first = ChangedRevision("shared.txt", "first", RunId.New());
        var second = ChangedRevision("shared.txt", "second", RunId.New());
        Action act = () => ImmutableExecutionInputPlan.Compose("source-commit", "source-tree", source,
            [(Claim(first), first), (Claim(second), second)]);

        act.Should().Throw<RunOutputRevisionUnavailableException>()
            .Which.Reason.Should().Be("divergent_prerequisite_overlap");
    }

    [Fact]
    public void Untouched_files_from_older_outputs_do_not_replace_the_pinned_source()
    {
        var revision = ChangedRevision("new.txt", "new", RunId.New());
        var source = RunOutputTree.Encode(
            [new RunOutputTree.File("base.txt", 33188, "different source"u8.ToArray())]);
        var plan = ImmutableExecutionInputPlan.Compose("source-commit", "source-tree", source,
            [(Claim(revision), revision)]);

        RunOutputTree.Decode(plan.TreeContent)
            .ToDictionary(file => file.Path, file => Encoding.UTF8.GetString(file.Bytes))
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["base.txt"] = "different source",
                ["new.txt"] = "new",
            });
    }

    [Fact]
    public void Unsupported_patch_blocks_even_when_tree_bytes_are_retained()
    {
        var revision = Revision("not a git patch", RunId.New());
        Action act = () => ImmutableExecutionInputPlan.Compose("source-commit", "source-tree",
            RunOutputTree.Encode([]), [(Claim(revision), revision)]);
        act.Should().Throw<RunOutputRevisionUnavailableException>()
            .Which.Reason.Should().Be("unsupported_revision_patch");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Absent_or_invalid_durable_claim_is_rejected(string? json)
    {
        var action = () => ClaimedPrerequisiteResolver.ResolveAsync(
            json, (_, _, _) => throw new Xunit.Sdk.XunitException("unexpected resolution"),
            CancellationToken.None);
        await action.Should().ThrowAsync<RunOutputRevisionUnavailableException>();
    }

    [Fact]
    public async Task Pickup_with_valid_claimed_revision_binds_and_materializes_exact_execution_input()
    {
        await using var factory = new CoordinatorWebApplicationFactory();
        using var owner = factory.CreateOwnerClient();
        var workingDirectory = factory.NewWorkingDirectory();
        var create = await owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Pinned inputs {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = workingDirectory,
        });
        create.EnsureSuccessStatusCode();
        var projectId = ProjectId.Parse((await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!);
        SquadTestFixtureHelper.CreateMinimalSquad(workingDirectory);
        Repository.Init(workingDirectory);
        using (var repository = new Repository(workingDirectory))
        {
            File.WriteAllText(Path.Combine(workingDirectory, "README.md"), "pinned source");
            Commands.Stage(repository, "README.md");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var commit = repository.Commit("source", signature, signature);
            if (!string.Equals(repository.Head.FriendlyName, "main", StringComparison.Ordinal))
            {
                var main = repository.Branches["main"] ?? repository.CreateBranch("main", commit);
                Commands.Checkout(repository, main);
            }
        }
        await factory.PrepareAiExecutionAsync(owner, "orchestration", projectId.ToString());

        var backlog = factory.Services.GetRequiredService<IBacklogTaskStore>();
        var runs = factory.Services.GetRequiredService<IRunStore>();
        var upstream = BacklogTestData.MakeReadyTask(projectId, "upstream");
        AiOperationCatalog.TryGet("orchestration", out var operation).Should().BeTrue();
        string providerKey;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<AiExecutionPlanService>();
            var plan = await plans.PrepareAsync(operation, projectId,
                new CallerContext { User = CoordinatorWebApplicationFactory.OwnerUser },
                CancellationToken.None);
            providerKey = plans.CreateQueuedProviderKey(plan);
        }
        var downstream = BacklogTestData.MakeReadyTask(projectId, "downstream") with
        {
            ReadyByUserId = CoordinatorWebApplicationFactory.OwnerUser,
            AiExecutionProviderKey = providerKey,
        };
        await backlog.InsertAsync(upstream);
        await backlog.InsertAsync(downstream);
        await backlog.EditDependenciesAsync(projectId, 0,
            new BacklogDependencyEdit(downstream.Id, [upstream.Id], []));
        var upstreamRun = BacklogTestData.MakeCoordinatorRun(projectId, RunId.New());
        (await backlog.TryClaimAndReserveCoordinatorRunWithPolicyAsync(
            projectId, upstream.Id, upstreamRun, DateTimeOffset.UtcNow))
            .Result.Should().Be(ClaimReserveResult.Won);
        await runs.PinDefaultExecutableWorkflowForTestAsync(upstreamRun.Id);
        await runs.UpdateAssemblyArtifactsAsync(
            upstreamRun.Id,
            "tree",
            "diff --git a/output.txt b/output.txt\n");
        var current = (await runs.GetAsync(upstreamRun.Id))!;
        (await runs.TryMutateTerminalOutcomeAsync(upstreamRun.Id,
            new TerminalRunMutation(
                TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                    new { result = "assembly_complete" }, DateTimeOffset.UtcNow,
                    current.LifecycleGeneration),
                "assembly_complete",
                MergedCommitHash: "accepted-commit",
                TreeHash: "tree",
                CollectiveOutput: new CollectiveOutputPublication("plan", "effect",
                    "accepted-commit", "tree", false,
                    RunOutputTree.Encode([
                        new RunOutputTree.File("README.md", 33188, Encoding.UTF8.GetBytes("pinned source")),
                        new RunOutputTree.File("output.txt", 33188, Encoding.UTF8.GetBytes("accepted bytes")),
                    ]))))).Should().BeTrue();

        var project = (await factory.Services.GetRequiredService<IProjectStore>().GetAsync(projectId))!;
        await factory.Services.GetRequiredService<CoordinatorPickupService>()
            .TryPickupAsync(project, downstream, CancellationToken.None);
        var claimed = (await backlog.GetAsync(projectId, downstream.Id))!;
        claimed.State.Should().Be(BacklogTaskState.Claimed);
        var downstreamRun = (await runs.GetAsync(claimed.RunId!.Value))!;
        downstreamRun.Status.Should().NotBe(RunStatus.Failed);
        downstreamRun.ExecutionInputRequired.Should().BeTrue();
        downstreamRun.ExecutionInputSourceCommitHash.Should().NotBeNullOrWhiteSpace();
        downstreamRun.ExecutionInputCommitHash.Should().NotBeNullOrWhiteSpace();
        downstreamRun.ExecutionInputCompositeId.Should().StartWith("sha256:");
        downstreamRun.ExecutionInputCommitHash.Should().NotBe(downstreamRun.ExecutionInputSourceCommitHash);
        using (var repository = new Repository(workingDirectory))
        {
            var execution = repository.Lookup<Commit>(downstreamRun.ExecutionInputCommitHash!);
            execution.Should().NotBeNull();
            var files = RunOutputTree.Decode(
                RunOutputTreeCapture.Capture(workingDirectory, execution!.Tree.Id.Sha));
            Encoding.UTF8.GetString(files.Single(file => file.Path == "README.md").Bytes)
                .Should().Be("pinned source");
            Encoding.UTF8.GetString(files.Single(file => file.Path == "output.txt").Bytes)
                .Should().Be("accepted bytes");
            repository.Head.FriendlyName.Should().Be("main");
        }
        claimed.ClaimedPrerequisitesJson.Should().Contain(
            (await runs.GetAsync(upstreamRun.Id))!.CurrentOutputRevisionId!);
    }

    private static BacklogClaimedPrerequisite Claim(RunOutputRevision revision) =>
        new("task", revision.RunId.ToString(), "integrated", revision.LifecycleGeneration,
            revision.MergedCommitHash, revision.TreeHash, revision.WorkflowDigest, revision.RevisionId);

    private static RunOutputRevision Revision(string contents, RunId runId)
    {
        var tree = RunOutputTree.Encode(
            [new RunOutputTree.File("output.txt", 33188, Encoding.UTF8.GetBytes(contents))]);
        var diff = RunOutputRevision.EncodeDiff(contents);
        return new RunOutputRevision(
            $"revision-{contents}", RunOutputRevision.CollectiveSchemaVersion,
            runId, 1, "sha256:workflow", false, "tree", RunOutputRevision.Sha256(diff),
            null, diff, DateTimeOffset.UtcNow, "collective", "commit", "plan", "effect",
            treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));
    }

    private static RunOutputRevision ChangedRevision(string path, string contents, RunId runId)
    {
        var tree = RunOutputTree.Encode(
            [new RunOutputTree.File("base.txt", 33188, "base"u8.ToArray()),
             new RunOutputTree.File(path, 33188, Encoding.UTF8.GetBytes(contents))]);
        var diff = RunOutputRevision.EncodeDiff($"diff --git a/{path} b/{path}\n");
        return new RunOutputRevision($"revision-{runId}", RunOutputRevision.CollectiveSchemaVersion,
            runId, 1, "sha256:workflow", false, "tree", RunOutputRevision.Sha256(diff),
            null, diff, DateTimeOffset.UtcNow, "collective", "commit", "plan", "effect",
            treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));
    }
}
