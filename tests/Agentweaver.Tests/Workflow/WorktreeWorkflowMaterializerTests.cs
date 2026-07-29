using Agentweaver.Api.Git;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Workflow;

/// <summary>
/// Regression coverage for #597: selected workflows should follow the skills-style delivery pattern,
/// materialized directly into the run worktree for the agent to inspect, but excluded from git so
/// they do not pollute repository history.
/// </summary>
public sealed class WorktreeWorkflowMaterializerTests : IDisposable
{
    private readonly string _repoPath = MakeTempDir("repo");
    private readonly string _basePath = MakeTempDir("worktrees");

    public WorktreeWorkflowMaterializerTests()
    {
        Repository.Init(_repoPath);
        using var repo = new Repository(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "readme.txt"), "initial");
        Directory.CreateDirectory(Path.Combine(_repoPath, ".agentweaver", "workflows"));
        File.WriteAllText(
            Path.Combine(_repoPath, ".agentweaver", "workflows", "default.yaml"),
            WorkflowDefinitionYamlSerializer.Serialize(DefaultWorkflow("tracked-default")));
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", sig, sig);
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");
    }

    [Fact]
    public void TryMaterialize_CustomWorkflow_WritesFileIntoWorktree_WithoutCommittingIt()
    {
        var manager = CreateWorktreeManager();
        var runId = RunId.New();
        var worktree = manager.AddWorktree(_repoPath, "main", runId);
        var materializer = new WorkflowWorktreeMaterializer(NullLogger<WorkflowWorktreeMaterializer>.Instance);

        materializer.TryMaterialize(worktree.WorktreePath, CustomWorkflow("custom-triage"));

        var workflowPath = Path.Combine(worktree.WorktreePath, ".agentweaver", "workflows", "custom-triage.yaml");
        File.Exists(workflowPath).Should().BeTrue();
        File.ReadAllText(workflowPath).Should().Contain("id: custom-triage");

        File.WriteAllText(Path.Combine(worktree.WorktreePath, "feature.txt"), "deliverable");
        manager.CommitChanges(worktree.WorktreePath, runId);

        using var committed = new Repository(worktree.WorktreePath);
        ResolveTreeEntry(committed.Head.Tip!.Tree, "feature.txt").Should().NotBeNull();
        ResolveTreeEntry(committed.Head.Tip.Tree, ".agentweaver/workflows/custom-triage.yaml")
            .Should().BeNull("materialized workflow files are run-local context, not repository history");
    }

    [Fact]
    public void TryMaterialize_TrackedWorkflow_DoesNotOverwriteRepositoryOwnedFile()
    {
        var manager = CreateWorktreeManager();
        var worktree = manager.AddWorktree(_repoPath, "main", RunId.New());
        var materializer = new WorkflowWorktreeMaterializer(NullLogger<WorkflowWorktreeMaterializer>.Instance);
        var trackedPath = Path.Combine(worktree.WorktreePath, ".agentweaver", "workflows", "default.yaml");
        var original = File.ReadAllText(trackedPath);

        materializer.TryMaterialize(worktree.WorktreePath, DefaultWorkflow("ephemeral-default"));

        File.ReadAllText(trackedPath).Should().Be(original);
        using var repo = new Repository(worktree.WorktreePath);
        repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            IncludeIgnored = true,
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs = true,
        }).Should().BeEmpty("tracked workflow files already in the repo must stay untouched");
    }

    private WorktreeManager CreateWorktreeManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = _basePath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
            })
            .Build();
        return new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    private static WorkflowDefinition CustomWorkflow(string id) => new()
    {
        Id = id,
        Name = "Custom Triage",
        Description = "Custom workflow for propagation coverage.",
        Version = "1.0",
        Start = "triage",
        Nodes =
        [
            new WorkflowNode
            {
                Id = "triage",
                Type = WorkflowNodeType.Prompt,
                Label = "Triage",
                Role = "agent",
                Kind = "live",
                Prompt = "Triage the issue.",
            },
            new WorkflowNode
            {
                Id = "done",
                Type = WorkflowNodeType.Terminal,
                Label = "Done",
                Role = "plumbing",
                Kind = "terminal",
            }
        ],
        Edges =
        [
            new WorkflowEdge { From = "triage", To = "done" }
        ],
    };

    private static WorkflowDefinition DefaultWorkflow(string name) => new()
    {
        Id = "default",
        Name = name,
        Description = "Tracked default workflow.",
        Version = "1.0",
        Start = "agent",
        Nodes =
        [
            new WorkflowNode
            {
                Id = "agent",
                Type = WorkflowNodeType.Prompt,
                Label = "Agent",
                Role = "agent",
                Kind = "live",
                Prompt = "Do the work.",
            },
            new WorkflowNode
            {
                Id = "done",
                Type = WorkflowNodeType.Terminal,
                Label = "Done",
                Role = "plumbing",
                Kind = "terminal",
            }
        ],
        Edges =
        [
            new WorkflowEdge { From = "agent", To = "done" }
        ],
    };

    private static TreeEntry? ResolveTreeEntry(Tree tree, string path)
    {
        TreeEntry? entry = null;
        Tree? current = tree;
        foreach (var segment in path.Split('/'))
        {
            if (current is null) return null;
            entry = current[segment];
            if (entry is null) return null;
            current = entry.TargetType == TreeEntryTargetType.Tree ? (Tree)entry.Target : null;
        }
        return entry;
    }

    private static string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-workflow-materialize-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _repoPath, _basePath })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
