using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
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

}
