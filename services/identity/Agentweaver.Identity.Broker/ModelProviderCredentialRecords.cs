using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Broker;

public enum ModelProviderCredentialScope
{
    Platform,
    Project,
}

public enum ModelProviderCredentialVaultSource
{
    Existing,
    Byok,
}

public enum ModelProviderCredentialState
{
    Enabled,
    Disabled,
}

public enum ModelProviderCredentialMutationKind
{
    RecordReference,
    Disable,
}

public enum ModelProviderCredentialWriteClaimState
{
    Claimed,
    Quarantined,
    Completed,
    PublicationPending,
    Published,
    Cancelled,
}

public enum ModelProviderCredentialWriteClaimStatus
{
    Acquired,
    Quarantined,
    Completed,
    Claimed,
    PublicationPending,
    Published,
    Cancelled,
}

public enum ModelProviderCredentialCurrentStatus
{
    Found,
    NotFound,
    Disabled,
}

public enum ModelProviderCredentialPinnedStatus
{
    Found,
    NotFound,
}

public sealed class ModelProviderCredentialHead
{
    public Guid CredentialSlotId { get; set; }
    public ModelProviderCredentialScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public required string ProviderId { get; set; }
    public ModelProviderCredentialVaultSource VaultSource { get; set; }
    public required string VaultUri { get; set; }
    public required string SecretName { get; set; }
    public required string CurrentVersion { get; set; }
    public ModelProviderCredentialState State { get; set; }
    public long Revision { get; set; }
    public required string SetBy { get; set; }
    public DateTimeOffset SetAt { get; set; }
}

public sealed class ModelProviderCredentialRevision
{
    public Guid CredentialSlotId { get; set; }
    public long Revision { get; set; }
    public ModelProviderCredentialScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public required string ProviderId { get; set; }
    public ModelProviderCredentialVaultSource VaultSource { get; set; }
    public required string VaultUri { get; set; }
    public required string SecretName { get; set; }
    public required string SecretVersion { get; set; }
    public ModelProviderCredentialState State { get; set; }
    public ModelProviderCredentialMutationKind MutationKind { get; set; }
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ModelProviderCredentialWriteClaim
{
    public Guid OperationId { get; set; }
    public Guid CredentialSlotId { get; set; }
    public ModelProviderCredentialScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public required string ProviderId { get; set; }
    public ModelProviderCredentialVaultSource VaultSource { get; set; }
    public required string VaultUri { get; set; }
    public required string SecretName { get; set; }
    public long ExpectedRevision { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string ActorId { get; set; }
    public ModelProviderCredentialWriteClaimState State { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? QuarantinedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? PendingSecretVersion { get; set; }
    public long? CompletedRevision { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? ExpectedProviderRevision { get; set; }
    public string? Purpose { get; set; }
    public string? Issuer { get; set; }
    public string? Tenant { get; set; }
    public long? PublishedProviderRevision { get; set; }
    public string? PublishedConfigurationHash { get; set; }
    public string? PublishedProviderState { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record RecordModelProviderCredentialReference(
    Guid OperationId,
    long ExpectedRevision,
    Guid CredentialSlotId,
    ModelProviderCredentialScope Scope,
    string? ProjectId,
    string ProviderId,
    ModelProviderCredentialVaultSource VaultSource,
    SecretRef Reference,
    string ActorId);

public sealed record DisableModelProviderCredential(
    Guid OperationId,
    long ExpectedRevision,
    Guid CredentialSlotId,
    ModelProviderCredentialScope Scope,
    string? ProjectId,
    string ProviderId,
    string ActorId);

public sealed record ClaimModelProviderCredentialWrite(
    Guid OperationId,
    long ExpectedRevision,
    Guid CredentialSlotId,
    ModelProviderCredentialScope Scope,
    string? ProjectId,
    string ProviderId,
    ModelProviderCredentialVaultSource VaultSource,
    string SecretName,
    string ActorId);

internal sealed record ClaimModelProviderCredentialPublication(
    ClaimModelProviderCredentialWrite Claim,
    long ExpectedProviderRevision,
    string Purpose,
    string Issuer,
    string Tenant,
    bool IsRecheck = false);

internal sealed record ModelProviderCredentialPublicationClaimResult(
    ModelProviderCredentialWriteClaimStatus Status,
    bool Acquired,
    ModelProviderCredentialReference? Credential,
    long? PublishedProviderRevision,
    string? PublishedConfigurationHash,
    DateTimeOffset? PublishedAt,
    string? PublishedProviderState = null);

public sealed record ModelProviderCredentialIdentity(
    Guid CredentialSlotId,
    ModelProviderCredentialScope Scope,
    string? ProjectId,
    string ProviderId);

public sealed record ModelProviderCredentialReference(
    ModelProviderCredentialIdentity Identity,
    ModelProviderCredentialVaultSource VaultSource,
    string VaultUri,
    SecretRef Reference,
    ModelProviderCredentialState State,
    long Revision,
    string ActorId,
    DateTimeOffset RecordedAt);

public sealed record ModelProviderCredentialCurrentRead(
    ModelProviderCredentialCurrentStatus Status,
    ModelProviderCredentialReference? Credential);

public sealed record ModelProviderCredentialPinnedRead(
    ModelProviderCredentialPinnedStatus Status,
    ModelProviderCredentialReference? Credential);

public sealed record ModelProviderCredentialWriteClaimResult(
    ModelProviderCredentialWriteClaimStatus Status,
    bool Acquired,
    ModelProviderCredentialReference? Credential);

public sealed class ModelProviderCredentialException(string code)
    : Exception($"Model provider credential operation failed ({code}).")
{
    public string Code { get; } = code;

    public static ModelProviderCredentialException InvalidRequest() => new("credential_request_invalid");
    public static ModelProviderCredentialException Conflict() => new("credential_slot_conflict");
    public static ModelProviderCredentialException VaultUnavailable() => new("credential_vault_unavailable");
}

internal sealed record ModelProviderCredentialRedemptionBackends(ISecretRedemption? Byok);
