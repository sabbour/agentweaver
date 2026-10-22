using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Go standard-library behaviors that upstream AgentSuite error text and checks depend on.</summary>
internal static class Go
{
    /// <summary>Go <c>errors.Join</c>: non-nil messages joined by newlines, or null when all are nil.</summary>
    public static string? Join(IEnumerable<string?> errors)
    {
        // Nil errors are dropped; if nothing is left the joined error is nil too.
        var present = errors.Where(error => error is not null).ToList();
        return present.Count == 0 ? null : string.Join('\n', present);
    }

    /// <summary>Go <c>strconv.Quote</c> (the <c>%q</c> verb) for a string.</summary>
    public static string Quote(string value) => Quote(Bytes(value));

    /// <summary>
    /// Decodes Go byte strings losslessly: invalid UTF-8 bytes become lone surrogates U+DC80..U+DCFF
    /// so that <see cref="Bytes"/> restores the exact original bytes.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> value)
    {
        var builder = new StringBuilder(value.Length);
        while (!value.IsEmpty)
        {
            // An invalid byte is stored as the lone surrogate U+DC00+byte (only 0x80-0xFF can be invalid,
            // giving U+DC80..U+DCFF). Valid text never contains lone surrogates, so this cannot collide.
            if (Rune.DecodeFromUtf8(value, out var rune, out var width) != System.Buffers.OperationStatus.Done)
            {
                builder.Append((char)(0xDC00 + value[0]));
                value = value[1..];
                continue;
            }
            // Valid UTF-8 sequence: append the character and advance by its byte width.
            builder.Append(rune.ToString());
            value = value[width..];
        }
        return builder.ToString();
    }

    /// <summary>The Go bytes of a string produced by <see cref="Decode"/> or a valid .NET string.</summary>
    public static byte[] Bytes(string value)
    {
        var output = new List<byte>(value.Length);
        // Scratch space for one UTF-8 encoded character (at most 4 bytes).
        Span<byte> buffer = stackalloc byte[4];
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            // A valid surrogate pair is one supplementary character; encode it as 4 UTF-8 bytes.
            if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                var written = new Rune(c, value[index + 1]).EncodeToUtf8(buffer);
                for (var i = 0; i < written; i++)
                    output.Add(buffer[i]);
                index++;
                continue;
            }
            // A lone surrogate in U+DC80..U+DCFF is a raw byte saved by Decode; write the original byte back.
            if (c >= 0xDC80 && c <= 0xDCFF)
            {
                output.Add((byte)(c - 0xDC00));
                continue;
            }
            // Any other lone surrogate cannot be encoded, so it becomes U+FFFD, as Go does for invalid runes.
            var rune = char.IsSurrogate(c) ? Rune.ReplacementChar : new Rune(c);
            var count = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < count; i++)
                output.Add(buffer[i]);
        }
        return [.. output];
    }

    /// <summary>Go <c>utf8.ValidString</c> for a string produced by <see cref="Decode"/>.</summary>
    public static bool ValidString(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            // Proper surrogate pairs are valid characters; skip both halves.
            if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }
            // A lone surrogate means the original Go bytes were not valid UTF-8 (see Decode).
            if (char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    /// <summary>Go <c>strconv.Quote</c> over raw bytes; invalid UTF-8 bytes render as <c>\xNN</c>.</summary>
    public static string Quote(ReadOnlySpan<byte> value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        while (!value.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(value, out var rune, out var width);
            // Each invalid byte is printed on its own as \xNN, then decoding resumes at the next byte.
            if (status != System.Buffers.OperationStatus.Done)
            {
                builder.Append("\\x").Append(value[0].ToString("x2", CultureInfo.InvariantCulture));
                value = value[1..];
                continue;
            }
            value = value[width..];
            AppendEscapedRune(builder, rune);
        }
        builder.Append('"');
        return builder.ToString();
    }

    // Escapes one character using the same rules as Go's strconv.Quote.
    private static void AppendEscapedRune(StringBuilder builder, Rune rune)
    {
        var r = rune.Value;
        // The quote and backslash always need a backslash in front.
        if (r == '"' || r == '\\')
        {
            builder.Append('\\').Append((char)r);
            return;
        }
        // Printable characters (including non-ASCII letters) are written as is.
        if (IsPrint(rune))
        {
            builder.Append(rune.ToString());
            return;
        }
        // Common control characters use their short C-style escapes.
        switch (r)
        {
            case '\a': builder.Append("\\a"); return;
            case '\b': builder.Append("\\b"); return;
            case '\f': builder.Append("\\f"); return;
            case '\n': builder.Append("\\n"); return;
            case '\r': builder.Append("\\r"); return;
            case '\t': builder.Append("\\t"); return;
            case '\v': builder.Append("\\v"); return;
        }
        // Other non-printable characters: \xNN for ASCII controls and DEL, \uNNNN for the
        // Basic Multilingual Plane, and \UNNNNNNNN above it.
        if (r < ' ' || r == 0x7f)
            builder.Append("\\x").Append(r.ToString("x2", CultureInfo.InvariantCulture));
        else if (r < 0x10000)
            builder.Append("\\u").Append(r.ToString("x4", CultureInfo.InvariantCulture));
        else
            builder.Append("\\U").Append(r.ToString("x8", CultureInfo.InvariantCulture));
    }

    /// <summary>Go <c>unicode.IsPrint</c>: letters, marks, numbers, punctuation, symbols, and ASCII space.</summary>
    public static bool IsPrint(Rune rune)
    {
        // ASCII space is the only space character Go counts as printable.
        if (rune.Value == ' ')
            return true;
        // Everything is printable except separators, control/format characters, surrogates,
        // private-use and unassigned code points (Go's graphic categories L, M, N, P, S).
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned => false,
            _ => true,
        };
    }

    /// <summary>Go <c>%#o</c> for a signed integer.</summary>
    public static string AlternateOctal(long value)
    {
        // Go prints zero as plain "0" (no extra leading zero).
        if (value == 0)
            return "0";
        var negative = value < 0;
        // Negate as unsigned so long.MinValue does not overflow.
        var magnitude = negative ? unchecked((ulong)(-value)) : (ulong)value;
        var digits = new StringBuilder();
        // Take 3 bits at a time to produce octal digits, most significant first.
        while (magnitude > 0)
        {
            digits.Insert(0, (char)('0' + (int)(magnitude & 7)));
            magnitude >>= 3;
        }
        // The '#' flag adds a leading 0; the minus sign goes before it (e.g. -0755).
        return (negative ? "-0" : "0") + digits;
    }

    /// <summary>Go <c>path.Clean</c>.</summary>
    public static string CleanPath(string path)
    {
        // Go's path.Clean returns "." for an empty path.
        if (path.Length == 0)
            return ".";
        var rooted = path[0] == '/';
        var n = path.Length;
        var output = new StringBuilder(n);
        // r is the read index into path. dotdot is the output length that ".." may not back up past.
        int r = 0, dotdot = 0;
        if (rooted)
        {
            output.Append('/');
            r = 1;
            dotdot = 1;
        }
        while (r < n)
        {
            // Collapse repeated slashes.
            if (path[r] == '/')
                r++;
            // Drop "." elements.
            else if (path[r] == '.' && (r + 1 == n || path[r + 1] == '/'))
                r++;
            // A ".." element removes the previous element if there is one.
            else if (path[r] == '.' && path[r + 1] == '.' && (r + 2 == n || path[r + 2] == '/'))
            {
                r += 2;
                // Back up to the previous slash in the output.
                if (output.Length > dotdot)
                {
                    var w = output.Length - 1;
                    while (w > dotdot && output[w] != '/')
                        w--;
                    output.Length = w;
                }
                // Relative path with nothing to remove: keep the ".." and move the backup limit past it.
                // For a rooted path, ".." at the root is simply dropped ("/.." becomes "/").
                else if (!rooted)
                {
                    if (output.Length > 0)
                        output.Append('/');
                    output.Append("..");
                    dotdot = output.Length;
                }
            }
            // A normal element: add a separator when needed, then copy the element up to the next slash.
            else
            {
                if ((rooted && output.Length != 1) || (!rooted && output.Length != 0))
                    output.Append('/');
                for (; r < n && path[r] != '/'; r++)
                    output.Append(path[r]);
            }
        }
        // Everything removed (e.g. "a/..") means the current directory.
        return output.Length == 0 ? "." : output.ToString();
    }

    /// <summary>Go <c>path.Dir</c>.</summary>
    public static string PathDir(string path)
    {
        // Keep everything up to and including the last slash, then clean it.
        // With no slash, slash is -1 and the empty prefix cleans to ".".
        var slash = path.LastIndexOf('/');
        return CleanPath(path[..(slash + 1)]);
    }

    /// <summary>Go <c>path.Join</c> for two elements.</summary>
    public static string PathJoin(string first, string second)
    {
        // Go's path.Join ignores empty elements and returns "" only when all elements are empty.
        if (first.Length == 0 && second.Length == 0)
            return string.Empty;
        if (first.Length == 0)
            return CleanPath(second);
        if (second.Length == 0)
            return CleanPath(first);
        return CleanPath(first + "/" + second);
    }

    /// <summary>Go <c>strings.TrimSpace</c>: trims Unicode white space.</summary>
    public static string TrimSpace(string value)
    {
        var start = 0;
        var end = value.Length;
        // Work on whole characters (runes), not UTF-16 units, so surrogate pairs are handled correctly.
        // Trim from the front until a non-space character (or an invalid lone surrogate) is found.
        while (start < end)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(start, end - start), out var rune, out var width);
            if (status != System.Buffers.OperationStatus.Done || !IsSpace(rune))
                break;
            start += width;
        }
        // Then trim from the back in the same way.
        while (end > start)
        {
            var status = Rune.DecodeLastFromUtf16(value.AsSpan(start, end - start), out var rune, out var width);
            if (status != System.Buffers.OperationStatus.Done || !IsSpace(rune))
                break;
            end -= width;
        }
        return value[start..end];
    }

    /// <summary>Go <c>unicode.IsSpace</c>.</summary>
    public static bool IsSpace(Rune rune) => rune.Value switch
    {
        // Go has a fixed list for Latin-1 (note: U+0085 NEL and U+00A0 NBSP count as space)
        // and uses the Unicode White_Space property above that.
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or 0x85 or 0xA0 => true,
        _ => rune.Value > 0xff && Rune.IsWhiteSpace(rune),
    };

    /// <summary>Go <c>unicode.IsControl</c>: C0, DEL and C1 code points only.</summary>
    public static bool IsControl(Rune rune) => rune.Value < 0x20 || (rune.Value >= 0x7f && rune.Value <= 0x9f);

    /// <summary>Go <c>utf8.RuneCountInString</c> for a well-formed string.</summary>
    public static int RuneCount(string value)
    {
        var count = 0;
        // Counts Unicode characters, so a surrogate pair counts once (UTF-16 Length would count it twice).
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }

    // Fast path for normal strings; strings holding raw bytes from Decode need the exact byte conversion.
    /// <summary>Go byte length of a string (UTF-8).</summary>
    public static int ByteLength(string value) => ValidString(value) ? Encoding.UTF8.GetByteCount(value) : Bytes(value).Length;

    /// <summary>Go ordinal string comparison (byte order of UTF-8 equals code point order).</summary>
    public static int Compare(string left, string right)
    {
        // .NET's ordinal compare uses UTF-16 order, which differs from UTF-8 byte order for characters
        // above U+FFFF. Comparing code points instead gives the same order as Go's byte comparison.
        var leftRunes = left.EnumerateRunes().GetEnumerator();
        var rightRunes = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext();
            var hasRight = rightRunes.MoveNext();
            // When one string runs out, the shorter one sorts first; equal length means equal strings.
            if (!hasLeft || !hasRight)
                return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;
            var difference = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (difference != 0)
                return difference;
        }
    }

    /// <summary>Go-ordered comparer for sorting strings by UTF-8 bytes.</summary>
    public static readonly IComparer<string> Ordinal = Comparer<string>.Create(Compare);

    /// <summary>
    /// Approximates <c>golang.org/x/text/cases.Fold</c> with invariant upper-then-lower mapping.
    /// Full Unicode case folding differs only for special characters (for example ß and final sigma).
    /// </summary>
    public static string FoldCase(string value) => value.ToUpperInvariant().ToLowerInvariant();

    public static string DigestBytes(ReadOnlySpan<byte> data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Upstream <c>IsValidDigest</c>.</summary>
    public static bool IsValidDigest(string value)
    {
        // Exactly "sha256:" followed by 64 lowercase hex digits; uppercase hex is rejected.
        if (!value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 7 + 64)
            return false;
        for (var index = 7; index < value.Length; index++)
        {
            var c = value[index];
            if (c < '0' || (c > '9' && c < 'a') || c > 'f')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Compiles a Go RE2 pattern. Go <c>$</c> without the m flag is end of text, so patterns are written with
    /// <c>\z</c>. NonBacktracking keeps matching linear-time like RE2 on untrusted input.
    /// </summary>
    public static Regex Re(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | extra);

    // Patterns use \z (true end of text) because .NET's $ also matches before a final newline,
    // which Go's $ does not.
    public static readonly Regex IdentifierPattern = Re(@"^[a-z0-9][a-z0-9._-]{0,127}\z");
    public static readonly Regex VersionPattern = Re(@"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?\z");
    public static readonly Regex EnvPattern = Re(@"^[A-Z_][A-Z0-9_]*\z");
    public static readonly Regex CapabilityPattern = Re(@"^[a-z0-9][a-z0-9.-]{0,127}\z");
    public static readonly Regex SchemePattern = Re(@"^[a-z][a-z0-9+.-]*\z");
    public static readonly Regex HeaderNamePattern = Re(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+\z");
    public static readonly Regex SecretKeyPattern = Re(@"^[A-Za-z0-9._-]{1,253}\z");
    public static readonly Regex EvaluationIdPattern = Re(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z");
    private static readonly Regex EvidenceTokenPattern = Re(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z");
    // Evidence tokens must not look like secrets or private data: known credential prefixes
    // (OpenAI/GitHub/AWS keys, JWTs starting with "eyJ"), host names, or IPv4 addresses.
    private static readonly Regex EvidencePrivatePattern = Re(@"^(sk[-_]|gh[pousr]_|github_pat_|AKIA|ASIA|eyJ)|^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\z", RegexOptions.IgnoreCase);
    private static readonly Regex EvidenceIpPattern = Re(@"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z");

    // Accept only safe-looking tokens that do not match the secret, host-name or IP patterns.
    /// <summary>Upstream <c>evidenceToken</c>.</summary>
    public static bool EvidenceToken(string value) =>
        EvidenceTokenPattern.IsMatch(value) && !EvidencePrivatePattern.IsMatch(value) && !EvidenceIpPattern.IsMatch(value);
}
