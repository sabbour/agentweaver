using Agentweaver.AgentTools;

namespace Agentweaver.AgentHost;

/// <summary>Provides the in-memory repository credential for the configured run.</summary>
internal sealed class RunScopedRepositoryCredentialProvider(
    AgentHostRuntimeState runtimeState) : ISandboxRepositoryCredentialProvider
{
    public string? GetAccessToken() => runtimeState.RepositoryAccessToken;
}
