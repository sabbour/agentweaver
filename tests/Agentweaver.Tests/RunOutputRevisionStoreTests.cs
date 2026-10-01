using System.Text;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Git;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Api;

public sealed class RunOutputRevisionStoreTests
{
    [Fact]
    public async Task FanChildTerminal_AtomicallyRetainsOnlyDeclaredFiles_AndRejectsDuplicateWinner()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var parentId = await InsertAsync(store);
        var childId = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = childId,
            ParentRunId = parentId.ToString(),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "short reply",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var generation = (await store.GetAsync(childId))!.LifecycleGeneration;
        var content = RunOutputTree.Encode([
            new RunOutputTree.File("demo/incident-brief.md", 33188, "original artifact bytes"u8.ToArray()),
        ]);
        var publication = new FanDeclaredFilesPublication("42",
            new string('a', 40), new string('b', 40), content);
        var mutation = new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.AssembleReady, EventTypes.RunAssembleReady,
                new { treeHash = publication.TreeHash }, DateTimeOffset.UtcNow, generation),
            null, TreeHash: publication.TreeHash, WorktreeBranch: "agentweaver/child",
            Diff: "diff --git a/demo/incident-brief.md b/demo/incident-brief.md",
            FanDeclaredFiles: publication,
            ExpectedParentLifecycleGeneration: (await store.GetAsync(parentId))!.LifecycleGeneration);

        var lease = await new SqliteRunLeaseStore(db.Db).TryClaimAsync(
            childId.ToString(), "current-fan-owner", TimeSpan.FromMinutes(1));
        lease.Claimed.Should().BeTrue();
        (await store.TryMutateTerminalOutcomeAsync(childId, mutation with
        {
            RequiredLease = new RunLeaseFence("stale-fan-owner", lease.FencingToken, generation),
        })).Should().BeFalse();
        (await store.GetLatestOutputRevisionAsync(childId)).Should().BeNull();
        var owned = mutation with
        {
            RequiredLease = new RunLeaseFence("current-fan-owner", lease.FencingToken, generation),
        };
        (await store.TryMutateTerminalOutcomeAsync(childId, owned)).Should().BeTrue();
        (await store.TryMutateTerminalOutcomeAsync(childId, mutation)).Should().BeFalse();
        var child = (await store.GetAsync(childId))!;
        child.CurrentOutputRevisionId.Should().NotBeNull();
        var revision = await ((IRunStore)store).ResolveOutputRevisionAsync(
            childId, child.CurrentOutputRevisionId!);
        revision.SchemaVersion.Should().Be(RunOutputRevision.FanDeclaredFilesSchemaVersion);
        revision.OutputKind.Should().Be("fan_declared_files");
        revision.MergedCommitHash.Should().Be(publication.CommitHash);
        revision.DiffBytes.Should().BeEmpty();
        revision.ResolveFile("demo/incident-brief.md").Bytes.Should()
            .Equal("original artifact bytes"u8.ToArray());
        (await store.ListOutputRevisionsAsync(childId)).Should().ContainSingle();
        var parentBranch = "agentweaver/" + parentId;
        await store.UpdateWorktreeAsync(parentId, "parent-worktree", parentBranch);
        await store.UpdateStatusAsync(parentId, RunStatus.AwaitingReview, null);
        (await store.TryRecordFanInputProjectionAsync(parentId, 1,
            "base-tree", "projected-tree", parentBranch)).Should().BeTrue();
        (await store.TryRecordFanInputProjectionAsync(parentId, 1,
            "base-tree", "different-tree", parentBranch)).Should().BeFalse();
        (await store.GetAsync(parentId))!.TreeHash.Should().Be("projected-tree");
    }

    [Fact]
    public void FanDeclaredFileRevision_ProjectsOriginalBytesAfterChildBranchIsRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-fan-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            using var repository = new Repository(path);
            File.WriteAllText(Path.Combine(path, "base.txt"), "original base");
            Commands.Stage(repository, "base.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var baseline = repository.Commit("base", signature, signature);
            var manager = new WorktreeManager(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worktrees:BasePath"] = Path.Combine(path, "worktrees"),
                }).Build(), NullLogger<WorktreeManager>.Instance);
            var parentId = RunId.New();
            var childId = RunId.New();
            var parent = manager.AddWorktree(path, repository.Head.FriendlyName, parentId);
            var child = manager.AddWorktree(path, repository.Head.FriendlyName, childId);
            Directory.CreateDirectory(Path.Combine(child.WorktreePath, "demo"));
            var original = "p95 300ms to 2400ms; rollback at 09:12 UTC"u8.ToArray();
            File.WriteAllBytes(Path.Combine(child.WorktreePath, "demo", "incident-brief.md"), original);
            var childTree = manager.CommitChanges(child.WorktreePath, childId);
            var captured = RunOutputTreeCapture.CaptureDeclaredFiles(
                path, child.BranchName, childTree, ["demo/incident-brief.md"]);
            FluentActions.Invoking(() => RunOutputTreeCapture.CaptureDeclaredFiles(
                path, child.BranchName, childTree, ["../demo/incident-brief.md"]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*invalid_declared_path*");
            FluentActions.Invoking(() => RunOutputTreeCapture.CaptureDeclaredFiles(
                path, child.BranchName, childTree, ["demo/absent.md"]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*declared_file_missing*");
            var revision = new RunOutputRevision("fan-revision",
                RunOutputRevision.FanDeclaredFilesSchemaVersion, childId, 1, null, false,
                childTree, RunOutputRevision.Sha256([]), null, [],
                DateTimeOffset.UtcNow, "fan_declared_files", captured.CommitHash, "42",
                treeContent: captured.Files, treeContentSha256: RunOutputRevision.Sha256(captured.Files));
            FluentActions.Invoking(() => new RunOutputRevision("tampered",
                RunOutputRevision.FanDeclaredFilesSchemaVersion, childId, 1, null, false,
                childTree, RunOutputRevision.Sha256([]), null, [],
                DateTimeOffset.UtcNow, "fan_declared_files", captured.CommitHash, "42",
                treeContent: captured.Files, treeContentSha256: new string('0', 64)))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*corrupt_content*");

            File.WriteAllText(Path.Combine(child.WorktreePath, "demo", "incident-brief.md"), "moved child branch");
            manager.CommitChanges(child.WorktreePath, childId).Should().NotBe(childTree);
            manager.RemoveWorktree(path, child.WorktreePath, child.BranchName);
            revision.ResolveFile("demo/incident-brief.md").Bytes.Should().Equal(original);
            var prepared = manager.PrepareFanInputProjection(path, parent.WorktreePath,
                parentId, baseline.Sha, baseline.Tree.Sha, revision.ResolveFiles());
            FluentActions.Invoking(() => manager.PrepareFanInputProjection(
                path, parent.WorktreePath, parentId, baseline.Sha, baseline.Tree.Sha,
                [new RunOutputTree.File("base.txt", 33188, "overwrite"u8.ToArray())]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*fan_projection_path_collision*");
            FluentActions.Invoking(() => manager.PrepareFanInputProjection(
                path, parent.WorktreePath, parentId, baseline.Sha, baseline.Tree.Sha,
                [.. revision.ResolveFiles(), .. revision.ResolveFiles()]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*fan_projection_path_collision*");
            manager.ApplyFanInputProjection(parent.WorktreePath, parentId,
                baseline.Sha, prepared.CommitHash, prepared.TreeHash);
            manager.ApplyFanInputProjection(parent.WorktreePath, parentId,
                baseline.Sha, prepared.CommitHash, prepared.TreeHash);
            File.ReadAllBytes(Path.Combine(parent.WorktreePath, "demo", "incident-brief.md"))
                .Should().Equal(original);
            var composed = manager.AddWorktree(path, parent.BranchName, RunId.New());
            File.ReadAllBytes(Path.Combine(composed.WorktreePath, "demo", "incident-brief.md"))
                .Should().Equal(original);
            manager.RemoveWorktree(path, composed.WorktreePath, composed.BranchName);
            File.WriteAllText(Path.Combine(parent.WorktreePath, "unrelated.txt"), "new user edit");
            manager.CommitChanges(parent.WorktreePath, parentId);
            FluentActions.Invoking(() => manager.ApplyFanInputProjection(
                parent.WorktreePath, parentId, baseline.Sha, prepared.CommitHash, prepared.TreeHash))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*fan_projection_base_changed*");
            manager.RemoveWorktree(path, parent.WorktreePath, parent.BranchName);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void FanDeclaredCapture_RejectsSymlinkEvenWhenGitTreeContainsPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-fan-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            using var repository = new Repository(path);
            File.WriteAllText(Path.Combine(path, "base.txt"), "base");
            Commands.Stage(repository, "base.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var baseline = repository.Commit("base", signature, signature);
            var definition = TreeDefinition.From(baseline.Tree);
            var link = repository.ObjectDatabase.CreateBlob(
                new MemoryStream("base.txt"u8.ToArray()));
            definition.Add("demo/link.md", link, Mode.SymbolicLink);
            var oversized = repository.ObjectDatabase.CreateBlob(
                new MemoryStream(new byte[WorktreeManager.MaxContentBytes + 1]));
            definition.Add("demo/large.bin", oversized, Mode.NonExecutableFile);
            var tree = repository.ObjectDatabase.CreateTree(definition);
            var commit = repository.ObjectDatabase.CreateCommit(
                signature, signature, "linked", tree, [baseline], prettifyMessage: false);
            repository.Branches.Add("linked-source", commit);

            FluentActions.Invoking(() => RunOutputTreeCapture.CaptureDeclaredFiles(
                path, "linked-source", tree.Sha, ["demo/link.md"]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*declared_file_not_regular*");
            FluentActions.Invoking(() => RunOutputTreeCapture.CaptureDeclaredFiles(
                path, "linked-source", tree.Sha, ["demo/large.bin"]))
                .Should().Throw<RunOutputRevisionUnavailableException>()
                .WithMessage("*declared_file_too_large*");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void MaterializedCompositeUsesRetainedBytesAfterSourceBranchMoves()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "materialize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            using var repo = new Repository(path);
            File.WriteAllText(Path.Combine(path, "base.txt"), "pinned source");
            Commands.Stage(repo, "base.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var source = repo.Commit("source", signature, signature);
            var sourceBytes = RunOutputTreeCapture.Capture(path, source.Tree.Id.Sha);
            var output = new byte[] { 0, 255, 13, 10 };
            var tree = RunOutputTree.Encode([
                new RunOutputTree.File("base.txt", 33188, "pinned source"u8.ToArray()),
                new RunOutputTree.File("output.bin", 33188, output),
            ]);
            var diff = RunOutputRevision.EncodeDiff("diff --git a/output.bin b/output.bin\n");
            var runId = RunId.New();
            var revision = new RunOutputRevision("revision", RunOutputRevision.CollectiveSchemaVersion,
                runId, 1, "sha256:workflow", false, "retained-tree", RunOutputRevision.Sha256(diff),
                null, diff, DateTimeOffset.UtcNow, "collective", "merged", "plan", "effect",
                treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));
            var claim = new BacklogClaimedPrerequisite(
                "task", runId.ToString(), "integrated", 1, "merged", "retained-tree",
                "sha256:workflow", "revision");
            var plan = ImmutableExecutionInputPlan.Compose(
                source.Id.Sha, source.Tree.Id.Sha, sourceBytes, [(claim, revision)]);

            File.WriteAllText(Path.Combine(path, "base.txt"), "moved source");
            Commands.Stage(repo, "base.txt");
            repo.Commit("moved", signature, signature);
            var commit = RunOutputTreeCapture.Materialize(path, plan);
            RunOutputTreeCapture.Materialize(path, plan).Should().Be(commit);
            var files = RunOutputTree.Decode(RunOutputTreeCapture.Capture(
                path, repo.Lookup<Commit>(commit)!.Tree.Id.Sha));
            files.Single(file => file.Path == "base.txt").Bytes.Should().Equal("pinned source"u8.ToArray());
            files.Single(file => file.Path == "output.bin").Bytes.Should().Equal(output);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void WorktreeStartsFromMaterializedCommitWithoutChangingPublicationBranch()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-execution-input-" + Guid.NewGuid().ToString("N"));
        var worktrees = Path.Combine(path, "worktrees");
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            string sourceCommit;
            string movedCommit;
            using (var repository = new Repository(path))
            {
                File.WriteAllText(Path.Combine(path, "input.txt"), "retained");
                Commands.Stage(repository, "input.txt");
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
                sourceCommit = repository.Commit("source", signature, signature).Id.Sha;
                File.WriteAllText(Path.Combine(path, "input.txt"), "moved");
                Commands.Stage(repository, "input.txt");
                movedCommit = repository.Commit("moved", signature, signature).Id.Sha;
            }

            var manager = new WorktreeManager(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worktrees:BasePath"] = worktrees,
                }).Build(),
                NullLogger<WorktreeManager>.Instance);
            var runId = RunId.New();
            var worktree = manager.AddWorktree(path, sourceCommit, runId);
            using (var executionRepository = new Repository(worktree.WorktreePath))
            {
                executionRepository.Head.Tip.Id.Sha.Should().Be(sourceCommit);
                File.ReadAllText(Path.Combine(worktree.WorktreePath, "input.txt")).Should().Be("retained");
            }
            using (var publicationRepository = new Repository(path))
                publicationRepository.Head.Tip.Id.Sha.Should().Be(movedCommit);
            manager.RemoveWorktree(path, worktree.WorktreePath, worktree.BranchName);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecutionInputBindingIsDurableIdempotentAndConflictSafe()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store, executionInputRequired: true);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;

        (await store.TryBindExecutionInputAsync(
            id, generation, "source", "materialized", "sha256:composite")).Should().BeTrue();
        (await store.TryBindExecutionInputAsync(
            id, generation, "source", "materialized", "sha256:composite")).Should().BeTrue();
        (await store.TryBindExecutionInputAsync(
            id, generation, "other", "other", "sha256:other")).Should().BeFalse();

        var persisted = await store.GetAsync(id);
        persisted!.ExecutionInputRequired.Should().BeTrue();
        persisted.ExecutionInputSourceCommitHash.Should().Be("source");
        persisted.ExecutionInputCommitHash.Should().Be("materialized");
        persisted.ExecutionInputCompositeId.Should().Be("sha256:composite");
    }

    [Fact]
    public void NoChangeReceiptUsesTheExactExecutionInputInsteadOfThePublicationBranch()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-no-change-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            string executionCommit;
            string publicationCommit;
            using (var repository = new Repository(path))
            {
                File.WriteAllText(Path.Combine(path, "input.txt"), "executed");
                Commands.Stage(repository, "input.txt");
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
                executionCommit = repository.Commit("execution input", signature, signature).Id.Sha;
                File.WriteAllText(Path.Combine(path, "input.txt"), "publication moved");
                Commands.Stage(repository, "input.txt");
                publicationCommit = repository.Commit("publication moved", signature, signature).Id.Sha;
            }
            var run = new Run
            {
                Id = RunId.New(),
                RepositoryPath = path,
                OriginatingBranch = "master",
                ExecutionInputRequired = true,
                ExecutionInputSourceCommitHash = executionCommit,
                ExecutionInputCommitHash = executionCommit,
                ExecutionInputCompositeId = "sha256:input",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "no change",
                SubmittingUser = "test",
                Status = RunStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
            };

            var receipt = CoordinatorRunService.CaptureNoChangeReceipt(run);

            receipt.CommitHash.Should().Be(executionCommit);
            receipt.CommitHash.Should().NotBe(publicationCommit);
            Encoding.UTF8.GetString(
                RunOutputTree.Decode(receipt.TreeContent).Single(file => file.Path == "input.txt").Bytes)
                .Should().Be("executed");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task CollectiveCandidatesArePinnedBeforeReviewAndTerminalReusesApprovedAttempt()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var firstTree = RunOutputTree.Encode([new RunOutputTree.File("artifact.txt", 33188, [1, 0, 255])]);
        var secondTree = RunOutputTree.Encode([new RunOutputTree.File("artifact.txt", 33188, [2, 0, 254])]);

        await store.UpdateAssemblyArtifactsAsync(id, "tree-one", "first");
        var first = await store.PublishCollectiveCandidateAsync(id, generation, "7", "tree-one", "first", firstTree);
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().Be(first.RevisionId);
        first.ResolveFile("artifact.txt").Bytes.Should().Equal(1, 0, 255);
        (await store.PublishCollectiveCandidateAsync(id, generation, "7", "tree-one", "first", firstTree))
            .RevisionId.Should().Be(first.RevisionId);
        (await store.ApproveCollectiveCandidateAsync(id, generation, first.RevisionId)).Should().BeTrue();

        await store.UpdateAssemblyArtifactsAsync(id, "tree-two", "second");
        var second = await store.PublishCollectiveCandidateAsync(id, generation, "7", "tree-two", "second", secondTree);
        second.PredecessorRevisionId.Should().Be(first.RevisionId);
        second.RevisionId.Should().NotBe(first.RevisionId);
        (await store.GetAsync(id))!.ApprovedOutputRevisionId.Should().BeNull();
        (await store.ApproveCollectiveCandidateAsync(id, generation, first.RevisionId)).Should().BeFalse();
        var stale = new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                new { result = "assembly_complete" }, DateTimeOffset.UtcNow, generation),
            "assembly_complete", TreeHash: "tree-two", MergedCommitHash: "commit-two",
            CollectiveOutput: new CollectiveOutputPublication(
                "7", "effect-two", "commit-two", "tree-two", false, secondTree),
            ApprovedCollectiveRevisionId: first.RevisionId);
        (await store.TryMutateTerminalOutcomeAsync(id, stale)).Should().BeFalse();
        (await store.ApproveCollectiveCandidateAsync(id, generation, second.RevisionId)).Should().BeTrue();
        var approved = stale with { ApprovedCollectiveRevisionId = second.RevisionId };
        (await store.TryMutateTerminalOutcomeAsync(id, approved)).Should().BeTrue();
        var completed = (await store.GetAsync(id))!;
        completed.CurrentOutputRevisionId.Should().NotBe(second.RevisionId);
        completed.ApprovedOutputRevisionId.Should().Be(second.RevisionId);
        var final = (await store.GetOutputRevisionAsync(id, completed.CurrentOutputRevisionId!))!;
        final.SchemaVersion.Should().Be(RunOutputRevision.CollectiveSchemaVersion);
        final.PredecessorRevisionId.Should().Be(second.RevisionId);
        final.MergedCommitHash.Should().Be("commit-two");
        final.MergeEffectId.Should().Be("effect-two");
        final.ResolveFile("artifact.txt").Bytes.Should().Equal(2, 0, 254);
        (await store.ListOutputRevisionsAsync(id)).Select(r => r.RevisionId)
            .Should().Equal(final.RevisionId, second.RevisionId, first.RevisionId);
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.ResolveFile("artifact.txt")
            .Bytes.Should().Equal(1, 0, 255);
        (await store.TryMutateTerminalOutcomeAsync(id, approved)).Should().BeFalse();
    }

    [Fact]
    public async Task DigestlessCollectiveRevision_RetainsCompleteManifestAcrossRepeatedCorrectionsAndApproval()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var retained = new RunOutputTree.File("app.js", 33188, "original"u8.ToArray());

        for (var round = 1; round <= 3; round++)
        {
            var treeHash = $"tree-{round}";
            var diff = $"correction-{round}";
            var tree = RunOutputTree.Encode([retained,
                new RunOutputTree.File($"correction-{round}.txt", 33188, [(byte)round])]);
            await store.UpdateAssemblyArtifactsAsync(id, treeHash, diff);
            var candidate = await store.PublishCollectiveCandidateAsync(
                id, generation, "7", treeHash, diff, tree);
            candidate.WorkflowDigest.Should().BeNull();
            candidate.ManifestIncomplete.Should().BeFalse();
            (await ((IRunStore)store).ResolveOutputRevisionAsync(id, candidate.RevisionId))
                .ResolveFile("app.js").Bytes.Should().Equal("original"u8.ToArray());
            if (round == 3)
            {
                (await store.ApproveCollectiveCandidateAsync(id, generation, candidate.RevisionId))
                    .Should().BeTrue();
                var completed = await store.TryMutateTerminalOutcomeAsync(id, new TerminalRunMutation(
                    TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                        new { result = "assembly_complete" }, DateTimeOffset.UtcNow, generation),
                    "assembly_complete", TreeHash: treeHash, MergedCommitHash: "commit-three",
                    CollectiveOutput: new CollectiveOutputPublication(
                        "7", "effect-three", "commit-three", treeHash, false, tree),
                    ApprovedCollectiveRevisionId: candidate.RevisionId));
                completed.Should().BeTrue();
                var final = (await store.GetLatestOutputRevisionAsync(id))!;
                final.ManifestIncomplete.Should().BeFalse();
                final.ResolveFile("app.js").Bytes.Should().Equal("original"u8.ToArray());
            }

        }
    }

    [Fact]
    public void LegacyIncompleteCollectiveRevision_StillRejectsFileResolution()
    {
        var tree = RunOutputTree.Encode([new RunOutputTree.File("app.js", 33188, [1])]);
        var revision = new RunOutputRevision(
            "legacy", RunOutputRevision.CollectiveCandidateSchemaVersion, RunId.New(), 1,
            null, true, "tree", RunOutputRevision.Sha256([]), null, [], DateTimeOffset.UtcNow,
            outputKind: "collective", workPlanId: "7",
            treeContent: tree, treeContentSha256: RunOutputRevision.Sha256(tree));

        FluentActions.Invoking(() => revision.ResolveFiles())
            .Should().Throw<RunOutputRevisionUnavailableException>()
            .WithMessage("incomplete_manifest");
        FluentActions.Invoking(() => new RunOutputRevision(
                "corrupt", RunOutputRevision.CollectiveCandidateSchemaVersion, RunId.New(), 1,
                null, false, "tree", RunOutputRevision.Sha256([]), null, [], DateTimeOffset.UtcNow,
                outputKind: "collective", workPlanId: "7",
                treeContent: tree, treeContentSha256: RunOutputRevision.Sha256([0])))
            .Should().Throw<RunOutputRevisionUnavailableException>()
            .WithMessage("corrupt_content");
    }

    [Fact]
    public async Task RequiredExecutableWorkflowPin_CannotPublishDigestlessCollectiveCandidate()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store, workflowPinRequired: true);
        await store.UpdateAssemblyArtifactsAsync(id, "tree", "diff");
        var tree = RunOutputTree.Encode([new RunOutputTree.File("app.js", 33188, [1])]);

        await FluentActions.Invoking(() => store.PublishCollectiveCandidateAsync(
                id, 1, "7", "tree", "diff", tree))
            .Should().ThrowAsync<RunOutputRevisionUnavailableException>()
            .WithMessage("invalid_collective_manifest");
    }

    [Fact]
    public async Task CollectiveAssemblyWritesRejectSupersededLeaseToken()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var leases = new SqliteRunLeaseStore(db.Db);
        var id = await InsertAsync(store);
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var tree = RunOutputTree.Encode([new RunOutputTree.File("artifact.txt", 33188, [1, 2, 3])]);

        var firstClaim = await leases.TryClaimAsync(id.ToString(), "pod-a:assembly:first", TimeSpan.FromMinutes(1));
        firstClaim.Claimed.Should().BeTrue();
        var first = new RunLeaseClaim("pod-a:assembly:first", firstClaim.FencingToken, generation);
        (await store.TryUpdateAssemblyArtifactsAsync(id, "tree-one", "first", first)).Should().BeTrue();
        var firstCandidate = await store.PublishCollectiveCandidateAsync(
            id, generation, "7", "tree-one", "first", tree, default, first);

        await leases.ReleaseAsync(id.ToString(), first.OwnerId, first.FencingToken);
        var secondClaim = await leases.TryClaimAsync(id.ToString(), "pod-b:assembly:second", TimeSpan.FromMinutes(1));
        secondClaim.Claimed.Should().BeTrue();
        secondClaim.FencingToken.Should().BeGreaterThan(first.FencingToken);
        var second = new RunLeaseClaim("pod-b:assembly:second", secondClaim.FencingToken, generation);

        (await store.TryUpdateAssemblyArtifactsAsync(id, "stale-tree", "stale", first)).Should().BeFalse();
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
                    RunStatus.Failed, EventTypes.RunFailed, new { reason = "stale" },
                    DateTimeOffset.UtcNow, generation),
                "stale",
                RequiredLease: new RunLeaseFence(
                    first.OwnerId, first.FencingToken, first.LifecycleGeneration)))).Should().BeFalse();

        (await store.TryUpdateAssemblyArtifactsAsync(id, "tree-two", "second", second)).Should().BeTrue();
        var secondCandidate = await store.PublishCollectiveCandidateAsync(
            id, generation, "7", "tree-two", "second", tree, default, second);
        (await store.ApproveCollectiveCandidateAsync(
            id, generation, secondCandidate.RevisionId, default, second)).Should().BeTrue();
    }

    [Fact]
    public async Task NoChangeReceiptIsAtomicGenerationFencedAndRetainsExactFiles()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        var files = RunOutputTree.Encode([new RunOutputTree.File("source.txt", 33188, [1, 0, 255])]);
        TerminalRunMutation Confirm(int expected) => new(
            TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                new { result = "confirmed" }, DateTimeOffset.UtcNow, expected),
            "confirmed",
            NoChangeOutput: new NoChangeOutputPublication("commit-before-move", "tree-before-move", files));

        (await store.TryMutateTerminalOutcomeAsync(id, Confirm(generation + 1))).Should().BeFalse();
        (await store.GetLatestOutputRevisionAsync(id)).Should().BeNull();
        (await store.TryMutateTerminalOutcomeAsync(id, Confirm(generation))).Should().BeTrue();
        var receipt = (await store.GetLatestOutputRevisionAsync(id))!;
        receipt.SchemaVersion.Should().Be(RunOutputRevision.NoChangeSchemaVersion);
        receipt.OutputKind.Should().Be("no_change");
        receipt.AcceptedNoChange.Should().BeTrue();
        receipt.DiffBytes.Should().BeEmpty();
        receipt.WorkflowDigest.Should().NotBeNullOrEmpty();
        receipt.ResolveFile("source.txt").Bytes.Should().Equal(1, 0, 255);
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().Be(receipt.RevisionId);
        (await store.TryMutateTerminalOutcomeAsync(id, Confirm(generation))).Should().BeFalse();
        (await store.ListOutputRevisionsAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public void UnsupportedSchemaAndMissingNoChangeContentAreTypedUnavailable()
    {
        var id = RunId.New();
        Action unsupported = () => new RunOutputRevision(
            "revision", 99, id, 0, "pin", false, "tree",
            RunOutputRevision.Sha256([]), null, [], DateTimeOffset.UtcNow);
        unsupported.Should().Throw<RunOutputRevisionUnavailableException>()
            .Which.Reason.Should().Be("unsupported_schema");
        Action missing = () => new RunOutputRevision(
            "revision", RunOutputRevision.NoChangeSchemaVersion, id, 0, "pin", false, "tree",
            RunOutputRevision.Sha256([]), null, [], DateTimeOffset.UtcNow,
            outputKind: "no_change", mergedCommitHash: "commit", acceptedNoChange: true);
        missing.Should().Throw<RunOutputRevisionUnavailableException>()
            .Which.Reason.Should().Be("invalid_manifest");
    }

    [Fact]
    public async Task CapturedTreeRetainsBinaryBytesAfterBranchMoveAndWorkspaceRemoval()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            var first = new byte[] { 0, 255, 13, 10, 65 };
            File.WriteAllBytes(Path.Combine(path, "binary.dat"), first);
            using (var repository = new Repository(path))
            {
                Commands.Stage(repository, "binary.dat");
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
                var commit = repository.Commit("first", signature, signature);
                var retained = RunOutputTreeCapture.Capture(path, commit.Tree.Id.Sha);
                await using var db = await TestSqliteDb.CreateAsync();
                var store = new SqliteRunStore(db.Db);
                var id = await InsertAsync(store);
                await store.PinDefaultExecutableWorkflowForTestAsync(id);
                await store.UpdateAssemblyArtifactsAsync(id, commit.Tree.Id.Sha, "binary patch");
                var generation = (await store.GetAsync(id))!.LifecycleGeneration;
                (await store.TryMutateTerminalOutcomeAsync(id, new TerminalRunMutation(
                    TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                        new { result = "assembly_complete" }, DateTimeOffset.UtcNow, generation),
                    "assembly_complete", MergedCommitHash: commit.Id.Sha, TreeHash: commit.Tree.Id.Sha,
                    CollectiveOutput: new CollectiveOutputPublication(
                        "1", "effect", commit.Id.Sha, commit.Tree.Id.Sha, false, retained)))).Should().BeTrue();
                var revision = (await store.GetLatestOutputRevisionAsync(id))!;
                File.WriteAllText(Path.Combine(path, "binary.dat"), "moved");
                Commands.Stage(repository, "binary.dat");
                repository.Commit("second", signature, signature);
                File.Delete(Path.Combine(path, "binary.dat"));
                (await ((IRunStore)store).ResolveOutputRevisionAsync(id, revision.RevisionId))
                    .ResolveFile("binary.dat").Bytes.Should().Equal(first);
                var corrupt = revision.TreeContent!;
                corrupt[^1] ^= 1;
                revision.ResolveFile("binary.dat").Bytes.Should().Equal(first);
                Action invalid = () => new RunOutputRevision(revision.RevisionId, revision.SchemaVersion,
                    id, revision.LifecycleGeneration, revision.WorkflowDigest, revision.ManifestIncomplete,
                    revision.TreeHash, revision.DiffSha256, revision.PredecessorRevisionId, revision.DiffBytes,
                    revision.CreatedAt, revision.OutputKind, revision.MergedCommitHash, revision.WorkPlanId,
                    revision.MergeEffectId, revision.AcceptedNoChange, corrupt, revision.TreeContentSha256);
                invalid.Should().Throw<RunOutputRevisionUnavailableException>()
                    .Which.Reason.Should().Be("corrupt_content");
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task CollectivePublicationIsAtomicGenerationFencedAndRetainsOlderBytes()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        await store.UpdateAssemblyArtifactsAsync(id, "tree-a", "first-diff");
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        TerminalRunMutation Publish(int expected, string commit, string tree) => new(
            TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                new { result = "assembly_complete" }, DateTimeOffset.UtcNow, expected),
            "assembly_complete", MergedCommitHash: commit, TreeHash: tree,
            CollectiveOutput: new CollectiveOutputPublication("7", "effect-7", commit, tree, false,
                RunOutputTree.Encode([new RunOutputTree.File("artifact.txt", 33188, Encoding.UTF8.GetBytes(tree))])));

        (await store.TryMutateTerminalOutcomeAsync(id, Publish(generation, "commit-a", "wrong-tree")))
            .Should().BeFalse();
        (await store.GetAsync(id))!.Status.Should().Be(RunStatus.InProgress);
        (await store.GetLatestOutputRevisionAsync(id)).Should().BeNull();

        (await store.TryMutateTerminalOutcomeAsync(id, Publish(generation, "commit-a", "tree-a")))
            .Should().BeTrue();
        var first = (await store.GetLatestOutputRevisionAsync(id))!;
        first.SchemaVersion.Should().Be(RunOutputRevision.CollectiveSchemaVersion);
        first.OutputKind.Should().Be("collective");
        first.WorkPlanId.Should().Be("7");
        first.MergeEffectId.Should().Be("effect-7");
        first.DiffBytes.Should().Equal(Encoding.UTF8.GetBytes("first-diff"));
        (await store.GetAsync(id))!.CurrentOutputRevisionId.Should().Be(first.RevisionId);
        (await store.TryMutateTerminalOutcomeAsync(id, Publish(generation, "commit-a", "tree-a")))
            .Should().BeFalse();

        await store.UpdateStatusAsync(id, RunStatus.InProgress, null);
        await store.UpdateAssemblyArtifactsAsync(id, "tree-b", "second-diff");
        var next = (await store.GetAsync(id))!.LifecycleGeneration;
        (await store.TryMutateTerminalOutcomeAsync(id, Publish(generation, "stale", "tree-b")))
            .Should().BeFalse();
        (await store.TryMutateTerminalOutcomeAsync(id, Publish(next, "commit-b", "tree-b")))
            .Should().BeTrue();
        var second = (await store.GetLatestOutputRevisionAsync(id))!;
        second.PredecessorRevisionId.Should().Be(first.RevisionId);
        (await store.GetOutputRevisionAsync(id, first.RevisionId))!.DiffBytes
            .Should().Equal(Encoding.UTF8.GetBytes("first-diff"));
    }

    [Fact]
    public async Task MissingAndCorruptCollectiveTreeFailTypedWithoutPublishingAnEmptySuccess()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(db.Db);
        var id = await InsertAsync(store);
        await store.PinDefaultExecutableWorkflowForTestAsync(id);
        await store.UpdateAssemblyArtifactsAsync(id, "tree-a", "diff");
        var generation = (await store.GetAsync(id))!.LifecycleGeneration;
        TerminalRunMutation Publish(byte[]? treeContent) => new(
            TerminalRunOutcome.Create(RunStatus.Completed, EventTypes.RunCompleted,
                new { result = "assembly_complete" }, DateTimeOffset.UtcNow, generation),
            "assembly_complete", TreeHash: "tree-a", MergedCommitHash: "commit-a",
            CollectiveOutput: new CollectiveOutputPublication(
                "1", "effect-a", "commit-a", "tree-a", false, treeContent));
        await FluentActions.Invoking(() => store.TryMutateTerminalOutcomeAsync(id, Publish(null)))
            .Should().ThrowAsync<RunOutputRevisionUnavailableException>()
            .WithMessage("missing_content");
        (await store.GetAsync(id))!.Status.Should().Be(RunStatus.InProgress);
        (await store.GetLatestOutputRevisionAsync(id)).Should().BeNull();

        (await store.TryMutateTerminalOutcomeAsync(id, Publish(
            RunOutputTree.Encode([new RunOutputTree.File("file.txt", 33188, [1, 2, 3])]))))
            .Should().BeTrue();
        var revision = (await store.GetLatestOutputRevisionAsync(id))!;
        await using (var conn = await db.Db.OpenConnectionAsync())
        {
            await using var command = conn.CreateCommand();
            command.CommandText = """
                DROP TRIGGER trg_run_output_revisions_no_update;
                UPDATE run_output_revisions SET tree_content = x'00'
                 WHERE revision_id = $revision;
                """;
            command.Parameters.AddWithValue("$revision", revision.RevisionId);
            await command.ExecuteNonQueryAsync();
        }
        await FluentActions.Invoking(() => ((IRunStore)store).ResolveOutputRevisionAsync(id, revision.RevisionId))
            .Should().ThrowAsync<RunOutputRevisionUnavailableException>()
            .WithMessage("corrupt_content");
    }

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

    private static async Task<RunId> InsertAsync(
        SqliteRunStore store,
        bool executionInputRequired = false,
        bool workflowPinRequired = false)
    {
        var id = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ExecutionInputRequired = executionInputRequired,
            ExecutableWorkflowPinRequired = workflowPinRequired,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "output revision",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow
        });
        return id;
    }
}
