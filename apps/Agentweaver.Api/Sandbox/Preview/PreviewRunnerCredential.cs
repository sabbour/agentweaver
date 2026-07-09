using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Helpers for the per-run preview-runner credential (spec-006 decouple-preview, BLOCKER A).
///
/// <para>
/// A fresh random credential is minted per run, delivered to the AgentHost pod in-memory via the
/// existing <c>POST /configure</c> channel (never in pod env/file, so it is not inheritable by the
/// untrusted preview process), and persisted to the run secret store so any API replica can re-fetch
/// it during a reconcile. It is durably deleted on pod release / terminal cleanup and never reused.
/// </para>
///
/// <para>
/// The secret-store KEY is derived deterministically from the run id via <see cref="SecretKey"/> so
/// the mint (executor <c>/configure</c>) and delete (executor release / terminal cleanup) sites
/// derive the SAME key and the delete actually matches the minted secret.
/// </para>
/// </summary>
public static class PreviewRunnerCredential
{
    private const string KeyPrefix = "preview-runner-cred--";

    /// <summary>
    /// Deterministic, replica-safe secret-store key for <paramref name="runId"/>'s preview-runner
    /// credential. Sanitizes the run id to the KeyVaultSecretStore-safe alphabet (letters, digits,
    /// and <c>-</c>) so the same key is produced on both mint and delete. The exact same derivation
    /// MUST be used at both sites (locked rubber-duck condition).
    /// </summary>
    public static string SecretKey(string runId)
    {
        var sanitized = Sanitize(runId ?? string.Empty);
        return KeyPrefix + sanitized;
    }

    /// <summary>Mints a fresh, high-entropy, URL-safe preview-runner credential.</summary>
    public static string Mint()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string Sanitize(string value)
    {
        // Key Vault secret names allow [0-9a-zA-Z-]. Map everything else to a stable token derived
        // from a SHA-256 of the raw value so distinct run ids never collide after sanitization.
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')
                sb.Append(c);
            else
                sb.Append('-');
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        var head = sb.ToString();
        if (head.Length > 96)
            head = head[..96];
        return head + "-" + hash;
    }
}
