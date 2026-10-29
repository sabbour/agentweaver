using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli.Api;

internal sealed record GatewayParameter(
    string Name,
    string Location,
    bool Required,
    JsonObject Schema);

internal sealed record GatewayOperation(
    string OperationId,
    string Method,
    string Path,
    JsonObject Document,
    IReadOnlyList<GatewayParameter> Parameters,
    bool HasRequestBody,
    bool BodyRequired,
    IReadOnlyList<string> ResponseMediaTypes,
    IReadOnlyList<string> ResponseHeaders,
    int MaximumRequestBytes,
    int MaximumResponseBytes);

internal static partial class GatewayContract
{
    private static readonly HashSet<string> Methods =
        new(StringComparer.OrdinalIgnoreCase) { "get", "post", "put", "patch", "delete" };
    private static readonly HashSet<string> ParameterLocations =
        new(StringComparer.Ordinal) { "path", "query", "header" };
    private static readonly HashSet<string> MediaTypes =
        new(StringComparer.Ordinal)
        {
            "application/json",
            "application/problem+json",
            "application/octet-stream",
            "text/event-stream",
        };
    private static readonly HashSet<string> PathItemMetadata =
        new(StringComparer.Ordinal) { "$ref", "summary", "description", "servers", "parameters" };

    public static IReadOnlyDictionary<string, GatewayOperation> Parse(JsonObject document)
    {
        var securitySchemes = (document["components"] as JsonObject)?["securitySchemes"] as JsonObject;
        var bearerSchemes = securitySchemes?
            .Where(item => item.Key.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (String(document, "openapi") != "3.1.0" ||
            document["paths"] is not JsonObject paths ||
            bearerSchemes.Length != 1 ||
            bearerSchemes[0].Value is not JsonObject bearer ||
            String(bearer, "type") != "http" ||
            String(bearer, "scheme") != "bearer" ||
            !HasBearerSecurity(document["security"], bearerSchemes[0].Key))
        {
            throw new InvalidDataException("The live Gateway OpenAPI document has an unsupported security or document shape.");
        }

        var bearerSchemeName = bearerSchemes[0].Key;
        var operations = new Dictionary<string, GatewayOperation>(StringComparer.Ordinal);
        foreach (var (path, pathNode) in paths)
        {
            if (!path.StartsWith("/api/v1/", StringComparison.Ordinal) ||
                pathNode is not JsonObject pathItem ||
                pathItem.ContainsKey("$ref") ||
                pathItem.ContainsKey("servers"))
            {
                throw new InvalidDataException("The live Gateway contract contains an unsupported public path.");
            }

            var pathParameters = ParseParameterArray(pathItem["parameters"]);
            var hasOperation = false;
            foreach (var (method, operationNode) in pathItem)
            {
                if (PathItemMetadata.Contains(method) || method.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Methods.Contains(method) || operationNode is not JsonObject operation)
                    throw new InvalidDataException("The live Gateway contract contains an unsupported HTTP method.");
                if (!HasBearerSecurity(operation["security"] ?? document["security"], bearerSchemeName))
                    throw new InvalidDataException("The live Gateway contract contains an operation without bearer security.");

                hasOperation = true;
                var operationId = String(operation, "operationId");
                if (string.IsNullOrWhiteSpace(operationId))
                    throw new InvalidDataException("The live Gateway operation identity is missing or invalid.");

                var parameters = MergeParameters(pathParameters, ParseParameterArray(operation["parameters"]));
                ValidatePathParameters(path, parameters);
                var (hasBody, bodyRequired) = ParseRequestBody(operation["requestBody"]);
                var responseMediaTypes = ParseResponseMediaTypes(operation["responses"]);
                var responseHeaders = ParseResponseHeaders(operation["responses"]);
                var maximumRequestBytes = Bound(operation["x-agentweaver-max-request-body-bytes"], 1024 * 1024, 64 * 1024 * 1024, "request");
                var maximumResponseBytes = Bound(operation["x-agentweaver-max-response-bytes"], 8 * 1024 * 1024, 64 * 1024 * 1024, "response");
                var normalized = new GatewayOperation(
                    operationId,
                    method.ToUpperInvariant(),
                    path,
                    (JsonObject)operation.DeepClone(),
                    parameters,
                    hasBody,
                    bodyRequired,
                    responseMediaTypes,
                    responseHeaders,
                    maximumRequestBytes,
                    maximumResponseBytes);

                if (!operations.TryAdd(operationId, normalized))
                    throw new InvalidDataException("The live Gateway contract contains duplicate operation identities.");
            }

            if (!hasOperation)
                throw new InvalidDataException("The live Gateway path contains no supported operation.");
        }

        if (operations.Count == 0)
            throw new InvalidDataException("The live Gateway advertises no API operations.");
        return operations;
    }

    public static bool HasBearerSecurity(JsonNode? security, string schemeName = "Bearer")
    {
        if (security is not JsonArray entries || entries.Count == 0)
            return false;
        return entries.Any(entry =>
            entry is JsonObject requirement &&
            requirement.Any(scheme =>
                scheme.Key.Equals(schemeName, StringComparison.OrdinalIgnoreCase) &&
                scheme.Value is JsonArray scopes &&
                scopes.All(scope => scope is JsonValue value && value.TryGetValue<string>(out _))));
    }

    private static IReadOnlyList<GatewayParameter> ParseParameterArray(JsonNode? node)
    {
        if (node is null)
            return [];
        if (node is not JsonArray entries)
            throw new InvalidDataException("The live Gateway contract contains unsupported parameters.");

        var result = new List<GatewayParameter>();
        foreach (var item in entries)
        {
            if (item is not JsonObject parameter)
                throw new InvalidDataException("The live Gateway contract contains an invalid parameter.");
            var name = String(parameter, "name");
            var location = String(parameter, "in");
            if (string.IsNullOrWhiteSpace(name) ||
                location is null ||
                !ParameterLocations.Contains(location) ||
                parameter.ContainsKey("content") ||
                parameter["required"] is not null && parameter["required"] is not JsonValue requiredValue ||
                parameter["required"] is JsonValue requiredNode && !requiredNode.TryGetValue<bool>(out _))
            {
                throw new InvalidDataException("The live Gateway contract contains an unsupported parameter shape.");
            }

            var required = parameter["required"]?.GetValue<bool>() ?? false;
            if (location == "path" && !required)
                throw new InvalidDataException("A Gateway path parameter is not required.");
            var schema = parameter["schema"] as JsonObject ?? new JsonObject { ["type"] = "string" };
            ValidateParameterSchema(schema);
            if (parameter["style"] is JsonValue styleNode && styleNode.TryGetValue<string>(out var style) &&
                style != (location == "query" ? "form" : "simple"))
            {
                throw new InvalidDataException("The live Gateway contract uses an unsupported parameter serialization style.");
            }
            if (parameter["explode"] is JsonValue explodeNode && explodeNode.TryGetValue<bool>(out var explode) &&
                explode != (location == "query"))
            {
                throw new InvalidDataException("The live Gateway contract uses an unsupported parameter serialization style.");
            }

            result.Add(new GatewayParameter(name, location, required, (JsonObject)schema.DeepClone()));
        }
        return result;
    }

    private static IReadOnlyList<GatewayParameter> MergeParameters(
        IReadOnlyList<GatewayParameter> pathParameters,
        IReadOnlyList<GatewayParameter> operationParameters)
    {
        var merged = new Dictionary<string, GatewayParameter>(StringComparer.Ordinal);
        foreach (var parameter in pathParameters.Concat(operationParameters))
        {
            var key = Key(parameter);
            merged[key] = parameter;
        }
        return merged.Values.ToArray();
    }

    private static void ValidateParameterSchema(JsonObject schema)
    {
        var type = String(schema, "type") ?? "string";
        if (type is not ("string" or "integer" or "number" or "boolean"))
            throw new InvalidDataException("The live Gateway operation advertises an unsupported parameter type.");

        if (schema["enum"] is JsonArray values &&
            values.Any(value => value is not JsonValue scalar ||
                !(scalar.TryGetValue<string>(out _) ||
                  scalar.TryGetValue<long>(out _) ||
                  scalar.TryGetValue<double>(out _) ||
                  scalar.TryGetValue<bool>(out _))))
        {
            throw new InvalidDataException("The live Gateway operation advertises an invalid parameter enum.");
        }
        if (schema.ContainsKey("enum") && schema["enum"] is not JsonArray)
            throw new InvalidDataException("The live Gateway operation advertises an invalid parameter enum.");
        if (schema["pattern"] is JsonValue patternValue)
        {
            if (!patternValue.TryGetValue<string>(out var pattern))
                throw new InvalidDataException("The live Gateway operation advertises an invalid parameter pattern.");
            try
            {
                _ = Regex.IsMatch("", pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The live Gateway operation advertises an invalid parameter pattern.", exception);
            }
        }

        ValidateNonnegativeInteger(schema, "minLength");
        ValidateNonnegativeInteger(schema, "maxLength");
        ValidateFiniteNumber(schema, "minimum");
        ValidateFiniteNumber(schema, "maximum");
    }

    private static void ValidatePathParameters(string path, IReadOnlyList<GatewayParameter> parameters)
    {
        var expected = PathParameterRegex().Matches(path)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var supplied = parameters.Where(parameter => parameter.Location == "path")
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(supplied))
            throw new InvalidDataException("The live Gateway path parameters do not match its operation contract.");
    }

    private static (bool HasBody, bool Required) ParseRequestBody(JsonNode? node)
    {
        if (node is null)
            return (false, false);
        if (node is not JsonObject body ||
            body["required"] is not null && body["required"] is not JsonValue requiredNode ||
            body["required"] is JsonValue requiredValue && !requiredValue.TryGetValue<bool>(out _) ||
            body["content"] is not JsonObject content ||
            !content.Any(entry => NormalizeMediaType(entry.Key) == "application/json"))
        {
            throw new InvalidDataException("The live Gateway operation has an unsupported request-body shape.");
        }
        foreach (var (mediaType, media) in content)
        {
            if (NormalizeMediaType(mediaType) != "application/json" || media is not JsonObject)
                throw new InvalidDataException("The live Gateway operation advertises an unsupported request-body media type.");
        }
        return (true, body["required"]?.GetValue<bool>() ?? false);
    }

    private static IReadOnlyList<string> ParseResponseMediaTypes(JsonNode? node)
    {
        if (node is null)
            return ["application/json"];
        if (node is not JsonObject responses)
            throw new InvalidDataException("The live Gateway operation has an unsupported response shape.");

        var mediaTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (status, responseNode) in responses)
        {
            if (!IsSuccessOrDefault(status))
                continue;
            if (responseNode is not JsonObject response)
                throw new InvalidDataException("The live Gateway operation has an unsupported response shape.");
            if (response["content"] is not JsonObject content)
                continue;
            foreach (var (mediaType, mediaNode) in content)
            {
                var normalized = NormalizeMediaType(mediaType);
                if (!MediaTypes.Contains(normalized) || mediaNode is not JsonObject media)
                    throw new InvalidDataException($"The live Gateway operation advertises unsupported response media type: {normalized}.");
                if (media["schema"] is JsonObject schema)
                {
                    if (normalized == "text/event-stream" && String(schema, "type") is { } eventType && eventType != "string")
                        throw new InvalidDataException("The live Gateway operation has an unsupported event-stream response schema.");
                    if (normalized == "application/octet-stream" && String(schema, "format") is { } format && format != "binary")
                        throw new InvalidDataException("The live Gateway operation has an unsupported binary response schema.");
                }
                mediaTypes.Add(normalized);
            }
        }
        return mediaTypes.ToArray();
    }

    private static IReadOnlyList<string> ParseResponseHeaders(JsonNode? node)
    {
        if (node is not JsonObject responses)
            return [];
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (status, responseNode) in responses)
        {
            if (!IsSuccessOrDefault(status) || responseNode is not JsonObject response ||
                response["headers"] is not JsonObject responseHeaders)
                continue;
            foreach (var name in responseHeaders.Select(entry => entry.Key))
            {
                if (!ReservedHeaders.Contains(name))
                    headers.Add(name);
            }
        }
        return headers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static readonly HashSet<string> ReservedHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization", "cookie", "x-agentweaver-tenant", "x-correlation-id",
        };

    private static int Bound(JsonNode? node, int fallback, int maximum, string name)
    {
        if (node is null)
            return fallback;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var result) || result is < 1 || result > maximum)
            throw new InvalidDataException($"The advertised {name} limit is outside the supported range.");
        return result;
    }

    private static void ValidateNonnegativeInteger(JsonObject schema, string name)
    {
        if (schema[name] is JsonValue value && (!value.TryGetValue<int>(out var number) || number < 0))
            throw new InvalidDataException($"The live Gateway operation advertises an invalid {name} constraint.");
    }

    private static void ValidateFiniteNumber(JsonObject schema, string name)
    {
        if (schema[name] is JsonValue value &&
            (!value.TryGetValue<double>(out var number) || !double.IsFinite(number)))
        {
            throw new InvalidDataException($"The live Gateway operation advertises an invalid {name} constraint.");
        }
    }

    private static bool IsSuccessOrDefault(string status) =>
        status.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        status.Length == 3 && status[0] == '2' && status.Skip(1).All(char.IsDigit) ||
        status.Equals("2XX", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeMediaType(string value) =>
        value.Split(';', 2)[0].Trim().ToLowerInvariant();

    internal static string? String(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static string Key(GatewayParameter parameter) =>
        $"{parameter.Location}:{(parameter.Location == "header" ? parameter.Name.ToLowerInvariant() : parameter.Name)}";

    [GeneratedRegex(@"\{(?<name>[^}:]+)(?::[^}]+)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PathParameterRegex();
}
