// Ported without behavior changes from the 0.x Agentweaver.Domain blueprint package code.
// The AgentSuite blueprint converter uses it so 0.x packages are checked by the exact 0.x rules.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Projects.Config.BlueprintPackages;

/// <summary>Raw bytes of one validated package payload.</summary>
public sealed record BlueprintPackagePayload(string Path, byte[] Bytes);

/// <summary>Canonical payload-set hashing shared by package validation and owner-library persistence.</summary>
public static class BlueprintPackagePayloadSetDigest
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Versioned domain prefix. The trailing NUL separates it from the first length field.
    private static readonly byte[] Prefix = "blueprint-package-payload-set-v1\0"u8.ToArray();

    /// <summary>Hashes exact payload bytes under ordinal-sorted canonical paths using the package v1 framing.</summary>
    public static string Calculate(IEnumerable<BlueprintPackagePayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        // Read the sequence once and reject null entries before hashing anything.
        var canonicalPayloads = payloads.ToArray();
        foreach (var payload in canonicalPayloads)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(payload.Path);
            ArgumentNullException.ThrowIfNull(payload.Bytes);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // The prefix keeps this digest distinct from any other hash of the same bytes.
        hash.AppendData(Prefix);
        // Hash in ordinal path order so the digest doesn't depend on input order.
        // Each path and payload is length-prefixed, so two different payload sets can't frame to the same bytes.
        foreach (var payload in canonicalPayloads.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            AppendLengthPrefixed(hash, EncodePathUtf8(payload.Path));
            AppendLengthPrefixed(hash, payload.Bytes);
        }
        // Lowercase hex, matching the 0.x digest format.
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        // An 8-byte big-endian length, then the bytes themselves.
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static byte[] EncodePathUtf8(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            // Strict encoding fails on lone surrogates instead of hashing replacement characters.
            return StrictUtf8.GetBytes(path);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Payload paths must be valid Unicode.", nameof(path), exception);
        }
    }
}
