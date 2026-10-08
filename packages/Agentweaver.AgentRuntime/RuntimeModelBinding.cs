using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

public sealed record RuntimeByokProvider(
    string Type,
    Uri BaseUrl,
    string WireApi = "responses",
    string? AzureApiVersion = null)
{
    public ImmutableDictionary<string, string> Headers { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    internal ProviderConfig ToSdkProvider(string modelId, string apiKey)
    {
        var baseUrl = BaseUrl.AbsoluteUri.TrimEnd('/');
        if (Type == "azure" && !baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            baseUrl += "/openai";
        return new ProviderConfig
        {
            Type = Type,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            WireApi = WireApi,
            Headers = Headers.Count == 0 ? null : new Dictionary<string, string>(Headers),
            Azure = AzureApiVersion is null ? null : new AzureOptions { ApiVersion = AzureApiVersion },
            ModelId = modelId,
            WireModel = modelId
        };
    }
}

public sealed record RuntimeModelBinding(
    string ModelId, ModelSourceMode SourceMode, RuntimeByokProvider? Provider = null)
{
    internal void Validate()
    {
        RuntimeContractValidation.ValidateIdentifier(ModelId);
        if (!Enum.IsDefined(SourceMode) ||
            SourceMode == ModelSourceMode.HostedCopilot && Provider is not null ||
            SourceMode == ModelSourceMode.Byok && Provider is null)
            throw new ArgumentException("An explicit, mode-compatible model binding is required.");
        if (Provider is not { } provider)
            return;
        if (provider.Type is not ("openai" or "azure" or "anthropic") ||
            !RuntimeContractValidation.IsHttpsEndpoint(provider.BaseUrl) ||
            provider.WireApi is not ("responses" or "completions") ||
            provider.Type != "azure" && provider.AzureApiVersion is not null ||
            provider.AzureApiVersion is { } apiVersion &&
                (string.IsNullOrWhiteSpace(apiVersion) || apiVersion.Any(char.IsControl)) ||
            provider.Headers is null ||
            provider.Headers.Any(header => string.IsNullOrWhiteSpace(header.Key) ||
                header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                header.Value is null || header.Value.Any(char.IsControl)))
            throw new ArgumentException("The approved BYOK provider configuration is invalid.");
    }
}
