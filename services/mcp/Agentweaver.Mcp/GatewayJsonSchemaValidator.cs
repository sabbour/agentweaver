using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Mcp;

internal static class GatewayJsonSchemaValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    internal static bool IsValid(JsonElement value, JsonElement schema) =>
        IsValid(value, schema, 0);

    private static bool IsValid(JsonElement value, JsonElement schema, int depth)
    {
        if (depth > 64)
            return false;
        if (schema.ValueKind == JsonValueKind.True)
            return true;
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        if (schema.TryGetProperty("allOf", out var allOf) &&
            (!IsSchemaArray(allOf) || !allOf.EnumerateArray().All(branch => IsValid(value, branch, depth + 1))))
            return false;
        if (schema.TryGetProperty("anyOf", out var anyOf) &&
            (!IsSchemaArray(anyOf) || !anyOf.EnumerateArray().Any(branch => IsValid(value, branch, depth + 1))))
            return false;
        if (schema.TryGetProperty("oneOf", out var oneOf) &&
            (!IsSchemaArray(oneOf) || oneOf.EnumerateArray()
                .Count(branch => IsValid(value, branch, depth + 1)) != 1))
            return false;
        if (schema.TryGetProperty("not", out var not) && IsValid(value, not, depth + 1))
            return false;

        if (schema.TryGetProperty("type", out var type) && !MatchesType(value, type))
            return false;
        if (schema.TryGetProperty("const", out var constant) && !Equivalent(value, constant))
            return false;
        if (schema.TryGetProperty("enum", out var enumeration) &&
            (!IsSchemaArray(enumeration) || !enumeration.EnumerateArray().Any(item => Equivalent(value, item))))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Object => ValidateObject(value, schema, depth),
            JsonValueKind.Array => ValidateArray(value, schema, depth),
            JsonValueKind.String => ValidateString(value.GetString()!, schema),
            JsonValueKind.Number => ValidateNumber(value, schema),
            _ => true,
        };
    }

    private static bool ValidateObject(JsonElement value, JsonElement schema, int depth)
    {
        if (!TryGetNonNegativeInteger(schema, "minProperties", out var minimumProperties) ||
            !TryGetNonNegativeInteger(schema, "maxProperties", out var maximumProperties))
            return false;
        var properties = value.EnumerateObject().ToArray();
        if (minimumProperties is { } minimum && properties.Length < minimum ||
            maximumProperties is { } maximum && properties.Length > maximum)
            return false;

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array ||
                required.EnumerateArray().Any(name =>
                    name.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty(name.GetString()!, out _)))
                return false;
        }

        if (schema.TryGetProperty("properties", out var declaredProperties) &&
            declaredProperties.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in properties)
        {
            if (declaredProperties.ValueKind == JsonValueKind.Object &&
                declaredProperties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!IsValid(property.Value, propertySchema, depth + 1))
                    return false;
                continue;
            }

            if (!schema.TryGetProperty("additionalProperties", out var additionalProperties))
                continue;
            if (additionalProperties.ValueKind == JsonValueKind.False ||
                !IsValid(property.Value, additionalProperties, depth + 1))
                return false;
        }
        return true;
    }

    private static bool ValidateArray(JsonElement value, JsonElement schema, int depth)
    {
        if (!TryGetNonNegativeInteger(schema, "minItems", out var minimumItems) ||
            !TryGetNonNegativeInteger(schema, "maxItems", out var maximumItems))
            return false;
        var items = value.EnumerateArray().ToArray();
        if (minimumItems is { } minimum && items.Length < minimum ||
            maximumItems is { } maximum && items.Length > maximum)
            return false;

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems))
        {
            if (uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            if (uniqueItems.ValueKind == JsonValueKind.True &&
                items.Where((item, index) => items.Take(index).Any(previous => Equivalent(item, previous))).Any())
                return false;
        }

        if (schema.TryGetProperty("items", out var itemSchema) &&
            items.Any(item => !IsValid(item, itemSchema, depth + 1)))
            return false;
        return true;
    }

    private static bool ValidateString(string value, JsonElement schema)
    {
        if (!TryGetNonNegativeInteger(schema, "minLength", out var minimumLength) ||
            !TryGetNonNegativeInteger(schema, "maxLength", out var maximumLength))
            return false;
        var length = value.EnumerateRunes().Count();
        if (minimumLength is { } minimum && length < minimum ||
            maximumLength is { } maximum && length > maximum)
            return false;

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
                return false;
            try
            {
                if (!Regex.IsMatch(
                        value,
                        pattern.GetString()!,
                        RegexOptions.CultureInvariant,
                        PatternTimeout))
                    return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        if (!schema.TryGetProperty("format", out var format))
            return true;
        if (format.ValueKind != JsonValueKind.String)
            return false;
        return format.GetString() switch
        {
            "uuid" => Guid.TryParseExact(value, "D", out _),
            "date" => DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "date-time" => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            _ => true,
        };
    }

    private static bool ValidateNumber(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String &&
            !MatchesIntegerFormat(value, format.GetString()!))
            return false;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            return false;
        return IsWithinBound(schema, "minimum", number, lowerBound: true, exclusive: false) &&
            IsWithinBound(schema, "maximum", number, lowerBound: false, exclusive: false) &&
            IsWithinBound(schema, "exclusiveMinimum", number, lowerBound: true, exclusive: true) &&
            IsWithinBound(schema, "exclusiveMaximum", number, lowerBound: false, exclusive: true);
    }

    private static bool IsWithinBound(
        JsonElement schema,
        string name,
        double value,
        bool lowerBound,
        bool exclusive)
    {
        if (!schema.TryGetProperty(name, out var bound))
            return true;
        if (!bound.TryGetDouble(out var number) || !double.IsFinite(number))
            return false;
        return lowerBound
            ? exclusive ? value > number : value >= number
            : exclusive ? value < number : value <= number;
    }

    private static bool MatchesIntegerFormat(JsonElement value, string format)
    {
        if (format is not ("int32" or "int64"))
            return true;
        if (!decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) ||
            decimal.Truncate(number) != number)
            return false;

        return format == "int32"
            ? number is >= int.MinValue and <= int.MaxValue
            : number is >= long.MinValue and <= long.MaxValue;
    }

    private static bool MatchesType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(candidate => MatchesType(value, candidate));
        if (type.ValueKind != JsonValueKind.String)
            return false;

        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => IsInteger(value),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return false;
        if (value.TryGetInt64(out _))
            return true;
        return decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) &&
            decimal.Truncate(number) == number;
    }

    private static bool TryGetNonNegativeInteger(
        JsonElement schema,
        string name,
        out int? result)
    {
        result = null;
        if (!schema.TryGetProperty(name, out var value))
            return true;
        if (!value.TryGetInt32(out var number) || number < 0)
            return false;
        result = number;
        return true;
    }

    private static bool IsSchemaArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array;

    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEquivalent(left, right),
            JsonValueKind.Array => ArraysEquivalent(left, right),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out var leftDecimal) &&
                right.TryGetDecimal(out var rightDecimal)
                    ? leftDecimal == rightDecimal
                    : left.GetRawText() == right.GetRawText(),
            _ => true,
        };
    }

    private static bool ObjectsEquivalent(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToArray();
        var rightProperties = right.EnumerateObject().ToArray();
        return leftProperties.Length == rightProperties.Length &&
            leftProperties.All(property =>
                right.TryGetProperty(property.Name, out var value) &&
                Equivalent(property.Value, value));
    }

    private static bool ArraysEquivalent(JsonElement left, JsonElement right)
    {
        var leftItems = left.EnumerateArray().ToArray();
        var rightItems = right.EnumerateArray().ToArray();
        return leftItems.Length == rightItems.Length &&
            leftItems.Zip(rightItems).All(pair => Equivalent(pair.First, pair.Second));
    }
}
