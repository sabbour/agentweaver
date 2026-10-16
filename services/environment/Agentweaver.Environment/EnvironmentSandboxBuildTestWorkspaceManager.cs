using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed partial class EnvironmentSandboxBuildTestCommandManager
{
    private async Task<EnvironmentSandboxBuildTestCommandState> PrepareNativeWorkspaceAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId,
        SandboxBuildTestApiRequest request, SandboxBuildTestAcceptedCommand acceptedCommand,
        EnvironmentSandboxBuildTestCommandState state, CancellationToken cancellationToken)
    {
        if (state.Operation.WorkspacePreparation is not null)
            return state;
        state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
            request, acceptedCommand, state, cancellationToken).ConfigureAwait(false);
        if (IsTerminal(state.Operation.Status))
            return state;
        state = await AddAttemptAsync(state, NextAttemptMarker(state, "workspace:prepare:"), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var pod = state.Operation.Pod ??
                throw new EnvironmentLifecycleException("buildtest_record_invalid", "The native command Pod is missing.");
            var receipt = await commandProvider.PrepareWorkspaceAsync(
                CreateProviderRequest(state), pod, cancellationToken).ConfigureAwait(false);
            if (receipt is null)
                return state;
            receipt.Validate(acceptedCommand, state.Operation.RequestFingerprint, pod);
            state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
                request, acceptedCommand, state, cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Operation.Status))
                return state;
            return await SaveOperationAsync(state, state.Operation with
            {
                WorkspacePreparation = receipt, Status = SandboxBuildTestOperationStatus.Running, FailureCode = null
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException failure)
        {
            return await MarkProviderFailureAsync(state, failure, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> CaptureNativeWorkspaceAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId,
        SandboxBuildTestApiRequest request, SandboxBuildTestAcceptedCommand acceptedCommand,
        EnvironmentSandboxBuildTestCommandState state, SandboxBuildTestPodObservation commandObservation,
        CancellationToken cancellationToken)
    {
        state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
            request, acceptedCommand, state, cancellationToken).ConfigureAwait(false);
        if (IsTerminal(state.Operation.Status))
            return state;
        state = await AddAttemptAsync(state, NextAttemptMarker(state, "workspace:capture:"), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var pod = state.Operation.CollectorPod ??
                throw new EnvironmentLifecycleException("buildtest_record_invalid", "The native collector Pod is missing.");
            var preparation = state.Operation.WorkspacePreparation ??
                throw new EnvironmentLifecycleException("buildtest_record_invalid", "The native preparation receipt is missing.");
            var capture = await commandProvider.CaptureWorkspaceAsync(
                CreateProviderRequest(state), pod, preparation, cancellationToken).ConfigureAwait(false);
            if (capture is null)
                return state;
            capture.Validate(acceptedCommand, state.Operation.RequestFingerprint, pod);
            state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
                request, acceptedCommand, state, cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Operation.Status))
                return state;
            return await SaveOperationAsync(state, state.Operation with
            {
                WorkspaceCapture = capture,
                CollectorManifestSha256 = capture.ManifestSha256,
                OutputEvidence = capture.Outputs,
                Terminal = commandObservation.Terminal,
                Output = commandObservation.Output,
                Status = SandboxBuildTestOperationStatus.Completed,
                FailureCode = null
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException failure)
        {
            return await MarkProviderFailureAsync(state, failure, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImmutableArray<byte>> ReadWorkspaceCaptureAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId, Guid operationId,
        CancellationToken cancellationToken)
    {
        var (state, command, request) = await ReadCurrentCapturedWorkspaceAsync(
            caller, projectId, runId, environmentId, operationId, cancellationToken).ConfigureAwait(false);
        if (state.Operation.WorkspaceCaptureAcknowledgment is not null)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_already_admitted",
                "The admitted bytes must be read from SourceControl, not the native collector.");
        var bytes = await commandProvider.ReadWorkspaceCaptureAsync(CreateProviderRequest(state),
            state.Operation.CollectorPod!, state.Operation.WorkspacePreparation!,
            state.Operation.WorkspaceCapture!, cancellationToken).ConfigureAwait(false);
        state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
            request, command, state, cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status != SandboxBuildTestOperationStatus.Completed)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_stale",
                "The native workspace capture lost current owner authority during export.");
        if (bytes.IsDefault || bytes.Length != state.Operation.WorkspaceCapture!.DocumentByteLength ||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan())) !=
                state.Operation.WorkspaceCapture.DocumentSha256)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_integrity_invalid",
                "The exported native bytes differ from the persisted capture reference.");
        return bytes;
    }

    public async Task<SandboxBuildTestOperationResult> AcknowledgeWorkspaceCaptureAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId, Guid operationId,
        SandboxBuildTestWorkspaceCaptureAcknowledgmentRequest acknowledgmentRequest,
        CancellationToken cancellationToken)
    {
        var (state, command, request) = await ReadCurrentCapturedWorkspaceAsync(
            caller, projectId, runId, environmentId, operationId, cancellationToken).ConfigureAwait(false);
        if (!SameJson(request, acknowledgmentRequest.Request))
            throw new EnvironmentLifecycleException("buildtest_request_conflict",
                "The acknowledgment request differs from the accepted native operation.");
        var reference = state.Operation.WorkspaceCapture!;
        reference.ValidateAcknowledgment(command.WorkspaceSource!, acknowledgmentRequest.Capture);
        if (state.Operation.WorkspaceCaptureAcknowledgment is { } previous &&
            previous != acknowledgmentRequest.Capture)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_acknowledgment_conflict",
                "The native operation already has a different journal acknowledgment.");
        await commandVerifier.VerifyWorkspaceCaptureAcknowledgmentAsync(command,
            new(reference, state.Operation.CollectorPod!, acknowledgmentRequest.Capture), cancellationToken)
            .ConfigureAwait(false);
        state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
            request, command, state, cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status != SandboxBuildTestOperationStatus.Completed)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_stale",
                "The native capture lost current owner authority before acknowledgment.");
        if (state.Operation.WorkspaceCaptureAcknowledgment is null)
            state = await SaveOperationAsync(state, state.Operation with
            {
                WorkspaceCaptureAcknowledgment = acknowledgmentRequest.Capture
            }, cancellationToken).ConfigureAwait(false);
        const string cleanupMarker = "workspace:capture:released";
        if (!state.AttemptedEffects.Contains(cleanupMarker, StringComparer.Ordinal))
        {
            foreach (var pod in new[] { state.Operation.CollectorPod!, state.Operation.Pod! })
            {
                state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
                    request, command, state, cancellationToken).ConfigureAwait(false);
                if (state.Operation.Status != SandboxBuildTestOperationStatus.Completed)
                    throw new EnvironmentLifecycleException("buildtest_workspace_capture_stale",
                        "The native capture lost current owner authority before cleanup.");
                await commandProvider.DeleteAsync(CreateProviderRequest(state), pod,
                    cancellationToken).ConfigureAwait(false);
            }
            state = await commandStore.SaveAsync(state.Operation.ExpectedBinding.Fence.Owner,
                state with { AttemptedEffects = state.AttemptedEffects.Add(cleanupMarker) },
                cancellationToken).ConfigureAwait(false);
        }
        return new(state.Operation, true);
    }

    private async Task<(EnvironmentSandboxBuildTestCommandState State,
        SandboxBuildTestAcceptedCommand Command, SandboxBuildTestApiRequest Request)> ReadCurrentCapturedWorkspaceAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId, Guid operationId,
        CancellationToken cancellationToken)
    {
        var context = await ReadPinnedContextAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var state = await commandStore.GetAsync(context.Owner, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException("buildtest_operation_unknown", "The exact native operation is missing.");
        var command = await ResolveAcceptedCommandAsync(state.AcceptedCommand.Checkpoint, cancellationToken)
            .ConfigureAwait(false);
        state = RestoreAcceptedCommand(state, command);
        var request = new SandboxBuildTestApiRequest(command.Checkpoint, state.Operation.ExpectedBinding);
        state = await RequireCurrentIntentAsync(caller, projectId, runId, environmentId,
            request, command, state, cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status != SandboxBuildTestOperationStatus.Completed ||
            state.Operation.WorkspacePreparation is null || state.Operation.WorkspaceCapture is null ||
            state.Operation.CollectorPod is null)
            throw new EnvironmentLifecycleException("buildtest_workspace_capture_unavailable",
                "The exact native workspace has no completed, current capture.");
        state.Operation.Validate(command, command.ExecutionOptions.MaximumOutputBytes);
        return (state, command, request);
    }
}
