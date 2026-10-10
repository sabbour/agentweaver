using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogTaskOperationsTests
{
    [Fact]
    public void AddingTaskAdvancesGraphRevisionAndRejectsDuplicateIdentity()
    {
        var graph = CreateGraph(graphRevision: 0);

        var added = graph.AddTask(Ref("feature"), expectedGraphRevision: 0);
        var duplicate = added.Value!.Graph.AddTask(Ref("feature"), expectedGraphRevision: 1);

        Assert.True(added.IsSuccess);
        Assert.True(added.Value!.Changed);
        Assert.Equal(1, added.Value.Graph.Revision);
        Assert.Equal(new BacklogTaskSnapshot(Ref("feature"), 0, BacklogTaskState.Backlog),
            Assert.Single(added.Value.Graph.Tasks));
        Assert.Contains(duplicate.Issues, issue => issue.Code == BacklogIssueCode.DuplicateTask);
    }

    [Fact]
    public void StateAndArchiveMutationsAdvanceRevisionsOnlyWhenTheyChangeState()
    {
        var graph = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Backlog));

        var stateChange = graph.SetTaskState(
            Ref("feature"), BacklogTaskState.Ready, expectedGraphRevision: 4, expectedTaskRevision: 8);
        var stateNoOp = stateChange.Value!.Graph.SetTaskState(
            Ref("feature"), BacklogTaskState.Ready, expectedGraphRevision: 5, expectedTaskRevision: 9);
        var archive = stateNoOp.Value!.Graph.ArchiveTask(
            Ref("feature"), expectedGraphRevision: 5, expectedTaskRevision: 9);
        var archiveNoOp = archive.Value!.Graph.ArchiveTask(
            Ref("feature"), expectedGraphRevision: 6, expectedTaskRevision: 10);

        Assert.True(stateChange.Value!.Changed);
        Assert.Equal(5, stateChange.Value.Graph.Revision);
        Assert.Equal(new BacklogTaskSnapshot(Ref("feature"), 9, BacklogTaskState.Ready),
            Assert.Single(stateChange.Value.Graph.Tasks));
        Assert.False(stateNoOp.Value!.Changed);
        Assert.Same(stateChange.Value.Graph, stateNoOp.Value.Graph);
        Assert.True(archive.Value!.Changed);
        Assert.Equal(6, archive.Value.Graph.Revision);
        Assert.Equal(new BacklogTaskSnapshot(Ref("feature"), 10, BacklogTaskState.Ready, IsArchived: true),
            Assert.Single(archive.Value.Graph.Tasks));
        Assert.False(archiveNoOp.Value!.Changed);
        Assert.Same(archive.Value.Graph, archiveNoOp.Value.Graph);
    }

    [Fact]
    public void RejectsStaleClaimedProvisionalAndArchivedTaskMutations()
    {
        var backlog = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Backlog));
        var staleGraph = backlog.ArchiveTask(Ref("feature"), 3, 8);
        var staleTask = backlog.ArchiveTask(Ref("feature"), 4, 7);
        var claim = backlog.SetTaskState(Ref("feature"), BacklogTaskState.Claimed, 4, 8);
        var claimed = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Claimed));
        var provisional = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(
                Ref("feature"), 8, BacklogTaskState.Backlog, IsAutomationInvocationPending: true));
        var archived = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Backlog, IsArchived: true));

        Assert.Contains(staleGraph.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Contains(staleTask.Issues, issue => issue.Code == BacklogIssueCode.StaleTaskRevision);
        Assert.Contains(claim.Issues, issue => issue.Code == BacklogIssueCode.TaskNotEditable);
        Assert.Contains(claimed.ArchiveTask(Ref("feature"), 4, 8).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
        Assert.Contains(provisional.ArchiveTask(Ref("feature"), 4, 8).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
        Assert.Contains(archived.SetTaskState(Ref("feature"), BacklogTaskState.Ready, 4, 8).Issues,
            issue => issue.Code == BacklogIssueCode.TaskNotEditable);
    }

    [Fact]
    public void RejectsTaskAndGraphRevisionExhaustion()
    {
        var taskRevision = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), long.MaxValue, BacklogTaskState.Backlog));
        var graphRevision = CreateGraph(
            graphRevision: long.MaxValue,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Backlog));

        Assert.Contains(taskRevision.ArchiveTask(Ref("feature"), 4, long.MaxValue).Issues,
            issue => issue.Code == BacklogIssueCode.TaskRevisionExhausted);
        Assert.Contains(graphRevision.ArchiveTask(Ref("feature"), long.MaxValue, 8).Issues,
            issue => issue.Code == BacklogIssueCode.GraphRevisionExhausted);
    }

    [Fact]
    public void ClaimRequiresCurrentReadinessAndAdvancesBothRevisions()
    {
        var graph = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Ready));

        var claim = graph.ClaimTask(Ref("feature"), 4, 8, []);

        Assert.True(claim.IsSuccess);
        Assert.True(claim.Value!.Readiness.IsReady);
        Assert.Equal(4, claim.Value.Readiness.GraphRevision);
        Assert.Equal(8, claim.Value.Readiness.TaskRevision);
        Assert.Equal(5, claim.Value.Graph.Revision);
        Assert.Equal(
            new BacklogTaskSnapshot(Ref("feature"), 9, BacklogTaskState.Claimed),
            Assert.Single(claim.Value.Graph.Tasks));
    }

    [Fact]
    public void ClaimRejectsMissingOrUnprovenPrerequisiteEvidenceWithoutChangingGraph()
    {
        var graph = BacklogDependencyGraph.Create(new BacklogDependencyGraphSnapshot(
            "project-a",
            4,
            [
                new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Ready),
                new BacklogTaskSnapshot(Ref("foundation"), 3, BacklogTaskState.Claimed)
            ],
            [new BacklogDependencyEdge(Ref("feature"), Ref("foundation"))])).Value!;

        var missingEvidence = graph.ClaimTask(Ref("feature"), 4, 8, []);
        var missingOutputProof = graph.ClaimTask(
            Ref("feature"),
            4,
            8,
            [
                new BacklogPrerequisiteExecutionSnapshot(
                    Ref("foundation"),
                    3,
                    2,
                    BacklogPrerequisiteExecutionState.Merged,
                    BacklogOutputProofState.Missing,
                    null)
            ]);

        Assert.Contains(missingEvidence.Issues,
            issue => issue.Code == BacklogIssueCode.MissingPrerequisiteSnapshot);
        Assert.Contains(missingOutputProof.Issues,
            issue => issue.Code == BacklogIssueCode.OutputProofMissing);
        Assert.Equal(4, graph.Revision);
        Assert.Equal(BacklogTaskState.Ready, graph.Tasks.Single(task =>
            task.Reference.TaskId == "feature").State);
    }

    [Fact]
    public void ClaimRejectsStaleInputsAndRevisionExhaustion()
    {
        var stale = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Ready));
        var taskRevisionExhausted = CreateGraph(
            graphRevision: 4,
            new BacklogTaskSnapshot(Ref("feature"), long.MaxValue, BacklogTaskState.Ready));
        var graphRevisionExhausted = CreateGraph(
            graphRevision: long.MaxValue,
            new BacklogTaskSnapshot(Ref("feature"), 8, BacklogTaskState.Ready));

        Assert.Contains(stale.ClaimTask(Ref("feature"), 3, 8, []).Issues,
            issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Contains(stale.ClaimTask(Ref("feature"), 4, 7, []).Issues,
            issue => issue.Code == BacklogIssueCode.StaleTaskRevision);
        Assert.Contains(taskRevisionExhausted.ClaimTask(
                Ref("feature"), 4, long.MaxValue, []).Issues,
            issue => issue.Code == BacklogIssueCode.TaskRevisionExhausted);
        Assert.Contains(graphRevisionExhausted.ClaimTask(
                Ref("feature"), long.MaxValue, 8, []).Issues,
            issue => issue.Code == BacklogIssueCode.GraphRevisionExhausted);
    }

    private static BacklogDependencyGraph CreateGraph(
        long graphRevision,
        params BacklogTaskSnapshot[] tasks) =>
        BacklogDependencyGraph.Create(new BacklogDependencyGraphSnapshot(
            "project-a", graphRevision, [.. tasks], [])).Value!;

    private static BacklogTaskReference Ref(string taskId) =>
        new("project-a", taskId);
}
