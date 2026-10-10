using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestCheckpointTests
{
    [Fact]
    public void LegacyCheckpointWithoutBuildTestIntentsStillRecovers()
    {
        using var payload = JsonDocument.Parse("""
            {"revision":1,"workPlanId":"plan-1","decisionStateVersion":3,
             "progress":{"workItems":{},"nonModelSteps":{},"fixedWorkItems":{}},
             "fixedWorkAssociations":{},"pendingDispatches":{},"results":{}}
            """);

        var state = MafExecutionCheckpointContract.Deserialize(payload.RootElement);

        Assert.Empty(state.BuildTestIntents);
        Assert.Equal(1, state.Revision);
    }

    [Fact]
    public void DurableIntentPreservesOrderedArgumentsAndCompositeAuthorityAfterRecovery()
    {
        var intent = Intent();
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(Initial(), intent)!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(running));

        var restored = recovered.BuildTestIntents["build-test"];
        Assert.Equal(intent.OperationId, restored.OperationId);
        Assert.Equal(intent.Identity, restored.Identity);
        Assert.Equal(intent.CheckpointId, restored.CheckpointId);
        Assert.Equal(intent.CheckpointRevision, restored.CheckpointRevision);
        Assert.Equal(intent.AcceptedSelectionHash, restored.AcceptedSelectionHash);
        Assert.Equal(["test", "a b", "$(literal)", "--"], restored.Command.Arguments.ToArray());
        Assert.Equal(intent.Command.Outputs.ToArray(), restored.Command.Outputs.ToArray());
        Assert.Equal(MafExecutionTaskStatus.Running, recovered.Progress.NonModelSteps["build-test"]);
        Assert.True(MafExecutionCheckpointContract.BuildTestIntentsMatch(intent, restored));
    }

    [Fact]
    public void LostAcceptanceAckReusesExactlyThePersistedIntent()
    {
        var initial = Initial();
        var intent = Intent();
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(initial, intent)!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(running));

        Assert.Null(CoordinationEndpoints.PrepareBuildTestCheckpoint(
            new(new("root", intent.CheckpointId), recovered), intent));
        var conflict = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.PrepareBuildTestCheckpoint(
                new(new("root", intent.CheckpointId), recovered),
                intent with { OperationId = Guid.NewGuid() }));
        Assert.Equal("maf_execution_build_test_intent_conflict", conflict.Code);
    }

    [Fact]
    public void ModelWorkCheckpointMutationRetainsRunningCommandIntent()
    {
        var intent = Intent();
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(Initial(), intent)!;

        var next = CoordinationEndpoints.PrepareRunningCheckpoint(
            new(new("root", intent.CheckpointId), running), "plan-1", 3, "implementation", null)!;

        Assert.Same(intent, next.BuildTestIntents["build-test"]);
        MafExecutionCheckpointContract.ValidateTransition(running, next);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("arguments")]
    [InlineData("profile")]
    [InlineData("selection")]
    [InlineData("identity")]
    [InlineData("removed")]
    public void PersistedIntentCannotBeChangedOrRemovedByALaterCheckpoint(string change)
    {
        var intent = Intent();
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(Initial(), intent)!;
        var changed = change switch
        {
            "operation" => intent with { OperationId = Guid.NewGuid() },
            "arguments" => intent with { Command = intent.Command with { Arguments = ["build"] } },
            "profile" => intent with { Command = intent.Command with { ExecutionProfileReference = "profile-2" } },
            "selection" => intent with { AcceptedSelectionHash = new string('B', 64) },
            "identity" => intent with { Identity = new(intent.Identity.ProjectId, "other-run", intent.Identity.SessionId) },
            "removed" => intent,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var next = running with
        {
            Revision = 3,
            BuildTestIntents = change == "removed"
                ? running.BuildTestIntents.Clear()
                : running.BuildTestIntents.SetItem("build-test", changed)
        };

        Assert.Throws<ArgumentException>(() =>
            MafExecutionCheckpointContract.ValidateTransition(running, next));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("revision")]
    [InlineData("decision")]
    [InlineData("fence")]
    [InlineData("hash")]
    [InlineData("scope")]
    [InlineData("command")]
    [InlineData("duplicate-operation")]
    public void MalformedOrUnboundIntentsFailRecovery(string fault)
    {
        var intent = Intent();
        var running = CoordinationEndpoints.PrepareBuildTestCheckpoint(Initial(), intent)!;
        intent = fault switch
        {
            "revision" => intent with { CheckpointRevision = 3 },
            "decision" => intent with { DecisionStateVersion = 4 },
            "fence" => intent with { ExecutionFence = 0 },
            "hash" => intent with { AcceptedSelectionHash = "invalid" },
            "scope" => intent with { Identity = default },
            "command" => intent with { Command = intent.Command with { Arguments = ["test\n"] } },
            _ => intent
        };
        var state = running with { BuildTestIntents = running.BuildTestIntents.SetItem("build-test", intent) };
        if (fault == "pending")
            state = state with { Progress = MafExecutionProgress.Empty };
        if (fault == "duplicate-operation")
            state = state with
            {
                BuildTestIntents = state.BuildTestIntents.Add("another", intent with { StepId = "another" }),
                Progress = state.Progress with
                {
                    NonModelSteps = state.Progress.NonModelSteps.Add("another", MafExecutionTaskStatus.Running)
                }
            };

        Assert.Throws<ArgumentException>(() => MafExecutionCheckpointContract.Serialize(state));
    }

    [Fact]
    public void OnlyPendingCommandCanCreateAnIntentAndOnePlatformEffectRunsAtATime()
    {
        var initial = Initial();
        var busy = initial with
        {
            State = initial.State with
            {
                Progress = initial.State.Progress with
                {
                    NonModelSteps = initial.State.Progress.NonModelSteps.Add("preview", MafExecutionTaskStatus.Running)
                }
            }
        };

        var conflict = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.PrepareBuildTestCheckpoint(busy, Intent()));
        Assert.Equal("maf_execution_build_test_checkpoint_stale", conflict.Code);
    }

    private static MafExecutionCheckpointSnapshot Initial() =>
        new(new CheckpointInfo("root", "initial"), new(1, "plan-1", 3, MafExecutionProgress.Empty));

    internal static MafExecutionBuildTestIntent Intent() =>
        new(Guid.Parse("641e5139-66eb-4757-ad9b-8614d3ba47c7"),
            new("project-1", "run-1", "root"), "command-1", 2, "plan-1", "build-test", 3, 7,
            new string('A', 64),
            new("profile-1", "/usr/bin/dotnet", ["test", "a b", "$(literal)", "--"], ".",
                [new("results", "artifacts/results.xml", true, 1024)]),
            ExecutionOptions(), ExpectedBinding());

    internal static SandboxBuildTestAcceptedExecutionOptions ExecutionOptions() =>
        new("profile-1", $"build-test@sha256:{new string('a', 64)}", "linux/amd64",
            ["/usr/bin/dotnet"], "1000m", "1Gi", "1Gi", 120, 4096, 1024, "offline",
            $"collector@sha256:{new string('b', 64)}", "linux/amd64",
            SandboxBuildTestLimits.OutputCollectorExecutable,
            [SandboxBuildTestLimits.OutputCollectorAssembly],
            SandboxBuildTestLimits.OutputCollectorMode,
            SandboxBuildTestLimits.OutputCollectorContainerName);

    internal static SandboxBuildTestExpectedBinding ExpectedBinding()
    {
        using var descriptor = JsonDocument.Parse("{}");
        return new(new(new("tenant-1", "project-1", "run-1", "environment-1"), 1),
            Guid.Parse("9b22d1c4-2a0a-4d85-b1c9-1059bcf08bb7"),
            new(ProviderSeam.Sandbox, "sandbox-1", "sandbox-resource", 1), 1,
            new("project-1", "volume-1", 1), 2, 3)
        {
            SandboxProviderBinding = new("sandbox-1", "1.0.0", 1, "options-v1",
                descriptor.RootElement.Clone(), descriptor.RootElement.Clone())
        };
    }
}
