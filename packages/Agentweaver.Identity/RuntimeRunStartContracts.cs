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
        if (ContractVersion != 1 || (Owner is null) == (Cleanup is null))
            throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        if (Owner is { } owner)
        {
            RuntimeContractValidation.ValidateIdentifier(owner.SessionId);
            RuntimeContractValidation.ValidateHash(owner.AcceptedSelectionHash);
            if (owner.ContractVersion != 1 ||
                owner.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
                owner.LogicalTurnOrdinal != 0 ||
                owner.ExecutionFence <= 0 ||
                BootstrapOperationId == Guid.Empty)
                throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        }
        else if (BootstrapOperationId != Guid.Empty)
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
    public RuntimeRunStartCleanupProjection Validate()
    {
        RuntimeContractValidation.ValidateIdentifier(TenantId);
        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        if (EnvironmentId is not null)
            RuntimeContractValidation.ValidateIdentifier(EnvironmentId);
        if (ContractVersion != 1 ||
            RunState != "cancelled" ||
            CancellationFence <= ExecutionFence ||
            IntentId == Guid.Empty ||
            IntentVersion < 1 ||
            ExecutionFence < 1 ||
            BootstrapOperationId == Guid.Empty ||
            CleanupDisposition is not ("cleanup_pending" or "cleanup_complete") ||
            ReceiptLinks.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_projection_invalid");
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
    public RuntimeRunStartCleanupReceipt Validate()
    {
        if (ContractVersion != 1 ||
            IntentId == Guid.Empty ||
            LeaseState is not ("absent" or "active" or "released" or "failed" or "reconciliationRequired" or
                "releasing" or "provisioning") ||
            CleanupDisposition is not ("cleanup_pending" or "cleanup_complete"))
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        if (LeaseState == "absent")
        {
            if (SandboxOperationId != Guid.Empty || ResourceGeneration != 0 ||
                ProviderFencingGeneration != 0 || SandboxIdempotencyKey is not null ||
                CleanupDisposition != "cleanup_complete")
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
    public RuntimeRunStartCleanupAttempt Validate()
    {
        if (SandboxOperationId == Guid.Empty || ResourceGeneration < 1 ||
            ProviderFencingGeneration < 1 || string.IsNullOrWhiteSpace(SandboxIdempotencyKey) ||
            SandboxIdempotencyKey.Length > 128)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_invalid");
        return this;
    }

    public bool Matches(RuntimeRunStartCleanupAttempt other) =>
        SandboxOperationId == other.SandboxOperationId &&
        ResourceGeneration == other.ResourceGeneration &&
        ProviderFencingGeneration == other.ProviderFencingGeneration &&
        string.Equals(SandboxIdempotencyKey, other.SandboxIdempotencyKey, StringComparison.Ordinal);
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
