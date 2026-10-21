using System.Globalization;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config.AgentSuite;

internal enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>A parsed JSON value that keeps member order, raw number text and its exact source bytes.</summary>
internal sealed class JsonNode
{
    public required JsonKind Kind { get; init; }
    public required byte[] Source { get; init; }
    public int Start { get; set; }
    public int End { get; set; }
    public List<KeyValuePair<string, JsonNode>>? Members { get; init; }
    public List<JsonNode>? Items { get; init; }

    /// <summary>String value, or the raw literal text of a number.</summary>
    public string? Text { get; init; }

    public ReadOnlySpan<byte> Raw => Source.AsSpan(Start, End - Start);

    public byte[] RawBytes() => Raw.ToArray();

    public bool IsNull => Kind == JsonKind.Null;

    public JsonNode? Get(string key)
    {
        if (Members is null)
            return null;
        foreach (var member in Members)
        {
            if (string.Equals(member.Key, key, StringComparison.Ordinal))
                return member.Value;
        }
        return null;
    }

    public bool Has(string key) => Get(key) is not null;

    /// <summary>The <c>UnmarshalTypeError.Value</c> name Go uses for this JSON kind.</summary>
    public string GoValueName => Kind switch
    {
        JsonKind.Object => "object",
        JsonKind.Array => "array",
        JsonKind.String => "string",
        JsonKind.Number => "number",
        JsonKind.True or JsonKind.False => "bool",
        _ => "null",
    };
}

/// <summary>
/// Ports the upstream <c>internal/jsoncodec</c> strict decoder: Go <c>encoding/json</c> token scanning,
/// duplicate-key and size limits, JCS equivalence, exact field checks and typed decoding.
/// </summary>
internal static class AgentSuiteJson
{
    public const int MaxBytes = 4 << 20;
    public const int MaxDepth = 100;
    public const int MaxObjectMembers = 1024;
    public const int MaxDocumentMembers = 100_000;
    public const string MemberLimitError = "JSON object member limit exceeded";
    public static readonly string DepthLimitError = $"JSON nesting exceeds {MaxDepth}";

    // Go's JCS library has no nesting bound; this bound only protects the stack on hostile input.
    private static readonly CanonicalJsonLimits JcsLimits = new(MaxBytes: 64 * 1024 * 1024, MaxDepth: 1000);

    /// <summary>Upstream <c>jsoncodec.Unmarshal</c>; returns the Go error text or null and the parsed node.</summary>
    public static string? Unmarshal(byte[] data, GoType target, out JsonNode? node)
    {
        node = null;
        if (data.Length == 0)
            return "document is empty";
        if (data.Length > MaxBytes)
            return $"document exceeds {MaxBytes} bytes";
        if (!Utf8IsValid(data))
            return "document is not valid UTF-8";
        if (ValidateUnicodeEscapes(data) is { } escapeError)
            return escapeError;
        if (Scan(data, MaxObjectMembers, MaxDocumentMembers, out node) is { } scanError)
            return scanError;
        if (ValidateCanonicalEquivalence(node!) is { } jcsError)
            return jcsError;
        return StrictDecode(node!, target);
    }

    /// <summary>Exact field checks and typed decoding on an already scanned value (the nested-Unmarshal path).</summary>
    public static string? StrictDecode(JsonNode node, GoType target)
    {
        if (ValidateJsonType(node, target) is { } fieldError)
            return fieldError;
        return GoDecoder.Decode(node, target, strict: true);
    }

    public static bool Utf8IsValid(ReadOnlySpan<byte> data) => System.Text.Unicode.Utf8.IsValid(data);

    /// <summary>Upstream <c>matchesCanonicalDescriptor</c> raw checks plus stored-JCS equality.</summary>
    public static bool MatchesCanonical(byte[] data, string digest, long size) =>
        data.LongLength == size && Go.DigestBytes(data) == digest && IsCanonical(data);

    /// <summary>True when <paramref name="data"/> equals its own JCS form (Go <c>jcs.Transform</c>).</summary>
    public static bool IsCanonical(byte[] data)
    {
        try
        {
            return CanonicalJson.IsCanonical(data, JcsLimits);
        }
        catch (CanonicalJsonException)
        {
            return false;
        }
    }

    /// <summary>JCS bytes, or null when the JSON cannot be canonicalized.</summary>
    public static byte[]? Canonicalize(ReadOnlySpan<byte> data)
    {
        try
        {
            return CanonicalJson.Canonicalize(data, JcsLimits);
        }
        catch (CanonicalJsonException)
        {
            return null;
        }
    }

    /// <summary>Upstream <c>validateUnicodeEscapes</c>, a byte-level scan that runs before tokenizing.</summary>
    public static string? ValidateUnicodeEscapes(byte[] data)
    {
        var inString = false;
        for (var i = 0; i < data.Length; i++)
        {
            switch (data[i])
            {
                case (byte)'"':
                    inString = !inString;
                    break;
                case (byte)'\\':
                    if (!inString || i + 1 >= data.Length)
                        continue;
                    if (data[i + 1] != 'u')
                    {
                        i++;
                        continue;
                    }
                    if (!DecodeHexEscape(data, i + 2, out var value))
                        continue;
                    if (value >= 0xd800 && value <= 0xdbff)
                    {
                        if (i + 11 >= data.Length || data[i + 6] != '\\' || data[i + 7] != 'u')
                            return "JSON string contains an unpaired high surrogate";
                        if (!DecodeHexEscape(data, i + 8, out var low) || low < 0xdc00 || low > 0xdfff)
                            return "JSON string contains an unpaired high surrogate";
                        i += 11;
                    }
                    else if (value >= 0xdc00 && value <= 0xdfff)
                        return "JSON string contains an unpaired low surrogate";
                    else
                        i += 5;
                    break;
            }
        }
        return null;
    }

    private static bool DecodeHexEscape(byte[] data, int start, out int value)
    {
        value = 0;
        if (start + 4 > data.Length)
            return false;
        for (var index = start; index < start + 4; index++)
        {
            var c = data[index];
            value <<= 4;
            if (c >= '0' && c <= '9') value += c - '0';
            else if (c >= 'a' && c <= 'f') value += c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') value += c - 'A' + 10;
            else return false;
        }
        return true;
    }

    /// <summary>Upstream <c>validateJSONTokensWithLimits</c> (with no root array limit) that also builds the tree.</summary>
    public static string? Scan(byte[] data, int objectLimit, int documentLimit, out JsonNode? root)
    {
        var parser = new Parser(data, objectLimit, documentLimit);
        return parser.ParseDocument(out root);
    }

    /// <summary>Upstream <c>validateCanonicalEquivalence</c>: only numbers can change value under JCS.</summary>
    private static string? ValidateCanonicalEquivalence(JsonNode node)
    {
        string? failure = null;
        Visit(node);
        return failure;

        void Visit(JsonNode current)
        {
            if (failure is not null)
                return;
            switch (current.Kind)
            {
                case JsonKind.Object:
                    foreach (var member in current.Members!)
                        Visit(member.Value);
                    break;
                case JsonKind.Array:
                    foreach (var item in current.Items!)
                        Visit(item);
                    break;
                case JsonKind.Number:
                    if (!double.TryParse(current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
                    {
                        failure = $"document is not valid JCS JSON: strconv.ParseFloat: parsing {Go.Quote(current.Text!)}: value out of range";
                        return;
                    }
                    var transformed = CanonicalJson.FormatNumber(parsed);
                    if (long.TryParse(current.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    {
                        if (!long.TryParse(transformed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var other) || other != integer)
                            failure = "document value changes during JCS canonicalization";
                    }
                    break;
            }
        }
    }

    /// <summary>Upstream <c>validateJSONType</c>, the reflection walk run before typed decoding.</summary>
    public static string? ValidateJsonType(JsonNode node, GoType target)
    {
        var wasPointer = false;
        while (target is GoPointer pointer)
        {
            wasPointer = true;
            target = pointer.Elem;
        }
        if (target is GoRawMessage or GoInterface)
            return null;
        if (node.IsNull)
            return wasPointer || target is GoMap or GoSlice ? null : "null is not allowed";
        switch (target)
        {
            case GoStruct structType:
                if (node.Kind != JsonKind.Object)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
                foreach (var (name, value) in node.Members!)
                {
                    var field = structType.Field(name);
                    if (field is null)
                        return $"unknown field {Go.Quote(name)}";
                    if (ValidateJsonType(value, field) is { } error)
                        return $"field {Go.Quote(name)}: {error}";
                }
                break;
            case GoSlice slice:
                if (node.Kind != JsonKind.Array)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type []json.RawMessage";
                for (var index = 0; index < node.Items!.Count; index++)
                {
                    if (ValidateJsonType(node.Items[index], slice.Elem) is { } error)
                        return $"element {index}: {error}";
                }
                break;
            case GoMap map:
                if (node.Kind != JsonKind.Object)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
                foreach (var (name, value) in node.Members!)
                {
                    if (ValidateJsonType(value, map.Elem) is { } error)
                        return $"map value {Go.Quote(name)}: {error}";
                }
                break;
        }
        return null;
    }

    /// <summary>Go <c>json.Unmarshal(raw, &amp;map[string]json.RawMessage)</c> for an already parsed node.</summary>
    public static string? AsFieldMap(JsonNode node, out JsonNode? fields)
    {
        fields = null;
        if (node.IsNull)
            return null;
        if (node.Kind != JsonKind.Object)
            return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
        fields = node;
        return null;
    }

    /// <summary>Parses any JSON text with Go syntax rules and no limits beyond Go's own (used for non-strict reads).</summary>
    public static string? ParseLoose(byte[] data, out JsonNode? root)
    {
        if (!Utf8IsValid(data))
        {
            root = null;
            return "invalid character in string literal";
        }
        return new Parser(data, int.MaxValue, int.MaxValue, depthLimit: int.MaxValue).ParseDocument(out root);
    }

    /// <summary>A Go <c>encoding/json</c> compatible scanner that reports errors in document order.</summary>
    private sealed class Parser(byte[] data, int objectLimit, int documentLimit, int depthLimit = MaxDepth)
    {
        private int _position;
        private int _documentMembers;

        public string? ParseDocument(out JsonNode? root)
        {
            root = null;
            SkipWhitespace();
            if (_position >= data.Length)
                return "EOF";
            if (ParseValue(0, out root) is { } error)
                return error;
            SkipWhitespace();
            if (_position < data.Length)
            {
                var start = _position;
                if (ParseValue(0, out var trailing) is { } trailingError)
                {
                    _position = start;
                    return trailingError;
                }
                return $"unexpected trailing token {TokenText(trailing!)}";
            }
            return null;
        }

        private static string TokenText(JsonNode node) => node.Kind switch
        {
            JsonKind.Object => "{",
            JsonKind.Array => "[",
            JsonKind.String or JsonKind.Number => node.Text!,
            JsonKind.True => "true",
            JsonKind.False => "false",
            _ => "<nil>",
        };

        private void SkipWhitespace()
        {
            while (_position < data.Length && data[_position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                _position++;
        }

        private static string Invalid(byte c, string context) => $"invalid character {QuoteChar(c)} {context}";

        private static string QuoteChar(byte c)
        {
            if (c == '\'')
                return "'\\''";
            if (c == '"')
                return "'\"'";
            var quoted = Go.Quote(new Rune(c).ToString());
            return "'" + quoted[1..^1] + "'";
        }

        private string? ParseValue(int depth, out JsonNode? node)
        {
            node = null;
            SkipWhitespace();
            if (_position >= data.Length)
                return "unexpected EOF";
            var start = _position;
            var c = data[_position];
            switch (c)
            {
                case (byte)'{':
                {
                    if (depth >= depthLimit)
                        return DepthLimitError;
                    _position++;
                    var members = new List<KeyValuePair<string, JsonNode>>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    SkipWhitespace();
                    if (_position < data.Length && data[_position] == '}')
                    {
                        _position++;
                        node = new JsonNode { Kind = JsonKind.Object, Source = data, Start = start, End = _position, Members = members };
                        return null;
                    }
                    while (true)
                    {
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        if (seen.Count >= objectLimit || _documentMembers >= documentLimit)
                            return MemberLimitError;
                        if (data[_position] != '"')
                            return Invalid(data[_position], "looking for beginning of object key string");
                        if (ParseString(out var key) is { } keyError)
                            return keyError;
                        if (!seen.Add(key!))
                            return $"duplicate JSON key {Go.Quote(key!)}";
                        _documentMembers++;
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        if (data[_position] != ':')
                            return Invalid(data[_position], "after object key");
                        _position++;
                        if (ParseValue(depth + 1, out var value) is { } valueError)
                            return valueError;
                        members.Add(new(key!, value!));
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        if (data[_position] == ',')
                        {
                            _position++;
                            continue;
                        }
                        if (data[_position] == '}')
                        {
                            _position++;
                            break;
                        }
                        return Invalid(data[_position], "after object key:value pair");
                    }
                    node = new JsonNode { Kind = JsonKind.Object, Source = data, Start = start, End = _position, Members = members };
                    return null;
                }
                case (byte)'[':
                {
                    if (depth >= depthLimit)
                        return DepthLimitError;
                    _position++;
                    var items = new List<JsonNode>();
                    SkipWhitespace();
                    if (_position < data.Length && data[_position] == ']')
                    {
                        _position++;
                        node = new JsonNode { Kind = JsonKind.Array, Source = data, Start = start, End = _position, Items = items };
                        return null;
                    }
                    while (true)
                    {
                        if (ParseValue(depth + 1, out var item) is { } itemError)
                            return itemError;
                        items.Add(item!);
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        if (data[_position] == ',')
                        {
                            _position++;
                            continue;
                        }
                        if (data[_position] == ']')
                        {
                            _position++;
                            break;
                        }
                        return Invalid(data[_position], "after array element");
                    }
                    node = new JsonNode { Kind = JsonKind.Array, Source = data, Start = start, End = _position, Items = items };
                    return null;
                }
                case (byte)'"':
                {
                    if (ParseString(out var text) is { } stringError)
                        return stringError;
                    node = new JsonNode { Kind = JsonKind.String, Source = data, Start = start, End = _position, Text = text };
                    return null;
                }
                case (byte)'t':
                    return ParseLiteral("true", JsonKind.True, out node);
                case (byte)'f':
                    return ParseLiteral("false", JsonKind.False, out node);
                case (byte)'n':
                    return ParseLiteral("null", JsonKind.Null, out node);
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(out node);
                    return Invalid(c, "looking for beginning of value");
            }
        }

        private string? ParseLiteral(string literal, JsonKind kind, out JsonNode? node)
        {
            node = null;
            var start = _position;
            for (var index = 0; index < literal.Length; index++)
            {
                if (_position >= data.Length)
                    return "unexpected EOF";
                if (data[_position] != literal[index])
                    return Invalid(data[_position], $"in literal {literal} (expecting {QuoteChar((byte)literal[index])})");
                _position++;
            }
            node = new JsonNode { Kind = kind, Source = data, Start = start, End = _position };
            return null;
        }

        private string? ParseNumber(out JsonNode? node)
        {
            node = null;
            var start = _position;
            if (data[_position] == '-')
            {
                _position++;
                if (_position >= data.Length)
                    return "unexpected EOF";
                if (data[_position] < '0' || data[_position] > '9')
                    return Invalid(data[_position], "in numeric literal");
            }
            if (data[_position] == '0')
                _position++;
            else
            {
                while (_position < data.Length && data[_position] >= '0' && data[_position] <= '9')
                    _position++;
            }
            if (_position < data.Length && data[_position] == '.')
            {
                _position++;
                if (_position >= data.Length)
                    return "unexpected EOF";
                if (data[_position] < '0' || data[_position] > '9')
                    return Invalid(data[_position], "after decimal point in numeric literal");
                while (_position < data.Length && data[_position] >= '0' && data[_position] <= '9')
                    _position++;
            }
            if (_position < data.Length && data[_position] is (byte)'e' or (byte)'E')
            {
                _position++;
                if (_position < data.Length && data[_position] is (byte)'+' or (byte)'-')
                    _position++;
                if (_position >= data.Length)
                    return "unexpected EOF";
                if (data[_position] < '0' || data[_position] > '9')
                    return Invalid(data[_position], "in exponent of numeric literal");
                while (_position < data.Length && data[_position] >= '0' && data[_position] <= '9')
                    _position++;
            }
            var text = Encoding.ASCII.GetString(data, start, _position - start);
            node = new JsonNode { Kind = JsonKind.Number, Source = data, Start = start, End = _position, Text = text };
            return null;
        }

        private string? ParseString(out string? value)
        {
            value = null;
            _position++;
            var builder = new StringBuilder();
            var runStart = _position;
            while (true)
            {
                if (_position >= data.Length)
                    return "unexpected EOF";
                var c = data[_position];
                if (c == '"')
                {
                    builder.Append(Encoding.UTF8.GetString(data, runStart, _position - runStart));
                    _position++;
                    break;
                }
                if (c < 0x20)
                    return Invalid(c, "in string literal");
                if (c != '\\')
                {
                    _position++;
                    continue;
                }
                builder.Append(Encoding.UTF8.GetString(data, runStart, _position - runStart));
                _position++;
                if (_position >= data.Length)
                    return "unexpected EOF";
                var escape = data[_position];
                switch (escape)
                {
                    case (byte)'"': builder.Append('"'); break;
                    case (byte)'\\': builder.Append('\\'); break;
                    case (byte)'/': builder.Append('/'); break;
                    case (byte)'b': builder.Append('\b'); break;
                    case (byte)'f': builder.Append('\f'); break;
                    case (byte)'n': builder.Append('\n'); break;
                    case (byte)'r': builder.Append('\r'); break;
                    case (byte)'t': builder.Append('\t'); break;
                    case (byte)'u':
                    {
                        var code = 0;
                        for (var index = 1; index <= 4; index++)
                        {
                            if (_position + index >= data.Length)
                                return "unexpected EOF";
                            var h = data[_position + index];
                            int digit;
                            if (h >= '0' && h <= '9') digit = h - '0';
                            else if (h >= 'a' && h <= 'f') digit = h - 'a' + 10;
                            else if (h >= 'A' && h <= 'F') digit = h - 'A' + 10;
                            else return Invalid(h, "in \\u hexadecimal character escape");
                            code = code * 16 + digit;
                        }
                        _position += 4;
                        builder.Append((char)code);
                        break;
                    }
                    default:
                        return Invalid(escape, "in string escape code");
                }
                _position++;
                runStart = _position;
            }
            value = ReplaceLoneSurrogates(builder.ToString());
            return null;
        }

        // Go decodes an unpaired \u surrogate escape to U+FFFD.
        private static string ReplaceLoneSurrogates(string value)
        {
            StringBuilder? builder = null;
            for (var index = 0; index < value.Length; index++)
            {
                var c = value[index];
                if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder?.Append(c).Append(value[index + 1]);
                    index++;
                    continue;
                }
                if (char.IsSurrogate(c))
                {
                    builder ??= new StringBuilder(value[..index]);
                    builder.Append('\uFFFD');
                    continue;
                }
                builder?.Append(c);
            }
            return builder?.ToString() ?? value;
        }
    }
}
