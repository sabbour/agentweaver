using Scaffolder.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Scaffolder.Api.Agent.ModelSources;

/// <summary>
/// T039: IModelSourceAdapter backed by Microsoft Azure AI Foundry (Azure AI Inference).
///
/// STUB IMPLEMENTATION: Provides the correct IModelSourceAdapter interface so
/// GovernancePolicyEngine and AgentLoopHost can depend on it without an Azure
/// subscription configured. Replace SubmitPromptAsync with a real Azure AI
/// Inference SDK call (Azure.AI.Inference.ChatCompletionsClient) targeting
/// ScaffolderOptions.FoundryEndpoint with ScaffolderOptions.FoundryApiKey
/// when credentials are available.
/// </summary>
public sealed class MicrosoftFoundryAdapter : IModelSourceAdapter
{
    private readonly ScaffolderOptions _options;
    private readonly ILogger<MicrosoftFoundryAdapter> _logger;

    public MicrosoftFoundryAdapter(
        IOptions<ScaffolderOptions> options,
        ILogger<MicrosoftFoundryAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> SubmitPromptAsync(string prompt, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "MicrosoftFoundryAdapter: submitting prompt ({Len} chars) [stub]",
            prompt.Length);

        // TODO: Replace with real Azure AI Inference SDK call using
        // _options.FoundryEndpoint and _options.FoundryApiKey
        var preview = prompt.Length > 60 ? prompt[..60] + "..." : prompt;
        var response = $"[Foundry response stub for: {preview}]";
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public Task<string?> ApplyContentSafetyCheckAsync(string content, CancellationToken ct = default)
    {
        _logger.LogDebug("MicrosoftFoundryAdapter: content-safety check [stub — always safe]");
        // TODO: Wire real Azure Content Safety API call
        return Task.FromResult<string?>(null); // null = safe
    }
}
