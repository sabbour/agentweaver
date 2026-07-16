using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Api.Webhooks;

/// <summary>
/// Verifies the <c>X-Hub-Signature-256</c> HMAC-SHA256 signature GitHub attaches to every webhook
/// delivery (see https://docs.github.com/webhooks/using-webhooks/validating-webhook-deliveries).
/// GitHub computes <c>sha256=&lt;hex(HMAC-SHA256(secret, raw_request_body))&gt;</c>; verification MUST
/// run over the exact raw request body bytes (not a re-serialized/parsed form) or a byte-for-byte
/// re-encoding difference would cause false rejections.
/// </summary>
public static class GitHubWebhookSignatureVerifier
{
    private const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Returns true when <paramref name="signatureHeader"/> is a valid <c>sha256=...</c> signature of
    /// <paramref name="rawBody"/> computed with <paramref name="secret"/>. Uses a fixed-time comparison
    /// so verification does not leak timing information about the expected signature. Returns false
    /// (never throws) for a missing header, malformed header, empty secret, or genuine mismatch.
    /// </summary>
    public static bool Verify(string? secret, ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        if (string.IsNullOrEmpty(signatureHeader)) return false;
        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var expectedHex = signatureHeader[SignaturePrefix.Length..];
        Span<byte> expected = stackalloc byte[32]; // SHA-256 digest is always 32 bytes.
        if (!TryParseHex(expectedHex, expected)) return false;

        Span<byte> actual = stackalloc byte[32];
        var written = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, actual);
        if (written != actual.Length) return false;

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryParseHex(string hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2) return false;
        for (var i = 0; i < destination.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return false;
            destination[i] = b;
        }
        return true;
    }
}
