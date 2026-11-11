using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunStartPlacementRequest(
    int ContractVersion,
    RuntimeOwnerContext? Owner,
    RuntimeRunStartCleanupProjection? Cleanup = null,
    Guid BootstrapOperationId = default)
{
    public RuntimeRunStartPlacementRequest Validate()
    {
        if (ContractVersion is not (1 or 2) || (Owner is null) == (Cleanup is null))
            throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        if (Owner is { } owner)
        {
            RuntimeContractValidation.ValidateIdentifier(owner.SessionId);
            RuntimeContractValidation.ValidateHash(owner.AcceptedSelectionHash);
            if (owner.ContractVersion != 1 ||
                (ContractVersion == 1
                    ? owner.AgentId != RuntimeActionContract.CoordinatorRootStepId || owner.LogicalTurnOrdinal != 0
                    : owner.AgentId == RuntimeActionContract.CoordinatorRootStepId ||
                      owner.LogicalTurnOrdinal < 1 || string.IsNullOrWhiteSpace(owner.WorkflowStepId)) ||
                owner.ExecutionFence <= 0 ||
                BootstrapOperationId == Guid.Empty)
                throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        }
        else if (BootstrapOperationId != Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_request_invalid");
        if (Cleanup is { } cleanup && cleanup.ContractVersion != ContractVersion)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_request_invalid");
        Cleanup?.Validate();
        return this;
    }

    public RuntimeRunStartPlacementRequest ValidateFor(RuntimeOwnerContext owner)
    {
        Validate();
        if (Owner != owner || Cleanup is not null)
            throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementRequest(
    int ContractVersion,
    Guid OperationId,
    Guid ExpectedRuntimeInstanceId,
    long ExpectedRegistrationRevision,
    long ExpectedExecutionFence,
    string ExpectedAcceptedSelectionHash)
{
    public RuntimeReplacementRequest Validate()
    {
        RuntimeContractValidation.ValidateHash(ExpectedAcceptedSelectionHash);
        if (ContractVersion != 1 || OperationId == Guid.Empty ||
            ExpectedRuntimeInstanceId == Guid.Empty ||
            ExpectedRegistrationRevision < 1 || ExpectedExecutionFence < 1)
            throw new RuntimeAuthorizationException("runtime_replacement_request_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementSummary(
    int ContractVersion,
    Guid OperationId,
    string State,
    string Step,
    string? FailureCode,
    Guid PreviousRuntimeInstanceId,
    long PreviousRegistrationRevision,
    Guid? CurrentRuntimeInstanceId,
    long? CurrentRegistrationRevision,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string? PreviousPodUid,
    string? CurrentPodUid,
    string? WorkspaceVolumeId,
    long? WorkspaceResourceGeneration,
    long? WorkspaceDataGeneration,
    RuntimeHostRecoveryMode? RecoveryMode = null,
    RuntimeMaterialAcknowledgmentEvidence? RecoveryCacheMaterial = null,
    RuntimeHostNativeEmptyIntervalEvidence? ReconstructionInterval = null,
    RuntimeHostNativeCallSequenceEvidence? ReconstructionCallSequence = null)
{
    public RuntimeReplacementSummary Validate()
    {
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        if (ContractVersion != 1 || OperationId == Guid.Empty ||
            State is not ("pending" or "ready" or "blocked" or "failed" or "cleanup_pending" or "cleanup_complete") ||
            string.IsNullOrWhiteSpace(Step) || Step.Length > 48 ||
            FailureCode is { Length: > 96 } ||
            PreviousRuntimeInstanceId == Guid.Empty || PreviousRegistrationRevision < 1 ||
            ExecutionFence < 1 ||
            (CurrentRuntimeInstanceId is null) != (CurrentRegistrationRevision is null) ||
            CurrentRuntimeInstanceId == Guid.Empty || CurrentRegistrationRevision < 1 ||
            WorkspaceResourceGeneration < 1 || WorkspaceDataGeneration < 0)
            throw new RuntimeAuthorizationException("runtime_replacement_summary_invalid");
        if (PreviousPodUid is not null)
            RuntimeContractValidation.ValidateIdentifier(PreviousPodUid);
        if (CurrentPodUid is not null)
            RuntimeContractValidation.ValidateIdentifier(CurrentPodUid);
        if (WorkspaceVolumeId is not null)
            RuntimeContractValidation.ValidateIdentifier(WorkspaceVolumeId);
        if (RecoveryCacheMaterial is { } cache)
        {
            RuntimeContractValidation.ValidateHash(cache.ContentSha256);
            RuntimeContractValidation.ValidateHash(cache.BindingSha256);
            if (cache.EventId == Guid.Empty || cache.Position < 1 ||
                cache.Kind != SessionMaterialKind.SdkCache)
                throw new RuntimeAuthorizationException("runtime_replacement_summary_invalid");
        }
        if (ReconstructionInterval is { } interval)
        {
            RuntimeContractValidation.ValidateHash(interval.StartCursorSha256);
            RuntimeContractValidation.ValidateHash(interval.EndCursorSha256);
        }
        if (ReconstructionCallSequence is { } callSequence)
        {
            if (CurrentRuntimeInstanceId is not { } runtimeInstanceId ||
                CurrentRegistrationRevision is not { } registrationRevision)
                throw new RuntimeAuthorizationException("runtime_replacement_summary_invalid");
            callSequence.ValidateFor(runtimeInstanceId, registrationRevision, requireZero: true);
        }
        if (State == "ready" &&
            (RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
             RecoveryCacheMaterial is null ||
             ReconstructionCallSequence is null ||
             ReconstructionInterval is not { EventCount: 0, IsComplete: true } ||
             ReconstructionInterval.StartCursorSha256 != ReconstructionInterval.EndCursorSha256 ||
             string.IsNullOrWhiteSpace(PreviousPodUid) ||
             string.IsNullOrWhiteSpace(CurrentPodUid) ||
             string.Equals(PreviousPodUid, CurrentPodUid, StringComparison.Ordinal)))
            throw new RuntimeAuthorizationException("runtime_replacement_summary_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunStartCleanupProjection(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string AcceptedSelectionHash,
    string RunState,
    long CancellationFence,
    Guid IntentId,
    long IntentVersion,
    long ExecutionFence,
    Guid BootstrapOperationId,
    string? EnvironmentId,
    string CleanupDisposition,
    System.Text.Json.JsonElement ReceiptLinks)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TerminalFence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRootSessionId { get; init; }

    public RuntimeRunStartCleanupProjection Validate()
    {
        RuntimeContractValidation.ValidateIdentifier(TenantId);
        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        if (EnvironmentId is not null)
            RuntimeContractValidation.ValidateIdentifier(EnvironmentId);
        var terminalFenceValid = ContractVersion switch
        {
            1 => RunState == "cancelled" &&
                 CancellationFence > ExecutionFence && TerminalFence is null &&
                 ParentRootSessionId is null,
            2 => CancellationFence == 0 && TerminalFence is { } terminalFence &&
                 !string.IsNullOrWhiteSpace(ParentRootSessionId) &&
                 (RunState == "cancelled"
                     ? terminalFence > ExecutionFence
                     : RunState is "completed" or "failed" or "indeterminate" &&
                       terminalFence >= ExecutionFence),
            _ => false
        };
        if (!terminalFenceValid ||
            IntentId == Guid.Empty ||
            IntentVersion < 1 ||
            ExecutionFence < 1 ||
            BootstrapOperationId == Guid.Empty ||
            CleanupDisposition is not ("cleanup_pending" or "cleanup_complete") ||
            ReceiptLinks.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_projection_invalid");
        if (ParentRootSessionId is not null)
            RuntimeContractValidation.ValidateIdentifier(ParentRootSessionId);
        return this with { ReceiptLinks = ReceiptLinks.Clone() };
    }

    public void ValidateFor(RuntimeRunStartCleanupProjection current)
    {
        Validate();
        current.Validate();
        if (this with { ReceiptLinks = default } != current with { ReceiptLinks = default } ||
            !System.Text.Json.JsonElement.DeepEquals(ReceiptLinks, current.ReceiptLinks))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_projection_stale");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunStartCleanupReceipt(
    int ContractVersion,
    Guid IntentId,
    Guid SandboxOperationId,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    string? SandboxIdempotencyKey,
    string LeaseState,
    string CleanupDisposition)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderResourceRef? SandboxResource { get; init; }

    public RuntimeRunStartCleanupReceipt Validate()
    {
        if (ContractVersion is not (1 or 2) ||
            IntentId == Guid.Empty ||
            LeaseState is not ("absent" or "active" or "released" or "failed" or "reconciliationRequired" or
                "releasing" or "provisioning") ||
            CleanupDisposition is not ("cleanup_pending" or "cleanup_complete") ||
            ContractVersion == 1 && SandboxResource is not null ||
            SandboxResource is { } resource &&
                (resource.Seam != ProviderSeam.Sandbox || resource.Generation != ResourceGeneration ||
                 string.IsNullOrWhiteSpace(resource.ProviderId) || string.IsNullOrWhiteSpace(resource.ResourceId)))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if (LeaseState == "absent")
        {
            if (SandboxOperationId != Guid.Empty || ResourceGeneration != 0 ||
                ProviderFencingGeneration != 0 || SandboxIdempotencyKey is not null ||
                SandboxResource is not null || CleanupDisposition != "cleanup_complete")
                throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
            return this;
        }
        if (SandboxOperationId == Guid.Empty || ResourceGeneration < 1 || ProviderFencingGeneration < 1 ||
            string.IsNullOrWhiteSpace(SandboxIdempotencyKey) || SandboxIdempotencyKey.Length > 128)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if ((LeaseState == "released") != (CleanupDisposition == "cleanup_complete"))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        return this;
    }
}

public sealed record RuntimeRunStartCleanupAttempt(
    Guid SandboxOperationId,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    string SandboxIdempotencyKey)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderResourceRef? SandboxResource { get; init; }

    public RuntimeRunStartCleanupAttempt Validate()
    {
        if (SandboxOperationId == Guid.Empty || ResourceGeneration < 1 ||
            ProviderFencingGeneration < 1 || string.IsNullOrWhiteSpace(SandboxIdempotencyKey) ||
            SandboxIdempotencyKey.Length > 128 ||
            SandboxResource is { } resource &&
                (resource.Seam != ProviderSeam.Sandbox || resource.Generation != ResourceGeneration ||
                 string.IsNullOrWhiteSpace(resource.ProviderId) || string.IsNullOrWhiteSpace(resource.ResourceId)))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        return this;
    }

    public bool Matches(RuntimeRunStartCleanupAttempt other) =>
        SandboxOperationId == other.SandboxOperationId &&
        ResourceGeneration == other.ResourceGeneration &&
        ProviderFencingGeneration == other.ProviderFencingGeneration &&
        string.Equals(SandboxIdempotencyKey, other.SandboxIdempotencyKey, StringComparison.Ordinal) &&
        (SandboxResource is null || SandboxResource == other.SandboxResource);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunStartPlacementReceipt(
    int ContractVersion,
    string EnvironmentId,
    string SourceProfileId,
    string WorkspaceVolumeId,
    string SandboxIdempotencyKey,
    long WorkspaceResourceGeneration,
    long WorkspaceDataGeneration,
    long NetworkPolicyGeneration,
    Guid SandboxOperationId,
    ProviderResourceRef SandboxResource,
    long LifecycleGeneration,
    long LeaseRevision,
    long CurrentFencingGeneration,
    long ProviderFencingGeneration,
    DateTimeOffset LeaseExpiresAt)
{
    public string? PodUid { get; init; }

    public RuntimeRunStartPlacementReceipt Validate()
    {
        RuntimeContractValidation.ValidateIdentifier(EnvironmentId);
        RuntimeContractValidation.ValidateIdentifier(SourceProfileId);
        RuntimeContractValidation.ValidateIdentifier(WorkspaceVolumeId);
        if (string.IsNullOrWhiteSpace(SandboxIdempotencyKey) || SandboxIdempotencyKey.Length > 128)
            throw new RuntimeAuthorizationException("runtime_start_placement_receipt_invalid");
        if (ContractVersion != 1 ||
            WorkspaceResourceGeneration <= 0 || WorkspaceDataGeneration < 0 ||
            NetworkPolicyGeneration <= 0 || SandboxOperationId == Guid.Empty ||
            SandboxResource.Seam != ProviderSeam.Sandbox || SandboxResource.Generation <= 0 ||
            LifecycleGeneration <= 0 || LeaseRevision <= 0 ||
            CurrentFencingGeneration <= 0 ||
            ProviderFencingGeneration != CurrentFencingGeneration ||
            LeaseExpiresAt <= DateTimeOffset.MinValue)
            throw new RuntimeAuthorizationException("runtime_start_placement_receipt_invalid");
        if (PodUid is { } podUid)
            RuntimeContractValidation.ValidateIdentifier(podUid);
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRunStartRetirementRequest(
    int ContractVersion,
    RuntimeOwnerContext Owner,
    Guid BootstrapOperationId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    RuntimeRunStartPlacementReceipt Placement)
{
    public RuntimeRunStartRetirementRequest Validate()
    {
        new RuntimeRunStartPlacementRequest(1, Owner, null, BootstrapOperationId).Validate();
        Placement.Validate();
        if (ContractVersion != 1 ||
            RuntimeInstanceId == Guid.Empty ||
            RegistrationRevision < 1 ||
            Placement.EnvironmentId != RuntimeRunStartKeys.EnvironmentId(Owner) ||
            Placement.WorkspaceVolumeId != RuntimeRunStartKeys.WorkspaceVolumeId(Owner) ||
            Placement.SandboxIdempotencyKey !=
                RuntimeRunStartKeys.SandboxIdempotencyKey(Owner, BootstrapOperationId) ||
            Placement.CurrentFencingGeneration != Placement.ProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_start_retirement_request_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementRetirementRequest(
    int ContractVersion,
    Guid OperationId,
    Guid IntentId,
    RuntimeRunStartRetirementRequest Retirement)
{
    public RuntimeReplacementRetirementRequest Validate()
    {
        if (ContractVersion != 1 || OperationId == Guid.Empty || IntentId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_replacement_request_invalid");
        Retirement.Validate();
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementPlacementRequest(
    int ContractVersion,
    Guid OperationId,
    RuntimeOwnerContext Owner,
    Guid PreviousBootstrapOperationId,
    Guid BootstrapOperationId,
    RuntimeRunStartPlacementReceipt PreviousPlacement)
{
    public RuntimeReplacementPlacementRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Owner);
        ArgumentNullException.ThrowIfNull(PreviousPlacement);
        PreviousPlacement.Validate();
        if (ContractVersion != 1 || OperationId == Guid.Empty ||
            PreviousBootstrapOperationId == Guid.Empty ||
            BootstrapOperationId == Guid.Empty ||
            BootstrapOperationId == PreviousBootstrapOperationId ||
            Owner.ContractVersion != 1 ||
            Owner.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            Owner.LogicalTurnOrdinal != 0 ||
            Owner.ExecutionFence <= 0 ||
            PreviousPlacement.EnvironmentId != RuntimeRunStartKeys.EnvironmentId(Owner) ||
            PreviousPlacement.WorkspaceVolumeId != RuntimeRunStartKeys.WorkspaceVolumeId(Owner) ||
            PreviousPlacement.CurrentFencingGeneration != PreviousPlacement.ProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_replacement_request_invalid");
        return this;
    }
}

public sealed record RuntimeRunStartRetirementReceipt(
    int ContractVersion,
    Guid BootstrapOperationId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid SandboxOperationId,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    string SandboxIdempotencyKey,
    string State)
{
    public RuntimeRunStartRetirementReceipt ValidateFor(RuntimeRunStartRetirementRequest request)
    {
        request.Validate();
        if (ContractVersion != 1 ||
            BootstrapOperationId != request.BootstrapOperationId ||
            RuntimeInstanceId != request.RuntimeInstanceId ||
            RegistrationRevision != request.RegistrationRevision ||
            SandboxOperationId != request.Placement.SandboxOperationId ||
            ResourceGeneration != request.Placement.SandboxResource.Generation ||
            ProviderFencingGeneration != request.Placement.ProviderFencingGeneration ||
            SandboxIdempotencyKey != request.Placement.SandboxIdempotencyKey ||
            State is not ("pending" or "released"))
            throw new RuntimeAuthorizationException("runtime_start_retirement_receipt_invalid");
        return this;
    }
}

public static class RuntimeRunStartKeys
{
    public static Guid ReplacementOperationId(Guid replacementOperationId, string purpose)
    {
        if (replacementOperationId == Guid.Empty)
            throw new ArgumentException("A replacement operation ID is required.", nameof(replacementOperationId));
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 32 ||
            purpose.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("A replacement operation purpose is invalid.", nameof(purpose));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"agentweaver:runtime-replacement:{replacementOperationId:N}:{purpose}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string IdempotencyKey(RuntimeOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return IdempotencyKey(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId, owner.AcceptedSelectionHash);
    }

    public static string WorkspaceVolumeId(RuntimeOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return WorkspaceVolumeId(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId, owner.AcceptedSelectionHash);
    }

    public static string SandboxIdempotencyKey(RuntimeOwnerContext owner, Guid bootstrapOperationId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return SandboxIdempotencyKey(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId,
            owner.AcceptedSelectionHash, bootstrapOperationId);
    }

    public static string SandboxIdempotencyKey(
        string tenantId,
        string projectId,
        string runId,
        string sessionId,
        string acceptedSelectionHash,
        Guid bootstrapOperationId)
    {
        if (bootstrapOperationId == Guid.Empty)
            throw new ArgumentException("A bootstrap operation ID is required.", nameof(bootstrapOperationId));
        return IdempotencyKey(tenantId, projectId, runId, sessionId, acceptedSelectionHash) +
            ":sandbox:" + bootstrapOperationId.ToString("N");
    }

    public static string EnvironmentId(RuntimeOwnerContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return EnvironmentId(
            owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId, owner.AcceptedSelectionHash);
    }

    public static string EnvironmentId(
        string tenantId, string projectId, string runId, string sessionId, string acceptedSelectionHash)
    {
        var fields = new[]
        {
            tenantId, projectId, runId,
            IdempotencyKey(tenantId, projectId, runId, sessionId, acceptedSelectionHash)
        };
        var canonical = string.Concat(fields.Select(field => $"{field.Length}:{field}"));
        return "env-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string IdempotencyKey(
        string tenantId, string projectId, string runId, string sessionId, string acceptedSelectionHash) =>
        "coordinator-start-" + HashOwner(
            tenantId, projectId, runId, sessionId, acceptedSelectionHash);

    public static string WorkspaceVolumeId(
        string tenantId, string projectId, string runId, string sessionId, string acceptedSelectionHash) =>
        "workspace-" + HashOwner(
            tenantId, projectId, runId, sessionId, acceptedSelectionHash);

    private static string HashOwner(RuntimeOwnerContext owner)
        => HashOwner(owner.TenantId, owner.ProjectId, owner.RunId, owner.SessionId, owner.AcceptedSelectionHash);

    private static string HashOwner(
        string tenantId, string projectId, string runId, string sessionId, string acceptedSelectionHash)
    {
        RuntimeContractValidation.ValidateIdentifier(tenantId);
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        RuntimeContractValidation.ValidateHash(acceptedSelectionHash);
        var fields = new[]
        {
            tenantId, projectId, runId, sessionId,
            acceptedSelectionHash.ToLowerInvariant()
        };
        var canonical = string.Concat(fields.Select(field => $"{field.Length}:{field}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
