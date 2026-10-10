using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

internal static class CanvasSchemaSubsetValidator
{
    internal const int MaximumSchemaBytes = 32 * 1024;
    internal const int MaximumInputBytes = 64 * 1024;
    internal const int MaximumDepth = 32;
    private const int MaximumSignificantDigits = 28;
    private const int MaximumFractionalScale = 28;
    private const int MaximumExponentMagnitude = 64;

    private static readonly BigInteger MaximumAbsoluteNumber = new(1_000_000_000_000L);

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "integer", "number", "boolean", "null"
    };

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema",
        "title",
        "description",
        "type",
        "properties",
        "required",
        "additionalProperties",
        "items",
        "minProperties",
        "maxProperties",
        "minItems",
        "maxItems",
        "uniqueItems",
        "minLength",
        "maxLength",
        "enum",
        "const",
        "minimum",
        "maximum",
        "exclusiveMinimum",
        "exclusiveMaximum"
    };

    internal static ImmutableArray<CanvasContractValidationIssue> ValidateSchema(
        CanvasJsonSchema? schema,
        string path)
    {
        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        if (schema is null)
        {
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument, path,
                "A schema document is required.");
            return issues.ToImmutable();
        }
        if (!string.Equals(
                schema.Dialect,
                CanvasSchemaDialects.Draft202012SubsetV1,
                StringComparison.Ordinal))
        {
            Add(issues, CanvasContractValidationCode.SchemaDialectMismatch, path + ".dialect",
                $"Schema dialect must be '{CanvasSchemaDialects.Draft202012SubsetV1}'.");
            return issues.ToImmutable();
        }
        if (schema.Definition.ValueKind == JsonValueKind.Undefined)
        {
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument, path,
                "Schema definition must be a JSON boolean or object.");
            return issues.ToImmutable();
        }
        if (Encoding.UTF8.GetByteCount(schema.Definition.GetRawText()) > MaximumSchemaBytes)
        {
            Add(issues, CanvasContractValidationCode.SchemaTooLarge, path,
                $"Schema documents cannot exceed {MaximumSchemaBytes} UTF-8 bytes.");
            return issues.ToImmutable();
        }
        if (!ValidateJsonTree(schema.Definition, path, 0, issues))
            return issues.ToImmutable();

        ValidateSchemaNode(schema.Definition, path, 0, issues);
        return issues.ToImmutable();
    }

    internal static CanvasContractValidationIssue? ValidateInstance(
        CanvasJsonSchema schema,
        JsonElement instance,
        string path)
    {
        if (Encoding.UTF8.GetByteCount(instance.GetRawText()) > MaximumInputBytes)
            return new CanvasContractValidationIssue(
                CanvasContractValidationCode.InputTooLarge,
                path,
                $"Canvas JSON input cannot exceed {MaximumInputBytes} UTF-8 bytes.");

        var issues = ImmutableArray.CreateBuilder<CanvasContractValidationIssue>();
        if (!ValidateJsonTree(instance, path, 0, issues))
            return issues[0];

        if (!Matches(instance, schema.Definition, path))
            return new CanvasContractValidationIssue(
                CanvasContractValidationCode.SchemaMismatch,
                path,
                "JSON input does not satisfy the declared Canvas schema.");
        return null;
    }

    private static void ValidateSchemaNode(
        JsonElement schema,
        string path,
        int depth,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (depth > MaximumDepth)
        {
            Add(issues, CanvasContractValidationCode.SchemaDepthExceeded, path,
                $"Schema nesting cannot exceed {MaximumDepth} levels.");
            return;
        }
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument, path,
                "A schema must be a boolean or an object.");
            return;
        }

        var properties = schema.EnumerateObject().ToArray();
        foreach (var property in properties)
        {
            if (property.Name == "pattern")
            {
                Add(issues, CanvasContractValidationCode.PatternUnsupported, path + ".pattern",
                    "Pattern validation is not supported by this Canvas schema profile.");
            }
            else if (!SupportedKeywords.Contains(property.Name))
            {
                Add(issues, CanvasContractValidationCode.UnsupportedSchemaKeyword,
                    path + "." + property.Name,
                    $"Schema keyword '{property.Name}' is not supported by this Canvas schema profile.");
            }
        }

        ValidateAnnotation(schema, "$schema", path, issues, CanvasSchemaDialects.Draft202012SubsetV1);
        ValidateAnnotation(schema, "title", path, issues);
        ValidateAnnotation(schema, "description", path, issues);
        ValidateType(schema, path, issues);

        if (schema.TryGetProperty("properties", out var declaredProperties))
        {
            if (declaredProperties.ValueKind != JsonValueKind.Object)
            {
                Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                    path + ".properties", "Properties must be an object of schemas.");
            }
            else
            {
                foreach (var property in declaredProperties.EnumerateObject())
                    ValidateSchemaNode(property.Value, path + ".properties." + property.Name,
                        depth + 1, issues);
            }
        }

        if (schema.TryGetProperty("required", out var required) &&
            !ValidateUniqueStringArray(required, path + ".required", issues))
        {
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                path + ".required", "Required must be an array of unique strings.");
        }

        if (schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
            additionalProperties.ValueKind != JsonValueKind.False)
            Add(issues, CanvasContractValidationCode.UnsupportedSchemaKeyword,
                path + ".additionalProperties",
                "Only additionalProperties:false is supported by this Canvas schema profile.");

        if (schema.TryGetProperty("items", out var items))
            ValidateSchemaNode(items, path + ".items", depth + 1, issues);

        ValidateNonNegativeIntegerKeyword(schema, "minProperties", path, issues);
        ValidateNonNegativeIntegerKeyword(schema, "maxProperties", path, issues);
        ValidateNonNegativeIntegerKeyword(schema, "minItems", path, issues);
        ValidateNonNegativeIntegerKeyword(schema, "maxItems", path, issues);
        ValidateNonNegativeIntegerKeyword(schema, "minLength", path, issues);
        ValidateNonNegativeIntegerKeyword(schema, "maxLength", path, issues);

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
            uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                path + ".uniqueItems", "uniqueItems must be a boolean.");

        if (schema.TryGetProperty("enum", out var enumeration))
        {
            if (enumeration.ValueKind != JsonValueKind.Array ||
                enumeration.GetArrayLength() == 0)
            {
                Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                    path + ".enum", "enum must be a non-empty array.");
            }
            else
            {
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in enumeration.EnumerateArray())
                {
                    var canonical = Canonicalize(value);
                    if (canonical is not null && !values.Add(canonical))
                        Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                            path + ".enum", "enum values must be unique by JSON value.");
                }
            }
        }

        ValidateNumericKeyword(schema, "minimum", path, issues);
        ValidateNumericKeyword(schema, "maximum", path, issues);
        ValidateNumericKeyword(schema, "exclusiveMinimum", path, issues);
        ValidateNumericKeyword(schema, "exclusiveMaximum", path, issues);
    }

    private static void ValidateAnnotation(
        JsonElement schema,
        string name,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues,
        string? expected = null)
    {
        if (!schema.TryGetProperty(name, out var annotation))
            return;
        var valid = annotation.ValueKind == JsonValueKind.String &&
            (expected is null || string.Equals(annotation.GetString(), expected, StringComparison.Ordinal));
        if (!valid)
            Add(issues, name == "$schema"
                    ? CanvasContractValidationCode.SchemaDialectMismatch
                    : CanvasContractValidationCode.InvalidSchemaDocument,
                path + "." + name,
                name == "$schema"
                    ? "The $schema annotation must identify this exact local schema profile."
                    : $"{name} annotation must be a string.");
    }

    private static void ValidateType(
        JsonElement schema,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (!schema.TryGetProperty("type", out var type))
            return;
        var types = new List<string>();
        if (type.ValueKind == JsonValueKind.String)
            types.Add(type.GetString()!);
        else if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in type.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                        path + ".type", "type arrays must contain only supported type names.");
                    return;
                }
                types.Add(item.GetString()!);
            }
            if (types.Count == 0)
            {
                Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                    path + ".type", "type arrays cannot be empty.");
                return;
            }
        }
        else
        {
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                path + ".type", "type must be a supported name or a non-empty array of names.");
            return;
        }

        if (types.Any(typeName => !SupportedTypes.Contains(typeName)) ||
            types.Distinct(StringComparer.Ordinal).Count() != types.Count)
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                path + ".type", "type contains an unsupported or duplicate name.");
    }

    private static bool ValidateUniqueStringArray(
        JsonElement value,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return false;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !values.Add(item.GetString()!))
                return false;
        }
        return true;
    }

    private static void ValidateNonNegativeIntegerKeyword(
        JsonElement schema,
        string name,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (!schema.TryGetProperty(name, out var value))
            return;
        if (value.ValueKind != JsonValueKind.Number ||
            !ExactJsonNumber.TryParse(value, out var number) ||
            !number.TryGetInt64(out var integer) ||
            integer < 0)
            Add(issues, CanvasContractValidationCode.InvalidSchemaDocument,
                path + "." + name,
                $"{name} must be a non-negative integer in the supported numeric range.");
    }

    private static void ValidateNumericKeyword(
        JsonElement schema,
        string name,
        string path,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (!schema.TryGetProperty(name, out var value))
            return;
        if (value.ValueKind != JsonValueKind.Number ||
            !ExactJsonNumber.TryParse(value, out _))
            Add(issues, CanvasContractValidationCode.UnsupportedJsonNumber,
                path + "." + name,
                $"{name} must be an exact number within the supported precision and range.");
    }

    private static bool ValidateJsonTree(
        JsonElement value,
        string path,
        int depth,
        ImmutableArray<CanvasContractValidationIssue>.Builder issues)
    {
        if (depth > MaximumDepth)
        {
            Add(issues,
                path.StartsWith("request.", StringComparison.Ordinal)
                    ? CanvasContractValidationCode.InputDepthExceeded
                    : CanvasContractValidationCode.SchemaDepthExceeded,
                path,
                $"JSON nesting cannot exceed {MaximumDepth} levels.");
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        Add(issues,
                            path.StartsWith("request.", StringComparison.Ordinal)
                                ? CanvasContractValidationCode.InvalidActionInput
                                : CanvasContractValidationCode.InvalidSchemaDocument,
                            path + "." + property.Name,
                            "JSON object property names must be unique.");
                        return false;
                    }
                    if (!ValidateJsonTree(property.Value, path + "." + property.Name,
                            depth + 1, issues))
                        return false;
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!ValidateJsonTree(item, $"{path}[{index}]", depth + 1, issues))
                        return false;
                    index++;
                }
                return true;
            }
            case JsonValueKind.Number:
                if (ExactJsonNumber.TryParse(value, out _))
                    return true;
                Add(issues, CanvasContractValidationCode.UnsupportedJsonNumber, path,
                    "JSON number exceeds the supported precision or absolute-value limit.");
                return false;
            case JsonValueKind.Undefined:
                Add(issues, CanvasContractValidationCode.InvalidSchemaDocument, path,
                    "Undefined is not a JSON value.");
                return false;
            default:
                return true;
        }
    }

    private static bool Matches(JsonElement value, JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.True)
            return true;
        if (schema.ValueKind == JsonValueKind.False)
            return false;
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        if (schema.TryGetProperty("type", out var type) && !MatchesType(value, type))
            return false;
        if (schema.TryGetProperty("const", out var constant) &&
            !JsonValueEquals(value, constant))
            return false;
        if (schema.TryGetProperty("enum", out var enumeration))
        {
            var canonicalValue = Canonicalize(value);
            if (canonicalValue is null ||
                !enumeration.EnumerateArray().Any(candidate =>
                    string.Equals(canonicalValue, Canonicalize(candidate), StringComparison.Ordinal)))
                return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().ToArray();
                if (!WithinIntegerRange(schema, "minProperties", properties.Length, lower: true) ||
                    !WithinIntegerRange(schema, "maxProperties", properties.Length, lower: false))
                    return false;

                if (schema.TryGetProperty("required", out var required) &&
                    required.EnumerateArray().Any(name =>
                        !value.TryGetProperty(name.GetString()!, out _)))
                    return false;

                schema.TryGetProperty("properties", out var declaredProperties);
                foreach (var property in properties)
                {
                    if (declaredProperties.ValueKind == JsonValueKind.Object &&
                        declaredProperties.TryGetProperty(property.Name, out var propertySchema))
                    {
                        if (!Matches(property.Value, propertySchema, path + "." + property.Name))
                            return false;
                    }
                    else if (schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
                             additionalProperties.ValueKind == JsonValueKind.False)
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToArray();
                if (!WithinIntegerRange(schema, "minItems", items.Length, lower: true) ||
                    !WithinIntegerRange(schema, "maxItems", items.Length, lower: false))
                    return false;
                if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
                    uniqueItems.ValueKind == JsonValueKind.True)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    if (items.Any(item => !seen.Add(Canonicalize(item)!)))
                        return false;
                }
                if (schema.TryGetProperty("items", out var itemSchema) &&
                    items.Any(item => !Matches(item, itemSchema, path + "[]")))
                    return false;
                return true;
            }
            case JsonValueKind.String:
            {
                var length = value.GetString()!.EnumerateRunes().Count();
                return WithinIntegerRange(schema, "minLength", length, lower: true) &&
                    WithinIntegerRange(schema, "maxLength", length, lower: false);
            }
            case JsonValueKind.Number:
            {
                if (!ExactJsonNumber.TryParse(value, out var number))
                    return false;
                if (schema.TryGetProperty("minimum", out var minimum) &&
                    !Compare(number, minimum, static (actual, bound) => actual >= bound) ||
                    schema.TryGetProperty("maximum", out var maximum) &&
                    !Compare(number, maximum, static (actual, bound) => actual <= bound) ||
                    schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) &&
                    !Compare(number, exclusiveMinimum, static (actual, bound) => actual > bound) ||
                    schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) &&
                    !Compare(number, exclusiveMaximum, static (actual, bound) => actual < bound))
                    return false;
                return true;
            }
            default:
                return true;
        }
    }

    private static bool MatchesType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(candidate => MatchesType(value, candidate));
        return type.ValueKind == JsonValueKind.String &&
            type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number &&
                    ExactJsonNumber.TryParse(value, out var number) && number.IsInteger,
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };
    }

    private static bool WithinIntegerRange(
        JsonElement schema,
        string name,
        int value,
        bool lower)
    {
        if (!schema.TryGetProperty(name, out var bound) ||
            !ExactJsonNumber.TryParse(bound, out var number) ||
            !number.TryGetInt64(out var integer))
            return !schema.TryGetProperty(name, out _);
        return lower ? value >= integer : value <= integer;
    }

    private static bool Compare(
        ExactJsonNumber actual,
        JsonElement bound,
        Func<ExactJsonNumber, ExactJsonNumber, bool> comparison) =>
        ExactJsonNumber.TryParse(bound, out var exactBound) &&
        comparison(actual, exactBound);

    private static bool JsonValueEquals(JsonElement left, JsonElement right) =>
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    private static string? Canonicalize(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal);
                var builder = new StringBuilder("{");
                var first = true;
                foreach (var property in properties)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name))
                        .Append(':')
                        .Append(Canonicalize(property.Value));
                }
                return builder.Append('}').ToString();
            }
            case JsonValueKind.Array:
            {
                var builder = new StringBuilder("[");
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(Canonicalize(item));
                }
                return builder.Append(']').ToString();
            }
            case JsonValueKind.String:
                return JsonSerializer.Serialize(value.GetString());
            case JsonValueKind.Number:
                return ExactJsonNumber.TryParse(value, out var number)
                    ? "number:" + number.ToCanonicalString()
                    : null;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Null:
                return "null";
            default:
                return null;
        }
    }

    private static void Add(
        ImmutableArray<CanvasContractValidationIssue>.Builder issues,
        CanvasContractValidationCode code,
        string path,
        string message) =>
        issues.Add(new CanvasContractValidationIssue(code, path, message));

    private readonly record struct ExactJsonNumber(BigInteger Coefficient, int Scale)
    {
        public bool IsInteger => Scale == 0;

        public static bool TryParse(JsonElement value, out ExactJsonNumber result)
        {
            result = default;
            return value.ValueKind == JsonValueKind.Number &&
                TryParse(value.GetRawText(), out result);
        }

        public static bool TryParse(string text, out ExactJsonNumber result)
        {
            result = default;
            var exponentIndex = text.IndexOfAny(['e', 'E']);
            var mantissaEnd = exponentIndex < 0 ? text.Length : exponentIndex;
            var exponent = 0;
            if (exponentIndex >= 0 &&
                (!int.TryParse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out exponent) ||
                 exponent is < -MaximumExponentMagnitude or > MaximumExponentMagnitude))
                return false;

            var mantissa = text.AsSpan(0, mantissaEnd);
            var negative = mantissa.Length > 0 && mantissa[0] == '-';
            if (negative)
                mantissa = mantissa[1..];
            var decimalIndex = mantissa.IndexOf('.');
            var fractionLength = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
            var digits = new char[mantissa.Length];
            var digitCount = 0;
            foreach (var character in mantissa)
            {
                if (character == '.')
                    continue;
                if (character is < '0' or > '9')
                    return false;
                digits[digitCount++] = character;
            }

            var significantStart = 0;
            while (significantStart < digitCount && digits[significantStart] == '0')
                significantStart++;
            if (significantStart == digitCount)
            {
                result = new ExactJsonNumber(BigInteger.Zero, 0);
                return true;
            }

            var significantEnd = digitCount;
            while (significantEnd > significantStart && digits[significantEnd - 1] == '0')
                significantEnd--;
            if (significantEnd - significantStart > MaximumSignificantDigits)
                return false;
            var coefficient = BigInteger.Parse(
                digits.AsSpan(significantStart, significantEnd - significantStart),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            var scale = fractionLength - exponent - (digitCount - significantEnd);
            if (scale < 0)
            {
                coefficient *= BigInteger.Pow(10, -scale);
                scale = 0;
            }

            if (scale > MaximumFractionalScale ||
                BigInteger.Abs(coefficient) > MaximumAbsoluteNumber * BigInteger.Pow(10, scale))
                return false;
            result = new ExactJsonNumber(negative ? -coefficient : coefficient, scale);
            return true;
        }

        public bool TryGetInt64(out long value)
        {
            value = default;
            return Scale == 0 && Coefficient >= long.MinValue &&
                Coefficient <= long.MaxValue &&
                (value = (long)Coefficient) == Coefficient;
        }

        public string ToCanonicalString() =>
            Coefficient.ToString(CultureInfo.InvariantCulture) + "e-" +
            Scale.ToString(CultureInfo.InvariantCulture);

        public static int CompareTo(ExactJsonNumber left, ExactJsonNumber right)
        {
            if (left.Scale == right.Scale)
                return left.Coefficient.CompareTo(right.Coefficient);
            var commonScale = Math.Max(left.Scale, right.Scale);
            var leftValue = left.Coefficient * BigInteger.Pow(10, commonScale - left.Scale);
            var rightValue = right.Coefficient * BigInteger.Pow(10, commonScale - right.Scale);
            return leftValue.CompareTo(rightValue);
        }

        public static bool operator >=(ExactJsonNumber left, ExactJsonNumber right) =>
            CompareTo(left, right) >= 0;

        public static bool operator <=(ExactJsonNumber left, ExactJsonNumber right) =>
            CompareTo(left, right) <= 0;

        public static bool operator >(ExactJsonNumber left, ExactJsonNumber right) =>
            CompareTo(left, right) > 0;

        public static bool operator <(ExactJsonNumber left, ExactJsonNumber right) =>
            CompareTo(left, right) < 0;
    }
}
