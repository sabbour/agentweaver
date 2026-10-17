using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

/// <summary>Maps the accepted provider-neutral BYOK contract into the pinned Copilot SDK shape.</summary>
internal static class RuntimeByokProviderSdkExtensions
{
    /// <summary>
    /// Copies the selected provider configuration into SDK fields, adding the Azure OpenAI
    /// path segment when needed and leaving absent optional headers unset.
    /// </summary>
    internal static ProviderConfig ToSdkProvider(
        this RuntimeByokProvider provider, string modelId, string apiKey)
    {
        var baseUrl = provider.BaseUrl.AbsoluteUri.TrimEnd('/');
        if (provider.Type == "azure" && !baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            baseUrl += "/openai";
        return new ProviderConfig
        {
            Type = provider.Type,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            WireApi = provider.WireApi,
            Headers = provider.Headers.Count == 0 ? null : new Dictionary<string, string>(provider.Headers),
            Azure = provider.AzureApiVersion is null
                ? null : new AzureOptions { ApiVersion = provider.AzureApiVersion },
            ModelId = modelId,
            WireModel = modelId
        };
    }
}
