namespace Agentweaver.Domain;

public interface IGitHubTokenScopeProvider
{
    GitHubTokenScope Resolve(string? userId);

    /// <summary>
    /// Resolves a token scope for work that carries durable project context. Implementations that
    /// do not support project-linked identities preserve the existing user-default behavior.
    /// </summary>
    Task<GitHubTokenScope> ResolveAsync(
        string? userId,
        string? projectId,
        CancellationToken ct = default) =>
        Task.FromResult(Resolve(userId));
}
