using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogTaskContentTests
{
    [Fact]
    public void CapturePreservesContentAndNormalizesRepositoryIdentity()
    {
        var graph = Empty().AddTask(Ref("task"), Content("  Build it  "), 0).Value!.Graph;
        var task = Assert.Single(graph.Tasks);
        Assert.Equal("Build it", task.Title);
        Assert.Equal("Requirements", task.Description);
        Assert.Equal("owner/repo", task.RepositoryIssue!.Repository.FullName);
        Assert.Equal(0, task.Revision);
        Assert.Equal(1, graph.Revision);
        var restored = BacklogDependencyGraph.Create(JsonSerializer.Deserialize<BacklogDependencyGraphSnapshot>(
            JsonSerializer.Serialize(graph.Snapshot)));
        Assert.True(restored.IsSuccess);
        Assert.Equal(task, Assert.Single(restored.Value!.Tasks));
    }

    [Fact]
    public void ContentEditUsesBothRevisionsAndPreservesNoOpRevision()
    {
        var graph = Empty().AddTask(Ref("task"), Content(), 0).Value!.Graph;
        Assert.Contains(graph.SetTaskContent(Ref("task"), Content("New"), 0, 0).Issues,
            issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Contains(graph.SetTaskContent(Ref("task"), Content("New"), 1, 1).Issues,
            issue => issue.Code == BacklogIssueCode.StaleTaskRevision);
        var edit = graph.SetTaskContent(Ref("task"), Content("New"), 1, 0).Value!;
        Assert.True(edit.Changed);
        Assert.Equal(2, edit.Graph.Revision);
        Assert.Equal(1, Assert.Single(edit.Graph.Tasks).Revision);
        var noOp = edit.Graph.SetTaskContent(Ref("task"), Content("New"), 2, 1).Value!;
        Assert.False(noOp.Changed);
        Assert.Same(edit.Graph, noOp.Graph);
    }

    [Fact]
    public void IssueIdentityIsUniqueWithinProjectIncludingArchivedTasks()
    {
        var graph = Empty().AddTask(Ref("task"), Content(), 0).Value!.Graph;
        graph = graph.ArchiveTask(Ref("task"), 1, 0).Value!.Graph;
        Assert.Contains(graph.AddTask(Ref("duplicate"), Content(), 2).Issues,
            issue => issue.Code == BacklogIssueCode.DuplicateRepositoryIssue);
        Assert.True(Empty("other").AddTask(new("other", "task"), Content(), 0).IsSuccess);
        var other = graph.AddTask(Ref("other"), Content() with { RepositoryIssue = null }, 2).Value!.Graph;
        Assert.Contains(other.SetTaskContent(Ref("other"), Content(), 3, 0).Issues,
            issue => issue.Code == BacklogIssueCode.DuplicateRepositoryIssue);
    }

    [Fact]
    public void ClosingRemovesReadinessAndReopeningDoesNotScheduleWork()
    {
        var graph = Empty().AddTask(Ref("task"), Content(), 0).Value!.Graph;
        graph = graph.SetTaskState(Ref("task"), BacklogTaskState.Ready, 1, 0).Value!.Graph;
        graph = graph.SetTaskContent(Ref("task"),
            Content() with { Lifecycle = BacklogTaskLifecycle.Closed }, 2, 1).Value!.Graph;
        Assert.Equal(BacklogTaskState.Backlog, Assert.Single(graph.Tasks).State);
        Assert.Contains(graph.ClaimTask(Ref("task"), 3, 2, []).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEligible);
        Assert.False(graph.SetTaskState(Ref("task"), BacklogTaskState.Ready, 3, 2).IsSuccess);
        graph = graph.SetTaskContent(Ref("task"), Content(), 3, 2).Value!.Graph;
        Assert.Equal(BacklogTaskState.Backlog, Assert.Single(graph.Tasks).State);
    }

    [Fact]
    public void ClosingClaimedWorkPreservesItsExecutionState()
    {
        var graph = Empty().AddTask(Ref("task"), Content(), 0).Value!.Graph;
        graph = graph.ClaimTask(Ref("task"), 1, 0, []).Value!.Graph;
        graph = graph.SetTaskContent(Ref("task"),
            Content() with { Lifecycle = BacklogTaskLifecycle.Closed }, 2, 1).Value!.Graph;
        Assert.Equal(BacklogTaskState.Claimed, Assert.Single(graph.Tasks).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("line\nbreak")]
    public void CaptureRejectsInvalidTitles(string title) =>
        Assert.Contains(Empty().AddTask(Ref("task"), Content(title), 0).Issues,
            issue => issue.Code == BacklogIssueCode.InvalidTaskContent);

    [Fact]
    public void EnforcesExactContentBoundsAndIssueNumbers()
    {
        Assert.True(Empty().AddTask(Ref("task"),
            new BacklogTaskContent(new string('t', 512), new string('d', 65536)), 0).IsSuccess);
        Assert.False(Empty().AddTask(Ref("task"), Content(new string('t', 513)), 0).IsSuccess);
        Assert.False(Empty().AddTask(Ref("task"),
            Content() with { Description = new string('d', 65537) }, 0).IsSuccess);
        Assert.False(Empty().AddTask(Ref("task"),
            Content() with { Lifecycle = (BacklogTaskLifecycle)42 }, 0).IsSuccess);
        Assert.False(Empty().AddTask(Ref("task"),
            Content() with { RepositoryIssue = Content().RepositoryIssue! with { IssueNumber = 0 } }, 0).IsSuccess);
    }

    private static BacklogTaskContent Content(string title = "Build it") =>
        new(title, "Requirements", RepositoryIssue: new("github", new("Owner", "Repo"), 42));

    private static BacklogDependencyGraph Empty(string project = "project") =>
        BacklogDependencyGraph.Create(new(project, 0, [], [])).Value!;

    private static BacklogTaskReference Ref(string task) => new("project", task);
}
