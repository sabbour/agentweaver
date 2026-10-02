using FluentAssertions;
using System.Net.Http.Json;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using DomainRun = Agentweaver.Domain.Run;
using DomainRunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Tests.Graph;

/// <summary>
/// Golden parity test for wf-maf-binding (Feature 010): the live full run pipeline is now assembled by
/// ITERATING the default <c>WorkflowDefinition</c>'s edges (see <c>RunWorkflowGraphBinder</c>) instead of
/// the previous hand-coded <c>GraphDescriptorBuilder</c> chain. This test freezes the structure of the
/// hand-coded graph as a GOLDEN BASELINE and asserts the definition-driven graph reproduces it exactly:
/// same start node, same visible node set (id + label + role + kind + node_type), and same collapsed
/// edge set (from, to, cardinality, loopback). Because predicate lambdas are not directly comparable,
/// the assertion is on everything the <see cref="GraphDescriptor"/> exposes — which, together with the
/// existing <c>CoordinatorWorkflowGraphDescriptorTests</c> and <c>CoordinatorWorkflowGraphDriftGuard</c>
/// reflection test, pins the wiring.
///
/// The class name carries "Coordinator" so it is included by the coordinator-filtered test run.
/// </summary>
public sealed class CoordinatorRunWorkflowDefinitionBindingTests
    : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public CoordinatorRunWorkflowDefinitionBindingTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunWorkflowFactory Factory =>
        _factory.Services.GetRequiredService<RunWorkflowFactory>();

    // ── Golden baseline: the hand-coded full pipeline, frozen ───────────────────────────────────
    // Every field the GraphDescriptor exposes for a visible node.
    private sealed record NodeShape(string Id, string Label, string Role, string Kind, string NodeType);

    // Every field the GraphDescriptor exposes for a collapsed edge.
    private sealed record EdgeShape(string From, string To, string Cardinality, bool Loopback);

    private static readonly NodeShape[] GoldenNodes =
    {
        new("agent",  "Agent",        "agent",  "live", "agent"),
        new("rai",    "Rai",          "rai",    "live", "agent"),
        new("review", "Human Review", "review", "live", "gate"),
        new("merge",  "Merge",        "merge",  "live", "action"),
        new("push-pr","Push PR",      "action", "live", "action"),
        new("scribe", "Scribe",       "scribe", "live", "agent"),
    };

    private static readonly EdgeShape[] GoldenEdges =
    {
        new("agent",  "rai",    "direct", false),
        new("rai",    "scribe", "fanout", false),
        new("rai",    "review", "fanout", false),
        new("rai",    "agent",  "direct", true),   // RAI revise loop
        new("review", "merge",  "direct", false),
        new("review", "agent",  "direct", true),   // review request-changes loop
        new("merge",  "push-pr","direct", false),
        new("push-pr","scribe", "fanin",  false),
        new("merge",  "review", "direct", true),   // merge-blocked re-enter review loop
    };

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_MatchesGoldenNodeSet()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        d.Variant.Should().Be("full");
        d.StartNodeId.Should().Be("agent");

        var actual = d.Nodes
            .Select(n => new NodeShape(n.Id, n.Label, n.Role, n.Kind, n.NodeType))
            .ToHashSet();

        actual.Should().BeEquivalentTo(GoldenNodes);
    }

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_MatchesGoldenEdgeSet()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        var actual = d.Edges
            .Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback))
            .ToHashSet();

        actual.Should().BeEquivalentTo(GoldenEdges);
    }

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_IsDeterministicAcrossBuilds()
    {
        // Building twice must yield the identical structural projection — the binder is side-effect free
        // and order-stable, so the per-run descriptor never drifts between builds.
        var first = Project(Factory.GetGraphDescriptor(isChild: false));
        var second = Project(Factory.GetGraphDescriptor(isChild: false));

        second.Nodes.Should().BeEquivalentTo(first.Nodes);
        second.Edges.Should().BeEquivalentTo(first.Edges);
        second.StartNodeId.Should().Be(first.StartNodeId);
    }

    [Fact]
    public void ChildVariant_RemainsTrimmedAgentAssembleReady_ForStage2Parity()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        d.Variant.Should().Be("child");
        // FIX 2: child graph gains the graph-native failure->terminal (child-turn-failed) alongside
        // assemble-ready; agent now fans out to the two terminals on the TerminalFailureReason condition.
        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["agent", "assemble-ready", "child-turn-failed"]);
        d.Edges.Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback)).ToHashSet()
            .Should().BeEquivalentTo(
            [
                new EdgeShape("agent", "assemble-ready", "fanout", false),
                new EdgeShape("agent", "child-turn-failed", "fanout", false),
            ]);
    }

    [Fact]
    public void FullVariant_StaticFanDefinition_UsesProductionFanExecutors()
    {
        var definition = new WorkflowDefinition
        {
            Id = "static-fan",
            Name = "Static fan",
            Version = "1",
            Start = "fan",
            Nodes =
            [
                new WorkflowNode { Id = "fan", Type = WorkflowNodeType.FanOut, Label = "Parallel work" },
                new WorkflowNode
                {
                    Id = "branch-a",
                    Type = WorkflowNodeType.Prompt,
                    Label = "First branch",
                    Prompt = "Produce the first result.",
                },
                new WorkflowNode
                {
                    Id = "branch-b",
                    Type = WorkflowNodeType.Prompt,
                    Label = "Second branch",
                    Prompt = "Produce the second result.",
                },
                new WorkflowNode
                {
                    Id = "join",
                    Type = WorkflowNodeType.FanIn,
                    Label = "Join",
                    Target = "fan",
                },
                new WorkflowNode { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done" },
            ],
            Edges =
            [
                new WorkflowEdge { From = "fan", To = "branch-a" },
                new WorkflowEdge { From = "fan", To = "branch-b" },
                new WorkflowEdge { From = "branch-a", To = "join" },
                new WorkflowEdge { From = "branch-b", To = "join" },
                new WorkflowEdge { From = "join", To = "done" },
            ],
        };

        var (_, descriptor) = Factory.BuildWorkflowForTest(isChild: false, definition);

        descriptor.StartNodeId.Should().Be("fan");
        descriptor.Nodes.Select(node => node.Id).Should().Contain(["fan", "join"]);
        descriptor.Nodes.Select(node => node.Id).Should().NotContain(["branch-a", "branch-b"]);
        descriptor.Edges.Should().Contain(edge => edge.From == "fan" && edge.To == "join");
    }

    [Fact]
    public async Task StartAsync_FanFiles_VerifyAndComposedTerminal_UsesOriginalRetainedBytes()
    {
        using var baseFactory = new WorkflowWebApplicationFactory();
        using var testFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubCopilotCapabilityCredentialProvider>();
                services.AddSingleton<IGitHubCopilotCapabilityCredentialProvider>(
                    new FixedGitHubCopilotCapabilityCredentialProvider());
            }));
        var services = testFactory.Services;
        var repositoryPath = Path.Combine(Path.GetTempPath(), "fan-composed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".agentweaver", "workflows"));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, ".agentweaver", "workflows",
            "fan-composed.yaml"), FanComposedWorkflowYaml());
        Repository.Init(repositoryPath);
        using var repository = new Repository(repositoryPath);
        Commands.Stage(repository, "*");
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var baseline = repository.Commit("Initial commit", signature, signature);
        if (repository.Head.FriendlyName != "main")
            repository.Branches.Rename(repository.Head, "main");
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Fan composed byte handoff",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = repositoryPath,
            DefaultBranch = "main",
            Owner = CoordinatorWebApplicationFactory.OwnerUser,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DefaultWorkflowId = "fan-composed",
        };
        await services.GetRequiredService<IProjectStore>().InsertAsync(project);
        var manager = services.GetRequiredService<WorktreeManager>();
        var parentId = RunId.New();
        var worktree = manager.AddWorktree(repositoryPath, "main", parentId);
        var parent = new DomainRun
        {
            Id = parentId,
            RepositoryPath = repositoryPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Produce a synthetic incident summary",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            WorktreePath = worktree.WorktreePath,
            WorktreeBranch = worktree.BranchName,
        };
        var store = services.GetRequiredService<IRunStore>();
        await store.InsertAsync(parent);
        await SeedComposedRootAsync(services, store, parent);
        var sourceText = new[] { "p95 latency 2400 ms", "rollback at 09:12 UTC" };
        baseFactory.TestAgentRunner.ExecuteOverride = (task, workingDirectory) =>
        {
            if (task.Contains("Verify both source files", StringComparison.Ordinal))
            {
                File.ReadAllText(Path.Combine(workingDirectory, "demo", "incident-brief.md"))
                    .Should().Be(sourceText[0]);
                File.ReadAllText(Path.Combine(workingDirectory, "demo", "response-checklist.md"))
                    .Should().Be(sourceText[1]);
                return "Verified both original files.";
            }
            var ordinal = task.Contains("Write only demo/incident-brief.md", StringComparison.Ordinal)
                ? 0 : task.Contains("Write only demo/response-checklist.md", StringComparison.Ordinal)
                    ? 1 : throw new InvalidOperationException("Unexpected producing turn in fan-composed test.");
            Directory.CreateDirectory(Path.Combine(workingDirectory, "demo"));
            File.WriteAllText(Path.Combine(workingDirectory, "demo",
                ordinal == 0 ? "incident-brief.md" : "response-checklist.md"), sourceText[ordinal]);
            return "File written.";
        };
        var input = new AgentTurnInput(parentId.ToString(), parent.Task,
            worktree.WorktreePath, worktree.BranchName, repositoryPath, "main",
            "github-copilot", null, parent.SubmittingUser,
            ProjectId: project.Id.ToString());
        var started = await services.GetRequiredService<RunWorkflowFactory>()
            .StartAsync(input, parentId.ToString(), CancellationToken.None);
        var parentClaim = await services.GetRequiredService<IRunLeaseStore>()
            .TryClaimAsync(parentId.ToString(), "fan-composed-graph-producer", TimeSpan.FromMinutes(5));
        parentClaim.Claimed.Should().BeTrue();
        WorkflowComposedCompletedOutput? terminal = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await foreach (var evt in started.WatchStreamAsync(timeout.Token))
        {
            if (evt is RequestInfoEvent request
                && request.Request.TryGetDataAs<WorkflowChildWorkPauseRequest>(out var pause)
                && pause.ParentJoinNodeId == "join-documents")
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var branches = await db.Subtasks.Where(s => s.WorkPlanId == pause.WorkPlanId)
                    .OrderBy(s => s.WorkflowBranchOrdinal).ToListAsync(timeout.Token);
                await store.UpdateStatusAsync(parentId, DomainRunStatus.AwaitingReview, null, timeout.Token);
                await store.UpdateStatusAsync(RunId.Parse(pause.ChildCoordinatorRunId),
                    DomainRunStatus.InProgress, null, timeout.Token);
                foreach (var (subtask, ordinal) in branches.Select((s, index) => (s, index)))
                {
                    var childId = RunId.New();
                    var childWorktree = manager.AddWorktree(repositoryPath, "main", childId);
                    subtask.ChildRunId = childId.ToString();
                    subtask.Status = SubtaskStatus.Running;
                    await db.SaveChangesAsync(timeout.Token);
                    await store.InsertAsync(new DomainRun
                    {
                        Id = childId,
                        RepositoryPath = repositoryPath,
                        OriginatingBranch = "main",
                        ModelSource = ModelSource.GitHubCopilot,
                        Task = subtask.Scope,
                        SubmittingUser = parent.SubmittingUser,
                        Status = DomainRunStatus.InProgress,
                        StartedAt = DateTimeOffset.UtcNow,
                        ProjectId = project.Id,
                        ParentRunId = pause.ChildCoordinatorRunId,
                        SubtaskId = subtask.Id.ToString(),
                        WorktreeBranch = childWorktree.BranchName,
                        WorktreePath = childWorktree.WorktreePath,
                    });
                    var childInput = new AgentTurnInput(childId.ToString(), subtask.Scope,
                        childWorktree.WorktreePath, childWorktree.BranchName,
                        repositoryPath, "main", "github-copilot", null,
                        parent.SubmittingUser, ProjectId: project.Id.ToString());
                    var execution = await services.GetRequiredService<RunWorkflowFactory>()
                        .StartAsync(childInput, childId.ToString(), timeout.Token, isChild: true);
                    var childStream = services.GetRequiredService<RunStreamStore>()
                        .Create(childId.ToString(), parent.SubmittingUser);
                    var leases = services.GetRequiredService<IRunLeaseStore>();
                    var claim = await leases.TryClaimAsync(childId.ToString(),
                        "fan-test-owner", TimeSpan.FromMinutes(1), timeout.Token);
                    claim.Claimed.Should().BeTrue();
                    var terminalized = false;
                    await foreach (var childEvent in execution.WatchStreamAsync(timeout.Token))
                    {
                        if (childEvent is WorkflowOutputEvent childOutput
                            && childOutput.Is<AssembleReadyOutput>())
                        {
                            terminalized = await services.GetRequiredService<RunWatchLoopService>()
                                .HandleTerminalOutputAsync(childId.ToString(), childOutput, childStream,
                                    timeout.Token, new RunLeaseClaim("fan-test-owner",
                                        claim.FencingToken, 1));
                            break;
                        }
                    }
                    terminalized.Should().BeTrue();
                    (await store.GetAsync(childId, timeout.Token))!.CurrentOutputRevisionId
                        .Should().NotBeNull();
                    subtask.Status = SubtaskStatus.Completed;
                    manager.RemoveWorktree(repositoryPath, childWorktree.WorktreePath,
                        childWorktree.BranchName);
                }
                var plan = await db.WorkPlans.SingleAsync(row => row.Id == pause.WorkPlanId, timeout.Token);
                plan.Status = WorkPlanStatus.Complete;
                await db.SaveChangesAsync(timeout.Token);
                var childWork = services.GetRequiredService<WorkflowChildWorkService>();
                await childWork.ArmContinuationAsync(plan.Id, request.Request,
                    parent.SubmittingUser, timeout.Token);
                (await childWork
                    .TryPrepareResumeAsync(plan.Id, timeout.Token)).Should().BeTrue();
                var saved = await db.WorkPlans.AsNoTracking()
                    .SingleAsync(row => row.Id == plan.Id, timeout.Token);
                var result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    saved.ParentResumeResultJson!, JsonDefaults.Options)!;
                result.JoinedOutput.Should().NotContain(sourceText[0]).And.NotContain(sourceText[1]);
                await store.TryResumeFromChildWorkAsync(parentId, parent.LifecycleGeneration, timeout.Token);
                await started.SendResponseAsync(request.Request.CreateResponse(result));
            }
            else if (evt is RequestInfoEvent composedRequest
                && composedRequest.Request.TryGetDataAs<WorkflowChildWorkPauseRequest>(out var composed)
                && composed.ParentWorkflowNodeId == "summary-coordinator")
            {
                var child = (await store.GetAsync(RunId.Parse(composed.ChildCoordinatorRunId), timeout.Token))!;
                child.OriginatingBranch.Should().Be(worktree.BranchName);
                var childWorktree = manager.AddWorktree(repositoryPath, child.OriginatingBranch, child.Id);
                File.ReadAllText(Path.Combine(childWorktree.WorktreePath, "demo", "incident-brief.md"))
                    .Should().Be(sourceText[0]);
                File.ReadAllText(Path.Combine(childWorktree.WorktreePath, "demo", "response-checklist.md"))
                    .Should().Be(sourceText[1]);
                await File.WriteAllTextAsync(Path.Combine(childWorktree.WorktreePath, "demo", "summary.md"),
                    "Synthetic summary", timeout.Token);
                var tree = manager.CommitChanges(childWorktree.WorktreePath, child.Id);
                await started.SendResponseAsync(composedRequest.Request.CreateResponse(
                    new WorkflowChildWorkResult(composed.WorkPlanId,
                        composed.ChildCoordinatorRunId, "fan-composed",
                        "summary-coordinator", null, true, WorkPlanStatus.Complete, null,
                        [], "", new WorkflowComposedAssembly(childWorktree.BranchName,
                            tree, "summary diff", [child.Id.ToString()]))));
            }
            else if (evt is WorkflowOutputEvent output
                     && output.Is<WorkflowComposedCompletedOutput>(out var completed))
            {
                terminal = completed;
                break;
            }
        }
        terminal.Should().NotBeNull();
        terminal!.Assembly.TreeHash.Should().NotBeNullOrWhiteSpace();
        baseFactory.TestAgentRunner.LastTask.Should().Contain("Verify both source files");
        baseFactory.TestAgentRunner.LastTask.Should().NotContain(sourceText[0]);
    }

    [Fact]
    public async Task StartAsync_StaticFan_SuspendsAtChildWorkPort_AndReturnsJoinedOutput()
    {
        using var baseFactory = new WorkflowWebApplicationFactory();
        using var testFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubCopilotCapabilityCredentialProvider>();
                services.AddSingleton<IGitHubCopilotCapabilityCredentialProvider>(
                    new FixedGitHubCopilotCapabilityCredentialProvider());
            }));
        var services = testFactory.Services;
        var workflowFactory = services.GetRequiredService<RunWorkflowFactory>();
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"agentweaver-fan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".agentweaver", "workflows"));
        await File.WriteAllTextAsync(
            Path.Combine(workingDirectory, ".agentweaver", "workflows", "fan.yaml"),
            FanWorkflowYaml());
        Repository.Init(workingDirectory);
        using (var repository = new Repository(workingDirectory))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repository.Commit("Initial commit", signature, signature);
            if (!string.Equals(repository.Head.FriendlyName, "main", StringComparison.Ordinal))
                repository.Branches.Rename(repository.Head, "main");
        }

        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Fan workflow project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = CoordinatorWebApplicationFactory.OwnerUser,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DefaultWorkflowId = "fan",
        };
        await services.GetRequiredService<IProjectStore>().InsertAsync(project);

        var runId = RunId.New();
        var worktree = services.GetRequiredService<WorktreeManager>()
            .AddWorktree(workingDirectory, "main", runId);
        var run = new DomainRun
        {
            Id = runId,
            RepositoryPath = workingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "run parallel branches",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            WorktreePath = worktree.WorktreePath,
            WorktreeBranch = worktree.BranchName,
        };
        await services.GetRequiredService<IRunStore>().InsertAsync(run);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            run.Task,
            worktree.WorktreePath,
            worktree.BranchName,
            workingDirectory,
            "main",
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            ProjectId: project.Id.ToString());
        var started = await workflowFactory.StartAsync(input, run.Id.ToString(), CancellationToken.None);
        WorkflowFanCompletedOutput? terminal = null;

        await foreach (var evt in started.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is RequestInfoEvent request
                && request.Request.TryGetDataAs<WorkflowChildWorkPauseRequest>(out var pause))
            {
                pause.ParentRunId.Should().Be(run.Id.ToString());
                var result = new WorkflowChildWorkResult(
                    pause.WorkPlanId,
                    pause.ChildCoordinatorRunId,
                    "fan",
                    pause.ParentWorkflowNodeId,
                    pause.ParentJoinNodeId,
                    true,
                    WorkPlanStatus.Complete,
                    null,
                    [
                        new WorkflowChildWorkBranch(2, "branch-b", 1, SubtaskStatus.Completed, "child-b", "second"),
                        new WorkflowChildWorkBranch(1, "branch-a", 0, SubtaskStatus.Completed, "child-a", "first"),
                    ],
                    "[1. branch-a]\nfirst\n\n[2. branch-b]\nsecond");
                await started.SendResponseAsync(request.Request.CreateResponse(result));
            }
            else if (evt is WorkflowOutputEvent output
                     && output.Is<WorkflowFanCompletedOutput>(out var completed))
            {
                terminal = completed;
                break;
            }
        }

        terminal.Should().NotBeNull();
        terminal!.JoinedOutput.Should().Be("[1. branch-a]\nfirst\n\n[2. branch-b]\nsecond");
    }

    [Fact]
    public async Task StartAsync_ComposedCoordinator_SuspendsAtChildWorkPort_AndReturnsTypedAssembly()
    {
        const string joinedContext = "[Ordered parallel branch results]\n" +
            "[1. incident-brief-writer]\nSynthetic incident details\n\n" +
            "[2. response-checklist-writer]\nRecovery checklist";
        using var baseFactory = new WorkflowWebApplicationFactory();
        using var testFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubCopilotCapabilityCredentialProvider>();
                services.AddSingleton<IGitHubCopilotCapabilityCredentialProvider>(
                    new FixedGitHubCopilotCapabilityCredentialProvider());
            }));
        var services = testFactory.Services;
        var workflowFactory = services.GetRequiredService<RunWorkflowFactory>();
        var workingDirectory = Path.Combine(
            Path.GetTempPath(), $"agentweaver-composed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".agentweaver", "workflows"));
        await File.WriteAllTextAsync(
            Path.Combine(workingDirectory, ".agentweaver", "workflows", "composed.yaml"),
            ComposedWorkflowYaml());
        Repository.Init(workingDirectory);
        using (var repository = new Repository(workingDirectory))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repository.Commit("Initial commit", signature, signature);
            if (!string.Equals(repository.Head.FriendlyName, "main", StringComparison.Ordinal))
                repository.Branches.Rename(repository.Head, "main");
        }

        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Composed workflow project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = CoordinatorWebApplicationFactory.OwnerUser,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DefaultWorkflowId = "composed",
        };
        await services.GetRequiredService<IProjectStore>().InsertAsync(project);

        var runId = RunId.New();
        var worktree = services.GetRequiredService<WorktreeManager>()
            .AddWorktree(workingDirectory, "main", runId);
        var run = new DomainRun
        {
            Id = runId,
            RepositoryPath = workingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = $"derive and execute a dependent plan\n\n{joinedContext}",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            WorktreePath = worktree.WorktreePath,
            WorktreeBranch = worktree.BranchName,
        };
        await services.GetRequiredService<IRunStore>().InsertAsync(run);
        await SeedComposedRootAsync(services, services.GetRequiredService<IRunStore>(), run);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            run.Task,
            worktree.WorktreePath,
            worktree.BranchName,
            workingDirectory,
            "main",
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            ProjectId: project.Id.ToString());
        var started = await workflowFactory.StartAsync(input, run.Id.ToString(), CancellationToken.None);
        var producerClaim = await services.GetRequiredService<IRunLeaseStore>()
            .TryClaimAsync(run.Id.ToString(), "composed-graph-producer", TimeSpan.FromMinutes(5));
        producerClaim.Claimed.Should().BeTrue();
        WorkflowComposedCompletedOutput? terminal = null;
        var observedOutputs = new List<string>();

        await foreach (var evt in started.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent error)
                observedOutputs.Add(error.ToString());
            if (evt is RequestInfoEvent request
                && request.Request.TryGetDataAs<WorkflowChildWorkPauseRequest>(out var pause))
            {
                pause.ParentRunId.Should().Be(run.Id.ToString());
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var plan = await db.WorkPlans.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == pause.WorkPlanId);
                plan.ParentRunId.Should().Be(run.Id.ToString());
                plan.ParentWorkflowNodeId.Should().Be("compose");
                plan.ParentJoinNodeId.Should().BeNull();
                var dispatch = services.GetRequiredService<CoordinatorDispatchService>();
                var dispatchContext = new CoordinatorDispatchContext(pause.ChildCoordinatorRunId,
                    workingDirectory, worktree.BranchName, run.SubmittingUser,
                    project.Id, ComposedWorkflowChild: true);
                var subtask = new Subtask
                {
                    Title = "Write the summary",
                    Scope = "Read verified source documents and write demo/summary.md.",
                    Phase = "execution",
                    AssignedAgent = "Morpheus",
                    SelectedModelId = "test-model",
                    IsolationStrategy = "worktree",
                    Status = SubtaskStatus.Pending,
                    DeclaredOutputPathsJson = "[\"demo/summary.md\"]",
                };
                var actualChildTask = await dispatch.ComposeChildTaskAsync(
                    dispatchContext, plan.Id, subtask, CancellationToken.None);
                actualChildTask.Should().Contain(joinedContext);
                var reattached = await services.GetRequiredService<WorkflowChildWorkService>()
                    .PrepareComposedAsync(new WorkflowComposedWorkRequest(
                        run, "composed", "compose", "Derive dependent tasks",
                        input with { Task = "edited context" }, "edited-tree"));
                reattached.WorkPlanId.Should().Be(plan.Id);
                var resumedChildTask = await dispatch.ComposeChildTaskAsync(
                    dispatchContext, plan.Id, subtask, CancellationToken.None);
                resumedChildTask.Should().Contain(joinedContext).And.NotContain("edited context");

                var assembly = new WorkflowComposedAssembly(
                    "agentweaver/integration-child",
                    new string('a', 40),
                    "diff --git a/generated.txt b/generated.txt",
                    ["child-a", "child-b"]);
                var result = new WorkflowChildWorkResult(
                    pause.WorkPlanId,
                    pause.ChildCoordinatorRunId,
                    "composed",
                    pause.ParentWorkflowNodeId,
                    null,
                    true,
                    WorkPlanStatus.Complete,
                    null,
                    [],
                    string.Empty,
                    assembly);
                await started.SendResponseAsync(request.Request.CreateResponse(result));
            }
            else if (evt is WorkflowOutputEvent output
                     && output.Is<WorkflowComposedCompletedOutput>(out var completed))
            {
                terminal = completed;
                break;
            }
        }

        terminal.Should().NotBeNull("workflow outputs: {0}", string.Join(", ", observedOutputs));
        terminal!.WorkPlanId.Should().BeGreaterThan(0);
        terminal.Assembly.TreeHash.Should().Be(new string('a', 40));
        terminal.Assembly.IncludedChildRunIds.Should().Equal("child-a", "child-b");
    }

    private static async Task SeedComposedRootAsync(
        IServiceProvider services, IRunStore store, DomainRun run)
    {
        var tree = services.GetRequiredService<IWorktreeOperations>().GetTreeHash(run.WorktreePath!)
            ?? throw new InvalidOperationException("Composed root source tree is missing.");
        await store.UpdateStatusAsync(run.Id, DomainRunStatus.AwaitingReview, null);
        (await store.TryRecordFanInputProjectionAsync(
            run.Id, run.LifecycleGeneration, tree, tree, run.WorktreeBranch!)).Should().BeTrue();
        await store.UpdateStatusAsync(run.Id, DomainRunStatus.InProgress, null);
    }

    [Fact]
    public async Task ResumeAsync_ProjectWorkflowMutationAndDeletionAfterCheckpoint_UsesStartedDefinition()
    {
        using var baseFactory = new WorkflowWebApplicationFactory();
        using var testFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubCopilotCapabilityCredentialProvider>();
                services.AddSingleton<IGitHubCopilotCapabilityCredentialProvider>(
                    new FixedGitHubCopilotCapabilityCredentialProvider());
            }));
        var services = testFactory.Services;
        var workflowFactory = services.GetRequiredService<RunWorkflowFactory>();
        var workingDirectory = Path.Combine(
            Path.GetTempPath(), $"agentweaver-workflow-pin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var workflowsDirectory = Path.Combine(workingDirectory, ".agentweaver", "workflows");
        Directory.CreateDirectory(workflowsDirectory);
        var workflowPath = Path.Combine(workflowsDirectory, "custom.yaml");
        await File.WriteAllTextAsync(workflowPath, WorkflowYaml("original-agent", "Original Agent"));
        Repository.Init(workingDirectory);
        using (var repository = new Repository(workingDirectory))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repository.Commit("Initial commit", signature, signature);
            if (!string.Equals(repository.Head.FriendlyName, "main", StringComparison.Ordinal))
                repository.Branches.Rename(repository.Head, "main");
        }

        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Pinned workflow project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = CoordinatorWebApplicationFactory.OwnerUser,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DefaultWorkflowId = "custom",
        };
        await services.GetRequiredService<IProjectStore>().InsertAsync(project);

        var runId = RunId.New();
        var worktree = services.GetRequiredService<WorktreeManager>()
            .AddWorktree(workingDirectory, "main", runId);
        var run = new DomainRun
        {
            Id = runId,
            RepositoryPath = workingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "prove workflow pinning",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            WorktreePath = worktree.WorktreePath,
            WorktreeBranch = worktree.BranchName,
        };
        var runStore = services.GetRequiredService<IRunStore>();
        await runStore.InsertAsync(run);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            run.Task,
            worktree.WorktreePath,
            worktree.BranchName,
            workingDirectory,
            "main",
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            ProjectId: project.Id.ToString(),
            AgentName: "agent",
            RunStartedAt: run.StartedAt);
        var started = await workflowFactory.StartAsync(input, run.Id.ToString(), CancellationToken.None);
        var eventTypes = new List<string>();
        await foreach (var evt in started.WatchStreamAsync(CancellationToken.None))
        {
            eventTypes.Add(evt switch
            {
                ExecutorInvokedEvent invoked => $"{evt.GetType().Name}:{invoked.ExecutorId}",
                ExecutorCompletedEvent completed =>
                    $"{evt.GetType().Name}:{completed.ExecutorId}:{System.Text.Json.JsonSerializer.Serialize(completed.Data)}",
                _ => evt.GetType().Name,
            });
            if (evt is RequestInfoEvent)
                break;
        }

        var persistedRun = await runStore.GetAsync(run.Id);
        persistedRun.Should().NotBeNull();
        persistedRun!.GetExecutableWorkflowPin().Should().NotBeNull();
        persistedRun.ExecutableWorkflowDefinitionId.Should().Be("custom");
        var checkpoint = started.LastCheckpoint;
        checkpoint.Should().NotBeNull("the started workflow should suspend at review; events: {0}",
            string.Join(", ", eventTypes));

        await File.WriteAllTextAsync(workflowPath, WorkflowYaml("mutated-agent", "Mutated Agent"));
        await workflowFactory.ResumeAsync(checkpoint!, CancellationToken.None);
        workflowFactory.TryGetExecutorMeta(
            run.Id.ToString(), "agent-turn-original-agent", out var mutatedResumeMeta).Should().BeTrue();
        mutatedResumeMeta.LogicalNodeId.Should().Be("original-agent");
        workflowFactory.TryGetExecutorMeta(run.Id.ToString(), "agent-turn-mutated-agent", out _).Should().BeFalse();

        File.Delete(workflowPath);
        await workflowFactory.ResumeAsync(checkpoint!, CancellationToken.None);
        workflowFactory.TryGetExecutorMeta(
            run.Id.ToString(), "agent-turn-original-agent", out var deletedResumeMeta).Should().BeTrue();
        deletedResumeMeta.LogicalNodeId.Should().Be("original-agent");
        workflowFactory.TryGetExecutorMeta(run.Id.ToString(), "agent-turn-agent", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RubberduckPass_FirstHumanReviewCapturesGenerationWithoutPriorReviewRequest()
    {
        using var factory = new WorkflowWebApplicationFactory();
        var services = factory.Services;
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"agentweaver-rubberduck-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "seed.txt"), "seed");
        Repository.Init(repositoryPath);
        using (var repository = new Repository(repositoryPath))
        {
            Commands.Stage(repository, "*");
            var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repository.Commit("Initial commit", signature, signature);
            if (repository.Head.FriendlyName != "main")
                repository.Branches.Rename(repository.Head, "main");
        }

        var runId = RunId.New();
        var worktrees = services.GetRequiredService<WorktreeManager>();
        var worktree = worktrees.AddWorktree(repositoryPath, "main", runId);
        using var watchCancellation = new CancellationTokenSource();
        try
        {
            var yaml = RubberduckReviewWorkflowYaml();
            var run = PinnedRun(
                ExecutableWorkflowPin.CurrentSchemaVersion,
                "sha256:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml)))
                    .ToLowerInvariant()) with
            {
                Id = runId,
                RepositoryPath = repositoryPath,
                WorktreePath = worktree.WorktreePath,
                WorktreeBranch = worktree.BranchName,
                ExecutableWorkflowDefinitionYaml = yaml,
                Task = "rubberduck first human review",
                SubmittingUser = WorkflowWebApplicationFactory.TestUser,
            };
            await services.GetRequiredService<IRunStore>().InsertAsync(run);
            var input = new AgentTurnInput(
                runId.ToString(), run.Task, worktree.WorktreePath, worktree.BranchName,
                repositoryPath, "main", run.ModelSource.ToApiString(), run.ModelId, run.SubmittingUser);

            var started = await services.GetRequiredService<RunWorkflowFactory>()
                .StartAsync(input, runId.ToString(), CancellationToken.None);
            var entry = services.GetRequiredService<RunStreamStore>()
                .Create(runId.ToString(), run.SubmittingUser);
            services.GetRequiredService<RunWatchLoopService>().StartWatching(
                runId.ToString(), started, entry, run.SubmittingUser,
                watchCancellation.Token, run.LifecycleGeneration);
            var pending = services.GetRequiredService<PendingRequestStore>();
            PendingEntry? gate = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (gate is null && DateTimeOffset.UtcNow < deadline)
            {
                gate = await pending.GetAsync(runId.ToString());
                if (gate is null)
                    await Task.Delay(50);
            }

            gate.Should().NotBeNull("the production watcher must persist the workflow review request");
            gate!.Request.RequestId.Should().NotBeNullOrWhiteSpace();
            (await pending.GetRequestKindAsync(runId.ToString()))
                .Should().Be(PendingRequestDeliveryKinds.WorkflowReview);
            started.LastCheckpoint.Should().NotBeNull();
            var published = (await services.GetRequiredService<IRunStore>().GetAsync(runId))!;
            published.LifecycleGeneration.Should().Be(run.LifecycleGeneration);
            published.Diff.Should().NotBeNullOrEmpty();
            var revision = await services.GetRequiredService<IRunStore>()
                .GetLatestOutputRevisionAsync(runId);
            revision.Should().NotBeNull();
            published.CurrentOutputRevisionId.Should().Be(revision!.RevisionId);
            (await pending.GetActionableRequestKindAsync(published, revision))
                .Should().Be(PendingRequestDeliveryKinds.WorkflowReview);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", WorkflowWebApplicationFactory.TestApiKey);
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
            detail.GetProperty("pending_request_kind").GetString()
                .Should().Be(PendingRequestDeliveryKinds.WorkflowReview);
        }
        finally
        {
            watchCancellation.Cancel();
            worktrees.RemoveWorktree(repositoryPath, worktree.WorktreePath, worktree.BranchName);
            foreach (var file in Directory.EnumerateFiles(repositoryPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_MissingDurableRun_FailsBeforeWorkflowExecution()
    {
        var runId = RunId.New().ToString();
        var input = new AgentTurnInput(
            runId,
            "must not execute",
            Path.GetTempPath(),
            "agentweaver/missing",
            Path.GetTempPath(),
            "main",
            ModelSource.GitHubCopilot.ToApiString(),
            null,
            CoordinatorWebApplicationFactory.OwnerUser);

        var start = async () => await Factory.StartAsync(input, runId, CancellationToken.None);

        await start.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*durable run record is missing*");
    }

    [Fact]
    public async Task ResumeAsync_MissingDurableRun_FailsInsteadOfSelectingCurrentDefault()
    {
        var runId = RunId.New().ToString();
        var checkpoint = new CheckpointInfo(runId, "missing-checkpoint");

        var resume = async () => await Factory.ResumeAsync(checkpoint, CancellationToken.None);

        await resume.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*durable run record is missing*");
    }

    [Fact]
    public async Task GetGraphDescriptorAsync_UnsupportedPinnedManifestSchema_FailsExplicitly()
    {
        var run = PinnedRun(
            manifestSchemaVersion: ExecutableWorkflowPin.CurrentSchemaVersion + 1,
            contentDigest: new string('0', 64));

        var load = async () => await Factory.GetGraphDescriptorAsync(run, CancellationToken.None);

        await load.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*manifest schema version*not supported*");
    }

    [Fact]
    public async Task GetGraphDescriptorAsync_PinnedManifestDigestMismatch_FailsExplicitly()
    {
        var run = PinnedRun(
            manifestSchemaVersion: ExecutableWorkflowPin.CurrentSchemaVersion,
            contentDigest: new string('0', 64));

        var load = async () => await Factory.GetGraphDescriptorAsync(run, CancellationToken.None);

        await load.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*content digest mismatch*");
    }

    [Fact]
    public async Task GetGraphDescriptorAsync_PinnedManifestVersionMismatch_FailsExplicitly()
    {
        var yaml = WorkflowYaml("original-agent", "Original Agent");
        var run = PinnedRun(
            manifestSchemaVersion: ExecutableWorkflowPin.CurrentSchemaVersion,
            contentDigest: "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(yaml)))
                .ToLowerInvariant()) with
        {
            ExecutableWorkflowDefinitionVersion = "different-version",
        };

        var load = async () => await Factory.GetGraphDescriptorAsync(run, CancellationToken.None);

        await load.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*version mismatch*");
    }

    private static DomainRun PinnedRun(int manifestSchemaVersion, string contentDigest) =>
        new()
        {
            Id = RunId.New(),
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "validate pinned workflow",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ExecutableWorkflowPinRequired = true,
            ExecutableWorkflowManifestSchemaVersion = manifestSchemaVersion,
            ExecutableWorkflowDefinitionId = "custom",
            ExecutableWorkflowDefinitionVersion = "1",
            ExecutableWorkflowSource = "project",
            ExecutableWorkflowContentDigest = contentDigest,
            ExecutableWorkflowDefinitionYaml = WorkflowYaml("original-agent", "Original Agent"),
            ExecutableWorkflowPinnedAt = DateTimeOffset.UtcNow,
        };

    private static (string StartNodeId, HashSet<NodeShape> Nodes, HashSet<EdgeShape> Edges) Project(
        GraphDescriptor d) =>
    (
        d.StartNodeId,
        d.Nodes.Select(n => new NodeShape(n.Id, n.Label, n.Role, n.Kind, n.NodeType)).ToHashSet(),
        d.Edges.Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback)).ToHashSet()
    );

    private static string WorkflowYaml(string agentId, string agentLabel) =>
        $$"""
        id: custom
        name: Custom Workflow
        version: "1"
        start: {{agentId}}
        nodes:
          - id: {{agentId}}
            type: prompt
            label: {{agentLabel}}
            role: agent
            kind: live
            prompt: Do the work.
          - id: done
            type: terminal
            label: Done
            role: plumbing
            kind: terminal
          - id: review
            type: check
            label: Human Review
            role: review
            kind: gate
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: declined
            type: terminal
            label: Declined
            role: plumbing
            kind: terminal
        edges:
          - from: {{agentId}}
            to: review
          - from: review
            to: done
            when: approved
          - from: review
            to: {{agentId}}
            when: request-changes
          - from: review
            to: declined
            when: declined
        """;

    private static string RubberduckReviewWorkflowYaml() =>
        """
        id: custom
        name: Rubberduck review
        version: "1"
        start: agent
        nodes:
          - id: agent
            type: prompt
            label: Agent
            prompt: Do the work.
          - id: peer-review
            type: peer_review
            label: Peer review
          - id: rubberduck
            type: check
            label: Rubberduck
            gate_kind: rubberduck
            branches:
              - pass
              - revise
          - id: review
            type: check
            label: Human Review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: done
            type: terminal
            label: Done
          - id: declined
            type: terminal
            label: Declined
        edges:
          - from: agent
            to: peer-review
          - from: peer-review
            to: rubberduck
            when: pass
          - from: peer-review
            to: agent
            when: request-changes
          - from: rubberduck
            to: review
            when: pass
          - from: rubberduck
            to: agent
            when: revise
          - from: review
            to: done
            when: approved
          - from: review
            to: agent
            when: request-changes
          - from: review
            to: declined
            when: declined
        """;

    private static string FanWorkflowYaml() =>
        """
        id: fan
        name: Static Fan
        version: "1"
        start: fan
        nodes:
          - id: fan
            type: fan_out
            label: Parallel work
          - id: branch-a
            type: prompt
            label: First branch
            agent: researcher
            prompt: Produce the first result.
          - id: branch-b
            type: prompt
            label: Second branch
            agent: researcher
            prompt: Produce the second result.
          - id: join
            type: fan_in
            label: Join
            target: fan
          - id: done
            type: terminal
            label: Done
        edges:
          - from: fan
            to: branch-a
          - from: fan
            to: branch-b
          - from: branch-a
            to: join
          - from: branch-b
            to: join
          - from: join
            to: done
        """;

    private static string FanComposedWorkflowYaml() =>
        """
        id: fan-composed
        name: Retained fan composed
        version: "1"
        start: split-documents
        nodes:
          - id: split-documents
            type: fan_out
            label: Split documents
          - id: incident-brief-writer
            type: prompt
            label: Incident brief
            agent: Neo
            prompt: Write only demo/incident-brief.md.
            independent: true
            declared_output_paths:
              - demo/incident-brief.md
          - id: response-checklist-writer
            type: prompt
            label: Response checklist
            agent: Trinity
            prompt: Write only demo/response-checklist.md.
            independent: true
            declared_output_paths:
              - demo/response-checklist.md
          - id: join-documents
            type: fan_in
            label: Join
            target: split-documents
          - id: verify-inputs
            type: prompt
            label: Verify inputs
            agent: Morpheus
            prompt: Verify both source files by reading demo/incident-brief.md and demo/response-checklist.md.
          - id: summary-coordinator
            type: coordinator_composed
            label: Compose summary
            prompt: Write only demo/summary.md using the source files.
          - id: done
            type: terminal
            label: Done
        edges:
          - from: split-documents
            to: incident-brief-writer
          - from: split-documents
            to: response-checklist-writer
          - from: incident-brief-writer
            to: join-documents
          - from: response-checklist-writer
            to: join-documents
          - from: join-documents
            to: verify-inputs
          - from: verify-inputs
            to: summary-coordinator
          - from: summary-coordinator
            to: done
        """;

    private static string ComposedWorkflowYaml() =>
        """
        id: composed
        name: Dynamic composed plan
        version: "1"
        start: prepare
        nodes:
          - id: prepare
            type: prompt
            label: Prepare the runtime goal
            prompt: Refine the goal before deriving dependent work.
          - id: compose
            type: coordinator_composed
            label: Derive and execute dependent work
            prompt: Derive a runtime-dependent implementation plan, execute it, and assemble the result.
          - id: done
            type: terminal
            label: Done
        edges:
          - from: prepare
            to: compose
          - from: compose
            to: done
        """;

}
