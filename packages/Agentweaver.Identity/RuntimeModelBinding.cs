using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Allow-listed provider configuration for one hosted or BYOK model in the runtime.</summary>
public sealed record RuntimeByokProvider(
    string Type,
    Uri BaseUrl,
    string WireApi = "responses",
    string? AzureApiVersion = null)
{
    /// <summary>Additional provider headers copied into the SDK configuration.</summary>
    public ImmutableDictionary<string, string> Headers { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// One enabled model choice and its source mode, optional BYOK provider, prompt capacity, and reasoning support.
/// Validation rejects any source/provider combination that is not explicitly supported.
/// </summary>
public sealed record RuntimeModelBinding(
    string ModelId, ModelSourceMode SourceMode, RuntimeByokProvider? Provider = null)
{
    public bool Enabled { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptCapacityTokens { get; init; }

    /// <summary>Reasoning levels explicitly supported by a BYOK model binding.</summary>
    public ImmutableArray<string> SupportedReasoningEfforts { get; init; } = [];

    /// <summary>Checks model identity, source mode, prompt capacity, and the provider allow-list.</summary>
    internal void Validate()
    {
        RuntimeContractValidation.ValidateIdentifier(ModelId);
        if (!Enum.IsDefined(SourceMode) || PromptCapacityTokens is < 1 ||
            SupportedReasoningEfforts.IsDefault ||
            SupportedReasoningEfforts.Any(effort => effort is not ("low" or "medium" or "high" or "xhigh")) ||
            SupportedReasoningEfforts.Distinct(StringComparer.Ordinal).Count() != SupportedReasoningEfforts.Length ||
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
