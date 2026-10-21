using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>Reports JSON that cannot be represented as RFC 8785 canonical JSON.</summary>
public sealed class CanonicalJsonException(string code, string message) : FormatException(message)
{
    /// <summary>Stable machine-readable reason, for example <c>duplicate_key</c> or <c>invalid_utf8</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>Bounds applied while parsing JSON for canonicalization.</summary>
/// <param name="MaxBytes">Largest accepted UTF-8 input.</param>
/// <param name="MaxDepth">Deepest accepted object or array nesting.</param>
public sealed record CanonicalJsonLimits(int MaxBytes = 16 * 1024 * 1024, int MaxDepth = 64)
{
    /// <summary>Default limits used when the caller does not supply stricter ones.</summary>
    public static CanonicalJsonLimits Default { get; } = new();
}

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme (JCS). Parsing rejects duplicate object keys, lone surrogates,
/// invalid UTF-8, and non-finite numbers instead of silently choosing one value.
/// </summary>
/// <remarks>
/// Hashed records and AgentSuite normative JSON documents use these canonical bytes.
/// Object members sort by UTF-16 code units, strings escape only the characters RFC 8785 requires, and
/// numbers use the ECMAScript shortest round-trip form.
/// </remarks>
public static class CanonicalJson
{
    /// <summary>Returns the canonical UTF-8 bytes for one JSON value.</summary>
    /// <exception cref="CanonicalJsonException">The input is not valid, bounded, duplicate-free JSON.</exception>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json, CanonicalJsonLimits? limits = null)
    {
        // Parse fully first so every validation error is raised before any output is produced.
        var value = Parse(utf8Json, limits);
        // Canonical output is usually no larger than the input, so the input size is a good starting capacity.
        var buffer = new ArrayBufferWriter<byte>(Math.Max(16, utf8Json.Length));
        Write(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Returns true when the bytes are already exactly the canonical form of their JSON value.</summary>
    /// <exception cref="CanonicalJsonException">The input is not valid, bounded, duplicate-free JSON.</exception>
    public static bool IsCanonical(ReadOnlySpan<byte> utf8Json, CanonicalJsonLimits? limits = null) =>
        utf8Json.SequenceEqual(Canonicalize(utf8Json, limits));

    /// <summary>Appends one JSON string using RFC 8785 escaping.</summary>
    /// <exception cref="CanonicalJsonException">The value contains a lone UTF-16 surrogate.</exception>
    public static void WriteString(IBufferWriter<byte> output, string value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);
        // A lone surrogate has no UTF-8 encoding, so it would change silently when written. Reject it instead.
        RequireWellFormed(value);
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            // RFC 8785 escapes only the quote, the backslash, and control characters below U+0020.
            // Every other character, including non-ASCII and '/', is written as itself.
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    // Control characters without a short escape use \u00XX with lowercase hex, as RFC 8785 requires.
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
        AppendUtf8(output, builder.ToString());
    }

    /// <summary>Formats a finite double with the ECMAScript Number-to-String algorithm required by RFC 8785.</summary>
    /// <exception cref="CanonicalJsonException">The value is NaN or infinite.</exception>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
            throw new CanonicalJsonException("invalid_number", "Canonical JSON numbers must be finite.");
        // This also covers -0, which ECMAScript prints as "0" without a sign.
        if (value == 0)
            return "0";
        var negative = value < 0;
        // "R" gives the shortest digit string that parses back to the same double. Only the layout of
        // those digits differs from ECMAScript, so the code below re-lays them out.
        var roundTrip = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        // Split .NET output such as "1.5E-07" into the mantissa "1.5" and the exponent -7.
        var exponentIndex = roundTrip.IndexOfAny(['E', 'e']);
        var mantissa = exponentIndex < 0 ? roundTrip : roundTrip[..exponentIndex];
        var exponent = exponentIndex < 0 ? 0 : int.Parse(roundTrip[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        // Remove the decimal point to get the raw digit string, and remember how many digits came before it.
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa[..point] + mantissa[(point + 1)..];
        var integerDigits = point < 0 ? mantissa.Length : point;
        // Leading zeros (for example in "0.001") shift the decimal position, so count them before trimming.
        // Trailing zeros are not significant digits and are dropped too.
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        if (digits.Length == 0)
            return "0";
        // ECMAScript: value = digits × 10^(n − k), with k significant digits.
        var n = integerDigits - leadingZeros + exponent;
        var k = digits.Length;
        var builder = new StringBuilder(negative ? "-" : string.Empty);
        // The four branches follow the ECMAScript Number::toString steps in order.
        // Whole number up to 21 digits: write the digits, then pad with zeros. Example: 1e20 -> "100000000000000000000".
        if (k <= n && n <= 21)
            builder.Append(digits).Append('0', n - k);
        // Decimal point falls inside the digits. Example: 123.45.
        else if (0 < n && n <= 21)
            builder.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        // Small fraction down to 1e-6: write "0." and leading zeros. Example: 0.000001.
        else if (-6 < n && n <= 0)
            builder.Append("0.").Append('0', -n).Append(digits);
        // Anything larger or smaller uses exponent form with an explicit sign. Example: 1e+21 or 1.5e-7.
        else
        {
            builder.Append(digits[0]);
            if (k > 1)
                builder.Append('.').Append(digits, 1, k - 1);
            var e = n - 1;
            builder.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    // A small in-memory tree. Objects keep members in input order; sorting happens only when writing.
    // Numbers are stored as doubles because RFC 8785 defines numbers by their IEEE 754 double value.
    private abstract record Node;
    private sealed record ObjectNode(List<KeyValuePair<string, Node>> Members) : Node;
    private sealed record ArrayNode(List<Node> Items) : Node;
    private sealed record StringNode(string Value) : Node;
    private sealed record NumberNode(double Value) : Node;
    private sealed record LiteralNode(string Text) : Node;

    private static Node Parse(ReadOnlySpan<byte> utf8Json, CanonicalJsonLimits? limits)
    {
        limits ??= CanonicalJsonLimits.Default;
        // Check the size before any parsing work so oversized input is cheap to reject.
        if (utf8Json.Length > limits.MaxBytes)
            throw new CanonicalJsonException("too_large", $"JSON input exceeds the {limits.MaxBytes}-byte limit.");
        try
        {
            // Validate the whole buffer up front. Otherwise invalid bytes could be replaced while decoding
            // strings, and two different inputs could produce the same canonical bytes.
            if (!System.Text.Unicode.Utf8.IsValid(utf8Json))
                throw new CanonicalJsonException("invalid_utf8", "JSON input is not valid UTF-8.");
            // Strict RFC 8259 JSON only: no comments, no trailing commas, and bounded nesting depth.
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaxDepth,
            });
            if (!reader.Read())
                throw new CanonicalJsonException("invalid_json", "JSON input is empty.");
            var value = ReadValue(ref reader);
            // Exactly one top-level value is allowed. A second value after it is an error, not ignored.
            if (reader.Read())
                throw new CanonicalJsonException("invalid_json", "JSON input has trailing content.");
            return value;
        }
        // Convert reader errors into the single exception type that callers handle, with a stable code.
        catch (JsonException exception)
        {
            throw new CanonicalJsonException("invalid_json", $"JSON input is invalid: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new CanonicalJsonException("invalid_json", $"JSON input is invalid: {exception.Message}");
        }
    }

    private static Node ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var members = new List<KeyValuePair<string, Node>>();
                // Keys are compared after unescaping, so "a" and "\u0061" count as the same key.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    // Inside an object the reader is now on a property name token.
                    var key = ReadString(ref reader);
                    RequireWellFormed(key);
                    // Parsers disagree on which duplicate wins, so a duplicate key makes the meaning ambiguous.
                    if (!seen.Add(key))
                        throw new CanonicalJsonException("duplicate_key", $"JSON object repeats the key '{key}'.");
                    // Move from the property name to its value.
                    reader.Read();
                    members.Add(new(key, ReadValue(ref reader)));
                }
                return new ObjectNode(members);
            }
            case JsonTokenType.StartArray:
            {
                var items = new List<Node>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    items.Add(ReadValue(ref reader));
                return new ArrayNode(items);
            }
            case JsonTokenType.String:
            {
                var value = ReadString(ref reader);
                RequireWellFormed(value);
                return new StringNode(value);
            }
            case JsonTokenType.Number:
                // A number too large for a double (for example 1e400) would become infinity. Reject it
                // instead of writing a different value. Precision beyond a double is rounded, as in ECMAScript.
                if (!reader.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new CanonicalJsonException("invalid_number", "JSON number is not a finite IEEE 754 double.");
                return new NumberNode(number);
            case JsonTokenType.True: return new LiteralNode("true");
            case JsonTokenType.False: return new LiteralNode("false");
            case JsonTokenType.Null: return new LiteralNode("null");
            default:
                throw new CanonicalJsonException("invalid_json", $"Unexpected JSON token {reader.TokenType}.");
        }
    }

    private static void Write(IBufferWriter<byte> output, Node node)
    {
        switch (node)
        {
            case ObjectNode obj:
                AppendUtf8(output, "{");
                var first = true;
                // RFC 8785 sorts keys by UTF-16 code units. StringComparer.Ordinal compares .NET strings
                // char by char, and a .NET char is one UTF-16 code unit, so it gives exactly that order.
                // Do not use a culture-aware comparer here.
                foreach (var member in obj.Members.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (!first) AppendUtf8(output, ",");
                    first = false;
                    WriteString(output, member.Key);
                    AppendUtf8(output, ":");
                    Write(output, member.Value);
                }
                AppendUtf8(output, "}");
                break;
            case ArrayNode array:
                AppendUtf8(output, "[");
                // Array order is meaningful, so items keep their input order.
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) AppendUtf8(output, ",");
                    Write(output, array.Items[index]);
                }
                AppendUtf8(output, "]");
                break;
            case StringNode text: WriteString(output, text.Value); break;
            case NumberNode number: AppendUtf8(output, FormatNumber(number.Value)); break;
            case LiteralNode literal: AppendUtf8(output, literal.Text); break;
        }
        // No whitespace is written anywhere; canonical JSON has none between tokens.
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString()!;
        }
        catch (InvalidOperationException) when (reader.ValueIsEscaped)
        {
            // The input is valid UTF-8, so an escape that fails to decode is an unpaired \u surrogate.
            throw new CanonicalJsonException("lone_surrogate", "JSON string contains a lone UTF-16 surrogate escape.");
        }
    }

    private static void RequireWellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            // A high surrogate followed by a low surrogate is one valid character outside the BMP; skip both.
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                index++;
            // Any other surrogate is unpaired and cannot be encoded as UTF-8.
            else if (char.IsSurrogate(value[index]))
                throw new CanonicalJsonException("lone_surrogate", "JSON string contains a lone UTF-16 surrogate.");
        }
    }

    private static void AppendUtf8(IBufferWriter<byte> output, string text)
    {
        // Reserve the worst-case size, then advance only by the bytes actually written.
        var span = output.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        var written = Encoding.UTF8.GetBytes(text, span);
        output.Advance(written);
    }
}
