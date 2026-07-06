using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Token-scope provider for the shared-store path (spec-018 P1.5). Resolves the
/// per-user scope that matches what the API persisted, so
/// <see cref="SharedHomeGitHubTokenStore"/> reads the correct <c>user_&lt;id&gt;.json</c>.
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>An explicitly configured user id (<c>AgentHost:UserId</c> / the run's submitting user),
///   if present, -&gt; <see cref="GitHubTokenScope.ForUser(string)"/>.</item>
///   <item>Fail closed if no user id is configured; installation tokens cannot authorize Copilot model turns.</item>
/// </list>
/// </summary>
internal sealed class SharedUserScopeProvider : IGitHubTokenScopeProvider
{
    private readonly string? _configuredUserId;
    private readonly ILogger<SharedUserScopeProvider>? _logger;

    public SharedUserScopeProvider(
        string authDir,
        string? configuredUserId,
        ILogger<SharedUserScopeProvider>? logger = null)
    {
        _configuredUserId = string.IsNullOrWhiteSpace(configuredUserId) ? null : configuredUserId;
        _logger = logger;
    }

    public GitHubTokenScope Resolve(string? userId)
    {
        var effective = _configuredUserId ?? (string.IsNullOrWhiteSpace(userId) ? null : userId);
        if (effective is not null)
            return GitHubTokenScope.ForUser(effective);

        _logger?.LogError("AgentHost userId not configured — refusing installation-scope Copilot auth");
        throw new InvalidOperationException(
            "AgentHost cannot resolve a Copilot token scope without the submitting user identity; " +
            "installation-scope Copilot auth is not permitted.");
    }
}
