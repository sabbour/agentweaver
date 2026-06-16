using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// Windows Credential Manager backed token store (CRED_TYPE_GENERIC, DPAPI-protected).
/// Credential target name embeds the scope key so installation-scoped and per-caller-scoped
/// credentials are always distinct entries. The SignedOut tombstone is a distinct credential
/// with status="signed-out" so config fallback is suppressed after explicit sign-out.
/// On non-Windows platforms falls back to InMemoryGitHubTokenStore.
/// </summary>
public sealed class OsCredentialStoreGitHubTokenStore : IGitHubTokenStore
{
    private const string TargetPrefix = "Scaffolder.GitHub.";
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

    public async Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        { await _fallback.SetAsync(scope, token, ct).ConfigureAwait(false); return; }

        var stored = new StoredCredential
        {
            Status = "signed-in",
            AccessToken = token.AccessToken,
            Login = token.Login,
            AvatarUrl = token.AvatarUrl
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

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string TargetName(GitHubTokenScope scope) =>
        $"{TargetPrefix}{scope.Key}";

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
        public string? Login { get; init; }
        public string? AvatarUrl { get; init; }
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
