using FluentAssertions;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Blueprints;

public sealed class CatalogConformanceSnapshotTests
{
    [Fact]
    public void Snapshot_ValidatesExactEmbeddedInventoryThroughProductionPaths()
    {
        var snapshot = new CatalogConformanceSnapshot(new CatalogReader());

        snapshot.Blueprints.Select(item => item.Blueprint.Id).Should().Equal(
            "blueprint-ai-agent-engineering",
            "blueprint-content-authoring",
            "blueprint-pm-and-software-development",
            "blueprint-product-management",
            "blueprint-software-development");
        snapshot.Blueprints.Should().OnlyContain(item => item.IsExportable);
        snapshot.Workflows.Should().HaveCount(8, "the shipped default plus seven embedded workflow resources");
        snapshot.Workflows.Should().OnlyContain(item => item.IsValid);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("c:\\escape")]
    [InlineData("CON")]
    [InlineData("Upper-case")]
    [InlineData("role_name")]
    [InlineData("role\u202Ename")]
    [InlineData("role\nname")]
    public void Identifier_RejectsUnsafePortableNames(string value)
    {
        CatalogIdentifier.IsSafe(value).Should().BeFalse();
        CatalogIdentifier.ToResourceStem(value).Should().BeNull();
    }

    [Fact]
    public void ExportabilityDiagnostics_AreSanitizedSortedAndBounded()
    {
        var diagnostics = BlueprintExportability.FromCodes(
            Enumerable.Repeat("z_code", 20).Concat(["b_code", "A_BAD", "x-code", new string('a', 65)]));

        diagnostics.Status.Should().Be("unavailable");
        diagnostics.Codes.Should().Equal("b_code", "z_code");
    }

    [Fact]
    public void MalformedRoleResource_IsReportedAsSanitizedExportabilityDiagnostics()
    {
        var snapshot = new CatalogConformanceSnapshot(FixtureCatalog());

        var entry = snapshot.FindBlueprint("blueprint-malformed-role");

        entry.Should().NotBeNull();
        entry!.Exportability.Status.Should().Be("unavailable");
        entry.Exportability.Codes.Should().Equal("role_malformed");
        entry.Exportability.Codes.Should().OnlyContain(code =>
            code.All(c => (c >= 'a' && c <= 'z') || c == '_' || (c >= '0' && c <= '9')));
        entry.Exportability.Codes.Should().NotContain(code =>
            code.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("jsonexception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidCatalogWorkflow_CannotReserveOrShadowProjectWorkflow()
    {
        var root = CreateTestRoot();
        try
        {
            var snapshot = new CatalogConformanceSnapshot(FixtureCatalog());
            snapshot.Workflows
                .Where(result => result.Definition?.Id == "shadow-target" && !result.IsValid)
                .Should().ContainSingle();

            var workflowDirectory = Path.Combine(root, ".agentweaver", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "shadow-target.yaml"), WorkflowYaml("shadow-target"));

            var project = CreateProject(root, defaultWorkflowId: "shadow-target");
            var registry = new WorkflowRegistry(snapshot);
            var set = registry.GetOrLoad(project);

            set.Results.Should().NotContain(result => result.Source == "shadow_target.yaml",
                "unavailable catalog workflows must not enter the registry");
            set.FindById("shadow-target")!.Definition!.Name.Should().Be("shadow-target");
            registry.ResolveDefault(project).Definition!.Id.Should().Be("shadow-target",
                "the valid project workflow must remain selectable rather than falling back to default");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Theory]
    [InlineData("Legacy_Workflow")]
    [InlineData("UpperCaseWorkflow")]
    public void LegacyCustomWorkflowIds_RemainValidWhileBuiltInIdsFailClosed(string id)
    {
        var yaml = WorkflowYaml(id);

        WorkflowDefinitionLoader.Load(yaml, "custom.yaml").IsValid.Should().BeTrue();
        WorkflowDefinitionLoader.Load(yaml, "catalog.yaml", isBuiltIn: true).IsValid.Should().BeFalse();
    }

    [Fact]
    public void LegacyCustomBlueprintIds_RemainValid()
    {
        var root = CreateTestRoot();
        try
        {
            var workflowDirectory = Path.Combine(root, ".agentweaver", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "legacy.yaml"), WorkflowYaml("Legacy_Workflow"));

            var catalog = new CatalogReader();
            var service = new BlueprintService(
                catalog,
                casting: null!,
                projectStore: null!,
                sandboxPolicyStore: null!,
                workflowRegistry: new WorkflowRegistry(),
                generator: null!,
                workflowGenerator: null!,
                logger: NullLogger<BlueprintService>.Instance);
            var blueprint = new Blueprint(
                "Legacy Blueprint",
                "Legacy Blueprint",
                "Compatibility fixture",
                ["qa-engineer"],
                ["Legacy_Workflow"],
                "default",
                "default");

            var validation = service.Validate(blueprint, CreateProject(root));

            validation.Valid.Should().BeTrue(string.Join("; ", validation.Errors));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static CatalogReader FixtureCatalog() =>
        new(typeof(CatalogConformanceSnapshotTests).Assembly, "Agentweaver.Tests.CatalogFixtures");

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "test-artifacts",
            "catalog-conformance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static Project CreateProject(string root, string? defaultWorkflowId = null) => new()
    {
        Id = ProjectId.New(),
        Name = "Catalog conformance",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = root,
        DefaultBranch = "main",
        Owner = "alice",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        DefaultWorkflowId = defaultWorkflowId,
    };

    private static string WorkflowYaml(string id) =>
        $$"""
        id: {{id}}
        name: {{id}}
        start: scribe
        nodes:
          - id: scribe
            type: scribe
          - id: done
            type: terminal
        edges:
          - from: scribe
            to: done
        """;
}
