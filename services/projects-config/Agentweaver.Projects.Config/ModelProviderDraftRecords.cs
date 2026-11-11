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
    public string? RateCardId { get; set; }
    public long? RateCardVersion { get; set; }
    public required string CreatedByActorId { get; set; }
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
    int? PromptCapacityTokens = null);

public sealed record ModelProviderDraft(
    ModelProviderDraftState State,
    ModelProviderBindingRevision Binding);

public sealed class ModelProviderDraftException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static ModelProviderDraftException InvalidRequest(string message) =>
        new("model_provider_draft_invalid_request", message);

    public static ModelProviderDraftException Conflict() =>
        new("model_provider_draft_conflict", "The provider draft changed or the operation ID was reused.");

    public static ModelProviderDraftException NotFound() =>
        new("model_provider_draft_not_found", "The provider draft was not found.");
}
