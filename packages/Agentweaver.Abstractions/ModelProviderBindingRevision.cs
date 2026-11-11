using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public enum ModelProviderBindingScope
{
    Platform,
    Project,
}

public sealed record ModelProviderBindingRevision(
    int ContractVersion,
    string ProviderId,
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string DisplayName,
    string ProviderType,
    string BaseUrl,
    string Deployment,
    string? WireApi,
    string? AzureApiVersion,
    ImmutableArray<string> ReasoningEfforts,
    int? PromptCapacityTokens,
    long ProviderRevision,
    string? CredentialVersion,
    string? RateCardId,
    long? RateCardVersion,
    string ConfigurationHash)
{
    public const int CurrentContractVersion = 1;
}
