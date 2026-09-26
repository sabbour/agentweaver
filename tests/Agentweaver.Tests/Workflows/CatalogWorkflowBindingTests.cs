using FluentAssertions;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Catalog workflows are coordinator-authored topologies: special review gates are first-class workflow
/// nodes, while merge, pull-request publication, and scribe are platform-appended by coordinator assembly.
/// </summary>
public sealed class CatalogWorkflowBindingTests
{
    [Theory]
    [InlineData("bug-fix")]
    [InlineData("content-authoring")]
    [InlineData("incident-response")]
    [InlineData("infra-ops")]
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_LoadsAndIsBindableForCoordinatorSelection(string workflowId)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);

        errors.Should().BeEmpty(because: $"catalog workflow '{workflowId}' must be selectable by the coordinator");
    }

    [Theory]
    [InlineData("software-delivery", new[] { "rai", "rubberduck", "build-test", "human-review" })]
    [InlineData("bug-fix", new[] { "rai", "build-test", "human-review" })]
    [InlineData("content-authoring", new[] { "rai", "human-review" })]
    [InlineData("incident-response", new[] { "human-review" })]
    [InlineData("infra-ops", new[] { "rai", "human-review" })]
    [InlineData("pm-discovery", new[] { "human-review" })]
    [InlineData("agent-evaluation", new[] { "rai" })]
    public void CatalogWorkflow_DeclaresExpectedAuthorableGates_WithoutMergeOrScribe(
        string workflowId,
        string[] expectedGates)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var gates = definition.Nodes
            .Where(n => n.Type == WorkflowNodeType.Check || n.Type == WorkflowNodeType.BuildTest)
            .Select(n => n.Type == WorkflowNodeType.BuildTest ? "build-test" : NodeClassifier.NormalizeGateKind(n))
            .Where(g => g is not null)
            .ToArray();

        gates.Should().Equal(expectedGates);
        definition.Nodes.Should().NotContain(n =>
            n.Type == WorkflowNodeType.Merge || n.Type == WorkflowNodeType.Scribe);
    }

    [Theory]
    [InlineData("software-delivery")]
    [InlineData("bug-fix")]
    public void CatalogWorkflow_UsesPlatformBuildTestGateWithoutInlinePrompt(string workflowId)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var buildTest = definition.Nodes.Single(n => n.Id == "build-test");

        buildTest.Type.Should().Be(WorkflowNodeType.BuildTest);
        buildTest.Label.Should().Be("Build & Test");
        buildTest.Agent.Should().Be("qa-engineer");
        buildTest.Prompt.Should().BeNull();
    }

    [Fact]
    public void PmDiscovery_UsesExactOrderedFanBeforeSynthesis()
    {
        var definition = LoadCatalogWorkflow("pm-discovery");

        definition.Start.Should().Be("discovery-fan-out");
        var fanOut = definition.Nodes.Single(node => node.Type == WorkflowNodeType.FanOut);
        var fanIn = definition.Nodes.Single(node => node.Type == WorkflowNodeType.FanIn);
        fanIn.Target.Should().Be(fanOut.Id);
        var branches = definition.Edges
            .Where(edge => edge.From == fanOut.Id)
            .Select(edge => definition.Nodes.Single(node => node.Id == edge.To))
            .ToArray();

        branches.Select(node => node.Id).Should().Equal(
            "customer-signal-research",
            "technical-feasibility-research");
        branches.Should().OnlyContain(node => node.Type == WorkflowNodeType.Prompt);
        branches.Should().OnlyContain(node => node.Independent == true);
        branches.SelectMany(node => node.DeclaredOutputPaths).Should().Equal(
            "customer-signals.md",
            "technical-feasibility.md");
        branches.Should().OnlyContain(branch => definition.Edges.Any(edge =>
            edge.From == branch.Id && edge.To == fanIn.Id && edge.When == null));
        definition.Edges.Should().ContainSingle(edge =>
            edge.From == fanIn.Id && edge.To == "synthesis" && edge.When == null);
        definition.Nodes.Should().NotContain(node => node.Type == WorkflowNodeType.CoordinatorComposed);
        RunWorkflowGraphBinder.GetBindabilityErrors(definition).Should().BeEmpty();
    }

    [Fact]
    public void PmDiscovery_BindsAgainstEveryBlueprintThatExposesIt()
    {
        var catalog = new CatalogReader();
        var workflow = LoadCatalogWorkflow("pm-discovery");
        var blueprints = catalog.LoadAllBlueprints()
            .Where(blueprint => blueprint.Workflows.Contains("pm-discovery", StringComparer.Ordinal))
            .ToArray();
        blueprints.Should().NotBeEmpty();

        foreach (var blueprint in blueprints)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "agentweaver-pm-discovery-binding",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var members = blueprint.Roster
                    .Select((roleId, index) => new CastMember(
                        $"Member-{index + 1}",
                        catalog.LoadRole(roleId)
                            ?? throw new InvalidOperationException($"Catalog role '{roleId}' was not found."),
                        $".squad/agents/member-{index + 1}/charter.md",
                        CastMemberStatus.Active,
                        true))
                    .ToArray();
                new SquadWriter(root).WriteTeam(
                    new Team(blueprint.Name, "test", members),
                    "test",
                    DateTimeOffset.UtcNow);
                var project = new Project
                {
                    Id = ProjectId.New(),
                    Name = blueprint.Name,
                    Origin = ProjectOrigin.Blank(),
                    WorkingDirectory = root,
                    DefaultBranch = "main",
                    Owner = "test",
                    ProviderSettings = new ProjectProviderSettings
                    {
                        DefaultProvider = ModelSource.GitHubCopilot,
                    },
                    State = ProjectState.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var binding = WorkflowTeamBinding.Bind(project, workflow);

                binding.IsResolved.Should().BeTrue(
                    $"blueprint '{blueprint.Id}' exposes pm-discovery but lacks roles for: " +
                    string.Join(", ", binding.UnresolvedRoles.Select(role => role.Role ?? role.Agent)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WorkflowDefinition LoadCatalogWorkflow(string workflowId)
    {
        var reader = new CatalogReader();
        foreach (var (yaml, source) in reader.LoadAllWorkflowYamls())
        {
            var result = WorkflowDefinitionLoader.Load(yaml, source, isBuiltIn: false);
            if (result.IsValid && result.Definition is not null &&
                string.Equals(result.Definition.Id, workflowId, StringComparison.Ordinal))
            {
                return result.Definition;
            }
        }

        throw new InvalidOperationException(
            $"Catalog workflow '{workflowId}' was not found among the embedded workflow resources.");
    }
}
