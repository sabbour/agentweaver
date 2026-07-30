using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Windows Credential Manager backed token store (CRED_TYPE_GENERIC, DPAPI-protected).
/// Credential target name embeds the scope key so installation-scoped and per-caller-scoped
/// credentials are always distinct entries. The SignedOut tombstone is a distinct credential
/// with status="signed-out" so config fallback is suppressed after explicit sign-out.
/// On non-Windows platforms falls back to InMemoryGitHubTokenStore.
/// </summary>
public sealed class OsCredentialStoreGitHubTokenStore : IMultiIdentityGitHubTokenStore
{
    private const string TargetPrefix = "Agentweaver.GitHub.";
    private const string TombstoneUsername = "signed-out";

    // On non-Windows platforms the OS credential manager is unavailable.
    // Use a file-based store (owner-only 0600 JSON) so tokens survive restarts.
    private readonly FileSystemGitHubTokenStore _fallback = new();

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.GetAsync(scope, ct).ConfigureAwait(false);

        var target = TargetName(scope);
        var json = ReadCredential(target);
        if (json is null)
            return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            if (stored?.Status == "signed-out")
                return new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null);
            if (stored?.AccessToken is not null)
                return new GitHubTokenEntry(GitHubTokenStatus.SignedIn, stored.AccessToken);
        }
        catch (JsonException) { /* malformed — treat as never signed in */ }
        return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
    }

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.GetTokenAsync(scope, ct).ConfigureAwait(false);

        var json = ReadCredential(TargetName(scope));
        if (json is null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
                return new GitHubToken(
                    stored.AccessToken,
                    stored.RefreshToken,
                    stored.ExpiresAt,
                    stored.Login ?? "unknown",
                    stored.AvatarUrl,
                    stored.Scopes ?? []);
        }
        catch (JsonException) { /* malformed — treat as no token */ }
        return null;
    }

    public async Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        { await _fallback.SetAsync(scope, token, ct).ConfigureAwait(false); return; }

        var stored = new StoredCredential
        {
            Status = "signed-in",
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Login = token.Login,
            AvatarUrl = token.AvatarUrl,
            Scopes = token.Scopes
        };
        WriteCredential(TargetName(scope), token.Login, JsonSerializer.Serialize(stored));
    }

    public async Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.GetIdentityAsync(scope, ct).ConfigureAwait(false);

        var json = ReadCredential(TargetName(scope));
        if (json is null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            if (stored?.Login is not null) return new GitHubIdentity(stored.Login, stored.AvatarUrl);
        }
        catch (JsonException) { }
        return null;
    }

    public async Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        { await _fallback.SignOutAsync(scope, ct).ConfigureAwait(false); return; }

        var tombstone = new StoredCredential { Status = "signed-out" };
        WriteCredential(TargetName(scope), TombstoneUsername, JsonSerializer.Serialize(tombstone));
    }

    public async Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedIdentitiesAsync(
        string entraUserId,
        CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false);

        var index = ReadLinkIndex(entraUserId);
        return index.Links
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.LinkedAt)
            .ToArray();
    }

    public async Task<GitHubIdentityLink?> GetLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var links = await ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false);
        return links.FirstOrDefault(x => string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal));
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await _fallback.LinkIdentityAsync(
                entraUserId,
                token,
                isDefault,
                copilotEntitled,
                copilotEntitledCheckedAt,
                ct).ConfigureAwait(false);
            return;
        }

        await SetAsync(GitHubTokenScope.ForLinkedIdentity(entraUserId, token.Login), token, ct).ConfigureAwait(false);

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
            GitHubTokenScope.ForLinkedIdentity(entraUserId, token.Login).Key,
            makeDefault || (existing?.IsDefault ?? false),
            existing?.LinkedAt ?? DateTimeOffset.UtcNow,
            copilotEntitled ?? existing?.CopilotEntitled,
            copilotEntitledCheckedAt ?? existing?.CopilotEntitledCheckedAt,
            token.AvatarUrl);

        WriteLinkIndex(entraUserId, new LinkIndex { Links = links.Values.OrderBy(x => x.GitHubLogin, StringComparer.Ordinal).ToArray() });
    }

    public async Task<bool> SetDefaultLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.SetDefaultLinkedIdentityAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);

        var index = ReadLinkIndex(entraUserId);
        if (!index.Links.Any(x => string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal)))
            return false;

        WriteLinkIndex(entraUserId, new LinkIndex
        {
            Links = index.Links
                .Select(x => x with { IsDefault = string.Equals(x.GitHubLogin, githubLogin, StringComparison.Ordinal) })
                .ToArray()
        });
        return true;
    }

    public async Task<bool> UnlinkIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await _fallback.UnlinkIdentityAsync(entraUserId, githubLogin, ct).ConfigureAwait(false);

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

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string TargetName(GitHubTokenScope scope) =>
        $"{TargetPrefix}{scope.Key}";

    private static string LinkIndexTargetName(string entraUserId) =>
        $"{TargetPrefix}links.{NormalizeTargetSegment(entraUserId)}";

    private static string NormalizeTargetSegment(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static string? ReadCredential(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CRED_TYPE_GENERIC, 0, out nint credPtr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0) return null;
            return Encoding.Unicode.GetString(GetBytes(cred.CredentialBlob, cred.CredentialBlobSize));
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    private static void WriteCredential(string target, string username, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var gcHandle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = username,
                CredentialBlob = gcHandle.AddrOfPinnedObject(),
                CredentialBlobSize = blob.Length,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE
            };
            NativeMethods.CredWrite(ref cred, 0);
        }
        finally
        {
            gcHandle.Free();
        }
    }

    private static LinkIndex ReadLinkIndex(string entraUserId)
    {
        var json = ReadCredential(LinkIndexTargetName(entraUserId));
        if (json is null)
            return new LinkIndex();

        try
        {
            return JsonSerializer.Deserialize<LinkIndex>(json) ?? new LinkIndex();
        }
        catch (JsonException)
        {
            return new LinkIndex();
        }
    }

    private static void WriteLinkIndex(string entraUserId, LinkIndex index) =>
        WriteCredential(LinkIndexTargetName(entraUserId), "links", JsonSerializer.Serialize(index));

    private static byte[] GetBytes(nint ptr, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

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

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public nint CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public nint Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out nint credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree([In] nint buffer);
    }
}
