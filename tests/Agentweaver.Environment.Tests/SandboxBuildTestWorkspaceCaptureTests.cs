using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class SandboxBuildTestWorkspaceCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionTwoCompletionRequiresNativePreparationAndFullCaptureEvenWithNoDeclaredOutputs(bool emptyOutputs)
    {
        var (command, operation) = Fixture(emptyOutputs);

        operation.Validate(command, command.ExecutionOptions.MaximumOutputBytes);
        Assert.NotNull(operation.CollectorPolicy);
        Assert.Throws<ArgumentException>(() => (operation with { WorkspacePreparation = null })
            .Validate(command, command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() => (operation with { WorkspaceCapture = null })
            .Validate(command, command.ExecutionOptions.MaximumOutputBytes));
        Assert.Throws<ArgumentException>(() => (operation with { CollectorPolicy = null })
            .Validate(command, command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void ExactJournalAcknowledgmentIsTheOnlyAllowedNewEvidenceAfterNativeCompletion()
    {
        var (command, completed) = Fixture();
        var entry = Verification(command).Capture;
        var admitted = completed with { WorkspaceCaptureAcknowledgment = entry };

        admitted.Validate(command, command.ExecutionOptions.MaximumOutputBytes);
        Assert.True(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(completed, admitted));
        Assert.True(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(
            admitted, admitted with { UpdatedAt = admitted.UpdatedAt.AddSeconds(1) }));
        Assert.False(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(
            admitted, completed));
        Assert.False(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(
            completed, admitted with { Output = null }));
        Assert.False(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(
            admitted, admitted with { WorkspaceCaptureAcknowledgment = entry with { Position = entry.Position + 1 } }));
        Assert.False(EnvironmentSandboxBuildTestCommandStore.IsAllowedOperationTransition(
            admitted, admitted with { WorkspaceCapture = admitted.WorkspaceCapture! with { PodUid = "other" } }));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("repository")]
    [InlineData("workspace")]
    [InlineData("branch")]
    [InlineData("package")]
    public void CallerAcknowledgmentCannotReplaceTheExactCurrentCapture(string fault)
    {
        var (command, operation) = Fixture();
        var entry = Verification(command).Capture;
        entry = fault switch
        {
            "position" => entry with { Position = 0 },
            "actor" => entry with { Capture = entry.Capture with { ActorSubject = "other" } },
            "tenant" => entry with { Capture = entry.Capture with { TenantId = "other" } },
            "repository" => entry with { Capture = entry.Capture with { RepositoryId = "other" } },
            "workspace" => entry with { Capture = entry.Capture with { WorkspaceIncarnationId = Guid.NewGuid() } },
            "branch" => entry with { Capture = entry.Capture with { BranchName = "other" } },
            "package" => entry with { Capture = entry.Capture with { PackageSha256 = new string('f', 64) } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<ArgumentException>(() => (operation with { WorkspaceCaptureAcknowledgment = entry })
            .Validate(command, command.ExecutionOptions.MaximumOutputBytes));
    }

    [Fact]
    public void AdmittedStateRetainsOnlyReferencesAndNotFileBytesOrTheAcceptedSeed()
    {
        var (command, operation) = Fixture();
        var state = new EnvironmentSandboxBuildTestCommandState(command,
            operation with { WorkspaceCaptureAcknowledgment = Verification(command).Capture },
            ["workspace:capture:released"]);

        var json = EnvironmentSandboxBuildTestCommandStore.SerializeMutableState(state);

        Assert.Contains("workspaceCaptureAcknowledgment", json);
        Assert.Contains("documentSha256", json);
        Assert.DoesNotContain("workspaceSeed", json);
        Assert.DoesNotContain("packageBytes", json);
        Assert.DoesNotContain("manifestBytes", json);
    }

    [Fact]
    public void VersionTwoCannotAcceptAnOutputLargerThanTheExistingFullPackageFileLimit()
    {
        var (command, _) = Fixture();
        command = command with
        {
            ExecutionOptions = command.ExecutionOptions with { MaximumFileOutputBytes = 67_108_864 },
            Outputs = [command.Outputs[0] with { MaximumBytes = ProducedRunCaptureLimits.MaximumFileBytes + 1 }]
        };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };

        Assert.Throws<ArgumentException>(() => command.Validate());
    }

    internal static SandboxBuildTestWorkspaceCaptureAcknowledgmentVerification Verification(
        SandboxBuildTestAcceptedCommand command,
        SandboxBuildTestApiRequest? request = null,
        SandboxBuildTestPodReference? collector = null)
    {
        var source = command.WorkspaceSource!;
        collector ??= new SandboxBuildTestPodReference("sandbox", "collector", "collector-uid", "18");
        request ??= new SandboxBuildTestApiRequest(command.Checkpoint,
            SandboxBuildTestCommandContractTests.CreateFixture().Request.ExpectedBinding);
        var outputs = command.Outputs.Select(output => new SandboxBuildTestOutputEvidence(
            collector.Uid, SandboxBuildTestLimits.WorkspaceCaptureContainerName,
            output.Name, output.RelativePath, output.Required, output.MaximumBytes,
            true, 3, Convert.ToHexStringLower(SHA256.HashData("xml"u8)))).ToImmutableArray();
        var reference = new SandboxBuildTestWorkspaceCaptureReference(
            command.OperationId, command.ImmutableHash, request.ComputeRequestFingerprint(command), command.Checkpoint,
            collector.Uid, source.PreparedRepositoryRelativePath, source.SeedSha256,
            new string('a', 64), ProducedRunCaptureLimits.DocumentHeaderBytes + 128 + 23,
            new string('b', 40), new string('c', 64), 128, new string('d', 64), 0, new string('e', 64), 23, outputs);
        var identity = ProducedRunCaptureContractValidation.CreateIdentity(
            source.Identity, source.SourceControlPinId, source.AcceptedSelectionHash, source.WorkspaceId,
            source.WorkspaceIncarnationId, source.BranchName, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.BaseSha, reference.OutputTreeSha, reference.ManifestSha256);
        return new(reference, collector, new(new(
            2, source.Identity, identity.CaptureId, identity.EventId, source.ActorIssuer, source.ActorSubject,
            source.TenantId, source.SourceControlPinId, source.AcceptedSelectionHash, source.WorkspaceId,
            source.WorkspaceIncarnationId, source.BranchName, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.BaseSha, reference.OutputTreeSha,
            reference.ManifestSha256, reference.ManifestByteLength, reference.PatchSha256, reference.PatchByteLength,
            reference.PackageSha256, reference.PackageByteLength, DateTimeOffset.UnixEpoch), 17));
    }

    private static (SandboxBuildTestAcceptedCommand Command, SandboxBuildTestOperationSnapshot Operation) Fixture(
        bool emptyOutputs = false)
    {
        var fixture = SandboxBuildTestCommandContractTests.CreateFixture();
        var command = fixture.Command with
        {
            ContractVersion = 2,
            WorkspaceSource = SandboxBuildTestCommandContractTests.WorkspaceSource(),
            WorkspaceSeed = new byte[1024].ToImmutableArray(),
            ExecutionOptions = fixture.Command.ExecutionOptions with { CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode },
            Outputs = emptyOutputs ? [] : fixture.Command.Outputs
        };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        fixture = fixture with { Command = command };
        var operation = SandboxBuildTestCommandContractTests.CreateSnapshot(fixture);
        var verification = Verification(command);
        operation = operation with
        {
            Status = SandboxBuildTestOperationStatus.Completed,
            Terminal = new(operation.Pod!.Uid, operation.Output!.ContainerName,
                SandboxBuildTestTerminationKind.Exited, 0, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1), null),
            CollectorPod = verification.Pod,
            CollectorManifestSha256 = verification.Reference.ManifestSha256,
            OutputEvidence = verification.Reference.Outputs,
            WorkspacePreparation = new(command.OperationId, command.ImmutableHash, operation.RequestFingerprint,
                command.Checkpoint, operation.Pod.Uid, command.WorkspaceSource!.PreparedRepositoryRelativePath,
                command.WorkspaceSource.SeedSha256, command.WorkspaceSource.BaselineTreeSha),
            WorkspaceCapture = verification.Reference
        };
        return (command, operation);
    }
}
