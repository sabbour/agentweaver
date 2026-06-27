using System.Text.Json;
using Agentweaver.Domain;

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
///   <item>Otherwise discover the single signed-in <c>user_*.json</c> in the shared auth dir and
///   reconstruct its scope. Suits the controlled single-user PoC where the worker does not inject
///   the user id into the pod.</item>
///   <item>Fall back to <see cref="GitHubTokenScope.Installation"/> if nothing is found.</item>
/// </list>
/// </summary>
internal sealed class SharedUserScopeProvider : IGitHubTokenScopeProvider
{
    private const string UserKeyPrefix = "user_";
    private readonly string _authDir;
    private readonly string? _configuredUserId;

    public SharedUserScopeProvider(string authDir, string? configuredUserId)
    {
        _authDir = authDir;
        _configuredUserId = string.IsNullOrWhiteSpace(configuredUserId) ? null : configuredUserId;
    }

    public GitHubTokenScope Resolve(string? userId)
    {
        var effective = _configuredUserId ?? (string.IsNullOrWhiteSpace(userId) ? null : userId);
        if (effective is not null)
            return GitHubTokenScope.ForUser(effective);

        var discovered = DiscoverSignedInUserId();
        return discovered is not null
            ? GitHubTokenScope.ForUser(discovered)
            : GitHubTokenScope.Installation;
    }

    /// <summary>
    /// Returns the user id of the first signed-in <c>user_*.json</c> in the shared auth dir, or null.
    /// The file name maps back to the scope via the FileSystemGitHubTokenStore sanitization
    /// (<c>user:&lt;id&gt;</c> -&gt; <c>user_&lt;id&gt;</c>), so stripping the prefix recovers the id
    /// for login-style ids.
    /// </summary>
    private string? DiscoverSignedInUserId()
    {
        if (!Directory.Exists(_authDir))
            return null;

        foreach (var file in Directory.EnumerateFiles(_authDir, $"{UserKeyPrefix}*.json"))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<SharedHomeGitHubTokenStore.StoredCredential>(
                    File.ReadAllText(file));
                var signedIn = stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken);
                if (!signedIn)
                    continue;

                var name = Path.GetFileNameWithoutExtension(file); // e.g. user_sabbour
                if (name.Length > UserKeyPrefix.Length)
                    return name[UserKeyPrefix.Length..];
            }
            catch (Exception)
            {
                // malformed — skip
            }
        }

        return null;
    }
}
