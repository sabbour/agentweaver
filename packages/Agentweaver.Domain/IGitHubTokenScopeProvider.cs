namespace Agentweaver.Domain;

public interface IGitHubTokenScopeProvider
{
    GitHubTokenScope Resolve(string? userId);
}
