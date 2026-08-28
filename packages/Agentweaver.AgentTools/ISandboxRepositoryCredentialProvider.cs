namespace Agentweaver.AgentTools;

/// <summary>Returns the current run's short-lived repository credential.</summary>
public interface ISandboxRepositoryCredentialProvider
{
    string? GetAccessToken();
}
