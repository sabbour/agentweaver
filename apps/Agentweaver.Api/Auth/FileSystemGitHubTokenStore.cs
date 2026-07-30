using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// File-system backed GitHub token store for non-Windows platforms.
/// Writes one JSON file per scope to {DataDirectory}/auth/{scope-key}.json.
/// File permissions are set to owner-only (0600) on Unix.
/// </summary>
public sealed class FileSystemGitHubTokenStore : IMultiIdentityGitHubTokenStore
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

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var path = FilePath(scope);
        if (!File.Exists(path))
            return Task.FromResult<GitHubToken?>(null);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path), _json);
            if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
                return Task.FromResult<GitHubToken?>(new GitHubToken(
                    stored.AccessToken,
                    stored.RefreshToken,
                    stored.ExpiresAt,
                    stored.Login ?? "unknown",
                    stored.AvatarUrl,
                    stored.Scopes ?? []));
        }
        catch (Exception) { /* malformed — treat as no token */ }

        return Task.FromResult<GitHubToken?>(null);
    }

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        var stored = new StoredCredential
        {
            Status = "signed-in",
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Login = token.Login,
            AvatarUrl = token.AvatarUrl,
            Scopes = token.Scopes,
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

    public Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedIdentitiesAsync(
        string entraUserId,
        CancellationToken ct = default)
    {
        var index = ReadLinkIndex(entraUserId);
        var ordered = index.Links
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.LinkedAt)
            .Cast<GitHubIdentityLink>()
            .ToArray();
        return Task.FromResult<IReadOnlyList<GitHubIdentityLink>>(ordered);
    }

    public Task<GitHubIdentityLink?> GetLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var link = ReadLinkIndex(entraUserId).Links.FirstOrDefault(x =>
            string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal));
        return Task.FromResult<GitHubIdentityLink?>(link);
    }

    public async Task<GitHubIdentityLink?> GetDefaultLinkedIdentityAsync(
        string entraUserId,
        CancellationToken ct = default)
        => (await ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false))
            .FirstOrDefault(x => x.IsDefault);

    public async Task LinkIdentityAsync(
        string entraUserId,
        GitHubToken token,
        bool isDefault = false,
        bool? copilotEntitled = null,
        DateTimeOffset? copilotEntitledCheckedAt = null,
        CancellationToken ct = default)
    {
        var scope = GitHubTokenScope.ForLinkedIdentity(entraUserId, token.Login);
        await SetAsync(scope, token, ct).ConfigureAwait(false);

        var index = ReadLinkIndex(entraUserId);
        var links = index.Links.ToDictionary(x => x.GitHubLogin, StringComparer.Ordinal);
        links.TryGetValue(token.Login, out var existing);
        var makeDefault = isDefault || links.Count == 0 || (existing?.IsDefault ?? false);

        if (makeDefault)
        {
            foreach (var key in links.Keys.ToList())
                links[key] = links[key] with { IsDefault = false };
        }

        links[token.Login] = new GitHubIdentityLink(
            entraUserId,
            token.Login,
            scope.Key,
            makeDefault || (existing?.IsDefault ?? false),
            existing?.LinkedAt ?? DateTimeOffset.UtcNow,
            copilotEntitled ?? existing?.CopilotEntitled,
            copilotEntitledCheckedAt ?? existing?.CopilotEntitledCheckedAt,
            token.AvatarUrl);
        WriteLinkIndex(entraUserId, new LinkIndex { Links = links.Values.OrderBy(x => x.GitHubLogin, StringComparer.Ordinal).ToArray() });
    }

    public Task<bool> SetDefaultLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var index = ReadLinkIndex(entraUserId);
        if (!index.Links.Any(x => string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal)))
            return Task.FromResult(false);

        var updated = index.Links
            .Select(x => x with { IsDefault = string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal) })
            .ToArray();
        WriteLinkIndex(entraUserId, new LinkIndex { Links = updated });
        return Task.FromResult(true);
    }

    public async Task<bool> UnlinkIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var index = ReadLinkIndex(entraUserId);
        var removed = index.Links.FirstOrDefault(x => string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal));
        if (removed is null)
            return false;

        await SignOutAsync(GitHubTokenScope.ForLinkedIdentity(entraUserId, githubLogin), ct).ConfigureAwait(false);

        var remaining = index.Links
            .Where(x => !string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal))
            .ToList();
        if (removed.IsDefault && remaining.Count > 0)
            remaining[0] = remaining[0] with { IsDefault = true };

        WriteLinkIndex(entraUserId, new LinkIndex { Links = remaining.ToArray() });
        return true;
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

    private LinkIndex ReadLinkIndex(string entraUserId)
    {
        var path = FilePath(GitHubTokenScope.ForLinkedIdentityIndex(entraUserId));
        if (!File.Exists(path))
            return new LinkIndex();

        try
        {
            return JsonSerializer.Deserialize<LinkIndex>(File.ReadAllText(path), _json) ?? new LinkIndex();
        }
        catch (Exception)
        {
            return new LinkIndex();
        }
    }

    private void WriteLinkIndex(string entraUserId, LinkIndex index)
        => WriteFile(
            FilePath(GitHubTokenScope.ForLinkedIdentityIndex(entraUserId)),
            JsonSerializer.Serialize(index, _json));

    private sealed record StoredCredential
    {
        public string? Status { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? Login { get; init; }
        public string? AvatarUrl { get; init; }
        public string[]? Scopes { get; init; }
    }

    private sealed record LinkIndex
    {
        public GitHubIdentityLink[] Links { get; init; } = [];
    }
}
