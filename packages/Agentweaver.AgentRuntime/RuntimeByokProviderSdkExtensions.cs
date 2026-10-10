using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

internal static class RuntimeByokProviderSdkExtensions
{
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
