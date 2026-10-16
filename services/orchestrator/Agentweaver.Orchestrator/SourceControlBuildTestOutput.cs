using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static partial class SourceControlEndpoints
{
    internal static async Task<ProducedRunCaptureJournalEntry> AdmitBuildTestOutputAsync(
        HttpContext context,
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        CoordinationOwnerStore coordinationOwner,
        SourceControlOwnerStore sourceControlOwner,
        EventsAddressedMessageClient events,
        Func<CancellationToken, Task<byte[]>> readNativeBytes,
        Func<CancellationToken, Task> requireCurrentExecution,
        CancellationToken cancellationToken)
    {
        var command = intent.ToAcceptedCommand();
        var source = command.WorkspaceSource ??
            throw new CoordinationException("maf_execution_workspace_seed_unavailable", StatusCodes.Status409Conflict);
        var reference = operation.WorkspaceCapture ??
            throw new CoordinationException("maf_execution_workspace_capture_unavailable", StatusCodes.Status409Conflict);
        _ = operation.Validate(command, command.ExecutionOptions.MaximumOutputBytes);
        await requireCurrentExecution(cancellationToken).ConfigureAwait(false);
        var run = await ReadPinnedSourceControlRunAsync(
            context, source.Identity.ProjectId, source.Identity.RunId, source.Identity.SessionId,
            options, projects, decisions, sourceControlOwner, cancellationToken).ConfigureAwait(false);
        ValidateBuildTestInputRun(source, command.Checkpoint, run);
        var captureId = ProducedRunCaptureContractValidation.CreateIdentity(
            source.Identity, source.SourceControlPinId, source.AcceptedSelectionHash, source.WorkspaceId,
            source.WorkspaceIncarnationId, source.BranchName, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.BaseSha, reference.OutputTreeSha,
            reference.ManifestSha256).CaptureId;
        var retained = await sourceControlOwner.ReadOutputCaptureAsync(
            source.Identity, captureId, source.AcceptedSelectionHash, cancellationToken).ConfigureAwait(false);
        byte[] bytes;
        if (retained is null)
        {
            bytes = await readNativeBytes(cancellationToken).ConfigureAwait(false);
            await requireCurrentExecution(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                reference.ValidateProof(source, retained.Proof);
            }
            catch (ArgumentException)
            {
                throw new CoordinationException("maf_execution_workspace_capture_acknowledgment_conflict",
                    StatusCodes.Status409Conflict);
            }
            bytes = SerializeBuildTestOutput(retained);
        }
        var document = ParseBuildTestOutput(bytes, reference, command);
        await requireCurrentExecution(cancellationToken).ConfigureAwait(false);
        await RequireCurrentSourceControlRunAsync(
            context, run, projects, decisions, cancellationToken).ConfigureAwait(false);
        var capture = retained ?? await sourceControlOwner.RegisterOutputCaptureAsync(
            run.Actor, run.Identity, run.Selection, run.Pin, run.DecisionStateVersion,
            document, cancellationToken).ConfigureAwait(false);
        var (admitted, _) = await WriteAndAdmitOutputCaptureAsync(
            context, run, capture, projects, decisions, coordinationOwner, sourceControlOwner,
            events, cancellationToken).ConfigureAwait(false);
        var acknowledgment = ReadBuildTestAcknowledgment(admitted, reference, source);
        await requireCurrentExecution(cancellationToken).ConfigureAwait(false);
        return acknowledgment;
    }

    internal static GitWorkspaceCaptureDocument ParseBuildTestOutput(
        byte[] bytes, SandboxBuildTestWorkspaceCaptureReference reference,
        SandboxBuildTestAcceptedCommand command)
    {
        try
        {
            var seed = command.WorkspaceSeed ??
                throw new ArgumentException("The accepted command has no retained seed.");
            return GitWorkspaceBuildTestOutput.Parse(bytes, reference, command.WorkspaceSource ??
                throw new ArgumentException("The accepted command has no repository source."),
                seed.AsSpan(), command.Outputs);
        }
        catch (ArgumentException)
        {
            throw new CoordinationException("maf_execution_workspace_capture_invalid", StatusCodes.Status502BadGateway);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CoordinationException("maf_execution_workspace_capture_invalid", StatusCodes.Status502BadGateway);
        }
    }

    internal static byte[] SerializeBuildTestOutput(SourceControlOutputCaptureRecord capture) =>
        GitWorkspaceCapturePackage.SerializeDocument(new(
            GitWorkspaceCapturePackage.ParseManifest(capture.ManifestBytes),
            capture.ManifestBytes, capture.Proof.ManifestSha256,
            capture.PatchBytes, capture.Proof.PatchSha256,
            capture.PackageBytes, capture.Proof.PackageSha256));

    internal static ProducedRunCaptureJournalEntry ReadBuildTestAcknowledgment(
        SourceControlOutputCaptureRecord capture,
        SandboxBuildTestWorkspaceCaptureReference reference,
        SandboxBuildTestWorkspaceSource source)
    {
        if (capture.State != "admitted" || capture.EventPosition is not > 0 ||
            capture.AdmittedAt is null ||
            capture.ObjectKey != ProducedRunCaptureContractValidation.CreatePackageReference(capture.Proof).Key.Value)
            throw new CoordinationException("maf_execution_workspace_capture_unadmitted", StatusCodes.Status409Conflict);
        var entry = new ProducedRunCaptureJournalEntry(capture.Proof, capture.EventPosition.Value);
        try
        {
            reference.ValidateAcknowledgment(source, entry);
        }
        catch (ArgumentException)
        {
            throw new CoordinationException("maf_execution_workspace_capture_acknowledgment_conflict",
                StatusCodes.Status409Conflict);
        }
        return entry;
    }
}
