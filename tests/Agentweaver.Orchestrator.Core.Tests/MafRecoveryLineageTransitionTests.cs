using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafRecoveryLineageTransitionTests
{
    [Fact]
    public void NormalSuccessorPreservesRecoveryLineage()
    {
        var lineage = new MafCheckpointRecoveryLineage(
            "source", 1, new string('a', 64), 1, Guid.NewGuid(), 2);
        var recovered = new MafExecutionCheckpoint(2, "plan", 2, MafExecutionProgress.Empty)
        {
            RecoveryLineage = lineage
        };
        var successor = recovered with { Revision = 3 };

        MafExecutionCheckpointContract.ValidateTransition(recovered, successor);
        Assert.Equal(lineage, MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(successor)).RecoveryLineage);

        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            recovered, successor with { RecoveryLineage = null }));
        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            recovered, successor with { RecoveryLineage = lineage with { RecoveryOperationId = Guid.NewGuid() } }));
        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.ValidateTransition(
            recovered with { RecoveryLineage = null }, successor));
    }

    [Fact]
    public void ChildBoundaryRejectsRetainedRubberduckFailureBeforeAdmission()
    {
        var child = new MafExecutionDispatchIntent("child", "session", Guid.NewGuid(), new string('a', 64));
        var empty = new MafExecutionCheckpoint(1, "plan", 1, MafExecutionProgress.Empty);
        var checkpoint = empty with
        {
            PendingDispatches = empty.PendingDispatches.Add(child.AssociationId, child),
            RubberduckFailureCodes = empty.RubberduckFailureCodes.Add("review", "runtime_owner_unavailable")
        };

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireNoChildJoinPlatformHistory(checkpoint));
        Assert.Equal("checkpoint_recovery_boundary_unsupported", error.Code);
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public void ChildBoundaryAllowsOnlyOneRunningOpenPullRequestAsAnOwnerVerifiedCandidate()
    {
        var source = MafBuildTestSourceInputTests.Fixture().Run;
        var workspace = new SourceControlPullRequestWorkspaceLocator(
            "workspace", Guid.NewGuid(), new string('a', 40),
            "feature/agent-change", new string('c', 40), source.Pin.DefaultBranch);
        var intent = MafOpenPullRequestContract.CreateIntent(
            Guid.NewGuid(), source.Identity, "open-pr-intent", 2, "plan", "publish",
            1, source.Pin.AcceptedRun.Fence, source.Pin.AcceptedRun.AcceptedSelectionHash,
            source.Pin, workspace, 1);
        var empty = new MafExecutionCheckpoint(2, "plan", 1, MafExecutionProgress.Empty)
        {
            Progress = MafExecutionProgress.Empty with
            {
                NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                    intent.StepId, MafExecutionTaskStatus.Running)
            },
            OpenPullRequestIntents =
                ImmutableDictionary<string, MafExecutionOpenPullRequestIntent>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(intent.StepId, intent)
        };

        Assert.True(CoordinationOwnerStore.RequireNoChildJoinPlatformHistory(
            empty,
            hasRegisteredChild: true,
            allowCommittedOpenPullRequest: true));
        Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireNoChildJoinPlatformHistory(
                empty, hasRegisteredChild: true));
        var second = MafOpenPullRequestContract.CreateIntent(
            Guid.NewGuid(), source.Identity, "second-open-pr", 3, "plan", "publish-2",
            1, source.Pin.AcceptedRun.Fence, source.Pin.AcceptedRun.AcceptedSelectionHash,
            source.Pin, workspace, 1);
        var multiple = empty with
        {
            Revision = 3,
            Progress = empty.Progress with
            {
                NonModelSteps = empty.Progress.NonModelSteps.Add(
                    second.StepId, MafExecutionTaskStatus.Running)
            },
            OpenPullRequestIntents = empty.OpenPullRequestIntents.Add(second.StepId, second)
        };
        Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireNoChildJoinPlatformHistory(
                multiple,
                hasRegisteredChild: true,
                allowCommittedOpenPullRequest: true));
        var otherPlatformProgress = empty with
        {
            Revision = 3,
            Progress = empty.Progress with
            {
                NonModelSteps = empty.Progress.NonModelSteps.Add(
                    "merge", MafExecutionTaskStatus.Succeeded)
            }
        };
        Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireNoChildJoinPlatformHistory(
                otherPlatformProgress,
                hasRegisteredChild: true,
                allowCommittedOpenPullRequest: true));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("work-item")]
    [InlineData("fixed-work")]
    public void GateBoundaryRejectsCompletedChildEvidence(string evidence)
    {
        var empty = new MafExecutionCheckpoint(1, "plan", 1, MafExecutionProgress.Empty);
        var checkpoint = evidence switch
        {
            "result" => empty with { Results = empty.Results.Add("child", "result") },
            "work-item" => empty with
            {
                Progress = empty.Progress with
                {
                    WorkItems = empty.Progress.WorkItems.Add("child", MafExecutionTaskStatus.Succeeded)
                }
            },
            _ => empty with
            {
                Progress = empty.Progress with
                {
                    FixedWorkItems = empty.Progress.FixedWorkItems.Add("child", MafExecutionTaskStatus.Succeeded)
                }
            }
        };

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireGateCheckpointBoundary(checkpoint));
        Assert.Equal("checkpoint_recovery_boundary_unsupported", error.Code);
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public void GateBoundaryAllowsUnstartedChildWork()
    {
        var empty = new MafExecutionCheckpoint(1, "plan", 1, MafExecutionProgress.Empty);
        var checkpoint = empty with
        {
            Progress = empty.Progress with
            {
                WorkItems = empty.Progress.WorkItems.Add("child", MafExecutionTaskStatus.Pending)
            }
        };

        CoordinationOwnerStore.RequireGateCheckpointBoundary(checkpoint);
    }

    [Fact]
    public void PendingRecoveryProgressRequiresItsCurrentOwnerFence()
    {
        var status = new CheckpointRecoveryStatus(
            "pending", "source", 1, 4, 5, Guid.NewGuid(), 1);
        var operation = new RunExecutionTransitionResult(
            Guid.NewGuid(), new SessionIdentity("project", "run", "root"),
            "active", "active", 4, 5, 0, 1, 2, 1, 2, null, null, null, null, [])
        {
            CheckpointRecovery = status
        };

        CoordinationOwnerStore.RequireCurrentCheckpointRecoveryOwner(
            operation, status, 5, "active", 5, "active");

        foreach (var owner in new[]
        {
            (RunFence: 6L, RunState: "active", RootFence: 6L, RootState: "active"),
            (RunFence: 6L, RunState: "cancelled", RootFence: 6L, RootState: "cancelled"),
            (RunFence: 5L, RunState: "active", RootFence: 4L, RootState: "active"),
            (RunFence: 5L, RunState: "completed", RootFence: 5L, RootState: "completed")
        })
        {
            var error = Assert.Throws<CoordinationException>(() =>
                CoordinationOwnerStore.RequireCurrentCheckpointRecoveryOwner(
                    operation, status, owner.RunFence, owner.RunState, owner.RootFence, owner.RootState));
            Assert.Equal("checkpoint_recovery_progress_conflict", error.Code);
        }

        Assert.Throws<CoordinationException>(() =>
            CoordinationOwnerStore.RequireCurrentCheckpointRecoveryOwner(
                operation with { ExecutionFence = 6 }, status, 5, "active", 5, "active"));
    }
}
