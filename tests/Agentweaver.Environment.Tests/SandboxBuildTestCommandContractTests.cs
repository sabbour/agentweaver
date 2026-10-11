using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class SandboxBuildTestCommandContractTests
{
    [Fact]
    public void VersionOneCommandRetainsItsHistoricalHashAndSerializedShape()
    {
        var command = CreateFixture().Command;

        Assert.Equal(
            "sha256:50ccbec5ff8d980ee5e4e08e33fa655c38411b5975fc65dffcc5d33b3ccaa75b",
            command.ComputeImmutableHash());
        Assert.DoesNotContain("workspaceSource", JsonSerializer.Serialize(
            command, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(command.ImmutableHash, command.Validate().ImmutableHash);
    }

    [Fact]
    public void VersionTwoCommandBindsTheCompleteAcceptedRepositorySeed()
    {
        var command = SeedCommand();
        command = command with { ImmutableHash = command.ComputeImmutableHash() };

        var validated = command.Validate();

        Assert.Equal(command.WorkspaceSource, validated.WorkspaceSource);
        Assert.Contains("workspaceSource", JsonSerializer.Serialize(
            validated, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.NotEqual(CreateFixture().Command.ImmutableHash, validated.ImmutableHash);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("project")]
    [InlineData("session")]
    [InlineData("selection")]
    [InlineData("incarnation")]
    [InlineData("provider")]
    [InlineData("bundle")]
    [InlineData("length")]
    public void VersionTwoRejectsMissingOrMismatchedRepositorySeed(string fault)
    {
        var source = WorkspaceSource();
        source = fault switch
        {
            "version" => source with { ContractVersion = 1 },
            "project" => source with { Identity = new("other", source.Identity.RunId, source.Identity.SessionId) },
            "session" => source with { Identity = new(source.Identity.ProjectId, source.Identity.RunId, "other") },
            "selection" => source with { AcceptedSelectionHash = new string('B', 64) },
            "incarnation" => source with { WorkspaceIncarnationId = Guid.Empty },
            "provider" => source with
            {
                SourceControlResource = source.SourceControlResource with { Seam = ProviderSeam.Sandbox }
            },
            "bundle" => source with { BundleSha256 = "not-a-digest" },
            "length" => source with { BundleByteLength = source.SeedByteLength },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var command = SeedCommand() with
        {
            WorkspaceSource = source
        };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };

        Assert.Throws<ArgumentException>(() => command.Validate());
        Assert.Throws<ArgumentException>(() =>
            (CreateFixture().Command with { ContractVersion = 2 }).Validate());
    }

    [Fact]
    public void VersionOneCannotHideAnUnhashedRepositorySeed()
    {
        var command = CreateFixture().Command with { WorkspaceSource = WorkspaceSource() };

        Assert.Throws<ArgumentException>(() => command.Validate());
    }

    [Fact]
    public void VersionTwoCannotExecuteWithoutItsSavedExactSeedBytes()
    {
        var command = SeedCommand() with { WorkspaceSeed = null };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };

        Assert.Throws<ArgumentException>(() => command.Validate());
        Assert.Throws<ArgumentException>(() =>
            (command with { WorkspaceSeed = new byte[1023].ToImmutableArray() }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (command with { WorkspaceSeed = Enumerable.Repeat((byte)1, 1024).ToImmutableArray() }).Validate());
    }

    [Fact]
    public void VersionTwoStoredStateRecoversUpdatesAndReplaysWithMetadataOnly()
    {
        var command = SeedCommand();
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        var fixture = CreateFixture() with { Command = command };
        var state = new EnvironmentSandboxBuildTestCommandState(
            command, CreateSnapshot(fixture), ["command-pod:create"]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var commandJson = JsonSerializer.Serialize(command, options);
        Assert.DoesNotContain("workspaceSeed", commandJson);
        var row = new EnvironmentSandboxBuildTestCommandStore.StoredRow(
            fixture.Request.ExpectedBinding.Fence.Owner, "BuildTestCommand",
            fixture.Request.ComputeRequestFingerprint(command), commandJson,
            JsonSerializer.Serialize(fixture.Request.ExpectedBinding, options),
            EnvironmentSandboxBuildTestCommandStore.SerializeMutableState(state),
            state.Operation.CreatedAt);

        var restored = EnvironmentSandboxBuildTestCommandStore.DeserializeState(row);
        Assert.Null(restored.AcceptedCommand.WorkspaceSeed);
        Assert.Equal(command.ImmutableHash, restored.AcceptedCommand.ValidateMetadata().ImmutableHash);
        Assert.Equal(row.Fingerprint, fixture.Request.ComputeRequestFingerprint(restored.AcceptedCommand));
        Assert.Same(restored.Operation, restored.Operation.Validate(
            restored.AcceptedCommand, command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() => restored.AcceptedCommand.Validate());
        var updated = restored with
        {
            Operation = restored.Operation with
            {
            Status = SandboxBuildTestOperationStatus.Stale,
            FailureCode = "buildtest_checkpoint_stale",
            UpdatedAt = restored.Operation.UpdatedAt.AddSeconds(1)
            },
            AttemptedEffects = restored.AttemptedEffects.Add("command-pod:observed")
        };
        var replay = EnvironmentSandboxBuildTestCommandStore.DeserializeState(row with
        {
            TargetBindingJson = EnvironmentSandboxBuildTestCommandStore.SerializeMutableState(updated)
        });
        Assert.Equal(SandboxBuildTestOperationStatus.Stale, replay.Operation.Status);
        Assert.Equal(updated.AttemptedEffects.ToArray(), replay.AttemptedEffects.ToArray());
        Assert.Equal(row.Fingerprint, fixture.Request.ComputeRequestFingerprint(replay.AcceptedCommand));

        var executable = EnvironmentSandboxBuildTestCommandManager.RestoreAcceptedCommand(restored, command);
        Assert.Equal(command.WorkspaceSeed!.Value.ToArray(),
            executable.AcceptedCommand.Validate().WorkspaceSeed!.Value.ToArray());
        Assert.Throws<ArgumentException>(() =>
            EnvironmentSandboxBuildTestCommandManager.RestoreAcceptedCommand(restored, command with
            {
            WorkspaceSeed = null
            }));
        var changed = command with { Arguments = ["other"] };
        changed = changed with { ImmutableHash = changed.ComputeImmutableHash() };
        var conflict = Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxBuildTestCommandManager.RestoreAcceptedCommand(restored, changed));
        Assert.Equal("buildtest_request_conflict", conflict.Code);
    }

    [Fact]
    public void VersionTwoCannotRunTheLegacyHashOnlyCollector()
    {
        var command = SeedCommand() with { ExecutionOptions = CreateFixture().Command.ExecutionOptions };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };

        Assert.Throws<ArgumentException>(() => command.Validate());
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("seed")]
    [InlineData("bundle")]
    [InlineData("inventory")]
    [InlineData("workspace")]
    [InlineData("actor")]
    [InlineData("tree")]
    [InlineData("branch")]
    public void VersionTwoHashIncludesEveryRepositorySeedIdentityAndDigest(string change)
    {
        var original = SeedCommand();
        var source = original.WorkspaceSource!;
        var changed = change switch
        {
            "reference" => source with { SeedReference = "seed-2" },
            "seed" => source with { SeedSha256 = new string('b', 64) },
            "bundle" => source with { BundleSha256 = new string('b', 64) },
            "inventory" => source with { BaselineManifestSha256 = new string('b', 64) },
            "workspace" => source with { WorkspaceIncarnationId = Guid.NewGuid() },
            "actor" => source with { ActorSubject = "other-actor" },
            "tree" => source with { BaselineTreeSha = new string('b', 40) },
            "branch" => source with { BranchName = "other-branch" },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Assert.NotEqual(
            original.ComputeImmutableHash(),
            (original with { WorkspaceSource = changed }).ComputeImmutableHash());
    }

    internal static SandboxBuildTestWorkspaceSource WorkspaceSource() =>
        new(2, new SessionIdentity("project", "run", "session"), "tenant", "issuer", "actor",
            "source-pin", new string('A', 64),
            new(ProviderSeam.SourceControl, "github", "repository-1", 1),
            "workspace-1", Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            "work-branch", new string('a', 40), new string('c', 40),
            "seed-1", Convert.ToHexStringLower(SHA256.HashData(new byte[1024])), 1024,
            new string('c', 64), 512, new string('d', 64), 128);

    private static SandboxBuildTestAcceptedCommand SeedCommand()
    {
        var command = CreateFixture().Command;
        return command with
        {
            ContractVersion = 2,
            WorkspaceSource = WorkspaceSource(),
            WorkspaceSeed = new byte[1024].ToImmutableArray(),
            ExecutionOptions = command.ExecutionOptions with
            {
                CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode
            }
        };
    }

    [Fact]
    public void RequestFingerprintIncludesPinnedSandboxProviderBinding()
    {
        var fixture = CreateFixture();
        var original = fixture.Request.ComputeRequestFingerprint(fixture.Command);
        var changedBinding = fixture.Request with
        {
            ExpectedBinding = fixture.Request.ExpectedBinding with
            {
                SandboxProviderBinding = fixture.Request.ExpectedBinding.SandboxProviderBinding! with
                {
                    OptionsRevision = "options-2"
                }
            }
        };

        Assert.NotEqual(original, changedBinding.ComputeRequestFingerprint(fixture.Command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void CollectorRequestPermitsEmptyOutputsOnlyForExplicitVersionTwo(int? commandVersion)
    {
        var request = CreateCollectorRequest(CreateFixture()) with
        {
            AcceptedCommandContractVersion = commandVersion,
            Outputs = []
        };
        if (commandVersion == 2)
            Assert.Empty(request.Validate().Outputs);
        else
            Assert.Throws<ArgumentException>(() => request.Validate());
        Assert.Throws<ArgumentException>(() => (request with { Outputs = default }).Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void CollectorRequestRejectsUnknownAcceptedCommandVersions(int commandVersion)
    {
        var request = CreateCollectorRequest(CreateFixture()) with
        {
            AcceptedCommandContractVersion = commandVersion
        };
        Assert.Throws<ArgumentException>(() => request.Validate());
    }

    [Fact]
    public void VersionOneCollectorRequestRetainsItsHistoricalSerializedShape()
    {
        var request = CreateCollectorRequest(CreateFixture());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var actual = JsonSerializer.SerializeToElement(request.Validate(), options);
        var historical = JsonSerializer.SerializeToElement(new
        {
            request.OperationId,
            request.ImmutableHash,
            request.RequestFingerprint,
            request.Checkpoint,
            request.WorkspaceMountPath,
            request.MaximumTotalBytes,
            request.Outputs
        }, options);

        Assert.True(JsonElement.DeepEquals(historical, actual));
        Assert.Null(JsonSerializer.Deserialize<SandboxBuildTestOutputCollectorRequest>(
            actual.GetRawText(), options)!.AcceptedCommandContractVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionTwoWorkspaceRequestPermitsEmptyOutputsForPreparationAndCapture(bool preparing)
    {
        var command = SeedCommand() with { Outputs = [] };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        var request = new SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspaceRequest(
            command.OperationId, command.ImmutableHash,
            CreateFixture().Request.ComputeRequestFingerprint(command),
            command.Checkpoint, command.WorkspaceSource!, "/workspace", preparing, []);

        Assert.Empty(request.Validate().Outputs);
        Assert.Throws<ArgumentException>(() => (request with { Outputs = default }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (request with { Source = request.Source with { ContractVersion = 1 } }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (request with { Checkpoint = request.Checkpoint with { ProjectId = "other" } }).Validate());
    }

    [Fact]
    public void EmptyVersionTwoCollectorRequestKeepsReceiptVersionAndExactBindingChecks()
    {
        var request = CreateCollectorRequest(CreateFixture()) with
        {
            AcceptedCommandContractVersion = 2,
            Outputs = []
        };
        var pod = new SandboxBuildTestPodReference("sandbox", "collector-pod", "collector-uid", "17");
        var receipt = new SandboxBuildTestOutputCollectorReceipt(
            1, request.OperationId, request.ImmutableHash, request.RequestFingerprint, request.Checkpoint,
            pod.Uid, SandboxBuildTestLimits.OutputCollectorContainerName, [], string.Empty);
        receipt = receipt with
        {
            ManifestSha256 = SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt)
        };

        Assert.Equal(1, receipt.Validate(request, pod).ContractVersion);
        Assert.Throws<ArgumentException>(() =>
            receipt.Validate(request with { AcceptedCommandContractVersion = null }, pod));
        Assert.Throws<ArgumentException>(() =>
            receipt.Validate(request with { AcceptedCommandContractVersion = 1 }, pod));
        Assert.Throws<ArgumentException>(() =>
            receipt.Validate(request, pod with { Uid = "other" }));
        Assert.Throws<ArgumentException>(() =>
            (receipt with { RequestFingerprint = new string('f', 64) }).Validate(request, pod));
    }

    [Fact]
    public void CollectorReceiptCanReportMissingRequiredOutputForServerClassification()
    {
        var fixture = CreateFixture();
        var request = new SandboxBuildTestOutputCollectorRequest(
            fixture.Command.OperationId,
            fixture.Command.ImmutableHash,
            fixture.Request.ComputeRequestFingerprint(fixture.Command),
            fixture.Command.Checkpoint,
            "/workspace",
            1024,
            fixture.Command.Outputs).Validate();
        var receipt = new SandboxBuildTestOutputCollectorReceipt(
            1,
            request.OperationId,
            request.ImmutableHash,
            request.RequestFingerprint,
            request.Checkpoint,
            "collector-uid",
            SandboxBuildTestLimits.OutputCollectorContainerName,
            [
                new(
                    "collector-uid",
                    SandboxBuildTestLimits.OutputCollectorContainerName,
                    "result",
                    "out/result.bin",
                    true,
                    128,
                    false,
                    0,
                    null)
            ],
            string.Empty);
        receipt = receipt with
        {
            ManifestSha256 = SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt)
        };

        var validated = receipt.Validate(
            request,
            new SandboxBuildTestPodReference(
                "sandbox",
                "collector-pod",
                "collector-uid",
                "17"));

        Assert.False(validated.Outputs[0].Exists);
        Assert.True(validated.Outputs[0].Required);
    }

    [Theory]
    [InlineData(SandboxBuildTestTerminationKind.TimedOut, false)]
    [InlineData(SandboxBuildTestTerminationKind.Cancelled, false)]
    [InlineData(SandboxBuildTestTerminationKind.OutputLimitExceeded, true)]
    public async Task RequiredOutputCollectorInterruptionWithSuccessfulCommandHasNoOutputEvidence(
        SandboxBuildTestTerminationKind terminationKind,
        bool outputTruncated)
    {
        var fixture = CreateFixture();
        var operation = CreateSnapshot(fixture) with { CollectorPod = CollectorPod() };
        var state = new EnvironmentSandboxBuildTestCommandState(fixture.Command, operation, []);
        var commandObservation = CreateCommandObservation(operation);
        var collectorObservation = new SandboxBuildTestPodObservation(
            CollectorPod(),
            SandboxBuildTestPodState.Failed,
            CollectorTerminal(
                CollectorPod().Uid,
                terminationKind),
            CreateOutputCapture(
                CollectorPod().Uid,
                SandboxBuildTestLimits.OutputCollectorContainerName,
                [],
                outputTruncated)).Validate(SandboxBuildTestLimits.MaximumCollectorRequestBytes);
        var store = new RecordingCommandStore();
        var manager = CreateManager(store);
        var request = CreateCollectorRequest(fixture);

        var result = await manager.RecordCollectorObservationAsync(
            fixture.Request,
            fixture.Command,
            CreateProviderRequest(fixture, operation),
            request,
            state,
            commandObservation,
            collectorObservation,
            CancellationToken.None);

        Assert.Equal(SandboxBuildTestOperationStatus.Interrupted, result.Operation.Status);
        Assert.Equal(
            SandboxBuildTestTerminationKind.Exited,
            result.Operation.Terminal!.Kind);
        Assert.Equal(0, result.Operation.Terminal.ExitCode);
        Assert.Equal(
            terminationKind,
            result.Operation.CollectorTerminal!.Kind);
        Assert.Null(result.Operation.CollectorManifestSha256);
        Assert.Empty(result.Operation.OutputEvidence);
        Assert.Same(result, store.SavedState);
        _ = result.Operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes);
    }

    [Fact]
    public async Task MissingRequiredCollectorOutputFailsInsteadOfCompleting()
    {
        var fixture = CreateFixture();
        var operation = CreateSnapshot(fixture) with { CollectorPod = CollectorPod() };
        var state = new EnvironmentSandboxBuildTestCommandState(fixture.Command, operation, []);
        var collectorRequest = CreateCollectorRequest(fixture);
        var receipt = CreateMissingRequiredOutputReceipt(collectorRequest, CollectorPod());
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
            receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var collectorObservation = new SandboxBuildTestPodObservation(
            CollectorPod(),
            SandboxBuildTestPodState.Succeeded,
            CollectorTerminal(
                CollectorPod().Uid,
                SandboxBuildTestTerminationKind.Exited,
                0),
            CreateOutputCapture(
                CollectorPod().Uid,
                SandboxBuildTestLimits.OutputCollectorContainerName,
                receiptBytes)).Validate(SandboxBuildTestLimits.MaximumCollectorRequestBytes);
        var store = new RecordingCommandStore();
        var manager = CreateManager(store);

        var result = await manager.RecordCollectorObservationAsync(
            fixture.Request,
            fixture.Command,
            CreateProviderRequest(fixture, operation),
            collectorRequest,
            state,
            CreateCommandObservation(operation),
            collectorObservation,
            CancellationToken.None);

        Assert.Equal(SandboxBuildTestOperationStatus.Failed, result.Operation.Status);
        Assert.Equal("buildtest_required_output_missing", result.Operation.FailureCode);
        Assert.False(result.Operation.OutputEvidence.Single().Exists);
        Assert.NotEqual(SandboxBuildTestOperationStatus.Completed, result.Operation.Status);
        _ = result.Operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes);
    }

    [Fact]
    public void ApiRequestRejectsCheckpointOutsideExpectedProjectAndRun()
    {
        var fixture = CreateFixture();
        var mismatched = fixture.Request with
        {
            Checkpoint = fixture.Request.Checkpoint with { RunId = "different-run" }
        };

        Assert.Throws<ArgumentException>(() => mismatched.Validate());
    }

    [Fact]
    public void AcceptedCommandRejectsNonRootWorkingDirectory()
    {
        var fixture = CreateFixture();
        var nested = fixture.Command with { WorkingDirectory = "subdirectory" };
        nested = nested with { ImmutableHash = nested.ComputeImmutableHash() };

        Assert.Throws<ArgumentException>(() => nested.Validate());
    }

    [Fact]
    public void BindingPreparationIsBoundToSessionAndPinnedExecutionProfile()
    {
        var fixture = CreateFixture();
        var preparation = new SandboxBuildTestBindingPreparation(
            fixture.Command.Checkpoint.SessionId,
            fixture.Command.ExecutionOptions.ProfileId,
            "options-1",
            SandboxBuildTestBindingPreparation.ComputeExecutionOptionsSha256(fixture.Command.ExecutionOptions),
            fixture.Request.ExpectedBinding,
            fixture.Command.ExecutionOptions);

        Assert.Same(
            preparation,
            preparation.Validate(fixture.Command.Checkpoint.SessionId, fixture.Command.ExecutionOptions.ProfileId));
        Assert.Throws<ArgumentException>(() =>
            preparation.Validate(fixture.Command.Checkpoint.SessionId, "different-profile"));
    }

    [Fact]
    public void CollectorTimeoutCanInterruptOnlyAfterSuccessfulCommandWithBoundCollectorEvidence()
    {
        var fixture = CreateFixture();
        var operation = CreateSnapshot(fixture) with
        {
            Status = SandboxBuildTestOperationStatus.Interrupted,
            Terminal = CommandTerminal(0),
            CollectorPod = CollectorPod(),
            CollectorTerminal = CollectorTerminal(
                CollectorPod().Uid,
                SandboxBuildTestTerminationKind.TimedOut)
        };

        Assert.Same(
            operation,
            operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void SuccessfulCommandCannotBeInterruptedWithoutCollectorEvidence()
    {
        var fixture = CreateFixture();
        var operation = CreateSnapshot(fixture) with
        {
            Status = SandboxBuildTestOperationStatus.Interrupted,
            Terminal = CommandTerminal(0)
        };

        Assert.Throws<ArgumentException>(() =>
            operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void CollectorNonzeroExitIsFailedNotInterrupted()
    {
        var fixture = CreateFixture();
        var pod = CollectorPod();
        var operation = CreateSnapshot(fixture) with
        {
            Status = SandboxBuildTestOperationStatus.Failed,
            Terminal = CommandTerminal(0),
            CollectorPod = pod,
            CollectorTerminal = CollectorTerminal(pod.Uid, SandboxBuildTestTerminationKind.Exited, 1)
        };

        Assert.Same(
            operation,
            operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() =>
            (operation with { Status = SandboxBuildTestOperationStatus.Interrupted })
                .Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void CollectorInterruptionMustMatchItsPodAndContainer()
    {
        var fixture = CreateFixture();
        var pod = CollectorPod();
        var operation = CreateSnapshot(fixture) with
        {
            Status = SandboxBuildTestOperationStatus.Interrupted,
            Terminal = CommandTerminal(0),
            CollectorPod = pod,
            CollectorTerminal = CollectorTerminal(pod.Uid, SandboxBuildTestTerminationKind.Cancelled)
        };

        Assert.Throws<ArgumentException>(() =>
            (operation with
            {
                CollectorTerminal = operation.CollectorTerminal! with { SourcePodUid = "wrong-pod" }
            }).Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() =>
            (operation with
            {
                CollectorTerminal = operation.CollectorTerminal! with { ContainerName = "wrong-container" }
            }).Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void PrimaryNonzeroExitRemainsFailedEvenWhenCollectorEvidenceIsInterrupted()
    {
        var fixture = CreateFixture();
        var pod = CollectorPod();
        var operation = CreateSnapshot(fixture) with
        {
            Status = SandboxBuildTestOperationStatus.Failed,
            Terminal = CommandTerminal(1),
            CollectorPod = pod,
            CollectorTerminal = CollectorTerminal(pod.Uid, SandboxBuildTestTerminationKind.TimedOut)
        };

        Assert.Same(
            operation,
            operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() =>
            (operation with { Status = SandboxBuildTestOperationStatus.Interrupted })
                .Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes));
    }

    internal static Fixture CreateFixture(
        SandboxBuildTestExpectedBinding? binding = null,
        Guid? operationId = null)
    {
        var owner = new EnvironmentOwnerIdentity("tenant", "project", "run", "environment");
        var fence = new EnvironmentGenerationFence(owner, 3);
        var resource = new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", "claim-uid", 2);
        var providerBinding = new SandboxProviderBindingSnapshot(
            "agent-sandbox",
            "1.0.0",
            2,
            "options-1",
            JsonSerializer.SerializeToElement(new { namespaceName = "sandbox" }),
            JsonSerializer.SerializeToElement(new { claimName = "claim" }));
        var expectedBinding = new SandboxBuildTestExpectedBinding(
            fence,
            Guid.Parse("fe7bca04-b7d7-49a6-ae4a-12aa4173d744"),
            resource,
            4,
            new WorkspaceVolumeReference("project", "workspace", 5),
            6,
            7)
        {
            SandboxProviderBinding = providerBinding
        };
        expectedBinding = binding ?? expectedBinding;
        var checkpoint = new SandboxBuildTestCheckpointReference(
            expectedBinding.Fence.Owner.ProjectId,
            expectedBinding.Fence.Owner.RunId,
            "session",
            "checkpoint",
            "plan",
            "step",
            8,
            9,
            10,
            new string('A', 64));
        var options = new SandboxBuildTestAcceptedExecutionOptions(
            "profile",
            "registry.example/build@sha256:" + new string('a', 64),
            "linux/amd64",
            ["/usr/bin/make"],
            "1",
            "512Mi",
            "1Gi",
            60,
            4096,
            1024,
            SandboxBuildTestLimits.OfflineEgressProfile,
            "registry.example/collector@sha256:" + new string('b', 64),
            "linux/amd64",
            SandboxBuildTestLimits.OutputCollectorExecutable,
            [SandboxBuildTestLimits.OutputCollectorAssembly],
            SandboxBuildTestLimits.OutputCollectorMode,
            SandboxBuildTestLimits.OutputCollectorContainerName);
        var command = new SandboxBuildTestAcceptedCommand(
            1,
            operationId ?? Guid.Parse("9dd33480-4e5b-49c5-b678-03d6766a3864"),
            checkpoint,
            "/usr/bin/make",
            ["test"],
            ".",
            [new("result", "out/result.bin", true, 128)],
            options,
            string.Empty);
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        var request = new SandboxBuildTestApiRequest(checkpoint, expectedBinding);
        return new(command, request);
    }

    internal static SandboxBuildTestOperationSnapshot CreateSnapshot(Fixture fixture)
    {
        var operationId = fixture.Command.OperationId;
        var commandSelector = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(
                SandboxBuildTestLimits.PolicyOperationLabel,
                operationId.ToString("N")),
            new KeyValuePair<string, string>(
                SandboxBuildTestLimits.PolicyRoleLabel,
                SandboxBuildTestLimits.CommandPolicyRole)
        ]);
        var commandPolicy = new SandboxBuildTestCommandNetworkPolicyBinding(
            "command-policy-uid",
            new string('c', 64),
            fixture.Request.ExpectedBinding.NetworkPolicyGeneration,
            commandSelector);
        var collectorSelector = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(
                SandboxBuildTestLimits.PolicyOperationLabel,
                operationId.ToString("N")),
            new KeyValuePair<string, string>(
                SandboxBuildTestLimits.PolicyRoleLabel,
                SandboxBuildTestLimits.CollectorPolicyRole)
        ]);
        var collectorPolicy = new SandboxBuildTestOutputCollectorNetworkPolicyBinding(
            "collector-policy-uid",
            new string('d', 64),
            fixture.Request.ExpectedBinding.NetworkPolicyGeneration,
            collectorSelector);
        var pod = new SandboxBuildTestPodReference("sandbox", "command-pod", "command-uid", "17");
        var output = new SandboxBuildTestOutputCapture(
            pod.Uid,
            "buildtest-command",
            ImmutableArray<byte>.Empty,
            0,
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            false,
            0);
        var now = DateTimeOffset.UtcNow;
        var fingerprint = fixture.Request.ComputeRequestFingerprint(fixture.Command);
        var operation = new SandboxBuildTestOperationSnapshot(
            operationId,
            fixture.Command.ImmutableHash,
            fingerprint,
            SandboxBuildTestOperationStatus.Running,
            fixture.Request.ExpectedBinding,
            commandPolicy,
            new SandboxBuildTestObservedBinding(
                fixture.Request.ExpectedBinding.Fence,
                fixture.Request.ExpectedBinding.SandboxLeaseOperationId,
                fixture.Request.ExpectedBinding.SandboxResource,
                fixture.Request.ExpectedBinding.ProviderFencingGeneration,
                fixture.Request.ExpectedBinding.WorkspaceVolume,
                fixture.Request.ExpectedBinding.DataGeneration,
                fixture.Request.ExpectedBinding.NetworkPolicyGeneration,
                commandPolicy),
            pod,
            null,
            output,
            null,
            null,
            fixture.Command.Outputs.IsEmpty && fixture.Command.WorkspaceSource is null ? null : collectorPolicy,
            null,
            [],
            null,
            now,
            now);
        return operation.Validate(fixture.Command, fixture.Command.ExecutionOptions.MaximumOutputBytes);
    }

    private static SandboxBuildTestPodReference CollectorPod() =>
        new("sandbox", "collector-pod", "collector-uid", "18");

    private static SandboxBuildTestTerminalEvidence CommandTerminal(int exitCode) =>
        new(
            "command-uid",
            "buildtest-command",
            SandboxBuildTestTerminationKind.Exited,
            exitCode,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            null);

    private static SandboxBuildTestTerminalEvidence CollectorTerminal(
        string podUid,
        SandboxBuildTestTerminationKind kind,
        int? exitCode = 124) =>
        new(
            podUid,
            SandboxBuildTestLimits.OutputCollectorContainerName,
            kind,
            exitCode,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            kind == SandboxBuildTestTerminationKind.Exited ? null : "collector-stop");

    private static EnvironmentSandboxBuildTestCommandManager CreateManager(
        RecordingCommandStore store) =>
        new(
            null!,
            null!,
            null!,
            store,
            null!,
            null!,
            null!,
            TimeProvider.System,
            null!);

    private static SandboxBuildTestPodObservation CreateCommandObservation(
        SandboxBuildTestOperationSnapshot operation) =>
        new SandboxBuildTestPodObservation(
            operation.Pod!,
            SandboxBuildTestPodState.Succeeded,
            CommandTerminal(0),
            operation.Output!).Validate(SandboxBuildTestLimits.MaximumLogOutputBytes);

    private static SandboxBuildTestOutputCollectorRequest CreateCollectorRequest(Fixture fixture) =>
        new SandboxBuildTestOutputCollectorRequest(
            fixture.Command.OperationId,
            fixture.Command.ImmutableHash,
            fixture.Request.ComputeRequestFingerprint(fixture.Command),
            fixture.Command.Checkpoint,
            "/workspace",
            fixture.Command.ExecutionOptions.MaximumFileOutputBytes,
            fixture.Command.Outputs).Validate();

    private static SandboxBuildTestProviderRequest CreateProviderRequest(
        Fixture fixture,
        SandboxBuildTestOperationSnapshot operation) =>
        new SandboxBuildTestProviderRequest(
            fixture.Command,
            fixture.Request.ExpectedBinding,
            operation.ExpectedCommandPolicy)
        {
            CollectorPolicy = operation.CollectorPolicy
        }.Validate();

    private static SandboxBuildTestOutputCollectorReceipt CreateMissingRequiredOutputReceipt(
        SandboxBuildTestOutputCollectorRequest request,
        SandboxBuildTestPodReference pod)
    {
        var receipt = new SandboxBuildTestOutputCollectorReceipt(
            1,
            request.OperationId,
            request.ImmutableHash,
            request.RequestFingerprint,
            request.Checkpoint,
            pod.Uid,
            SandboxBuildTestLimits.OutputCollectorContainerName,
            [
                new(
                    pod.Uid,
                    SandboxBuildTestLimits.OutputCollectorContainerName,
                    request.Outputs[0].Name,
                    request.Outputs[0].RelativePath,
                    request.Outputs[0].Required,
                    request.Outputs[0].MaximumBytes,
                    false,
                    0,
                    null)
            ],
            string.Empty);
        return receipt with
        {
            ManifestSha256 = SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt)
        };
    }

    private static SandboxBuildTestOutputCapture CreateOutputCapture(
        string podUid,
        string containerName,
        byte[] bytes,
        bool truncated = false)
    {
        var captured = ImmutableArray.CreateRange(bytes);
        return new(
            podUid,
            containerName,
            captured,
            captured.Length,
            Convert.ToHexString(SHA256.HashData(captured.AsSpan())).ToLowerInvariant(),
            truncated,
            truncated ? captured.Length + 1 : captured.Length);
    }

    private sealed class RecordingCommandStore : IEnvironmentSandboxBuildTestCommandStore
    {
        public EnvironmentSandboxBuildTestCommandState? SavedState { get; private set; }

        public Task<EnvironmentSandboxBuildTestCommandReservation> ReserveAsync(
            SandboxBuildTestApiRequest request,
            SandboxBuildTestAcceptedCommand acceptedCommand,
            SandboxBuildTestOperationSnapshot initialOperation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentSandboxBuildTestCommandState?> GetAsync(
            EnvironmentOwnerIdentity owner,
            Guid operationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentSandboxBuildTestCommandState> SaveAsync(
            EnvironmentOwnerIdentity owner,
            EnvironmentSandboxBuildTestCommandState state,
            CancellationToken cancellationToken)
        {
            SavedState = state;
            return Task.FromResult(state);
        }
    }

    internal sealed record Fixture(
        SandboxBuildTestAcceptedCommand Command,
        SandboxBuildTestApiRequest Request);
}
