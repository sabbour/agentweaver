using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class MafBuildTestCommandContract
{
    internal static bool IsExecutable(WorkflowStepDefinition step) =>
        step.Mode == WorkflowStepMode.Platform &&
        step.PlatformGate == WorkflowPlatformGate.BuildTest &&
        step.BuildTestCommand is not null;

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

    internal static MafExecutionTaskStatus Classify(
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation)
    {
        var accepted = intent.ToAcceptedCommand();
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

    internal static string HashOperation(SandboxBuildTestOperationSnapshot operation) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation)));
}

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
    internal MafExecutionTaskStatus ValidateFor(MafExecutionBuildTestIntent intent)
    {
        var accepted = intent.ToAcceptedCommand();
        if (OperationId != intent.OperationId || ImmutableHash != accepted.ImmutableHash ||
            RequestFingerprint != intent.ToApiRequest().ComputeRequestFingerprint(accepted) ||
            OperationEvidenceSha256 is not { Length: 64 } || !OperationEvidenceSha256.All(Uri.IsHexDigit) ||
            Outputs.IsDefault ||
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
        if (Status == SandboxBuildTestOperationStatus.Completed)
        {
            if (Pod is null || Terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 } ||
                OutputSha256 is null || OutputBytes is null ||
                Outputs.Length != accepted.Outputs.Length ||
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

    internal static MafBuildTestTerminalReceipt Create(
        MafExecutionBuildTestIntent intent,
        SandboxBuildTestOperationSnapshot operation)
    {
        if (MafBuildTestCommandContract.Classify(intent, operation) == MafExecutionTaskStatus.Running)
            throw new ArgumentException("A BuildTest terminal receipt requires a terminal owner operation.");
        return new(operation.OperationId, operation.ImmutableHash, operation.RequestFingerprint, operation.Status,
            MafBuildTestCommandContract.HashOperation(operation), operation.Pod, operation.Terminal,
            operation.Output?.CapturedSha256, operation.Output?.CapturedByteCount,
            operation.CollectorPod, operation.CollectorTerminal, operation.CollectorManifestSha256,
            operation.OutputEvidence, operation.FailureCode, operation.UpdatedAt);
    }
}
