using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafExecutionPlannerTests
{
    [Fact]
    public void OpenWorkFrontierIsBoundedByConfiguredChildSlots()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("second", outputs: ["src/second.cs"]),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var noSlots = MafExecutionPlanner.BuildFrontier(
            snapshot, MafExecutionProgress.Empty, 0, 0, 0, 2, _ => false);

        Assert.Empty(noSlots.ReadyActions);
        Assert.Empty(noSlots.UnavailableExecutorStepIds);
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, MafExecutionProgress.Empty, 0, 0, 8, 2, _ => false);

        Assert.Equal(["implementation", "second"], frontier.ReadyActions
            .Select(action => action.WorkItem!.Id).ToArray());
        Assert.All(frontier.ReadyActions, action => Assert.Equal(WorkflowStepMode.Open, action.Step.Mode));
        Assert.Empty(frontier.UnavailableExecutorStepIds);
    }

    [Fact]
    public void OpenWorkDependenciesAndChildConcurrencyAreEnforced()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("second", outputs: ["src/second.cs"]),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var progress = new MafExecutionProgress(
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("implementation", MafExecutionTaskStatus.Succeeded)
                .Add("second", MafExecutionTaskStatus.Running),
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal));

        var frontier = MafExecutionPlanner.BuildFrontier(snapshot, progress, 1, 1, 8, 2, _ => false);

        Assert.Equal(["followup"], frontier.ReadyActions
            .Select(action => action.WorkItem!.Id).ToArray());
    }

    [Fact]
    public void FailedDependenciesBlockDescendantsAndAreReported()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var progress = new MafExecutionProgress(
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("implementation", MafExecutionTaskStatus.Failed),
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal));

        var frontier = MafExecutionPlanner.BuildFrontier(snapshot, progress, 1, 0, 8, 2, _ => true);

        Assert.Empty(frontier.ReadyActions);
        Assert.Equal(2, frontier.FailedDependencyIds.Length);
        Assert.Contains("followup", frontier.FailedDependencyIds);
        Assert.Contains("review", frontier.FailedDependencyIds);
        Assert.False(frontier.IsComplete);
    }

    [Fact]
    public void PlatformGateIsASeparateExecutorActionAndOnlyAfterItsJoin()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("second", outputs: ["src/second.cs"]));
        var beforeJoin = MafExecutionPlanner.BuildFrontier(
            snapshot, MafExecutionProgress.Empty, 0, 0, 8, 2, _ => true);
        Assert.DoesNotContain(beforeJoin.ReadyActions, action => action.Step.Mode == WorkflowStepMode.Platform);

        var afterJoin = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems
                .Add("implementation", MafExecutionTaskStatus.Succeeded)
                .Add("second", MafExecutionTaskStatus.Succeeded)
        };
        var afterJoinFrontier = MafExecutionPlanner.BuildFrontier(snapshot, afterJoin, 2, 0, 8, 2, _ => true);

        var platform = Assert.Single(afterJoinFrontier.ReadyActions);
        Assert.Equal(WorkflowStepMode.Platform, platform.Step.Mode);
        Assert.Equal(WorkflowPlatformGate.IndependentReview, platform.Step.PlatformGate);
        Assert.Null(platform.WorkItem);
    }

    [Fact]
    public void MissingPlatformExecutorLeavesGateUnavailableAndWorkflowIncomplete()
    {
        var snapshot = CreateSnapshot(WorkflowTestData.Item("implementation"));
        var afterJoin = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems
                .Add("implementation", MafExecutionTaskStatus.Succeeded)
        };

        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, afterJoin, 1, 0, 8, 2, _ => false);

        Assert.Empty(frontier.ReadyActions);
        Assert.Equal(new[] { "review" }, frontier.UnavailableExecutorStepIds.ToArray());
        Assert.False(frontier.IsComplete);
        Assert.Empty(afterJoin.NonModelSteps);
    }

    [Fact]
    public void InvalidConcurrencyAndUnknownProgressFailClosed()
    {
        var snapshot = CreateSnapshot(WorkflowTestData.Item("implementation"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MafExecutionPlanner.BuildFrontier(snapshot, MafExecutionProgress.Empty, 0, 0, 8, 0, _ => true));

        var unknownProgress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add("unplanned", MafExecutionTaskStatus.Succeeded)
        };
        Assert.Throws<ArgumentException>(() =>
            MafExecutionPlanner.BuildFrontier(snapshot, unknownProgress, 0, 0, 8, 1, _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MafExecutionPlanner.BuildFrontier(snapshot, MafExecutionProgress.Empty, 9, 0, 8, 1, _ => true));
    }

    [Fact]
    public void FailedChildBlocksItsDependentsButDoesNotBlockIndependentFrontierWork()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("second", outputs: ["src/second.cs"]),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var progress = new MafExecutionProgress(
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("implementation", MafExecutionTaskStatus.Failed),
            ImmutableDictionary<string, MafExecutionTaskStatus>.Empty
                .WithComparers(StringComparer.Ordinal));

        var frontier = MafExecutionPlanner.BuildFrontier(snapshot, progress, 1, 0, 8, 2, _ => true);

        Assert.Equal("second", Assert.Single(frontier.ReadyActions).WorkItem!.Id);
        Assert.Contains("followup", frontier.FailedDependencyIds);
        Assert.Contains("review", frontier.FailedDependencyIds);
    }

    [Fact]
    public void TotalChildLimitStopsDispatchEvenWhenConcurrencyHasCapacity()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("second", outputs: ["src/second.cs"]));
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, MafExecutionProgress.Empty, 8, 0, 8, 2, _ => false);

        Assert.Empty(frontier.ReadyActions);
    }

    [Fact]
    public void ActiveChildrenOutsideThisPlanConsumeConfiguredConcurrency()
    {
        var snapshot = CreateSnapshot(WorkflowTestData.Item("implementation"));
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, MafExecutionProgress.Empty, 1, 2, 8, 2, _ => false);

        Assert.Empty(frontier.ReadyActions);
    }

    [Theory]
    [InlineData(CoordinationLifecycleState.Completed)]
    [InlineData(CoordinationLifecycleState.Archived)]
    public void CheckpointedSucceededChildSatisfiesItsJoinAndUnblocksDependents(
        CoordinationLifecycleState lifecycle)
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Succeeded),
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                parent.ProjectId,
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            lifecycle,
            2);

        var reconciled = MafExecutionPlanner.ReconcileChildren(
            snapshot,
            progress,
            parent,
            [child],
            executionFence: 2,
            joinedResultIds: new HashSet<string>(["implementation"], StringComparer.Ordinal));
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, reconciled, 1, 0, 8, 2, _ => true);

        Assert.Equal(MafExecutionTaskStatus.Succeeded, reconciled.WorkItems["implementation"]);
        Assert.Contains(frontier.ReadyActions, action => action.WorkItem?.Id == "followup");
        Assert.DoesNotContain("followup", frontier.FailedDependencyIds);
    }

    [Fact]
    public void CompletedChildWithoutCheckpointedResultCannotSatisfyJoin()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Running)
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                parent.ProjectId,
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            CoordinationLifecycleState.Completed,
            2);

        var reconciled = MafExecutionPlanner.ReconcileChildren(
            snapshot, progress, parent, [child], executionFence: 2);
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, reconciled, 1, 0, 8, 2, _ => true);

        Assert.Equal(MafExecutionTaskStatus.Indeterminate, reconciled.WorkItems["implementation"]);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.WorkItem?.Id == "followup");
        Assert.Contains("followup", frontier.FailedDependencyIds);
        Assert.False(frontier.IsComplete);
    }

    [Fact]
    public void PendingDispatchReplayPreservesRunningChildUntilJoinIsCheckpointed()
    {
        var snapshot = CreateSnapshot(WorkflowTestData.Item("implementation"));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Running)
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                parent.ProjectId,
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            CoordinationLifecycleState.Completed,
            2);

        var reconciled = MafExecutionPlanner.ReconcileChildren(
            snapshot,
            progress,
            parent,
            [child],
            executionFence: 2,
            pendingDispatchIds: new HashSet<string>(["implementation"], StringComparer.Ordinal));

        Assert.Equal(MafExecutionTaskStatus.Running, reconciled.WorkItems["implementation"]);
    }

    [Fact]
    public void DetachedChildPreservesRunningCheckpointWithoutJoining()
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Running)
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                parent.ProjectId,
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            CoordinationLifecycleState.Completed,
            2)
        {
            Detached = true
        };

        var reconciled = MafExecutionPlanner.ReconcileChildren(
            snapshot, progress, parent, [child], executionFence: 2);
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, reconciled, 1, 0, 8, 2, _ => true);

        Assert.Equal(MafExecutionTaskStatus.Running, reconciled.WorkItems["implementation"]);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.WorkItem?.Id == "followup");
    }

    [Theory]
    [InlineData(CoordinationLifecycleState.Cancelled, 2)]
    [InlineData(CoordinationLifecycleState.Completed, 1)]
    public void CancelledOrStaleChildCannotSatisfyJoin(
        CoordinationLifecycleState lifecycle,
        long childFence)
    {
        var snapshot = CreateSnapshot(
            WorkflowTestData.Item("implementation"),
            WorkflowTestData.Item("followup", dependsOn: ["implementation"]));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Running),
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                parent.ProjectId,
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            lifecycle,
            childFence);

        var reconciled = MafExecutionPlanner.ReconcileChildren(
            snapshot, progress, parent, [child], executionFence: 2);
        var frontier = MafExecutionPlanner.BuildFrontier(
            snapshot, reconciled, 1, 0, 8, 2, _ => true);

        Assert.Equal(MafExecutionTaskStatus.Indeterminate, reconciled.WorkItems["implementation"]);
        Assert.DoesNotContain(frontier.ReadyActions, action => action.WorkItem?.Id == "followup");
        Assert.Contains("followup", frontier.FailedDependencyIds);
        Assert.False(frontier.IsComplete);
    }

    [Fact]
    public void ChildFromAnotherParentScopeIsRejected()
    {
        var snapshot = CreateSnapshot(WorkflowTestData.Item("implementation"));
        var parent = new SessionIdentity("project-1", "run-1", "root");
        var progress = MafExecutionProgress.Empty with
        {
            WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                "implementation", MafExecutionTaskStatus.Running)
        };
        var child = new MafExecutionChildSnapshot(
            "implementation",
            new SessionIdentity(
                "different-project",
                parent.RunId,
                MafExecutionPlanner.CreateChildSessionId(parent, snapshot.Plan.Id, "implementation")),
            CoordinationLifecycleState.Completed,
            2);

        var exception = Assert.Throws<CoordinationException>(() =>
        {
            MafExecutionPlanner.ReconcileChildren(snapshot, progress, parent, [child], executionFence: 2);
        });

        Assert.Equal("maf_execution_child_identity_conflict", exception.Code);
    }

    private static WorkPlanSnapshot CreateSnapshot(params WorkPlanItem[] items)
    {
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("review", 1, ["implement"]));
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            WorkflowTestData.Plan(items),
            WorkflowTestData.SelectionContext());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }
}
