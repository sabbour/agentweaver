using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

internal static class ModelProviderDraftLimits
{
    public const int ProviderId = 128;
    public const int ProjectId = 32;
    public const int DisplayName = 128;
    public const int BaseUrl = 2048;
    public const int Deployment = 256;
    public const int WireApi = 32;
    public const int AzureApiVersion = 64;
    public const int ActorId = 256;
    public const int Hash = 64;
    public const int ReasoningEffortCount = 6;
    public const int ReasoningEffort = 16;
}

public enum ModelProviderDraftState
{
    Draft,
    Available,
}

public sealed class ModelProviderDraftHeadRecord
{
    public required string ProviderId { get; set; }
    public ModelProviderBindingScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public required string DisplayName { get; set; }
    public required string ProviderType { get; set; }
    public required string BaseUrl { get; set; }
    public required string Deployment { get; set; }
    public string? WireApi { get; set; }
    public string? AzureApiVersion { get; set; }
    public required string[] ReasoningEfforts { get; set; }
    public int? PromptCapacityTokens { get; set; }
    public ModelProviderDraftState State { get; set; }
    public long CurrentRevision { get; set; }
    public required string CreatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string UpdatedByActorId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ModelProviderDraftRevisionRecord
{
    public required string ProviderId { get; set; }
    public ModelProviderBindingScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public long ProviderRevision { get; set; }
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string DisplayName { get; set; }
    public required string ProviderType { get; set; }
    public required string BaseUrl { get; set; }
    public required string Deployment { get; set; }
    public string? WireApi { get; set; }
    public string? AzureApiVersion { get; set; }
    public required string[] ReasoningEfforts { get; set; }
    public int? PromptCapacityTokens { get; set; }
    public required string ConfigurationHash { get; set; }
    public string? CredentialVersion { get; set; }
    public Guid? CredentialSlotId { get; set; }
    public string? CredentialSecretId { get; set; }
    public string? RateCardId { get; set; }
    public string? RateCardVersion { get; set; }
    public string? RateCardPinJson { get; set; }
    public required string CreatedByActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ModelProviderReferenceReservationRecord
{
    public required string Reference { get; set; }
    public required string ReferenceFolded { get; set; }
    public required string ProviderId { get; set; }
    public long CreationRevision { get; set; }
    public Guid OperationId { get; set; }
    public Guid? InstallationId { get; set; }
    public required string Issuer { get; set; }
    public required string TenantId { get; set; }
    public required string Subject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record SaveModelProviderDraftRequest(
    Guid OperationId,
    long ExpectedRevision,
    string DisplayName,
    string ProviderType,
    string BaseUrl,
    string Deployment,
    string? WireApi = null,
    string? AzureApiVersion = null,
    ImmutableArray<string> ReasoningEfforts = default,
    int? PromptCapacityTokens = null,
    string? RateCardId = null,
    string? RateCardVersion = null)
{
    public string? Reference { get; init; }
    public string? TargetInstallationId { get; init; }
}

public sealed record ModelProviderDraft(
    ModelProviderDraftState State,
    ModelProviderBindingRevision Binding)
{
    public string? Reference { get; init; }
}

public sealed record ModelProviderDraftPage(
    IReadOnlyList<ModelProviderDraft> Providers,
    string? NextAfterProviderId)
{
    public string? SwitchMode { get; init; }
    public string? ActiveProviderId { get; init; }
    public long? SettingsRevision { get; init; }
}

public sealed class ModelProviderDraftException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static ModelProviderDraftException InvalidRequest(string message) =>
        new("model_provider_draft_invalid_request", message);

    public static ModelProviderDraftException Conflict() =>
        new("model_provider_draft_conflict", "The provider draft changed or the operation ID was reused.");

    public static ModelProviderDraftException ReferenceConflict() =>
        new("reference_conflict", "The provider reference is already in use.");

    public static ModelProviderDraftException TargetInstallationMismatch() =>
        new("target_installation_mismatch", "The target installation does not match.");

    public static ModelProviderDraftException ReferenceReconciliationRequired() =>
        new("reference_reconciliation_required", "The provider reference requires reconciliation.");

    public static ModelProviderDraftException Unavailable() =>
        new("model_provider_draft_unavailable", "The provider draft is unavailable.");

    public static ModelProviderDraftException NotFound() =>
        new("model_provider_draft_not_found", "The provider draft was not found.");
}

public enum ModelProviderCredentialOperationKind
{
    Publish,
    Cancel,
    Disable,
}

public sealed class ModelProviderCredentialOperationRecord
{
    public Guid OperationId { get; set; }
    public ModelProviderCredentialOperationKind OperationKind { get; set; }
    public required string ProviderId { get; set; }
    public ModelProviderBindingScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public long? ProviderRevision { get; set; }
    public long? ExpectedProviderRevision { get; set; }
    public long? OriginalProviderRevision { get; set; }
    public long? OriginalCredentialRevision { get; set; }
    public Guid? CredentialSlotId { get; set; }
    public string? CredentialSecretId { get; set; }
    public string? CredentialVersion { get; set; }
    public string? ConfigurationHash { get; set; }
    public string? ProviderState { get; set; }
    public string? RateCardId { get; set; }
    public string? RateCardVersion { get; set; }
    public string? RateCardPinHash { get; set; }
    public required string OriginalTenant { get; set; }
    public Guid OriginalMembershipId { get; set; }
    public long OriginalMembershipRevision { get; set; }
    public Guid CurrentMembershipId { get; set; }
    public long CurrentMembershipRevision { get; set; }
    public Guid CurrentAssignmentId { get; set; }
    public long CurrentAssignmentRevision { get; set; }
    public required string Issuer { get; set; }
    public required string ActorId { get; set; }
    public required string RequestFingerprint { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ModelProviderCredentialPreflightRecord
{
    public Guid PreflightId { get; set; }
    public Guid OperationId { get; set; }
    public required string Mode { get; set; }
    public ModelProviderBindingScope Scope { get; set; }
    public string? ProjectId { get; set; }
    public required string ProviderId { get; set; }
    public required string Issuer { get; set; }
    public required string ActorId { get; set; }
    public required string TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public Guid AssignmentId { get; set; }
    public long AssignmentRevision { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public ModelProviderCredentialOperationKind? ConsumedByKind { get; set; }
}
