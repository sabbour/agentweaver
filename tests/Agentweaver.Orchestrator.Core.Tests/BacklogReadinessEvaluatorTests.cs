using System.Collections.Immutable;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogReadinessEvaluatorTests
{
    [Fact]
    public void AllowsBacklogTaskWithNoPrerequisitesAndCompletedPrerequisites()
    {
        var emptyGraph = CreateGraph(withDependency: false);
        var noDependencies = BacklogReadinessEvaluator.Evaluate(
            emptyGraph, Ref("feature"), expectedGraphRevision: 4,
            expectedTaskRevision: 8, []);
        var graph = CreateGraph();
        var merged = BacklogReadinessEvaluator.Evaluate(
            graph, Ref("feature"), expectedGraphRevision: 5,
            expectedTaskRevision: 8, [Prerequisite()]);
        var completedCaptureOnly = Evaluate(
            graph, [Prerequisite(state: BacklogPrerequisiteExecutionState.Completed)]);
        var noProofRequired = Evaluate(
            graph,
            [Prerequisite(
                state: BacklogPrerequisiteExecutionState.Completed,
                outputProofState: BacklogOutputProofState.NotRequired)]);

        Assert.True(noDependencies.IsSuccess);
        Assert.True(noDependencies.Value!.IsReady);
        Assert.True(merged.IsSuccess);
        Assert.True(merged.Value!.IsReady);
        Assert.True(completedCaptureOnly.Value!.IsReady);
        Assert.Equal("proof:foundation@rev-3", merged.Value.Prerequisites[0].OutputProofReference);
        Assert.True(noProofRequired.Value!.IsReady);
    }

    [Theory]
    [InlineData(BacklogPrerequisiteExecutionState.Missing, BacklogIssueCode.PrerequisiteMissing)]
    [InlineData(BacklogPrerequisiteExecutionState.Pending, BacklogIssueCode.PrerequisiteNotMerged)]
    [InlineData(BacklogPrerequisiteExecutionState.Failed, BacklogIssueCode.PrerequisiteFailed)]
    [InlineData(BacklogPrerequisiteExecutionState.Archived, BacklogIssueCode.PrerequisiteArchived)]
    [InlineData(BacklogPrerequisiteExecutionState.Cancelled, BacklogIssueCode.PrerequisiteCancelled)]
    [InlineData(BacklogPrerequisiteExecutionState.Delegated, BacklogIssueCode.PrerequisiteDelegated)]
    [InlineData(BacklogPrerequisiteExecutionState.Indeterminate, BacklogIssueCode.PrerequisiteIndeterminate)]
    [InlineData(BacklogPrerequisiteExecutionState.Stale, BacklogIssueCode.StalePrerequisite)]
    public void BlocksPrerequisitesWithoutSuccessfulCurrentExecution(
        BacklogPrerequisiteExecutionState state,
        BacklogIssueCode expectedCode)
    {
        var result = Evaluate([Prerequisite(state: state)]);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsReady);
        Assert.Contains(result.Value.Blockers, blocker => blocker.Code == expectedCode);
    }

    [Theory]
    [InlineData(BacklogPrerequisiteExecutionState.Merged, BacklogOutputProofState.NotRequired, BacklogIssueCode.OutputProofMissing)]
    [InlineData(BacklogPrerequisiteExecutionState.Merged, BacklogOutputProofState.Missing, BacklogIssueCode.OutputProofMissing)]
    [InlineData(BacklogPrerequisiteExecutionState.Merged, BacklogOutputProofState.Indeterminate, BacklogIssueCode.OutputProofIndeterminate)]
    [InlineData(BacklogPrerequisiteExecutionState.Merged, BacklogOutputProofState.Stale, BacklogIssueCode.OutputProofStale)]
    [InlineData(BacklogPrerequisiteExecutionState.Completed, BacklogOutputProofState.Missing, BacklogIssueCode.OutputProofMissing)]
    [InlineData(BacklogPrerequisiteExecutionState.Completed, BacklogOutputProofState.Indeterminate, BacklogIssueCode.OutputProofIndeterminate)]
    [InlineData(BacklogPrerequisiteExecutionState.Completed, BacklogOutputProofState.Stale, BacklogIssueCode.OutputProofStale)]
    public void BlocksSuccessfulPrerequisitesWithoutCurrentRequiredOutputProof(
        BacklogPrerequisiteExecutionState state,
        BacklogOutputProofState proofState,
        BacklogIssueCode expectedCode)
    {
        var result = Evaluate([Prerequisite(state: state, outputProofState: proofState)]);

        Assert.False(result.Value!.IsReady);
        Assert.Contains(result.Value.Blockers, blocker => blocker.Code == expectedCode);
    }

    [Fact]
    public void BlocksCompletedEvidenceForArchivedOrUnclaimedPrerequisiteTasks()
    {
        var archived = Evaluate(CreateGraph(prerequisiteArchived: true), [Prerequisite()]);
        var unclaimed = Evaluate(
            CreateGraph(prerequisiteState: BacklogTaskState.Ready),
            [Prerequisite(state: BacklogPrerequisiteExecutionState.Completed)]);

        Assert.Contains(archived.Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.PrerequisiteArchived);
        Assert.Contains(unclaimed.Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.StalePrerequisite);
    }

    [Fact]
    public void BlocksIneligibleTasksMissingEvidenceAndUnexpectedEvidence()
    {
        var claimed = CreateGraph(taskState: BacklogTaskState.Claimed);
        var archived = CreateGraph(taskArchived: true);
        var pending = CreateGraph(taskPending: true);
        var missing = Evaluate([]);
        var unexpected = BacklogReadinessEvaluator.Evaluate(
            CreateGraph(withDependency: false), Ref("feature"), 4, 8, [Prerequisite()]);

        Assert.Contains(Evaluate(claimed, [Prerequisite()]).Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.TaskNotEligible);
        Assert.Contains(Evaluate(archived, [Prerequisite()]).Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.TaskNotEligible);
        Assert.Contains(Evaluate(pending, [Prerequisite()]).Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.TaskNotEligible);
        Assert.Contains(missing.Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.MissingPrerequisiteSnapshot);
        Assert.Contains(unexpected.Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.UnexpectedPrerequisiteSnapshot);
    }

    [Fact]
    public void RejectsStaleGraphTaskAndPrerequisiteGenerations()
    {
        var staleGraph = BacklogReadinessEvaluator.Evaluate(
            CreateGraph(), Ref("feature"), expectedGraphRevision: 4,
            expectedTaskRevision: 8, [Prerequisite()]);
        var staleTask = BacklogReadinessEvaluator.Evaluate(
            CreateGraph(), Ref("feature"), expectedGraphRevision: 5,
            expectedTaskRevision: 7, [Prerequisite()]);
        var stalePrerequisite = Evaluate([Prerequisite(taskRevision: 2)]);

        Assert.Contains(staleGraph.Issues, issue => issue.Code == BacklogIssueCode.StaleGraphRevision);
        Assert.Contains(staleTask.Issues, issue => issue.Code == BacklogIssueCode.StaleTaskRevision);
        Assert.Contains(stalePrerequisite.Value!.Blockers,
            issue => issue.Code == BacklogIssueCode.StalePrerequisite);
    }

    [Fact]
    public void RejectsCrossProjectDuplicateAndMalformedPrerequisiteSnapshots()
    {
        var graph = CreateGraph();
        var crossProject = Evaluate([new BacklogPrerequisiteExecutionSnapshot(
            new BacklogTaskReference("project-b", "foundation"), 3, 1,
            BacklogPrerequisiteExecutionState.Merged, BacklogOutputProofState.NotRequired, null)]);
        var duplicate = BacklogReadinessEvaluator.Evaluate(
            graph, Ref("feature"), 5, 8, [Prerequisite(), Prerequisite()]);
        var invalidProof = Evaluate([Prerequisite(outputProofState: BacklogOutputProofState.Verified,
            outputProofReference: null)]);

        Assert.Contains(crossProject.Issues, issue =>
            issue.Code == BacklogIssueCode.CrossProjectDependency);
        Assert.Contains(duplicate.Issues, issue =>
            issue.Code == BacklogIssueCode.DuplicatePrerequisiteSnapshot);
        Assert.Contains(invalidProof.Issues, issue =>
            issue.Code == BacklogIssueCode.InvalidPrerequisiteSnapshot);
    }

    private static BacklogCoreResult<BacklogReadinessEvaluation> Evaluate(
        ImmutableArray<BacklogPrerequisiteExecutionSnapshot> prerequisites) =>
        Evaluate(CreateGraph(), prerequisites);

    private static BacklogCoreResult<BacklogReadinessEvaluation> Evaluate(
        BacklogDependencyGraph graph,
        ImmutableArray<BacklogPrerequisiteExecutionSnapshot> prerequisites) =>
        BacklogReadinessEvaluator.Evaluate(
            graph, Ref("feature"), graph.Revision, 8, prerequisites);

    private static BacklogDependencyGraph CreateGraph(
        bool withDependency = true,
        BacklogTaskState taskState = BacklogTaskState.Backlog,
        bool taskArchived = false,
        bool taskPending = false,
        BacklogTaskState prerequisiteState = BacklogTaskState.Claimed,
        bool prerequisiteArchived = false,
        bool prerequisitePending = false)
    {
        var snapshot = new BacklogDependencyGraphSnapshot(
            "project-a",
            withDependency ? 5 : 4,
            [
                new BacklogTaskSnapshot(Ref("feature"), 8, taskState, taskArchived, taskPending),
                new BacklogTaskSnapshot(
                    Ref("foundation"), 3, prerequisiteState, prerequisiteArchived, prerequisitePending)
            ],
            withDependency
                ? [new BacklogDependencyEdge(Ref("feature"), Ref("foundation"))]
                : []);
        return BacklogDependencyGraph.Create(snapshot).Value!;
    }

    private static BacklogPrerequisiteExecutionSnapshot Prerequisite(
        BacklogPrerequisiteExecutionState state = BacklogPrerequisiteExecutionState.Merged,
        long taskRevision = 3,
        BacklogOutputProofState outputProofState = BacklogOutputProofState.Verified,
        string? outputProofReference = "proof:foundation@rev-3") =>
        new(Ref("foundation"), taskRevision, 12, state, outputProofState,
            outputProofState == BacklogOutputProofState.Verified ? outputProofReference : null);

    private static BacklogTaskReference Ref(string taskId) =>
        new("project-a", taskId);
}
