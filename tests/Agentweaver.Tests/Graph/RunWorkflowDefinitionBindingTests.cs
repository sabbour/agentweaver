using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task GetGraphDescriptorAsync_ProjectWorkflowMutationAfterRunStart_UsesStartedDefinition()
    {
        var workingDirectory = _factory.NewWorkingDirectory();
        var workflowsDirectory = Path.Combine(workingDirectory, ".agentweaver", "workflows");
        Directory.CreateDirectory(workflowsDirectory);
        var workflowPath = Path.Combine(workflowsDirectory, "custom.yaml");
        await File.WriteAllTextAsync(workflowPath, WorkflowYaml("original-agent", "Original Agent"));

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
        await _factory.Services.GetRequiredService<IProjectStore>().InsertAsync(project);

        var run = new DomainRun
        {
            Id = RunId.New(),
            RepositoryPath = workingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "prove workflow pinning",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = DomainRunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            WorktreePath = workingDirectory,
        };
        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        await runStore.InsertAsync(run);

        var persistedRun = await runStore.GetAsync(run.Id);
        persistedRun.Should().NotBeNull();
        var missingPin = async () => await Factory.GetGraphDescriptorAsync(persistedRun!, CancellationToken.None);
        await missingPin.Should().ThrowAsync<WorkflowBindException>()
            .WithMessage("*requires a pinned executable workflow manifest*");

        var originalDefinition = WorkflowDefinitionLoader.Load(
            await File.ReadAllTextAsync(workflowPath),
            "custom.yaml").Definition!;
        var pinnedYaml = WorkflowDefinitionYamlSerializer.Serialize(originalDefinition);
        await runStore.UpdateExecutableWorkflowPinAsync(
            run.Id,
            new ExecutableWorkflowPin
            {
                ManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
                DefinitionId = originalDefinition.Id,
                DefinitionVersion = originalDefinition.Version,
                Source = "custom.yaml",
                ContentDigest = TestDigest(pinnedYaml),
                DefinitionYaml = pinnedYaml,
                PinnedAt = DateTimeOffset.UtcNow,
            });
        persistedRun = await runStore.GetAsync(run.Id);
        persistedRun.Should().NotBeNull();
        var startedDescriptor = await Factory.GetGraphDescriptorAsync(persistedRun!, CancellationToken.None);
        startedDescriptor.Nodes.Select(node => node.Id).Should().Contain("original-agent");

        await File.WriteAllTextAsync(workflowPath, WorkflowYaml("mutated-agent", "Mutated Agent"));

        var pinnedRun = await runStore.GetAsync(run.Id);
        pinnedRun.Should().NotBeNull();
        var resumedDescriptor = await Factory.GetGraphDescriptorAsync(pinnedRun!, CancellationToken.None);

        resumedDescriptor.Nodes.Select(node => node.Id).Should().Contain("original-agent");
        resumedDescriptor.Nodes.Select(node => node.Id).Should().NotContain("mutated-agent");

        File.Delete(workflowPath);

        var deletedDescriptor = await Factory.GetGraphDescriptorAsync(pinnedRun!, CancellationToken.None);

        deletedDescriptor.Nodes.Select(node => node.Id).Should().Contain("original-agent");
        deletedDescriptor.Nodes.Select(node => node.Id).Should().NotContain("agent");
    }

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
        edges:
          - from: {{agentId}}
            to: done
        """;

    private static string TestDigest(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

}
