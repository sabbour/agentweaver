using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestCommandResolutionTests
{
    [Fact]
    public void ResolvesOnlyThePersistedCommandAndExactProfileAfterCheckpointRecovery()
    {
        var (checkpoint, current, intent) = Fixture();
        var recovered = checkpoint with
        {
            State = MafExecutionCheckpointContract.Deserialize(
                MafExecutionCheckpointContract.Serialize(checkpoint.State))
        };

        var accepted = CoordinationEndpoints.ResolveBuildTestCommand(
            recovered, current, intent.CheckpointReference(), "tenant-1");

        Assert.Equal(intent.OperationId, accepted.OperationId);
        Assert.Equal(intent.CheckpointReference(), accepted.Checkpoint);
        Assert.Equal(intent.Command.Arguments.ToArray(), accepted.Arguments.ToArray());
        Assert.Equal(intent.ExecutionOptions.ImageReference, accepted.ExecutionOptions.ImageReference);
        Assert.Equal(intent.ToAcceptedCommand().ImmutableHash, accepted.ImmutableHash);
        Assert.Equal(accepted.ComputeImmutableHash(), accepted.ImmutableHash);
    }

    [Fact]
    public void ANewerModelCheckpointDoesNotAllocateAnotherOperationOrChangeCommandReference()
    {
        var (checkpoint, current, intent) = Fixture();
        checkpoint = checkpoint with { State = checkpoint.State with { Revision = 3 } };

        var accepted = CoordinationEndpoints.ResolveBuildTestCommand(
            checkpoint, current, intent.CheckpointReference(), "tenant-1");

        Assert.Equal(2, accepted.Checkpoint.CheckpointRevision);
        Assert.Equal(intent.OperationId, accepted.OperationId);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("checkpoint")]
    [InlineData("revision")]
    [InlineData("decision")]
    [InlineData("fence")]
    [InlineData("selection")]
    [InlineData("step")]
    [InlineData("tenant")]
    [InlineData("missing")]
    [InlineData("owner-version")]
    public void RejectsForeignStaleOrUnpersistedReferences(string fault)
    {
        var (checkpoint, current, intent) = Fixture();
        var reference = intent.CheckpointReference();
        reference = fault switch
        {
            "project" => reference with { ProjectId = "other-project" },
            "run" => reference with { RunId = "other-run" },
            "session" => reference with { SessionId = "other-root" },
            "checkpoint" => reference with { CheckpointId = "other-checkpoint" },
            "revision" => reference with { CheckpointRevision = 3 },
            "decision" => reference with { DecisionStateVersion = 4 },
            "fence" => reference with { ExecutionFence = 8 },
            "selection" => reference with { AcceptedSelectionHash = new string('B', 64) },
            "step" => reference with { StepId = "another-step" },
            _ => reference
        };
        if (fault == "missing")
            checkpoint = checkpoint with
            {
                State = checkpoint.State with { BuildTestIntents = checkpoint.State.BuildTestIntents.Clear() }
            };
        if (fault == "owner-version")
            current = current with { StateVersion = 4 };

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ResolveBuildTestCommand(
                checkpoint, current, reference, fault == "tenant" ? "other-tenant" : "tenant-1"));

        Assert.Equal("maf_execution_build_test_checkpoint_stale", failure.Code);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("provider")]
    public void CurrentConfirmedPlanMustStillBindTheExactCommandAndProvider(string change)
    {
        var (checkpoint, _, intent) = Fixture();
        var command = change == "command"
            ? intent.Command with { Arguments = ["build"] }
            : intent.Command;
        var current = Current(command, change == "provider" ? "other-provider" : "sandbox-1");

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ResolveBuildTestCommand(
                checkpoint, current, intent.CheckpointReference(), "tenant-1"));

        Assert.Equal("maf_execution_build_test_binding_stale", failure.Code);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("fence")]
    [InlineData("checkpoint")]
    public void AppendBindingMustMatchThePersistedOperation(string mismatch)
    {
        var (checkpoint, _, intent) = Fixture();

        Assert.Throws<ArgumentException>(() =>
            MafExecutionCheckpointContract.ValidateBuildTestBinding(
                checkpoint.State,
                mismatch == "identity" ? new("project-1", "other-run", "root") : intent.Identity,
                mismatch == "fence" ? 8 : 7,
                mismatch == "checkpoint" ? "other-checkpoint" : intent.CheckpointId));
    }

    private static (
        MafExecutionCheckpointSnapshot Checkpoint,
        CoordinatorDecisionCurrentState Current,
        MafExecutionBuildTestIntent Intent) Fixture()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var initial = new MafExecutionCheckpointSnapshot(
            new("root", "initial"), new(1, "plan-1", 3, MafExecutionProgress.Empty));
        var state = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        return (new(new("root", intent.CheckpointId), state), Current(intent.Command), intent);
    }

    private static CoordinatorDecisionCurrentState Current(
        WorkflowBuildTestCommand command,
        string providerId = "sandbox-1")
    {
        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Platform("build-test", 0, [], WorkflowPlatformGate.BuildTest) with
            {
                BuildTestCommand = command
            }));
        var pinned = WorkflowTestData.PinnedSandboxBinding(providerId, SandboxCapabilities.BuildTestCommandPod);
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            new("plan-1", workflow.Definition.Id, workflow.Definition.Revision, workflow.Definition.CatalogVersion, []),
            new([], pinned));
        Assert.True(plan.IsValid);
        var outcome = CoordinatorOutcomeSpecValidator.ValidateAndSnapshot(
            new("outcome-1", "Build", "Test outputs", "This plan", "Pinned provider", []));
        var restored = CoordinatorDecisionState.Restore(
            7, outcome.Value, true, 0, workflow, true, plan.Value, null, null, null, []);
        Assert.True(restored.IsValid);
        return new(restored.Value!, 3, new string('A', 64));
    }
}
