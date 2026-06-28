using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Generates and validates per-preview <b>capability tokens</b>. The preview URL
/// (<c>https://{token}.{zone}</c>) is UNAUTHENTICATED — possession of the URL grants access —
/// so the token must be unguessable. All security entropy comes from a 128-bit CSPRNG base32
/// suffix; the leading words are a cosmetic, human-friendly prefix only and contribute NO
/// security entropy (Seraph requirement).
///
/// <para>
/// Shape: three cosmetic words joined by <c>-</c>, then <c>-</c>, then a 26-char lowercase
/// base32 suffix encoding 16 random bytes, e.g.
/// <c>swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2d4f7</c>. The suffix is the only thing that
/// keeps the URL secret: 128 bits ≫ the 2⁴⁰ floor, mirroring
/// <c>McpRefreshTokenStore.GenerateOpaqueToken</c>.
/// </para>
/// </summary>
public static class PreviewToken
{
    /// <summary>Labels a token must never equal (collide with platform/service hosts). Regenerate on hit.</summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal) { "agentweaver", "mcp", "api", "frontend" };

    /// <summary>128 bits of CSPRNG entropy (16 bytes) → 26 base32 chars. Floor is 64 bits (13 chars).</summary>
    private const int SuffixEntropyBytes = 16;

    private const int WordCount = 3;

    // a-z2-7: DNS-safe, lowercase RFC 4648 base32 alphabet.
    private static readonly char[] Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567".ToCharArray();

    private static readonly Regex Dns1123LabelRegex = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Cosmetic wordlist (64 short, unambiguous, lowercase, alpha-only words). These are a
    /// human-friendly prefix ONLY and provide no security entropy — see <see cref="PreviewToken"/>.
    /// </summary>
    public static readonly string[] Words =
    [
        "swift", "falcon", "amber", "cobalt", "maple", "willow", "cedar", "ember",
        "lunar", "solar", "comet", "nimbus", "quartz", "onyx", "ivory", "coral",
        "river", "canyon", "summit", "harbor", "meadow", "tundra", "delta", "ridge",
        "violet", "indigo", "crimson", "saffron", "olive", "teal", "scarlet", "azure",
        "tiger", "otter", "heron", "lynx", "raven", "sparrow", "marten", "bison",
        "copper", "silver", "golden", "platinum", "bronze", "marble", "granite", "slate",
        "breeze", "tempest", "zephyr", "cascade", "glacier", "prairie", "lagoon", "fjord",
        "orbit", "photon", "quasar", "nebula", "pulsar", "vector", "cipher", "matrix",
    ];

    /// <summary>
    /// Generates a fresh capability token. Security comes entirely from the 128-bit CSPRNG suffix.
    /// Regenerates if the assembled label is reserved or exceeds the 63-char DNS limit.
    /// </summary>
    public static string Generate()
    {
        while (true)
        {
            var sb = new StringBuilder(48);
            for (var i = 0; i < WordCount; i++)
            {
                if (i > 0) sb.Append('-');
                sb.Append(Words[RandomNumberGenerator.GetInt32(Words.Length)]);
            }
            sb.Append('-').Append(NewSuffix());

            var token = sb.ToString();
            if (IsValidLabel(token))
                return token;
        }
    }

    /// <summary>
    /// Produces a base32-encoded suffix carrying <see cref="SuffixEntropyBytes"/> CSPRNG bytes
    /// (128 bits → 26 chars). Drawn from <see cref="RandomNumberGenerator"/> (never System.Random).
    /// </summary>
    public static string NewSuffix()
    {
        Span<byte> bytes = stackalloc byte[SuffixEntropyBytes];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder((SuffixEntropyBytes * 8 + 4) / 5);
        int bits = 0, acc = 0;
        foreach (var b in bytes)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(acc >> bits) & 31]);
            }
        }
        if (bits > 0)
            sb.Append(Base32Alphabet[(acc << (5 - bits)) & 31]);

        return sb.ToString();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="token"/> is a valid single DNS-1123 label
    /// (1–63 chars, <c>[a-z0-9-]</c>, no leading/trailing hyphen) and is not a reserved label.
    /// </summary>
    public static bool IsValidLabel(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 63)
            return false;
        if (Reserved.Contains(token))
            return false;
        return Dns1123LabelRegex.IsMatch(token);
    }
}
