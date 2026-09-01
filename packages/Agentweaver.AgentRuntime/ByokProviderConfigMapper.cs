using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Shared helpers that map a <see cref="ByokProviderConfiguration"/> onto the GitHub Copilot SDK's
/// <see cref="GitHub.Copilot.ProviderConfig"/> shape. Used by every runtime entry point that can
/// run against a BYOK provider (assistant, workflow agents, operator assistant) so they all
/// forward wire API / custom headers / Azure API version consistently.
/// </summary>
internal static class ByokProviderConfigMapper
{
    /// <summary>Converts the optional custom-headers map to the dictionary shape the SDK expects,
    /// or <see langword="null"/> when there are none.</summary>
    public static Dictionary<string, string>? ToHeaderDictionary(IReadOnlyDictionary<string, string>? headers) =>
        headers is { Count: > 0 } ? new Dictionary<string, string>(headers) : null;

    /// <summary>Builds the Azure-specific options (API version) when the provider is an Azure
    /// OpenAI provider with an API version configured; otherwise <see langword="null"/>.</summary>
    public static GitHub.Copilot.AzureOptions? ToAzureOptions(ByokProviderConfiguration configuration) =>
        configuration.Type == "azure" && !string.IsNullOrWhiteSpace(configuration.AzureApiVersion)
            ? new GitHub.Copilot.AzureOptions { ApiVersion = configuration.AzureApiVersion }
            : null;
}
