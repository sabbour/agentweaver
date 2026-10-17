using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Shared validation and classification rules for BuildTest workflow steps and owner operations.
/// The dispatcher and checkpoint store use these checks to reject stale provider bindings and mismatched results.
/// </summary>
internal static class MafBuildTestCommandContract
{
    /// <summary>
    /// Returns whether a workflow step is a platform BuildTest step with a command payload.
    /// </summary>
    internal static bool IsExecutable(WorkflowStepDefinition step) =>
        step.Mode == WorkflowStepMode.Platform &&
        step.PlatformGate == WorkflowPlatformGate.BuildTest &&
        step.BuildTestCommand is not null;

    /// <summary>
    /// Checks that a pinned sandbox provider still matches the command's expected binding and run.
    /// </summary>
    /// <param name="pinned">The provider binding stored with the accepted plan, if any.</param>
    /// <param name="expected">The binding recorded for the BuildTest command.</param>
    /// <param name="runId">The run that must own the binding.</param>
    /// <returns><see langword="true"/> only when all identifiers, versions, options, and the BuildTest capability match.</returns>
    internal static bool MatchesProvider(
        PinnedProviderBinding? pinned,
        SandboxBuildTestExpectedBinding expected,
        string runId)
    {
        var provider = expected.SandboxProviderBinding;
        return pinned is not null && provider is not null &&
            pinned.RunId == runId && pinned.Resource == expected.SandboxResource &&
            pinned.ProviderId == provider.ProviderId &&
            pinned.AdapterVersion.ToString() == provider.AdapterVersion &&
            pinned.OptionsSchemaVersion == provider.OptionsSchemaVersion &&
            pinned.OptionsRevision == provider.OptionsRevision &&
            pinned.NegotiatedCapabilities.Contains(SandboxCapabilities.BuildTestCommandPod);
    }

    /// <summary>
    /// Validates an owner's preparation response against the accepted step, tenant, environment, and pinned provider.
    /// </summary>
    /// <param name="preparation">The environment owner's proposed command binding.</param>
    /// <param name="identity">The owning project, run, and session.</param>
    /// <param name="tenantId">The authorized tenant.</param>
    /// <param name="environmentId">The environment selected for this command.</param>
    /// <param name="step">The accepted workflow step.</param>
    /// <param name="pinned">The provider binding pinned by the run selection.</param>
    /// <exception cref="CoordinationException">Thrown when the step is not executable or the prepared binding is stale.</exception>
    internal static void ValidatePreparation(
        SandboxBuildTestBindingPreparation preparation,
        SessionIdentity identity,
        string tenantId,
        string environmentId,
        WorkflowStepDefinition step,
        PinnedProviderBinding? pinned)
    {
        if (!IsExecutable(step))
            throw new CoordinationException(
                "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
        _ = preparation.Validate(identity.SessionId, step.BuildTestCommand!.ExecutionProfileReference);
        var owner = preparation.ExpectedBinding.Fence.Owner;
        if (owner.TenantId != tenantId || owner.ProjectId != identity.ProjectId ||
            owner.RunId != identity.RunId || owner.EnvironmentId != environmentId ||
            preparation.ProviderOptionsRevision != preparation.ExpectedBinding.SandboxProviderBinding?.OptionsRevision ||
            !MatchesProvider(pinned, preparation.ExpectedBinding, identity.RunId))
            throw new CoordinationException(
                "maf_execution_build_test_binding_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Verifies an operation snapshot against the intent and maps its owner status to orchestration progress.
    /// </summary>
    /// <param name="intent">The immutable accepted command intent.</param>
    /// <param name="operation">The current operation snapshot returned by the environment owner.</param>
    /// <returns>The corresponding orchestration task status.</returns>
    /// <exception cref="CoordinationException">Thrown when the request fingerprint, expected binding, or owner status is inconsistent.</exception>
    internal static MafExecutionTaskStatus Classify(
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation)
    {
        var accepted = intent.ToAcceptedCommand(requireSeed: false);
        _ = operation.Validate(accepted, intent.ExecutionOptions.MaximumOutputBytes);
        if (operation.RequestFingerprint != intent.ToApiRequest().ComputeRequestFingerprint(accepted) ||
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(operation.ExpectedBinding),
                JsonSerializer.SerializeToElement(intent.ExpectedBinding)))
            throw new CoordinationException(
                "maf_execution_build_test_operation_conflict", StatusCodes.Status409Conflict);
        return operation.Status switch
        {
            SandboxBuildTestOperationStatus.Completed => MafExecutionTaskStatus.Succeeded,
            SandboxBuildTestOperationStatus.Failed => MafExecutionTaskStatus.Failed,
            SandboxBuildTestOperationStatus.Interrupted or SandboxBuildTestOperationStatus.Stale =>
                MafExecutionTaskStatus.Indeterminate,
            SandboxBuildTestOperationStatus.Reserved or SandboxBuildTestOperationStatus.Running or
                SandboxBuildTestOperationStatus.ReconciliationRequired => MafExecutionTaskStatus.Running,
            _ => throw new CoordinationException(
                "maf_execution_build_test_operation_invalid", StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>
    /// Hashes the serialized operation snapshot so a checkpoint can bind to the exact observed result.
    /// </summary>
    internal static string HashOperation(SandboxBuildTestOperationSnapshot operation) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation)));
}

/// <summary>
/// Immutable terminal evidence copied into a MAF checkpoint after the environment owner finishes a BuildTest command.
/// Validation ties the receipt to its accepted intent and requires output and capture acknowledgements for success.
/// </summary>
internal sealed record MafBuildTestTerminalReceipt(
    Guid OperationId,
    string ImmutableHash,
    string RequestFingerprint,
    SandboxBuildTestOperationStatus Status,
    string OperationEvidenceSha256,
    SandboxBuildTestPodReference? Pod,
    SandboxBuildTestTerminalEvidence? Terminal,
    string? OutputSha256,
    long? OutputBytes,
    SandboxBuildTestPodReference? CollectorPod,
    SandboxBuildTestTerminalEvidence? CollectorTerminal,
    string? CollectorManifestSha256,
    ImmutableArray<SandboxBuildTestOutputEvidence> Outputs,
    string? FailureCode,
    DateTimeOffset UpdatedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt? WorkspacePreparation
        { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxBuildTestWorkspaceCaptureReference? WorkspaceCapture { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProducedRunCaptureJournalEntry? WorkspaceCaptureAcknowledgment { get; init; }

    /// <summary>
    /// Validates that this terminal receipt belongs to the given intent and returns its orchestration status.
    /// </summary>
    /// <param name="intent">The checkpointed BuildTest intent whose command and limits must match.</param>
    /// <returns>The succeeded, failed, or indeterminate terminal status.</returns>
    /// <exception cref="ArgumentException">Thrown when the receipt is incomplete, malformed, or bound to different command evidence.</exception>
    internal MafExecutionTaskStatus ValidateFor(MafExecutionBuildTestIntent intent)
    {
        var accepted = intent.ToAcceptedCommand(requireSeed: false);
        if (OperationId != intent.OperationId || ImmutableHash != accepted.ImmutableHash ||
            RequestFingerprint != intent.ToApiRequest().ComputeRequestFingerprint(accepted) ||
            OperationEvidenceSha256 is not { Length: 64 } || !OperationEvidenceSha256.All(Uri.IsHexDigit) ||
            Outputs.IsDefault ||
            WorkspaceCaptureAcknowledgment is not null && Status != SandboxBuildTestOperationStatus.Completed ||
            FailureCode is { Length: > 128 } || FailureCode?.Any(char.IsControl) == true ||
            OutputBytes < 0 || OutputBytes > intent.ExecutionOptions.MaximumOutputBytes ||
            OutputSha256 is not null && (OutputSha256.Length != 64 || !OutputSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("The persisted BuildTest terminal receipt is invalid.");
        if (Pod is not null)
            _ = Pod.Validate();
        if (Terminal is not null)
        {
            _ = Terminal.Validate();
            if (Terminal.SourcePodUid != Pod?.Uid)
                throw new ArgumentException("The persisted BuildTest terminal Pod binding is invalid.");
        }
        if (CollectorPod is not null)
        {
            _ = CollectorPod.Validate();
            if (CollectorPod.Uid == Pod?.Uid)
                throw new ArgumentException("The persisted BuildTest collector must use a separate Pod.");
        }
        if (CollectorTerminal is not null)
        {
            _ = CollectorTerminal.Validate();
            if (CollectorTerminal.SourcePodUid != CollectorPod?.Uid ||
                CollectorTerminal.ContainerName != SandboxBuildTestLimits.OutputCollectorContainerName)
                throw new ArgumentException("The persisted BuildTest collector terminal binding is invalid.");
        }
        if (WorkspacePreparation is not null)
            WorkspacePreparation.Validate(accepted, RequestFingerprint, Pod ??
                throw new ArgumentException("The preparation receipt requires the native command Pod."));
        if (WorkspaceCapture is not null)
            WorkspaceCapture.Validate(accepted, RequestFingerprint, CollectorPod ??
                throw new ArgumentException("The capture reference requires the native collector Pod."));
        if (WorkspaceCaptureAcknowledgment is not null)
            (WorkspaceCapture ?? throw new ArgumentException("The acknowledgment requires its native capture."))
                .ValidateAcknowledgment(accepted.WorkspaceSource ??
                    throw new ArgumentException("The acknowledgment requires version 2 source input."),
                    WorkspaceCaptureAcknowledgment);
        if (Status == SandboxBuildTestOperationStatus.Completed)
        {
            if (Pod is null || Terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 } ||
                OutputSha256 is null || OutputBytes is null ||
                Outputs.Length != accepted.Outputs.Length)
                throw new ArgumentException("The persisted BuildTest success receipt lacks terminal and output evidence.");
            if (accepted.WorkspaceSource is not null)
            {
                if (WorkspacePreparation is null || WorkspaceCapture is null ||
                    WorkspaceCaptureAcknowledgment is null || CollectorPod is null || CollectorTerminal is not null ||
                    CollectorManifestSha256 != WorkspaceCapture.ManifestSha256 ||
                    !Outputs.SequenceEqual(WorkspaceCapture.Outputs))
                    throw new ArgumentException(
                        "A complete BuildTest receipt requires the admitted full output and durable Events acknowledgment.");
                return MafExecutionTaskStatus.Succeeded;
            }
            if (WorkspacePreparation is not null || WorkspaceCapture is not null ||
                WorkspaceCaptureAcknowledgment is not null ||
                accepted.Outputs.IsEmpty &&
                    (CollectorPod is not null || CollectorTerminal is not null || CollectorManifestSha256 is not null) ||
                !accepted.Outputs.IsEmpty &&
                    (CollectorPod is null || CollectorTerminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 } ||
                     CollectorTerminal.SourcePodUid != CollectorPod.Uid ||
                     CollectorManifestSha256 is not { Length: 64 } ||
                     !CollectorManifestSha256.All(Uri.IsHexDigit)))
                throw new ArgumentException("The persisted BuildTest success receipt lacks terminal and output evidence.");
            for (var index = 0; index < Outputs.Length; index++)
            {
                var evidence = Outputs[index];
                var required = accepted.Outputs[index];
                if (evidence is null || evidence.Name != required.Name ||
                    evidence.RelativePath != required.RelativePath || evidence.Required != required.Required ||
                    evidence.MaximumBytes != required.MaximumBytes ||
                    evidence.CollectorPodUid != CollectorPod?.Uid ||
                    evidence.CollectorContainerName != SandboxBuildTestLimits.OutputCollectorContainerName ||
                    evidence.CapturedBytes < 0 || evidence.CapturedBytes > required.MaximumBytes ||
                    required.Required && !evidence.Exists ||
                    (evidence.Exists
                        ? evidence.CapturedSha256 is not { Length: 64 } || !evidence.CapturedSha256.All(Uri.IsHexDigit)
                        : evidence.CapturedBytes != 0 || evidence.CapturedSha256 is not null))
                    throw new ArgumentException("The persisted BuildTest output evidence is invalid.");
            }
            return MafExecutionTaskStatus.Succeeded;
        }
        return Status switch
        {
            SandboxBuildTestOperationStatus.Failed => MafExecutionTaskStatus.Failed,
            SandboxBuildTestOperationStatus.Interrupted or SandboxBuildTestOperationStatus.Stale =>
                MafExecutionTaskStatus.Indeterminate,
            _ => throw new ArgumentException("A persisted BuildTest receipt must be terminal.")
        };
    }

    /// <summary>
    /// Copies the terminal operation fields into a receipt after rejecting a still-running operation.
    /// </summary>
    /// <param name="intent">The accepted command being executed.</param>
    /// <param name="operation">The owner snapshot to preserve as terminal evidence.</param>
    /// <returns>The receipt with a digest of the complete operation snapshot.</returns>
    /// <exception cref="ArgumentException">Thrown when the owner operation is not terminal.</exception>
    internal static MafBuildTestTerminalReceipt Create(
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation)
    {
        if (MafBuildTestCommandContract.Classify(intent, operation) == MafExecutionTaskStatus.Running)
            throw new ArgumentException("A BuildTest terminal receipt requires a terminal owner operation.");
        return new MafBuildTestTerminalReceipt(operation.OperationId, operation.ImmutableHash, operation.RequestFingerprint, operation.Status,
            MafBuildTestCommandContract.HashOperation(operation), operation.Pod, operation.Terminal,
            operation.Output?.CapturedSha256, operation.Output?.CapturedByteCount,
            operation.CollectorPod, operation.CollectorTerminal, operation.CollectorManifestSha256,
            operation.OutputEvidence, operation.FailureCode, operation.UpdatedAt)
        {
            WorkspacePreparation = operation.WorkspacePreparation,
            WorkspaceCapture = operation.WorkspaceCapture,
            WorkspaceCaptureAcknowledgment = operation.WorkspaceCaptureAcknowledgment
        };
    }
}
