using System.Text.Json;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// File-system backed GitHub token store for non-Windows platforms.
/// Writes one JSON file per scope to {DataDirectory}/auth/{scope-key}.json.
/// File permissions are set to owner-only (0600) on Unix.
/// </summary>
public sealed class FileSystemGitHubTokenStore : IGitHubTokenStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public FileSystemGitHubTokenStore()
        : this(Path.Combine(AppPaths.DataDirectory, "auth")) { }

    internal FileSystemGitHubTokenStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var path = FilePath(scope);
        if (!File.Exists(path))
            return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path), _json);
            if (stored?.Status == "signed-out")
                return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null));
            if (!string.IsNullOrEmpty(stored?.AccessToken))
                return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedIn, stored.AccessToken));
        }
        catch (Exception) { /* malformed — treat as never signed in */ }

        return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));
    }

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        var stored = new StoredCredential
        {
            Status = "signed-in",
            AccessToken = token.AccessToken,
            Login = token.Login,
            AvatarUrl = token.AvatarUrl,
        };
        WriteFile(FilePath(scope), JsonSerializer.Serialize(stored, _json));
        return Task.CompletedTask;
    }

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var path = FilePath(scope);
        if (!File.Exists(path))
            return Task.FromResult<GitHubIdentity?>(null);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path), _json);
            if (stored?.Login is not null)
                return Task.FromResult<GitHubIdentity?>(new GitHubIdentity(stored.Login, stored.AvatarUrl));
        }
        catch (Exception) { }

        return Task.FromResult<GitHubIdentity?>(null);
    }

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var tombstone = new StoredCredential { Status = "signed-out" };
        WriteFile(FilePath(scope), JsonSerializer.Serialize(tombstone, _json));
        return Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string FilePath(GitHubTokenScope scope)
    {
        // Sanitize key so it is safe as a filename.
        var safe = string.Concat(scope.Key.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        return Path.Combine(_dir, $"{safe}.json");
    }

    private static void WriteFile(string path, string content)
    {
        File.WriteAllText(path, content);
        // Set owner-only permissions on Unix (no-op on Windows — covered by DPAPI there).
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception) { /* best effort */ }
        }
    }

    private sealed record StoredCredential
    {
        public string? Status { get; init; }
        public string? AccessToken { get; init; }
        public string? Login { get; init; }
        public string? AvatarUrl { get; init; }
    }
}
