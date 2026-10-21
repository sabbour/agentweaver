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
/// Use these bytes when a content hash or signature must cover one exact JSON form.
/// Object members sort by UTF-16 code units, strings escape only the characters RFC 8785 requires, and
/// numbers use the ECMAScript shortest round-trip form.
/// </remarks>
public static class CanonicalJson
{
    /// <summary>Returns the canonical UTF-8 bytes for one JSON value.</summary>
    /// <exception cref="CanonicalJsonException">The input is not valid, bounded, duplicate-free JSON.</exception>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json, CanonicalJsonLimits? limits = null)
    {
        var value = Parse(utf8Json, limits);
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
        RequireWellFormed(value);
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
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
        if (value == 0)
            return "0";
        var negative = value < 0;
        var roundTrip = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = roundTrip.IndexOfAny(['E', 'e']);
        var mantissa = exponentIndex < 0 ? roundTrip : roundTrip[..exponentIndex];
        var exponent = exponentIndex < 0 ? 0 : int.Parse(roundTrip[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa[..point] + mantissa[(point + 1)..];
        var integerDigits = point < 0 ? mantissa.Length : point;
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        if (digits.Length == 0)
            return "0";
        // ECMAScript: value = digits × 10^(n − k), with k significant digits.
        var n = integerDigits - leadingZeros + exponent;
        var k = digits.Length;
        var builder = new StringBuilder(negative ? "-" : string.Empty);
        if (k <= n && n <= 21)
            builder.Append(digits).Append('0', n - k);
        else if (0 < n && n <= 21)
            builder.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        else if (-6 < n && n <= 0)
            builder.Append("0.").Append('0', -n).Append(digits);
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

    private abstract record Node;
    private sealed record ObjectNode(List<KeyValuePair<string, Node>> Members) : Node;
    private sealed record ArrayNode(List<Node> Items) : Node;
    private sealed record StringNode(string Value) : Node;
    private sealed record NumberNode(double Value) : Node;
    private sealed record LiteralNode(string Text) : Node;

    private static Node Parse(ReadOnlySpan<byte> utf8Json, CanonicalJsonLimits? limits)
    {
        limits ??= CanonicalJsonLimits.Default;
        if (utf8Json.Length > limits.MaxBytes)
            throw new CanonicalJsonException("too_large", $"JSON input exceeds the {limits.MaxBytes}-byte limit.");
        try
        {
            if (!System.Text.Unicode.Utf8.IsValid(utf8Json))
                throw new CanonicalJsonException("invalid_utf8", "JSON input is not valid UTF-8.");
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaxDepth,
            });
            if (!reader.Read())
                throw new CanonicalJsonException("invalid_json", "JSON input is empty.");
            var value = ReadValue(ref reader);
            if (reader.Read())
                throw new CanonicalJsonException("invalid_json", "JSON input has trailing content.");
            return value;
        }
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
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = ReadString(ref reader);
                    RequireWellFormed(key);
                    if (!seen.Add(key))
                        throw new CanonicalJsonException("duplicate_key", $"JSON object repeats the key '{key}'.");
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
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                index++;
            else if (char.IsSurrogate(value[index]))
                throw new CanonicalJsonException("lone_surrogate", "JSON string contains a lone UTF-16 surrogate.");
        }
    }

    private static void AppendUtf8(IBufferWriter<byte> output, string text)
    {
        var span = output.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        var written = Encoding.UTF8.GetBytes(text, span);
        output.Advance(written);
    }
}
