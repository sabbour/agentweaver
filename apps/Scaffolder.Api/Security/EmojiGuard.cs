using System.Globalization;

namespace Scaffolder.Api.Security;

/// <summary>
/// Enforces the no-emoji rule (Principle VIII) on any string that leaves the
/// API: event payloads, operational records, and outgoing response bodies. A
/// codepoint is treated as an emoji when it falls in the supplementary pictographic
/// range (U+1F300 through U+1FFFF) or the common symbol blocks that carry emoji
/// presentation.
/// </summary>
public static class EmojiGuard
{
    public static bool ContainsEmoji(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsEmojiCodepoint(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    public static void EnsureNone(string value, string context)
    {
        if (ContainsEmoji(value))
        {
            throw new EmojiContentException(context);
        }
    }

    private static bool IsEmojiCodepoint(int codepoint)
    {
        // Supplementary pictographic planes (emoji, symbols, transport, supplemental).
        if (codepoint is >= 0x1F300 and <= 0x1FFFF)
        {
            return true;
        }

        // Miscellaneous symbols and dingbats that commonly render as emoji.
        if (codepoint is >= 0x2600 and <= 0x27BF)
        {
            return true;
        }

        // Variation selector-16 forces emoji presentation on an otherwise plain glyph.
        return codepoint == 0xFE0F;
    }

    public static string Describe(int codepoint) =>
        "U+" + codepoint.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>Raised when emoji content is detected on an outbound surface.</summary>
public sealed class EmojiContentException : Exception
{
    public EmojiContentException(string context)
        : base($"Emoji content is not permitted in {context}.")
    {
    }
}
