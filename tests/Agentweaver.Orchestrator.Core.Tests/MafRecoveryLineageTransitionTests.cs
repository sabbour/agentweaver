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
