using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Generates and validates per-preview <b>capability tokens</b>. A token is the only
/// secret protecting a preview URL (<c>https://{token}.{zone}</c>), so it must be a
/// hard-to-guess, single DNS-1123 label.
///
/// <para>
/// Shape: three words from a curated wordlist joined by <c>-</c> plus four lowercase hex
/// characters, e.g. <c>swift-falcon-amber-7a3f</c>. With <see cref="Words"/>.Length = 64 the
/// brute-force space is 64³ · 16⁴ = 2¹⁸ · 2¹⁶ = 2³⁴ ≈ 1.7×10¹⁰ — far beyond an online-guessable
/// space for a single short-lived capability URL. All randomness is drawn from a CSPRNG
/// (<see cref="RandomNumberGenerator"/>).
/// </para>
/// </summary>
public static class PreviewToken
{
    /// <summary>Reserved label that a token must never equal (collides with the platform host).</summary>
    public const string Reserved = "agentweaver";

    private const int HexChars = 4;
    private const int WordCount = 3;

    private static readonly Regex Dns1123LabelRegex = new(
        @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Curated wordlist (64 short, unambiguous, lowercase, alpha-only words).
    /// 64³ · 16⁴ ≈ 2³⁴ combined token space — see <see cref="PreviewToken"/> remarks.
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

    /// <summary>Generates a fresh capability token. Never returns <see cref="Reserved"/>.</summary>
    public static string Generate()
    {
        // Loop is defensive only — the curated wordlist contains no word equal to "agentweaver",
        // so the assembled token can never collide, but we re-roll to be safe.
        while (true)
        {
            var sb = new StringBuilder(48);
            for (var i = 0; i < WordCount; i++)
            {
                if (i > 0) sb.Append('-');
                sb.Append(Words[RandomNumberGenerator.GetInt32(Words.Length)]);
            }
            sb.Append('-');
            for (var i = 0; i < HexChars; i++)
                sb.Append("0123456789abcdef"[RandomNumberGenerator.GetInt32(16)]);

            var token = sb.ToString();
            if (IsValidLabel(token))
                return token;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="token"/> is a valid single DNS-1123 label
    /// (1–63 chars, <c>[a-z0-9-]</c>, no leading/trailing hyphen) and is not the reserved label.
    /// </summary>
    public static bool IsValidLabel(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 63)
            return false;
        if (string.Equals(token, Reserved, StringComparison.Ordinal))
            return false;
        return Dns1123LabelRegex.IsMatch(token);
    }
}
