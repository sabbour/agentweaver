using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Shared-path resolution for the file-based GitHub token store used by the API/worker tier.
///
/// <para>
/// spec-018 P1.5: the live cluster mounts the <c>agentweaver-workspace</c> RWX Azure Files share
/// at <c>/workspace</c> on api + worker + (now) the agent-host pod, with <c>HOME=/workspace/.home</c>.
/// The API persists GitHub tokens via <c>FileSystemGitHubTokenStore</c> to
/// <c>{LocalApplicationData}/agentweaver/auth/{scope-key}.json</c> — i.e.
/// <c>/workspace/.home/.local/share/agentweaver/auth/user_&lt;id&gt;.json</c>. Because the pod mounts
/// the SAME share with the SAME HOME, it reads the very same files — the token never moves and no
/// secret is created.
/// </para>
///
/// <para>
/// Directory + filename derivation mirror <c>Agentweaver.Api.Infrastructure.AppPaths</c> and
/// <c>FileSystemGitHubTokenStore.FilePath</c> exactly so the pod reads what the API wrote.
/// </para>
/// </summary>
internal static class SharedTokenStorePaths
{
    /// <summary>
    /// Resolves the auth directory ({DataDirectory}/auth). Honors an explicit override
    /// (config <c>AgentHost:SharedTokenStorePath</c>); otherwise mirrors AppPaths:
    /// <c>{LocalApplicationData}/agentweaver/auth</c>, which is HOME-relative on Linux.
    /// </summary>
    public static string ResolveAuthDir(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath!;

        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = AppContext.BaseDirectory;

        return Path.Combine(baseDir, "agentweaver", "auth");
    }

    /// <summary>
    /// Sanitizes a scope key to a filename exactly as <c>FileSystemGitHubTokenStore</c> does
    /// (letters/digits/'-'/'_' kept, everything else -> '_'); e.g. <c>user:sabbour</c> -&gt;
    /// <c>user_sabbour</c>.
    /// </summary>
    public static string SanitizeKey(GitHubTokenScope scope) =>
        string.Concat(scope.Key.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

    public static string FilePath(string authDir, GitHubTokenScope scope) =>
        Path.Combine(authDir, $"{SanitizeKey(scope)}.json");
}
