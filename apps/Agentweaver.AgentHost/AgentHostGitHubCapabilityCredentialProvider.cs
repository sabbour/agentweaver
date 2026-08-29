using Agentweaver.AgentRuntime;

namespace Agentweaver.AgentHost;

/// <summary>
/// Exposes only the one immutable Copilot capability credential supplied for this pod's run.
/// It never resolves a caller, reads a token store, or falls back to host configuration.
/// </summary>
internal sealed class AgentHostGitHubCapabilityCredentialProvider(AgentHostRuntimeState runtimeState)
    : IGitHubCopilotCapabilityCredentialProvider
{
    public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        CancellationToken ct = default)
    {
        var credential = runtimeState.CopilotCredential;
        return Task.FromResult(
            string.Equals(runtimeState.RunId, runId, StringComparison.Ordinal) &&
            credential is not null &&
            credential.ExpiresAt > DateTimeOffset.UtcNow
                ? credential
                : null);
    }
}
