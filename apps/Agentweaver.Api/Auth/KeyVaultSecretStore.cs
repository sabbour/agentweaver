using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Azure Key Vault backed <see cref="ISecretStore"/>.
/// Secret-name mapping keeps KV names within the ^[0-9a-zA-Z-]+$ constraint:
///   scope "installation"      → "ghtok-installation"
///   scope "user:{userId}"     → "ghtok-user--{base32lower-nopad(utf8(userId))}"
///   other keys                → "ghtok-" + sanitized (letters/digits/hyphens only)
///
/// ETag semantics: each KV secret version carries an ETag.  When an ETag is supplied
/// to <see cref="SetSecretAsync"/>, the current version is read first and the write is
/// performed only if the ETags match (best-effort optimistic concurrency).
/// </summary>
public sealed class KeyVaultSecretStore : ISecretStore
{
    private readonly SecretClient _client;

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

        // Fallback: replace non-alphanumeric (except hyphen) with hyphens and prefix.
        var safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        return "ghtok-" + safe;
    }

    // Base32 (RFC 4648) lower-case alphabet, no padding — yields [a-z2-7]+ output.
    private static readonly char[] Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567".ToCharArray();

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

        // Optimistic concurrency: if an ETag is supplied, verify it matches the current version.
        // Note: Azure KV doesn't expose atomic conditional set on the value itself via the SDK,
        // so we read first and compare.  The check-then-write is not atomic but suffices for
        // the refresh-token-overwrite protection use-case.
        if (etag is not null)
        {
            var current = await GetSecretAsync(key, ct).ConfigureAwait(false);
            if (!current.Found || current.ETag != etag)
                throw new SecretPreconditionFailedException();
        }

        var setResponse = await _client.SetSecretAsync(kvKey, value, ct).ConfigureAwait(false);
        return setResponse.Value.Properties.Version ?? string.Empty;
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
}
