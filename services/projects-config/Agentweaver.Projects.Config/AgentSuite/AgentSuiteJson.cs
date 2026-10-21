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
        // Non-objects have no members, so every lookup misses.
        if (Members is null)
            return null;
        // Linear scan keeps source order; keys are already unique because the parser rejects duplicates.
        foreach (var member in Members)
        {
            // Exact, case-sensitive match. Go's case-insensitive field matching is rejected elsewhere.
            if (string.Equals(member.Key, key, StringComparison.Ordinal))
                return member.Value;
        }
        return null;
    }

    public bool Has(string key) => Get(key) is not null;

    /// <summary>The <c>UnmarshalTypeError.Value</c> name Go uses for this JSON kind.</summary>
    public string GoValueName => Kind switch
    {
        // Go reports both true and false as "bool" in its type error messages.
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
    // Limits copied from the upstream decoder: 4 MiB per document, 100 levels of nesting,
    // 1024 members per object and 100,000 members across the whole document.
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
        // The checks run in the same order as upstream so the first reported error matches Go exactly.
        // Cheap size and encoding checks come first, before any parsing work.
        if (data.Length == 0)
            return "document is empty";
        if (data.Length > MaxBytes)
            return $"document exceeds {MaxBytes} bytes";
        if (!Utf8IsValid(data))
            return "document is not valid UTF-8";
        // Reject \u escapes that encode broken UTF-16 surrogate pairs; Go would silently turn them into U+FFFD.
        if (ValidateUnicodeEscapes(data) is { } escapeError)
            return escapeError;
        // Tokenize and build the tree while enforcing syntax, duplicate-key and member-count limits.
        if (Scan(data, MaxObjectMembers, MaxDocumentMembers, out node) is { } scanError)
            return scanError;
        // Reject numbers whose value would change when the document is rewritten as canonical JSON (JCS).
        if (ValidateCanonicalEquivalence(node!) is { } jcsError)
            return jcsError;
        // Finally check field names and value types against the target Go type.
        return StrictDecode(node!, target);
    }

    /// <summary>Exact field checks and typed decoding on an already scanned value (the nested-Unmarshal path).</summary>
    public static string? StrictDecode(JsonNode node, GoType target)
    {
        // Upstream first walks the value to reject unknown fields and disallowed nulls,
        // then runs the real encoding/json decode, which reports type mismatches.
        if (ValidateJsonType(node, target) is { } fieldError)
            return fieldError;
        return GoDecoder.Decode(node, target, strict: true);
    }

    public static bool Utf8IsValid(ReadOnlySpan<byte> data) => System.Text.Unicode.Utf8.IsValid(data);

    // Size and digest are checked first because they are cheap; the JCS round trip is the expensive step.
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
            // Input that cannot be canonicalized at all (bad JSON, too deep) is treated as not canonical.
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
            // Callers treat null as "cannot canonicalize" instead of handling an exception.
            return null;
        }
    }

    /// <summary>Upstream <c>validateUnicodeEscapes</c>, a byte-level scan that runs before tokenizing.</summary>
    public static string? ValidateUnicodeEscapes(byte[] data)
    {
        // This is a simple byte scan, not a full parser. It only tracks whether we are inside a string.
        // Escaped quotes are skipped below, so an unescaped '"' always toggles the state.
        var inString = false;
        for (var i = 0; i < data.Length; i++)
        {
            switch (data[i])
            {
                case (byte)'"':
                    inString = !inString;
                    break;
                case (byte)'\\':
                    // Backslashes outside strings are syntax errors that the tokenizer reports later.
                    if (!inString || i + 1 >= data.Length)
                        continue;
                    // Any other escape (\n, \", \\ ...) is two bytes long; skip the escaped byte.
                    if (data[i + 1] != 'u')
                    {
                        i++;
                        continue;
                    }
                    // Malformed hex digits are also left for the tokenizer to report.
                    if (!DecodeHexEscape(data, i + 2, out var value))
                        continue;
                    // A high surrogate (D800-DBFF) must be followed immediately by a \u escape for a low surrogate.
                    if (value >= 0xd800 && value <= 0xdbff)
                    {
                        // Need 12 bytes in total: "\uXXXX\uYYYY".
                        if (i + 11 >= data.Length || data[i + 6] != '\\' || data[i + 7] != 'u')
                            return "JSON string contains an unpaired high surrogate";
                        if (!DecodeHexEscape(data, i + 8, out var low) || low < 0xdc00 || low > 0xdfff)
                            return "JSON string contains an unpaired high surrogate";
                        // Skip past both escapes of the valid pair.
                        i += 11;
                    }
                    // A low surrogate (DC00-DFFF) on its own, without a preceding high surrogate, is invalid.
                    else if (value >= 0xdc00 && value <= 0xdfff)
                        return "JSON string contains an unpaired low surrogate";
                    else
                        // Normal \uXXXX escape: skip the 'u' and four hex digits.
                        i += 5;
                    break;
            }
        }
        return null;
    }

    private static bool DecodeHexEscape(byte[] data, int start, out int value)
    {
        value = 0;
        // Not enough bytes left for four hex digits.
        if (start + 4 > data.Length)
            return false;
        // Read exactly four hex digits (upper or lower case) into a 16-bit code unit.
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
        // Uses the default depth limit (MaxDepth); only the member limits vary by caller.
        var parser = new Parser(data, objectLimit, documentLimit);
        return parser.ParseDocument(out root);
    }

    /// <summary>Upstream <c>validateCanonicalEquivalence</c>: only numbers can change value under JCS.</summary>
    private static string? ValidateCanonicalEquivalence(JsonNode node)
    {
        // Strings, booleans and nulls keep their meaning under JCS; only number formatting can change a value.
        // Walk the whole tree and stop at the first number that fails.
        string? failure = null;
        Visit(node);
        return failure;

        void Visit(JsonNode current)
        {
            // Keep only the first failure, like Go returning on the first error.
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
                    // JCS stores every number as an IEEE 754 double. A literal that overflows to infinity
                    // (for example 1e400) cannot be canonicalized; the message mirrors Go's strconv error.
                    if (!double.TryParse(current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
                    {
                        failure = $"document is not valid JCS JSON: strconv.ParseFloat: parsing {Go.Quote(current.Text!)}: value out of range";
                        return;
                    }
                    // The text JCS would write for this number.
                    var transformed = CanonicalJson.FormatNumber(parsed);
                    // Only integer literals are compared. An integer above 2^53 cannot be stored exactly in a
                    // double, so its canonical form is a different integer and the document is rejected as lossy.
                    // Non-integer literals (1.50, 1e2) may be reformatted but keep their value, so they pass.
                    if (long.TryParse(current.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    {
                        // Canonical output in exponent form (e.g. 1e+21) does not parse as long and also counts as changed.
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
        // Pointers only change whether null is allowed; remember that and validate the pointed-to type.
        var wasPointer = false;
        while (target is GoPointer pointer)
        {
            wasPointer = true;
            target = pointer.Elem;
        }
        // json.RawMessage and interface{} accept any JSON value, so there is nothing to check inside them.
        if (target is GoRawMessage or GoInterface)
            return null;
        // Upstream only allows null where Go has a nil value: pointers, maps and slices.
        // A null for a plain string, number, bool or struct field is an error.
        if (node.IsNull)
            return wasPointer || target is GoMap or GoSlice ? null : "null is not allowed";
        switch (target)
        {
            case GoStruct structType:
                // Upstream decodes into map[string]json.RawMessage first, so the wrong-kind error names that type.
                if (node.Kind != JsonKind.Object)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
                foreach (var (name, value) in node.Members!)
                {
                    // Field names match exactly. A name that differs only by case is an unknown field,
                    // unlike plain Go encoding/json which would match it case-insensitively.
                    var field = structType.Field(name);
                    if (field is null)
                        return $"unknown field {Go.Quote(name)}";
                    // Recurse and prefix the error with the path so the message points to the bad field.
                    if (ValidateJsonType(value, field) is { } error)
                        return $"field {Go.Quote(name)}: {error}";
                }
                break;
            case GoSlice slice:
                if (node.Kind != JsonKind.Array)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type []json.RawMessage";
                // Check each element against the element type; report the failing index.
                for (var index = 0; index < node.Items!.Count; index++)
                {
                    if (ValidateJsonType(node.Items[index], slice.Elem) is { } error)
                        return $"element {index}: {error}";
                }
                break;
            case GoMap map:
                if (node.Kind != JsonKind.Object)
                    return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
                // Map keys are free-form, so only the values are checked.
                foreach (var (name, value) in node.Members!)
                {
                    if (ValidateJsonType(value, map.Elem) is { } error)
                        return $"map value {Go.Quote(name)}: {error}";
                }
                break;
        }
        // Primitive kinds (string, bool, numbers) are type-checked later by GoDecoder.
        return null;
    }

    /// <summary>Go <c>json.Unmarshal(raw, &amp;map[string]json.RawMessage)</c> for an already parsed node.</summary>
    public static string? AsFieldMap(JsonNode node, out JsonNode? fields)
    {
        fields = null;
        // Go leaves the map nil for a JSON null and reports no error; callers must handle a null result.
        if (node.IsNull)
            return null;
        if (node.Kind != JsonKind.Object)
            return $"json: cannot unmarshal {node.GoValueName} into Go value of type map[string]json.RawMessage";
        // The object node itself serves as the field map; members keep their raw source bytes.
        fields = node;
        return null;
    }

    /// <summary>Parses any JSON text with Go syntax rules and no limits beyond Go's own (used for non-strict reads).</summary>
    public static string? ParseLoose(byte[] data, out JsonNode? root)
    {
        // Go's scanner rejects invalid UTF-8 inside strings with this message.
        if (!Utf8IsValid(data))
        {
            root = null;
            return "invalid character in string literal";
        }
        // No member or depth limits are applied here, matching a plain json.Unmarshal call upstream.
        // Note: duplicate keys are still rejected by the shared parser.
        return new Parser(data, int.MaxValue, int.MaxValue, depthLimit: int.MaxValue).ParseDocument(out root);
    }

    /// <summary>A Go <c>encoding/json</c> compatible scanner that reports errors in document order.</summary>
    private sealed class Parser(byte[] data, int objectLimit, int documentLimit, int depthLimit = MaxDepth)
    {
        // Current read offset into the byte buffer.
        private int _position;
        // Members counted across the whole document, checked against documentLimit.
        private int _documentMembers;

        public string? ParseDocument(out JsonNode? root)
        {
            root = null;
            SkipWhitespace();
            // An empty or whitespace-only document gives Go's io.EOF error text.
            if (_position >= data.Length)
                return "EOF";
            if (ParseValue(0, out root) is { } error)
                return error;
            SkipWhitespace();
            // Anything after the first value is an error. Go's token-based check parses the next value
            // first, so a malformed trailing value reports its own syntax error instead.
            if (_position < data.Length)
            {
                var start = _position;
                if (ParseValue(0, out var trailing) is { } trailingError)
                {
                    _position = start;
                    return trailingError;
                }
                // A well-formed trailing value is reported using its first token, as Go's Decoder.Token would.
                return $"unexpected trailing token {TokenText(trailing!)}";
            }
            return null;
        }

        // Text of the first token of a value, formatted the way Go prints a json.Token with %v.
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
            // JSON allows only these four whitespace bytes; other Unicode spaces are syntax errors.
            while (_position < data.Length && data[_position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                _position++;
        }

        // Builds Go's syntax error text, for example: invalid character 'x' looking for beginning of value.
        private static string Invalid(byte c, string context) => $"invalid character {QuoteChar(c)} {context}";

        // Formats a byte like Go's quoteChar: single quotes, with Go escapes for non-printable bytes.
        private static string QuoteChar(byte c)
        {
            // Go special-cases the two quote characters.
            if (c == '\'')
                return "'\\''";
            if (c == '"')
                return "'\"'";
            // Otherwise reuse strconv.Quote and swap the surrounding double quotes for single quotes.
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
            // The first byte decides the value kind, exactly as in Go's scanner state machine.
            switch (c)
            {
                case (byte)'{':
                {
                    // The depth check happens when a container opens, so scalars do not count toward depth.
                    if (depth >= depthLimit)
                        return DepthLimitError;
                    _position++;
                    var members = new List<KeyValuePair<string, JsonNode>>();
                    // Tracks keys already used in this object; matching is exact (case-sensitive).
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    SkipWhitespace();
                    // Empty object "{}".
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
                        // Limits are checked before reading the next key, so the limit-th member itself is allowed.
                        if (seen.Count >= objectLimit || _documentMembers >= documentLimit)
                            return MemberLimitError;
                        // Keys must be strings; this also rejects a trailing comma such as {"a":1,}.
                        if (data[_position] != '"')
                            return Invalid(data[_position], "looking for beginning of object key string");
                        if (ParseString(out var key) is { } keyError)
                            return keyError;
                        // Go's encoding/json silently keeps the last duplicate; upstream rejects duplicates instead.
                        // Keys are compared after unescaping, so "a" and "\u0061" count as the same key.
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
                        // Members are stored in source order, which later error messages depend on.
                        members.Add(new(key!, value!));
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        // After a member we need either ',' (another member) or '}' (end of object).
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
                    // Empty array "[]".
                    if (_position < data.Length && data[_position] == ']')
                    {
                        _position++;
                        node = new JsonNode { Kind = JsonKind.Array, Source = data, Start = start, End = _position, Items = items };
                        return null;
                    }
                    // Arrays have no element limit here; upstream does not limit root or nested arrays.
                    while (true)
                    {
                        // A trailing comma such as [1,] fails here because ']' cannot start a value.
                        if (ParseValue(depth + 1, out var item) is { } itemError)
                            return itemError;
                        items.Add(item!);
                        SkipWhitespace();
                        if (_position >= data.Length)
                            return "unexpected EOF";
                        // After an element we need either ',' (another element) or ']' (end of array).
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
                    // Numbers start with '-' or a digit. A leading '+' or '.' is not valid JSON.
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(out node);
                    return Invalid(c, "looking for beginning of value");
            }
        }

        private string? ParseLiteral(string literal, JsonKind kind, out JsonNode? node)
        {
            node = null;
            var start = _position;
            // Compare byte by byte so the error points at the first wrong character, as Go does (e.g. "tru!").
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
            // Grammar: [-] int [. digits] [(e|E) [+|-] digits]. Each part is checked separately so
            // the error text matches Go's scanner state.
            if (data[_position] == '-')
            {
                _position++;
                if (_position >= data.Length)
                    return "unexpected EOF";
                if (data[_position] < '0' || data[_position] > '9')
                    return Invalid(data[_position], "in numeric literal");
            }
            // A leading zero stands alone. In "01" the scan stops after "0", and the "1" is then
            // reported by the caller as an unexpected character.
            if (data[_position] == '0')
                _position++;
            else
            {
                while (_position < data.Length && data[_position] >= '0' && data[_position] <= '9')
                    _position++;
            }
            // Optional fraction: at least one digit must follow the '.'.
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
            // Optional exponent with an optional sign; at least one digit is required.
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
            // Keep the literal text, not a parsed value, so later checks can apply exact Go
            // integer rules and detect values that a double cannot represent.
            var text = Encoding.ASCII.GetString(data, start, _position - start);
            node = new JsonNode { Kind = JsonKind.Number, Source = data, Start = start, End = _position, Text = text };
            return null;
        }

        private string? ParseString(out string? value)
        {
            value = null;
            // Skip the opening quote.
            _position++;
            var builder = new StringBuilder();
            // Start of the current run of plain (unescaped) bytes; runs are copied in bulk for speed.
            var runStart = _position;
            while (true)
            {
                if (_position >= data.Length)
                    return "unexpected EOF";
                var c = data[_position];
                // Closing quote: flush the last plain run and stop.
                if (c == '"')
                {
                    builder.Append(Encoding.UTF8.GetString(data, runStart, _position - runStart));
                    _position++;
                    break;
                }
                // Raw control characters (including newline and tab) must be escaped inside JSON strings.
                if (c < 0x20)
                    return Invalid(c, "in string literal");
                if (c != '\\')
                {
                    _position++;
                    continue;
                }
                // Backslash: flush the plain run collected so far, then decode the escape sequence.
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
                        // \uXXXX: read four hex digits. Each escape becomes one UTF-16 code unit;
                        // surrogate pairs from two escapes combine naturally in the .NET string.
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
                    // Any other escaped character (for example \x or \') is invalid JSON.
                    default:
                        return Invalid(escape, "in string escape code");
                }
                // Move past the escape and start a new plain run.
                _position++;
                runStart = _position;
            }
            // Fix up any surrogate that did not form a valid pair.
            value = ReplaceLoneSurrogates(builder.ToString());
            return null;
        }

        // Go decodes an unpaired \u surrogate escape to U+FFFD.
        private static string ReplaceLoneSurrogates(string value)
        {
            // Copy-on-write: the builder is only created when the first lone surrogate is found,
            // so strings without problems are returned unchanged without allocating.
            StringBuilder? builder = null;
            for (var index = 0; index < value.Length; index++)
            {
                var c = value[index];
                // A valid high+low pair is kept as is.
                if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder?.Append(c).Append(value[index + 1]);
                    index++;
                    continue;
                }
                // Any other surrogate is unpaired: copy everything before it, then write U+FFFD.
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
