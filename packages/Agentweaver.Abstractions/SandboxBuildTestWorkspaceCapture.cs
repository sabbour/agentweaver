using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public sealed record SandboxBuildTestWorkspaceCaptureReference(
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestCheckpointReference Checkpoint,
    string PodUid,
    string RepositoryRelativePath,
    string SeedSha256,
    string DocumentSha256,
    long DocumentByteLength,
    string OutputTreeSha,
    string ManifestSha256,
    long ManifestByteLength,
    string PatchSha256,
    long PatchByteLength,
    string PackageSha256,
    long PackageByteLength,
    ImmutableArray<SandboxBuildTestOutputEvidence> Outputs)
{
    public SandboxBuildTestWorkspaceCaptureReference Validate(
        SandboxBuildTestAcceptedCommand command,
        string requestFingerprint,
        SandboxBuildTestPodReference pod)
    {
        _ = command.ValidateMetadata();
        _ = pod.Validate();
        var source = command.WorkspaceSource ??
            throw new ArgumentException("A complete workspace capture requires version 2 repository input.");
        if (OperationId != command.OperationId || ImmutableHash != command.ImmutableHash ||
            RequestFingerprint != requestFingerprint || Checkpoint != command.Checkpoint ||
            PodUid != pod.Uid || RepositoryRelativePath != source.PreparedRepositoryRelativePath ||
            SeedSha256 != source.SeedSha256 ||
            !SandboxBuildTestHashes.IsSha256(DocumentSha256) ||
            OutputTreeSha is not { Length: 40 } || !OutputTreeSha.All(Uri.IsHexDigit) ||
            !SandboxBuildTestHashes.IsSha256(ManifestSha256) ||
            !SandboxBuildTestHashes.IsSha256(PatchSha256) ||
            !SandboxBuildTestHashes.IsSha256(PackageSha256) ||
            ManifestByteLength is < 1 or > ProducedRunCaptureLimits.MaximumManifestBytes ||
            PatchByteLength is < 0 or > ProducedRunCaptureLimits.MaximumDiffBytes ||
            PackageByteLength is < ProducedRunCaptureLimits.PackageHeaderBytes or
                > ProducedRunCaptureLimits.MaximumPackageBytes ||
            DocumentByteLength != ProducedRunCaptureLimits.DocumentHeaderBytes +
                ManifestByteLength + PatchByteLength + PackageByteLength ||
            Outputs.IsDefault || Outputs.Length != command.Outputs.Length)
            throw new ArgumentException("The complete workspace capture differs from its accepted operation.");
        for (var index = 0; index < Outputs.Length; index++)
        {
            var evidence = Outputs[index];
            var required = command.Outputs[index];
            if (evidence is null || evidence.CollectorPodUid != pod.Uid ||
                evidence.CollectorContainerName != SandboxBuildTestLimits.WorkspaceCaptureContainerName ||
                evidence.Name != required.Name || evidence.RelativePath != required.RelativePath ||
                evidence.Required != required.Required || evidence.MaximumBytes != required.MaximumBytes ||
                evidence.CapturedBytes < 0 || evidence.CapturedBytes > required.MaximumBytes ||
                required.Required && !evidence.Exists ||
                (evidence.Exists ? !SandboxBuildTestHashes.IsSha256(evidence.CapturedSha256) :
                    evidence.CapturedBytes != 0 || evidence.CapturedSha256 is not null))
                throw new ArgumentException("The complete workspace capture has invalid output evidence.");
        }
        return this;
    }

    public void ValidateAcknowledgment(
        SandboxBuildTestWorkspaceSource source,
        ProducedRunCaptureJournalEntry acknowledgment)
    {
        ArgumentNullException.ThrowIfNull(acknowledgment);
        if (acknowledgment.Position < 1)
            throw new ArgumentException("The workspace capture has no durable journal position.");
        ValidateProof(source, acknowledgment.Capture);
    }

    public void ValidateProof(SandboxBuildTestWorkspaceSource source, ProducedRunCaptureProof proof)
    {
        _ = source.Validate();
        ProducedRunCaptureContractValidation.Validate(proof);
        if (proof.Identity != source.Identity || proof.TenantId != source.TenantId ||
            proof.ActorIssuer != source.ActorIssuer || proof.ActorSubject != source.ActorSubject ||
            proof.SourceControlPinId != source.SourceControlPinId ||
            !string.Equals(proof.AcceptedSelectionHash, source.AcceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase) ||
            proof.WorkspaceId != source.WorkspaceId ||
            proof.WorkspaceIncarnationId != source.WorkspaceIncarnationId ||
            proof.BranchName != source.BranchName || proof.BaseSha != source.BaseSha ||
            proof.RepositoryId != source.SourceControlResource.ResourceId ||
            proof.ResourceGeneration != source.SourceControlResource.Generation ||
            proof.ManifestSha256 != ManifestSha256 || proof.ManifestByteLength != ManifestByteLength ||
            proof.OutputTreeSha != OutputTreeSha ||
            proof.PatchSha256 != PatchSha256 || proof.PatchByteLength != PatchByteLength ||
            proof.PackageSha256 != PackageSha256 || proof.PackageByteLength != PackageByteLength)
            throw new ArgumentException("The output proof belongs to another workspace capture.");
    }

}

public sealed record SandboxBuildTestWorkspaceCaptureAcknowledgmentRequest(
    SandboxBuildTestApiRequest Request,
    ProducedRunCaptureJournalEntry Capture);

public sealed record SandboxBuildTestWorkspaceCaptureAcknowledgmentVerification(
    SandboxBuildTestWorkspaceCaptureReference Reference,
    SandboxBuildTestPodReference Pod,
    ProducedRunCaptureJournalEntry Capture);
