using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Workflows;

public sealed class DefinitionRegistryReplicaCoherenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(),
        "test-artifacts",
        "definition-registry",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WorkflowRegistry_ReloadsWhenSharedWorkflowFilesChangeAcrossInstances()
    {
        var project = CreateProject();
        var workflowDir = Path.Combine(_root, ".agentweaver", "workflows");
        Directory.CreateDirectory(workflowDir);
        File.WriteAllText(Path.Combine(workflowDir, "custom.yaml"), WorkflowYaml("wf-one"));

        var replicaA = new WorkflowRegistry();
        var replicaB = new WorkflowRegistry();
        replicaA.GetOrLoad(project).FindById("wf-one").Should().NotBeNull();
        replicaB.GetOrLoad(project).FindById("wf-one").Should().NotBeNull();

        File.WriteAllText(Path.Combine(workflowDir, "custom.yaml"), WorkflowYaml("wf-two"));
        replicaA.Sync(project).FindById("wf-two").Should().NotBeNull();

        var fromReplicaB = replicaB.GetOrLoad(project);
        fromReplicaB.FindById("wf-one").Should().BeNull();
        fromReplicaB.FindById("wf-two").Should().NotBeNull();
    }

    [Fact]
    public void ReviewPolicyRegistry_ReloadsWhenSharedPolicyFilesChangeAcrossInstances()
    {
        var project = CreateProject();
        var policyDir = Path.Combine(_root, ".agentweaver", "review-policies");
        Directory.CreateDirectory(policyDir);
        File.WriteAllText(Path.Combine(policyDir, "custom.yaml"), ReviewPolicyYaml("policy-one"));

        var replicaA = new ReviewPolicyRegistry();
        var replicaB = new ReviewPolicyRegistry();
        replicaA.GetOrLoad(project).FindByName("policy-one").Should().NotBeNull();
        replicaB.GetOrLoad(project).FindByName("policy-one").Should().NotBeNull();

        File.WriteAllText(Path.Combine(policyDir, "custom.yaml"), ReviewPolicyYaml("policy-two"));
        replicaA.Sync(project).FindByName("policy-two").Should().NotBeNull();

        var fromReplicaB = replicaB.GetOrLoad(project);
        fromReplicaB.FindByName("policy-one").Should().BeNull();
        fromReplicaB.FindByName("policy-two").Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Project CreateProject() => new()
    {
        Id = ProjectId.New(),
        Name = "Replica Coherence",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = _root,
        DefaultBranch = "main",
        Owner = "alice",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static string WorkflowYaml(string id) =>
        $$"""
        id: {{id}}
        name: {{id}}
        trigger:
          type: manual
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

    private static string ReviewPolicyYaml(string name) =>
        $$"""
        name: {{name}}
        steps:
          - kind: human-review
            label: Human Review
        """;
}
