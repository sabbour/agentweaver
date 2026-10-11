using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafBuildTestSourceOutputTests
{
    [Fact]
    public void NativeDocumentValidationRetainsAllBaselineAndBinaryOutputBytes()
    {
        var (command, document, reference) = Fixture();
        var bytes = GitWorkspaceCapturePackage.SerializeDocument(document);

        var parsed = SourceControlEndpoints.ParseBuildTestOutput(bytes, reference, command);

        Assert.Equal(document.ManifestBytes, parsed.ManifestBytes);
        Assert.Equal(document.PatchBytes, parsed.PatchBytes);
        Assert.Equal(document.PackageBytes, parsed.PackageBytes);
        Assert.Equal(new byte[] { 0, 255, 128, 10 },
            GitWorkspaceCapturePackage.ExtractFile(
                parsed.PackageBytes, parsed.Manifest, parsed.PackageSha256, command.Outputs[0].RelativePath));
        Assert.Contains(parsed.Manifest.Files, file => file.Path == "README.md");
    }

    [Fact]
    public void RetainedPendingBytesReconstructTheSameFullDocumentWithoutNativeRecapture()
    {
        var (command, document, reference) = Fixture();
        var proof = Acknowledgment(command.WorkspaceSource!, reference).Capture;
        var pending = new SourceControlOutputCaptureRecord(proof, "pending", null, null, null,
            document.ManifestBytes, document.PatchBytes, document.PackageBytes);

        var bytes = SourceControlEndpoints.SerializeBuildTestOutput(pending);
        var parsed = SourceControlEndpoints.ParseBuildTestOutput(bytes, reference, command);

        Assert.Equal(GitWorkspaceCapturePackage.SerializeDocument(document), bytes);
        Assert.Equal(reference.PackageSha256, parsed.PackageSha256);
        Assert.Throws<CoordinationException>(() =>
            SourceControlEndpoints.ReadBuildTestAcknowledgment(pending, reference, command.WorkspaceSource!));
    }

    [Fact]
    public void AdmittedAcknowledgmentRequiresTheCanonicalObjectAndJournalPosition()
    {
        var (command, document, reference) = Fixture();
        var entry = Acknowledgment(command.WorkspaceSource!, reference);
        var admitted = new SourceControlOutputCaptureRecord(entry.Capture, "admitted",
            ProducedRunCaptureContractValidation.CreatePackageReference(entry.Capture).Key.Value,
            entry.Position, DateTimeOffset.UnixEpoch,
            document.ManifestBytes, document.PatchBytes, document.PackageBytes);

        Assert.Equal(entry, SourceControlEndpoints.ReadBuildTestAcknowledgment(
            admitted, reference, command.WorkspaceSource!));
        foreach (var changed in new[]
                 {
                     admitted with { ObjectKey = "different-object" },
                     admitted with { EventPosition = 0 },
                     admitted with { AdmittedAt = null },
                     admitted with { State = "pending" }
                 })
            Assert.Throws<CoordinationException>(() =>
                SourceControlEndpoints.ReadBuildTestAcknowledgment(changed, reference, command.WorkspaceSource!));
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("digest")]
    [InlineData("manifest")]
    [InlineData("seed")]
    public void CompleteOutputRejectsChangedBytesSectionsOrAcceptedSeed(string fault)
    {
        var (command, document, reference) = Fixture();
        var bytes = GitWorkspaceCapturePackage.SerializeDocument(document);
        switch (fault)
        {
            case "byte": bytes[^1] ^= 1; break;
            case "short": bytes = bytes[..^1]; break;
            case "extra": bytes = [.. bytes, 0]; break;
            case "digest": reference = reference with { DocumentSha256 = new string('b', 64) }; break;
            case "manifest": reference = reference with { ManifestSha256 = new string('b', 64) }; break;
            case "seed": command = command with { WorkspaceSeed = new byte[1024].ToImmutableArray() }; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var failure = Assert.Throws<CoordinationException>(() =>
            SourceControlEndpoints.ParseBuildTestOutput(bytes, reference, command));

        Assert.Equal("maf_execution_workspace_capture_invalid", failure.Code);
    }

    [Theory]
    [InlineData("baseline-change")]
    [InlineData("baseline-delete")]
    [InlineData("undeclared-file")]
    public void OutputCannotReplaceAcceptedBaselineOrAddUndeclaredFiles(string fault)
    {
        var (command, document, _) = Fixture();
        var files = document.Manifest.Files.Select(file => new GitWorkspaceCapturedFile(
            file.Path, file.Mode, file.Sha256, file.ByteLength,
            GitWorkspaceCapturePackage.ExtractFile(document.PackageBytes, document.Manifest,
                document.PackageSha256, file.Path).ToImmutableArray())).ToImmutableArray();
        files = fault switch
        {
            "baseline-change" => files.SetItem(0, File("README.md", [1, 2])),
            "baseline-delete" => files.RemoveAt(0),
            "undeclared-file" => files.Add(File("unexpected", [1, 2])),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var changed = GitWorkspaceCapturePackage.Create(new(
            document.Manifest.WorkspaceId, document.Manifest.RunId, document.Manifest.RepositoryId,
            document.Manifest.ResourceGeneration, document.Manifest.WorkspaceIncarnationId,
            document.Manifest.BaseSha, document.Manifest.OutputTreeSha, string.Empty, files,
            document.Manifest.BranchName));
        var reference = Reference(command, changed);

        Assert.Throws<GitWorkspaceException>(() => SourceControlEndpoints.ParseBuildTestOutput(
            GitWorkspaceCapturePackage.SerializeDocument(changed), reference, command));
    }

    internal static ProducedRunCaptureJournalEntry Acknowledgment(
        SandboxBuildTestWorkspaceSource source, SandboxBuildTestWorkspaceCaptureReference reference)
    {
        var identity = ProducedRunCaptureContractValidation.CreateIdentity(
            source.Identity, source.SourceControlPinId, source.AcceptedSelectionHash,
            source.WorkspaceId, source.WorkspaceIncarnationId, source.BranchName,
            source.SourceControlResource.ResourceId, source.SourceControlResource.Generation,
            source.BaseSha, reference.OutputTreeSha, reference.ManifestSha256);
        var proof = new ProducedRunCaptureProof(
            2, source.Identity, identity.CaptureId, identity.EventId, source.ActorIssuer, source.ActorSubject,
            source.TenantId, source.SourceControlPinId, source.AcceptedSelectionHash, source.WorkspaceId,
            source.WorkspaceIncarnationId, source.BranchName, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.BaseSha, reference.OutputTreeSha,
            reference.ManifestSha256, reference.ManifestByteLength,
            reference.PatchSha256, reference.PatchByteLength,
            reference.PackageSha256, reference.PackageByteLength, DateTimeOffset.UnixEpoch);
        return new(proof, 17);
    }

    private static (SandboxBuildTestAcceptedCommand Command, GitWorkspaceCaptureDocument Document,
        SandboxBuildTestWorkspaceCaptureReference Reference) Fixture()
    {
        var input = MafBuildTestSourceInputTests.Fixture();
        var source = MafBuildTestSourceInputTests.Produce(input);
        var command = input.Intent.AttachWorkspaceInput(source, input.Seed.Bytes.ToImmutableArray()).ToAcceptedCommand();
        var document = GitWorkspaceCapturePackage.Create(new(
            source.WorkspaceId, source.Identity.RunId, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.WorkspaceIncarnationId,
            source.BaseSha, new string('b', 40), "binary output\n",
            [File("README.md", "input"u8.ToArray()), File(command.Outputs[0].RelativePath, [0, 255, 128, 10])],
            source.BranchName));
        return (command, document, Reference(command, document));
    }

    private static GitWorkspaceCapturedFile File(string path, byte[] bytes) =>
        new(path, "100644", GitWorkspaceCapturePackage.Hash(bytes), bytes.Length, bytes.ToImmutableArray());

    private static SandboxBuildTestWorkspaceCaptureReference Reference(
        SandboxBuildTestAcceptedCommand command, GitWorkspaceCaptureDocument document)
    {
        var source = command.WorkspaceSource!;
        var bytes = GitWorkspaceCapturePackage.SerializeDocument(document);
        var output = document.Manifest.Files.Single(file => file.Path == command.Outputs[0].RelativePath);
        var required = command.Outputs[0];
        return new(command.OperationId, command.ImmutableHash, new string('a', 64),
            command.Checkpoint, "collector-uid", source.PreparedRepositoryRelativePath, source.SeedSha256,
            GitWorkspaceCapturePackage.Hash(bytes), bytes.Length, document.Manifest.OutputTreeSha,
            document.ManifestSha256, document.ManifestBytes.Length,
            document.PatchSha256, document.PatchBytes.Length,
            document.PackageSha256, document.PackageBytes.Length,
            [new("collector-uid", SandboxBuildTestLimits.WorkspaceCaptureContainerName,
                required.Name, required.RelativePath, required.Required, required.MaximumBytes,
                true, output.ByteLength, output.Sha256)]);
    }
}
