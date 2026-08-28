using Agentweaver.AgentRuntime.Providers;

namespace Agentweaver.AgentHost;

/// <summary>
/// Supplies only the run-bound Copilot inference credential held in the trusted AgentHost process.
/// It deliberately has no repository scope, locator, file, environment, or secret-store fallback.
/// </summary>
internal sealed class RunBoundCopilotCredentialProvider(AgentHostRuntimeState runtimeState)
    : ICopilotCredentialProvider
{
    public Task<CopilotCredential?> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            string.IsNullOrWhiteSpace(runtimeState.CopilotAccessToken)
                ? null
                : new CopilotCredential(runtimeState.CopilotAccessToken, ExpiresAt: null));
    }
}
