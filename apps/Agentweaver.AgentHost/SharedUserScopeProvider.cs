using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Token-scope provider for the shared-store path (spec-018 P1.5). Unlike
/// <see cref="PodInstallationScopeProvider"/> (which always returns the installation scope), this
/// resolves the per-user scope that matches what the API persisted, so
/// <see cref="SharedHomeGitHubTokenStore"/> reads the correct <c>user_&lt;id&gt;.json</c>.
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>An explicitly configured user id (<c>AgentHost:UserId</c> / the run's submitting user),
///   if present, -&gt; <see cref="GitHubTokenScope.ForUser(string)"/>.</item>
///   <item>Fall back to <see cref="GitHubTokenScope.Installation"/> if no user id is configured.</item>
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

        _logger?.LogWarning("AgentHost userId not configured — falling back to installation scope");
        return GitHubTokenScope.Installation;
    }
}
