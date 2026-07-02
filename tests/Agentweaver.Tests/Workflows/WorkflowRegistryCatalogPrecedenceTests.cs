using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using FluentAssertions;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Bug #168 — a project-materialized copy of a built-in/catalog workflow must never shadow the
/// catalog version. A project created before a catalog update would otherwise run the stale on-disk
/// copy forever. The catalog version always wins, and the stale project copy is auto-refreshed in
/// place so the Workspace reflects what actually runs.
/// </summary>
public sealed class WorkflowRegistryCatalogPrecedenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(),
        "test-artifacts",
        "workflow-registry-precedence",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void StaleProjectCopyOfCatalogWorkflow_IsIgnored_CatalogVersionWins()
    {
        var (catalogYaml, catalogName) = LoadCatalogWorkflow("bug-fix");

        var project = CreateProject();
        var workflowDir = Path.Combine(_root, ".agentweaver", "workflows");
        Directory.CreateDirectory(workflowDir);
        var staleFile = Path.Combine(workflowDir, "bug-fix.yaml");
        File.WriteAllText(staleFile, StaleBugFixYaml);

        var registry = new WorkflowRegistry(new CatalogReader());
        var set = registry.GetOrLoad(project);

        var loaded = set.FindById("bug-fix");
        loaded.Should().NotBeNull();
        loaded!.Definition!.Name.Should().Be(catalogName,
            because: "the catalog version must win over a stale project-level copy (#168)");
        loaded.Definition.Name.Should().NotBe("STALE Bug Fix");

        // The stale on-disk copy is auto-refreshed to the canonical catalog text.
        File.ReadAllText(staleFile).Replace("\r\n", "\n").Trim()
            .Should().Be(catalogYaml.Replace("\r\n", "\n").Trim(),
                because: "a drifted built-in copy is refreshed from the catalog to avoid silent stale state (#168)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Project CreateProject() => new()
    {
        Id = ProjectId.New(),
        Name = "Catalog Precedence",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = _root,
        DefaultBranch = "main",
        Owner = "alice",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static (string Yaml, string Name) LoadCatalogWorkflow(string workflowId)
    {
        var reader = new CatalogReader();
        foreach (var (yaml, source) in reader.LoadAllWorkflowYamls())
        {
            var result = WorkflowDefinitionLoader.Load(yaml, source, isBuiltIn: true);
            if (result.IsValid && result.Definition is not null &&
                string.Equals(result.Definition.Id, workflowId, StringComparison.Ordinal))
            {
                return (yaml, result.Definition.Name);
            }
        }

        throw new InvalidOperationException(
            $"Catalog workflow '{workflowId}' was not found among the embedded workflow resources.");
    }

    // A valid but intentionally divergent copy of the catalog 'bug-fix' workflow (same id) — the
    // kind of stale artifact a project created before a catalog update would carry on disk.
    private const string StaleBugFixYaml =
        """
        id: bug-fix
        name: STALE Bug Fix
        description: An outdated project-level copy that must be ignored in favor of the catalog.
        start: fix
        nodes:
          - id: fix
            type: prompt
            role: engineer
          - id: done
            type: terminal
        edges:
          - from: fix
            to: done
        """;
}
