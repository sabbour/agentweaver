using Scaffolder.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Scaffolder.Api.Agent.ModelSources;

/// <summary>
/// T038: IModelSourceAdapter backed by the GitHub Copilot SDK.
///
/// STUB IMPLEMENTATION: The installed Microsoft.Agents.Builder 1.5.184 package
/// provides Bot-Framework-style activity primitives but not the Copilot Chat
/// completion API surface directly. This adapter is interface-correct so
/// AgentLoopHost can depend on IModelSourceAdapter today. Replace
/// SubmitPromptAsync with an HttpClient call to the Copilot API endpoint
/// (https://api.githubcopilot.com/chat/completions) using a GitHub token
/// from ScaffolderOptions.CopilotToken when the credential is available.
/// </summary>
public sealed class CopilotSdkAdapter : IModelSourceAdapter
{
    private readonly ScaffolderOptions _options;
    private readonly ILogger<CopilotSdkAdapter> _logger;

    public CopilotSdkAdapter(
        IOptions<ScaffolderOptions> options,
        ILogger<CopilotSdkAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> SubmitPromptAsync(string prompt, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "CopilotSdkAdapter: submitting prompt ({Len} chars) [stub]",
            prompt.Length);

        // TODO: Replace with real GitHub Copilot API call using _options.CopilotToken
        var preview = prompt.Length > 60 ? prompt[..60] + "..." : prompt;
        var response = $"[Copilot response stub for: {preview}]";
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public Task<string?> ApplyContentSafetyCheckAsync(string content, CancellationToken ct = default)
    {
        _logger.LogDebug("CopilotSdkAdapter: content-safety check [stub — always safe]");
        // TODO: Wire real content-safety API call
        return Task.FromResult<string?>(null); // null = safe
    }
}
