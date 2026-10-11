using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestCommandResolutionTests
{
    [Fact]
    public void ServerSourceInputResolvesWithoutChangingTheConfirmedCommand()
    {
        var (checkpoint, _, original) = SeedFixture();
        var intent = original with
        {
            Command = original.Command with { WorkspaceSource = null },
            WorkspaceInput = original.Command.WorkspaceSource
        };
        checkpoint = checkpoint with
        {
            State = checkpoint.State with
            {
                BuildTestIntents = checkpoint.State.BuildTestIntents.SetItem(intent.StepId, intent)
            }
        };
        var accepted = CoordinationEndpoints.ResolveBuildTestCommand(
            checkpoint, Current(intent.Command), intent.CheckpointReference(), "tenant-1");

        Assert.Null(intent.Command.WorkspaceSource);
        Assert.Equal(original.ToAcceptedCommand().ImmutableHash, accepted.ImmutableHash);
        Assert.Equal(intent.WorkspaceInput, accepted.WorkspaceSource);
        Assert.Null(accepted.WorkspaceSeed);
    }

    [Fact]
    public void ServerSourceInputDoesNotBypassTheExactConfirmedCommandGuard()
    {
        var (checkpoint, _, original) = SeedFixture();
        var intent = original with
        {
            Command = original.Command with { WorkspaceSource = null },
            WorkspaceInput = original.Command.WorkspaceSource
        };
        checkpoint = checkpoint with
        {
            State = checkpoint.State with
            {
                BuildTestIntents = checkpoint.State.BuildTestIntents.SetItem(intent.StepId, intent)
            }
        };
        var failure = Assert.Throws<CoordinationException>(() => CoordinationEndpoints.ResolveBuildTestCommand(
            checkpoint, Current(intent.Command with { Arguments = ["different"] }),
            intent.CheckpointReference(), "tenant-1"));

        Assert.Equal("maf_execution_build_test_binding_stale", failure.Code);
    }

    [Fact]
    public void RecoveredVersionTwoCommandResolvesMetadataWithoutPuttingSeedBytesInJson()
    {
        var (checkpoint, current, intent) = SeedFixture();
        checkpoint = checkpoint with
        {
            State = MafExecutionCheckpointContract.Deserialize(
                MafExecutionCheckpointContract.Serialize(checkpoint.State))
        };

        var accepted = CoordinationEndpoints.ResolveBuildTestCommand(
            checkpoint, current, intent.CheckpointReference(), "tenant-1");

        Assert.Equal(2, accepted.ContractVersion);
        Assert.Equal(intent.Command.WorkspaceSource, accepted.WorkspaceSource);
        Assert.Equal(intent.ToAcceptedCommand().ImmutableHash, accepted.ImmutableHash);
        Assert.Null(accepted.WorkspaceSeed);
        Assert.Equal(accepted.ImmutableHash, accepted.ValidateMetadata().ImmutableHash);
        Assert.Throws<ArgumentException>(() => accepted.Validate());
        var json = JsonSerializer.Serialize(accepted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"workspaceSource\":", json);
        Assert.DoesNotContain("\"workspaceSeed\":", json);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("fence")]
    [InlineData("selection")]
    public void VersionTwoMetadataIsDeniedWhenAuthorityChangesAfterSeedRecovery(string change)
    {
        var (checkpoint, current, intent) = SeedFixture();
        _ = CoordinationEndpoints.ResolveBuildTestCommand(
            checkpoint, current, intent.CheckpointReference(), "tenant-1");
        current = change switch
        {
            "decision" => current with { StateVersion = current.StateVersion + 1 },
            "selection" => current with { SelectionHash = new string('B', 64) },
            "fence" => current with
            {
                State = CoordinatorDecisionState.Restore(
                    8, current.State.OutcomeSpec, true, 0, current.State.SelectedWorkflow, true,
                    current.State.ConfirmedWorkPlan, null, null, null, []).Value!
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var failure = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ResolveBuildTestCommand(
                checkpoint, current, intent.CheckpointReference(), "tenant-1"));

        Assert.Equal("maf_execution_build_test_checkpoint_stale", failure.Code);
    }

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
        MafExecutionBuildTestIntent Intent) SeedFixture()
    {
        var intent = MafBuildTestCheckpointTests.Intent();
        var seed = new byte[1024];
        var source = new SandboxBuildTestWorkspaceSource(
            2, intent.Identity, "tenant-1", "issuer", "actor",
            "source-pin", intent.AcceptedSelectionHash,
            new(ProviderSeam.SourceControl, "github", "repository", 1),
            "workspace", Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            "branch", new string('a', 40), new string('c', 40),
            "seed", Convert.ToHexStringLower(SHA256.HashData(seed)), seed.Length,
            new string('c', 64), 512, new string('d', 64), 128);
        intent = intent with
        {
            Command = intent.Command with { WorkspaceSource = source },
            WorkspaceSeed = seed.ToImmutableArray(),
            WorkspaceSeedReference = new(
                intent.CheckpointReference(), intent.OperationId, source.SeedSha256, source.SeedByteLength),
            ExecutionOptions = intent.ExecutionOptions with
            {
                CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode
            }
        };
        var initial = new MafExecutionCheckpointSnapshot(
            new("root", "initial"), new(1, "plan-1", 3, MafExecutionProgress.Empty));
        var state = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        return (new(new("root", intent.CheckpointId), state), Current(intent.Command), intent);
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
