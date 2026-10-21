namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>A Go type shape used to reproduce <c>encoding/json</c> and upstream exact-field decoding.</summary>
internal abstract class GoType
{
    /// <summary>The Go <c>reflect.Type.String()</c> form used in <c>UnmarshalTypeError</c>.</summary>
    public abstract string GoName { get; }
}

internal enum GoPrimKind
{
    String,
    Bool,
    Int,
    Int64,
    Uint32,
}

internal sealed class GoPrim(GoPrimKind kind) : GoType
{
    public GoPrimKind Kind { get; } = kind;

    public override string GoName => Name;

    public string Name => Kind switch
    {
        GoPrimKind.String => "string",
        GoPrimKind.Bool => "bool",
        GoPrimKind.Int => "int",
        GoPrimKind.Int64 => "int64",
        _ => "uint32",
    };
}

internal sealed class GoPointer(GoType elem) : GoType
{
    public GoType Elem { get; } = elem;

    public override string GoName => "*" + Elem.GoName;
}

internal sealed class GoSlice(GoType elem) : GoType
{
    public GoType Elem { get; } = elem;

    public override string GoName => "[]" + Elem.GoName;
}

/// <summary><c>map[string]T</c>.</summary>
internal sealed class GoMap(GoType elem) : GoType
{
    public GoType Elem { get; } = elem;

    public override string GoName => "map[string]" + Elem.GoName;
}

internal sealed class GoRawMessage : GoType
{
    public override string GoName => "json.RawMessage";
}

internal sealed class GoInterface : GoType
{
    public override string GoName => "interface {}";
}

/// <summary>A custom <c>UnmarshalJSON</c>. It returns a decode error or null.</summary>
internal delegate GoDecodeError? GoHook(JsonNode node);

internal sealed class GoStruct : GoType
{
    private readonly Dictionary<string, GoType> _fields = new(StringComparer.Ordinal);

    public GoStruct(string name, string package = "agentsuite")
    {
        Name = name;
        Package = package;
    }

    public string Package { get; }

    /// <summary>The Go short type name used in <c>UnmarshalTypeError.Struct</c>.</summary>
    public string Name { get; }

    public override string GoName => Package + "." + Name;

    public GoHook? Hook { get; set; }

    public GoType? Field(string jsonName) => _fields.GetValueOrDefault(jsonName);

    public GoStruct With(string jsonName, GoType type)
    {
        _fields[jsonName] = type;
        return this;
    }

    /// <summary>The hook-free copy (<c>type wire T</c>) a custom decoder decodes into.</summary>
    public GoStruct Wire(string name)
    {
        // Same fields, but no Hook. A custom decoder decodes into this copy so it does not call itself
        // again, just like the Go trick of declaring "type wire T" inside UnmarshalJSON.
        var wire = new GoStruct(name, Package);
        foreach (var (key, value) in _fields)
            wire._fields[key] = value;
        return wire;
    }
}

/// <summary>A Go decode error: either an <c>UnmarshalTypeError</c> (with context) or an opaque message.</summary>
internal sealed class GoDecodeError
{
    public string? Message { get; init; }
    public string? Value { get; init; }
    public string? TypeName { get; init; }
    public string? Struct { get; set; }
    public string Field { get; set; } = string.Empty;

    public bool IsTypeError => Message is null;

    public static GoDecodeError Of(string message) => new() { Message = message };

    public override string ToString()
    {
        // Opaque errors (for example "json: unknown field ...") print their message unchanged.
        if (Message is not null)
            return Message;
        // Type errors inside a struct name the struct and the dotted field path, as Go's UnmarshalTypeError does.
        if (!string.IsNullOrEmpty(Struct) || Field.Length > 0)
            return $"json: cannot unmarshal {Value} into Go struct field {Struct}.{Field} of type {TypeName}";
        // Type error at the top level, outside any struct.
        return $"json: cannot unmarshal {Value} into Go value of type {TypeName}";
    }
}

/// <summary>Ports Go <c>encoding/json</c> typed decoding over an already scanned tree.</summary>
internal static class GoDecoder
{
    // Where the decoder currently is: the innermost Go struct name and the JSON field path from the root.
    // Go uses these to build "Go struct field T.a.b" in type error messages.
    private sealed record Context(string? Struct, IReadOnlyList<string> Fields)
    {
        public bool HasContext => Struct is not null || Fields.Count > 0;
    }

    private static readonly Context Empty = new(null, []);

    public static string? Decode(JsonNode node, GoType type, bool strict)
    {
        // Strict mode is kept for call-site clarity only; unknown fields are always rejected here.
        _ = strict;
        return DecodeError(node, type)?.ToString();
    }

    /// <summary>Decodes and returns the structured error so callers (hooks) can propagate type-error context.</summary>
    public static GoDecodeError? DecodeError(JsonNode node, GoType type)
    {
        // Go's decoder keeps going after a type mismatch and remembers only the first one ("saved").
        // Other errors (unknown field, hook failure) stop decoding at once and take priority.
        GoDecodeError? saved = null;
        var error = Value(node, type, Empty, ref saved);
        return error ?? saved;
    }

    private static GoDecodeError? Value(JsonNode node, GoType type, Context context, ref GoDecodeError? saved)
    {
        // JSON null is accepted for every Go type and leaves the value unchanged, as in Go.
        if (node.IsNull)
            return null;
        // Go allocates pointers as needed, so decoding continues with the pointed-to type.
        while (type is GoPointer pointer)
            type = pointer.Elem;
        switch (type)
        {
            // These accept any JSON value without further checks.
            case GoRawMessage or GoInterface:
                return null;
            // A struct with a custom UnmarshalJSON: run the hook instead of the normal field decoding.
            case GoStruct { Hook: { } hook }:
            {
                var error = hook(node);
                // Go adds the outer struct name and field path to a type error coming from a nested decode,
                // so prefix our path to whatever path the hook reported.
                if (error is not null && error.IsTypeError && context.HasContext)
                {
                    error.Struct = context.Struct;
                    error.Field = error.Field.Length > 0
                        ? string.Join('.', context.Fields.Append(error.Field))
                        : string.Join('.', context.Fields);
                }
                return error;
            }
            case GoStruct structType:
                // Wrong JSON kind: record a type error and keep going, like Go does.
                if (node.Kind != JsonKind.Object)
                {
                    Save(ref saved, node.GoValueName, structType.GoName, context);
                    return null;
                }
                foreach (var (name, value) in node.Members!)
                {
                    // Mirrors Go's DisallowUnknownFields: an unknown key stops decoding immediately.
                    var fieldType = structType.Field(name);
                    if (fieldType is null)
                        return GoDecodeError.Of($"json: unknown field {Go.Quote(name)}");
                    // Entering a field: this struct becomes the innermost struct and the field name joins the path.
                    var inner = new Context(structType.Name, [.. context.Fields, name]);
                    if (Value(value, fieldType, inner, ref saved) is { } error)
                        return error;
                }
                return null;
            case GoSlice slice:
                if (node.Kind != JsonKind.Array)
                {
                    Save(ref saved, node.GoValueName, slice.GoName, context);
                    return null;
                }
                // Elements share the parent context; Go does not add array indexes to the field path.
                foreach (var item in node.Items!)
                {
                    if (Value(item, slice.Elem, context, ref saved) is { } error)
                        return error;
                }
                return null;
            case GoMap map:
                if (node.Kind != JsonKind.Object)
                {
                    Save(ref saved, node.GoValueName, map.GoName, context);
                    return null;
                }
                // Map values also keep the parent context; map keys are not added to the path.
                foreach (var member in node.Members!)
                {
                    if (Value(member.Value, map.Elem, context, ref saved) is { } error)
                        return error;
                }
                return null;
            case GoPrim prim:
                // Primitive mismatches are only recorded, never returned, so decoding continues.
                Primitive(node, prim, context, ref saved);
                return null;
        }
        return null;
    }

    private static void Primitive(JsonNode node, GoPrim prim, Context context, ref GoDecodeError? saved)
    {
        switch (prim.Kind)
        {
            case GoPrimKind.String:
                if (node.Kind != JsonKind.String)
                    Save(ref saved, node.GoValueName, prim.Name, context);
                return;
            case GoPrimKind.Bool:
                if (node.Kind is not (JsonKind.True or JsonKind.False))
                    Save(ref saved, node.GoValueName, prim.Name, context);
                return;
            // All integer kinds: the value must be a JSON number first.
            default:
                if (node.Kind != JsonKind.Number)
                {
                    Save(ref saved, node.GoValueName, prim.Name, context);
                    return;
                }
                // Then the literal must parse as an integer of the target size. Fractions, exponents
                // and out-of-range values fail, just as Go's strconv.ParseInt/ParseUint would.
                var ok = prim.Kind switch
                {
                    // Go's int is 64-bit on the platforms upstream supports, so int and int64 share a check.
                    GoPrimKind.Int or GoPrimKind.Int64 => TryParseGoInt(node.Text!, out _),
                    _ => TryParseGoUint32(node.Text!, out _),
                };
                // Go reports these as "number <literal>" rather than just "number".
                if (!ok)
                    Save(ref saved, "number " + node.Text, prim.Name, context);
                return;
        }
    }

    private static void Save(ref GoDecodeError? saved, string value, string typeName, Context context)
    {
        // Only the first type error is kept, matching Go's decoder.
        if (saved is not null)
            return;
        // At the top level (no struct or field yet) the error uses the shorter "Go value of type" form.
        saved = new GoDecodeError
        {
            Value = value,
            TypeName = typeName,
            Struct = context.HasContext ? context.Struct : null,
            Field = context.HasContext ? string.Join('.', context.Fields) : string.Empty,
        };
    }

    // .NET accepts a leading '+' but Go's ParseInt on JSON input never sees one, so it is rejected explicitly.
    /// <summary>Go <c>strconv.ParseInt(s, 10, 64)</c> on a JSON number literal.</summary>
    public static bool TryParseGoInt(string text, out long value) =>
        long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value) && !text.StartsWith('+');

    // NumberStyles.None allows digits only, so a minus sign, fraction or exponent fails like Go's ParseUint.
    /// <summary>Go <c>strconv.ParseUint(s, 10, 32)</c> on a JSON number literal.</summary>
    public static bool TryParseGoUint32(string text, out uint value) =>
        uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
}
