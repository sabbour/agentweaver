namespace Scaffolder.Domain;

public interface IGitHubTokenScopeProvider
{
    GitHubTokenScope Resolve(string? userId);
}
