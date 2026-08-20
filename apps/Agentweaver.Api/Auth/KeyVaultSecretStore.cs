using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Azure Key Vault backed <see cref="ISecretStore"/>.
/// Secret-name mapping keeps KV names within the ^[0-9a-zA-Z-]+$ constraint:
///   scope "installation"              → "ghtok-installation"
///   scope "user:{userId}"             → "ghtok-user--{base32lower-nopad(utf8(userId))}"
///   scope "user-link:{oid}:{login}"   → "ghtok-user-link--{base32lower-nopad(utf8(<suffix>))}"
///   scope "user-links:{oid}"          → "ghtok-user-links--{base32lower-nopad(utf8(<suffix>))}"
///   other keys                        → "ghtok-" + sanitized (letters/digits/hyphens only)
///
/// ETag semantics: each KV secret version carries an ETag.  When an ETag is supplied
/// to <see cref="SetSecretAsync"/>, the current version is read first and the write is
/// performed only if the ETags match (best-effort optimistic concurrency).
/// </summary>
public sealed class KeyVaultSecretStore : ISecretStore, IAtomicSecretLeaseStore, ISecretListStore
{
    private readonly SecretClient _client;
    private const string LeaseOwnerTag = "agentweaver-lease-owner";
    private const string LeaseExpiresTag = "agentweaver-lease-expires";

    public KeyVaultSecretStore(SecretClient client) => _client = client;

    // ── Key mapping ─────────────────────────────────────────────────────────

    internal static string SanitizeKey(string key)
    {
        if (key == "installation")
            return "ghtok-installation";

        if (key.StartsWith("user:", StringComparison.Ordinal))
        {
            var userId = key.Substring(5); // skip "user:"
            var encoded = Base32Lower(System.Text.Encoding.UTF8.GetBytes(userId));
            return "ghtok-user--" + encoded;
        }

        if (key.StartsWith("user-link:", StringComparison.Ordinal))
        {
            var encoded = Base32Lower(System.Text.Encoding.UTF8.GetBytes(key.Substring("user-link:".Length)));
            return "ghtok-user-link--" + encoded;
        }

        if (key.StartsWith("user-links:", StringComparison.Ordinal))
        {
            var encoded = Base32Lower(System.Text.Encoding.UTF8.GetBytes(key.Substring("user-links:".Length)));
            return "ghtok-user-links--" + encoded;
        }

        // Fallback: replace non-alphanumeric (except hyphen) with hyphens and prefix.
        var safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        return "ghtok-" + safe;
    }

    // Base32 (RFC 4648) lower-case alphabet, no padding — yields [a-z2-7]+ output.
    private static readonly char[] Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567".ToCharArray();

    // Reverse lookup: char → 5-bit value, -1 for invalid characters.
    private static readonly int[] Base32Values;

    static KeyVaultSecretStore()
    {
        Base32Values = new int[128];
        Array.Fill(Base32Values, -1);
        for (int i = 0; i < Base32Alphabet.Length; i++)
            Base32Values[Base32Alphabet[i]] = i;
    }

    internal static byte[] Base32LowerDecode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return [];

        int outputLength = encoded.Length * 5 / 8;
        var result = new byte[outputLength];
        int buffer = 0, bitsLeft = 0, byteIdx = 0;
        foreach (char c in encoded)
        {
            var val = c < 128 ? Base32Values[c] : -1;
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                if (byteIdx < result.Length)
                    result[byteIdx++] = (byte)((buffer >> bitsLeft) & 0xFF);
            }
        }
        return result[..byteIdx];
    }

    /// <summary>
    /// Reverses <see cref="SanitizeKey"/> to recover the original scope key from a KV secret name.
    /// Returns null for names that cannot be decoded (link-index entries, unknown prefixes).
    /// </summary>
    internal static string? TryDecodeScopeKey(string kvName)
    {
        if (kvName == "ghtok-installation")
            return "installation";

        if (kvName.StartsWith("ghtok-user-link--", StringComparison.Ordinal))
        {
            var encoded = kvName["ghtok-user-link--".Length..];
            var suffix = System.Text.Encoding.UTF8.GetString(Base32LowerDecode(encoded));
            return $"user-link:{suffix}";
        }

        if (kvName.StartsWith("ghtok-user-links--", StringComparison.Ordinal))
            return null; // link-index entries — not token scopes

        if (kvName.StartsWith("ghtok-user--", StringComparison.Ordinal))
        {
            var encoded = kvName["ghtok-user--".Length..];
            var userId = System.Text.Encoding.UTF8.GetString(Base32LowerDecode(encoded));
            return $"user:{userId}";
        }

        return null;
    }

    // ── ISecretListStore ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListTokenScopeKeysAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        await foreach (var prop in _client.GetPropertiesOfSecretsAsync(ct).ConfigureAwait(false))
        {
            if (!prop.Enabled.GetValueOrDefault(true)) continue;
            var scopeKey = TryDecodeScopeKey(prop.Name);
            if (scopeKey is not null)
                result.Add(scopeKey);
        }
        return result;
    }

    internal static string Base32Lower(byte[] data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    // ── ISecretStore ─────────────────────────────────────────────────────────

    public async Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default)
    {
        var kvKey = SanitizeKey(key);
        try
        {
            var response = await _client.GetSecretAsync(kvKey, cancellationToken: ct).ConfigureAwait(false);
            var etag = response.Value.Properties.Version ?? string.Empty;
            return SecretGetResult.Of(response.Value.Value, etag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return SecretGetResult.NotFound;
        }
    }

    public async Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default)
    {
        var kvKey = SanitizeKey(key);

        // Best-effort optimistic concurrency for value writes. Azure Key Vault SetSecret
        // creates a new version and does not provide an atomic If-Match value update here;
        // refresh serialization uses TryAcquireLeaseAsync instead.
        if (etag is not null)
        {
            var current = await GetSecretAsync(key, ct).ConfigureAwait(false);
            if (!current.Found || current.ETag != etag)
                throw new SecretPreconditionFailedException();
        }

        var setResponse = await _client.SetSecretAsync(kvKey, value, ct).ConfigureAwait(false);
        return setResponse.Value.Properties.Version ?? string.Empty;
    }

    public async Task<ISecretStoreLease?> TryAcquireLeaseAsync(
        string key,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var kvKey = SanitizeKey(key);
        KeyVaultSecret current;
        try
        {
            current = (await _client.GetSecretAsync(kvKey, cancellationToken: ct).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var tags = current.Properties.Tags;
        if (tags.TryGetValue(LeaseExpiresTag, out var expiresRaw)
            && DateTimeOffset.TryParse(expiresRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt)
            && expiresAt > now)
        {
            return null;
        }

        tags[LeaseOwnerTag] = owner;
        tags[LeaseExpiresTag] = (now + ttl).ToString("O");

        try
        {
            await _client.UpdateSecretPropertiesAsync(current.Properties, ct).ConfigureAwait(false);
            return new KeyVaultSecretStoreLease(this, kvKey, owner);
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            return null;
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        var kvKey = SanitizeKey(key);
        try
        {
            await _client.StartDeleteSecretAsync(kvKey, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already absent — no error.
        }
    }

    private async ValueTask ReleaseLeaseAsync(string kvKey, string owner)
    {
        try
        {
            var current = (await _client.GetSecretAsync(kvKey).ConfigureAwait(false)).Value;
            var tags = current.Properties.Tags;
            if (!tags.TryGetValue(LeaseOwnerTag, out var currentOwner) || currentOwner != owner)
                return;

            tags.Remove(LeaseOwnerTag);
            tags.Remove(LeaseExpiresTag);
            await _client.UpdateSecretPropertiesAsync(current.Properties).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404 || ex.Status == 409 || ex.Status == 412)
        {
        }
    }

    private sealed class KeyVaultSecretStoreLease(
        KeyVaultSecretStore store,
        string kvKey,
        string owner) : ISecretStoreLease
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await store.ReleaseLeaseAsync(kvKey, owner).ConfigureAwait(false);
        }
    }
}
