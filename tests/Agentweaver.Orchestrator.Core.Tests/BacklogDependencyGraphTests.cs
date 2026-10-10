using System.Collections.Immutable;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogDependencyGraphTests
{
    [Fact]
    public void AddAndRemoveDependencyIncrementRevisionOnlyWhenGraphChanges()
    {
        var graph = CreateGraph();

        var added = graph.AddDependency(Ref("feature"), Ref("foundation"), expectedGraphRevision: 4);
        var repeated = added.Value!.Graph.AddDependency(
            Ref("feature"), Ref("foundation"), expectedGraphRevision: 5);
        var removed = repeated.Value!.Graph.RemoveDependency(
            Ref("feature"), Ref("foundation"), expectedGraphRevision: 5);
        var repeatedRemoval = removed.Value!.Graph.RemoveDependency(
            Ref("feature"), Ref("foundation"), expectedGraphRevision: 6);

        Assert.True(added.IsSuccess);
        Assert.True(added.Value!.Changed);
        Assert.Equal(5, added.Value.Graph.Revision);
        Assert.False(repeated.Value!.Changed);
        Assert.Same(added.Value.Graph, repeated.Value.Graph);
        Assert.Equal(5, repeated.Value.Graph.Revision);
        Assert.True(removed.Value!.Changed);
        Assert.Equal(6, removed.Value.Graph.Revision);
        Assert.False(repeatedRemoval.Value!.Changed);
        Assert.Equal(6, repeatedRemoval.Value.Graph.Revision);
    }

    [Fact]
    public void RejectsStaleRevisionSelfDependencyCrossProjectAndCycles()
    {
        var graph = CreateGraph();
        var stale = graph.AddDependency(Ref("feature"), Ref("foundation"), expectedGraphRevision: 3);
        var self = graph.AddDependency(Ref("feature"), Ref("feature"), expectedGraphRevision: 4);
        var crossProject = graph.AddDependency(
            Ref("feature"), new BacklogTaskReference("project-b", "foundation"), expectedGraphRevision: 4);
        var first = graph.AddDependency(Ref("feature"), Ref("foundation"), expectedGraphRevision: 4)
            .Value!.Graph;
        var cycle = first.AddDependency(Ref("foundation"), Ref("feature"), expectedGraphRevision: 5);

        Assert.Contains(stale.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Contains(self.Issues, issue => issue.Code == BacklogIssueCode.SelfDependency);
        Assert.Contains(crossProject.Issues, issue =>
            issue.Code == BacklogIssueCode.CrossProjectDependency);
        Assert.Contains(cycle.Issues, issue => issue.Code == BacklogIssueCode.DependencyCycle);
        Assert.Empty(graph.Dependencies);
    }

    [Fact]
    public void RejectsEditsToClaimedArchivedOrProvisionalTasks()
    {
        var claimed = CreateGraph(taskState: BacklogTaskState.Claimed);
        var archived = CreateGraph(taskArchived: true);
        var provisional = CreateGraph(taskPending: true);

        Assert.Contains(claimed.AddDependency(
                Ref("feature"), Ref("foundation"), expectedGraphRevision: 4).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
        Assert.Contains(archived.AddDependency(
                Ref("feature"), Ref("foundation"), expectedGraphRevision: 4).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
        Assert.Contains(provisional.AddDependency(
                Ref("feature"), Ref("foundation"), expectedGraphRevision: 4).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
    }

    [Fact]
    public void RejectsArchivedOrProvisionalPrerequisitesAndMalformedSeedGraphs()
    {
        var archivedPrerequisite = CreateGraph(prerequisiteArchived: true)
            .AddDependency(Ref("feature"), Ref("foundation"), expectedGraphRevision: 4);
        var provisionalPrerequisite = CreateGraph(prerequisitePending: true)
            .AddDependency(Ref("feature"), Ref("foundation"), expectedGraphRevision: 4);
        var cycle = BacklogDependencyGraph.Create(Snapshot(
            edges:
            [
                Edge("feature", "foundation"),
                Edge("foundation", "feature")
            ]));
        var crossProject = BacklogDependencyGraph.Create(Snapshot(
            edges: [new BacklogDependencyEdge(Ref("feature"), new BacklogTaskReference("other", "foundation"))]));

        Assert.Contains(archivedPrerequisite.Issues,
            issue => issue.Code == BacklogIssueCode.PrerequisiteUnavailable);
        Assert.Contains(provisionalPrerequisite.Issues,
            issue => issue.Code == BacklogIssueCode.PrerequisiteUnavailable);
        Assert.Contains(cycle.Issues, issue => issue.Code == BacklogIssueCode.DependencyCycle);
        Assert.Contains(crossProject.Issues,
            issue => issue.Code == BacklogIssueCode.CrossProjectDependency);
    }

    [Fact]
    public void ValidatesLongAcyclicGraphsWithoutRecursiveTraversal()
    {
        const int taskCount = 4_000;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(index => new BacklogTaskSnapshot(
                Ref($"task-{index}"), 0, BacklogTaskState.Backlog))
            .ToImmutableArray();
        var dependencies = Enumerable.Range(0, taskCount - 1)
            .Select(index => new BacklogDependencyEdge(
                Ref($"task-{index}"), Ref($"task-{index + 1}")))
            .ToImmutableArray();
        var graph = BacklogDependencyGraph.Create(
            new BacklogDependencyGraphSnapshot("project-a", 0, tasks, dependencies));

        Assert.True(graph.IsSuccess);
        Assert.Equal(taskCount - 1, graph.Value!.Dependencies.Length);
    }

    private static BacklogDependencyGraph CreateGraph(
        BacklogTaskState taskState = BacklogTaskState.Ready,
        BacklogTaskState prerequisiteState = BacklogTaskState.Ready,
        bool taskArchived = false,
        bool taskPending = false,
        bool prerequisiteArchived = false,
        bool prerequisitePending = false) =>
        BacklogDependencyGraph.Create(Snapshot(
            taskState,
            prerequisiteState,
            taskArchived,
            taskPending,
            prerequisiteArchived,
            prerequisitePending)).Value!;

    private static BacklogDependencyGraphSnapshot Snapshot(
        BacklogTaskState taskState = BacklogTaskState.Ready,
        BacklogTaskState prerequisiteState = BacklogTaskState.Ready,
        bool taskArchived = false,
        bool taskPending = false,
        bool prerequisiteArchived = false,
        bool prerequisitePending = false,
        ImmutableArray<BacklogDependencyEdge> edges = default) =>
        new(
            "project-a",
            4,
            [
                new BacklogTaskSnapshot(Ref("feature"), 8, taskState, taskArchived, taskPending),
                new BacklogTaskSnapshot(
                    Ref("foundation"), 3, prerequisiteState,
                    prerequisiteArchived, prerequisitePending)
            ],
            edges.IsDefault ? [] : edges);

    private static BacklogTaskReference Ref(string taskId) =>
        new("project-a", taskId);

    private static BacklogDependencyEdge Edge(string taskId, string prerequisiteId) =>
        new(Ref(taskId), Ref(prerequisiteId));
}
