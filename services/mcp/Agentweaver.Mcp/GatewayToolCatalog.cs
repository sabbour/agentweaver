using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Agentweaver.Mcp;

internal sealed record GatewayToolParameter(
    string ApiName,
    string InputName,
    string Location,
    bool Required,
    JsonElement Schema);

internal sealed record GatewayToolOperation(
    Tool Tool,
    JsonElement InputSchema,
    string Method,
    string PathTemplate,
    bool AcceptsOnly,
    IReadOnlyList<GatewayToolParameter> Parameters,
    bool HasBody,
    bool BodyRequired);

public sealed class GatewayToolCatalog(HttpClient client)
{
    private const int MaximumOpenApiBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile IReadOnlyList<GatewayToolOperation>? _operations;

    internal async Task<IReadOnlyList<GatewayToolOperation>> GetOperationsAsync(
        CancellationToken cancellationToken)
    {
        if (_operations is not null)
            return _operations;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(McpServiceOptions.GatewayRequestTimeout);
        await _loadLock.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (_operations is not null)
                return _operations;

            using var response = await client.GetAsync(
                "/openapi/v1.json",
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException(
                    "The configured Gateway did not provide its tool catalog.",
                    null,
                    response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaximumOpenApiBytes)
                throw new InvalidDataException("The Gateway tool catalog exceeded its size limit.");

            var bytes = await ReadBoundedAsync(
                response.Content,
                MaximumOpenApiBytes,
                deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            _operations = ParseOperations(document.RootElement);
            return _operations;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static IReadOnlyList<GatewayToolOperation> ParseOperations(JsonElement root)
    {
        if (!root.TryGetProperty("openapi", out var version) ||
            version.GetString() != "3.1.0" ||
            !root.TryGetProperty("paths", out var paths) ||
            paths.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("components", out var components) ||
            !components.TryGetProperty("schemas", out var schemas) ||
            schemas.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The Gateway tool catalog has an unsupported OpenAPI shape.");

        var operations = new Dictionary<string, GatewayToolOperation>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        {
            ValidateGatewayPath(path.Name);
            if (path.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Gateway tool catalog contains an invalid path item.");

            foreach (var method in path.Value.EnumerateObject())
            {
                if (!IsSupportedMethod(method.Name))
                    continue;
                if (method.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The Gateway tool catalog contains an invalid operation.");

                var operation = method.Value;
                var operationId = RequiredString(operation, "operationId");
                var operationDescription = OptionalString(operation, "description");
                var streamKind = OptionalString(operation, "x-agentweaver-stream");
                if (streamKind is not null)
                {
                    if (streamKind != "server-sent-events")
                        throw new InvalidDataException("The Gateway tool catalog contains an unsupported stream.");
                    continue;
                }

                var owner = RequiredString(operation, "x-agentweaver-owner");
                var summary = RequiredString(operation, "summary");
                var acceptedOnly = operation.TryGetProperty("x-agentweaver-accepted-only", out var acceptedOnlyValue) &&
                    acceptedOnlyValue.ValueKind == JsonValueKind.True;
                var parameters = ParseParameters(operation);
                var (hasBody, bodyRequired) = AddRequestBody(operation, parameters);
                var inputSchema = CreateInputSchema(parameters, hasBody, bodyRequired, schemas);
                var tool = new Tool
                {
                    Name = "agentweaver_" + operationId,
                    Title = summary,
                    Description = BuildDescription(owner, operationDescription, acceptedOnly),
                    InputSchema = inputSchema,
                };
                var gatewayOperation = new GatewayToolOperation(
                    tool,
                    inputSchema,
                    method.Name.ToUpperInvariant(),
                    path.Name,
                    acceptedOnly,
                    parameters,
                    hasBody,
                    bodyRequired);
                if (!operations.TryAdd(tool.Name, gatewayOperation))
                    throw new InvalidDataException("The Gateway tool catalog contains duplicate operation names.");
            }
        }

        if (operations.Count == 0)
            throw new InvalidDataException("The Gateway tool catalog contains no admitted operations.");
        return operations.Values.ToArray();
    }

    private static List<GatewayToolParameter> ParseParameters(
        JsonElement operation)
    {
        var parameters = new List<GatewayToolParameter>();
        if (!operation.TryGetProperty("parameters", out var entries))
            return parameters;
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Gateway tool catalog contains invalid operation parameters.");

        foreach (var parameter in entries.EnumerateArray())
        {
            var name = RequiredString(parameter, "name");
            var location = RequiredString(parameter, "in");
            var inputName = location switch
            {
                "path" or "query" => name,
                "header" when name == "X-Agentweaver-Tenant" => "tenantSelector",
                "header" when name == "Idempotency-Key" => "idempotencyKey",
                "header" => throw new InvalidDataException(
                    $"The Gateway operation exposes unsupported header '{name}'."),
                _ => throw new InvalidDataException("The Gateway tool catalog contains an unsupported parameter."),
            };
            if (!parameter.TryGetProperty("schema", out var schema))
                throw new InvalidDataException("The Gateway tool catalog contains a parameter without a schema.");

            var required = parameter.TryGetProperty("required", out var requiredValue) &&
                requiredValue.ValueKind == JsonValueKind.True;
            parameters.Add(new GatewayToolParameter(name, inputName, location, required, schema.Clone()));
        }
        return parameters;
    }

    private static (bool HasBody, bool Required) AddRequestBody(
        JsonElement operation,
        List<GatewayToolParameter> parameters)
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody) ||
            requestBody.ValueKind == JsonValueKind.Null)
            return (false, false);
        if (requestBody.ValueKind != JsonValueKind.Object ||
            !requestBody.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("application/json", out var json) ||
            !json.TryGetProperty("schema", out var schema))
            throw new InvalidDataException("The Gateway tool catalog contains an unsupported request body.");

        parameters.Add(new GatewayToolParameter(
            "body",
            "body",
            "body",
            requestBody.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.True,
            schema.Clone()));
        return (true, parameters[^1].Required);
    }

    private static JsonElement CreateInputSchema(
        IReadOnlyList<GatewayToolParameter> parameters,
        bool hasBody,
        bool bodyRequired,
        JsonElement schemas)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            if (properties.ContainsKey(parameter.InputName))
                throw new InvalidDataException("The Gateway tool catalog contains duplicate input names.");
            properties[parameter.InputName] = ResolveSchema(
                parameter.Schema,
                schemas,
                new HashSet<string>(StringComparer.Ordinal),
                0);
            if (parameter.Required)
                required.Add(parameter.InputName);
        }
        if (hasBody && bodyRequired &&
            !parameters.Any(parameter => parameter.Location == "body" && parameter.Required))
            throw new InvalidDataException("The Gateway request body requirement is missing.");

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
            inputSchema["required"] = required;
        return JsonSerializer.SerializeToElement(inputSchema, JsonOptions);
    }

    private static JsonNode? ResolveSchema(
        JsonElement element,
        JsonElement schemas,
        HashSet<string> referenceStack,
        int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("The Gateway tool catalog schema is too deeply nested.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$ref", out var reference))
            {
                const string prefix = "#/components/schemas/";
                var value = reference.GetString();
                if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("The Gateway tool catalog has an external schema reference.");
                var name = value[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                if (!schemas.TryGetProperty(name, out var definition) || !referenceStack.Add(name))
                    throw new InvalidDataException("The Gateway tool catalog has a missing or recursive schema reference.");
                try
                {
                    var resolved = ResolveSchema(definition, schemas, referenceStack, depth + 1);
                    if (resolved is not JsonObject resolvedObject)
                        throw new InvalidDataException("A Gateway schema reference did not resolve to an object.");
                    foreach (var sibling in element.EnumerateObject().Where(property => property.Name != "$ref"))
                        resolvedObject[sibling.Name] = ResolveSchema(
                            sibling.Value,
                            schemas,
                            referenceStack,
                            depth + 1);
                    return resolvedObject;
                }
                finally
                {
                    referenceStack.Remove(name);
                }
            }

            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
                result[property.Name] = ResolveSchema(property.Value, schemas, referenceStack, depth + 1);
            return result;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in element.EnumerateArray())
                result.Add(ResolveSchema(item, schemas, referenceStack, depth + 1));
            return result;
        }

        return JsonNode.Parse(element.GetRawText());
    }

    private static string BuildDescription(string owner, string? description, bool acceptsOnly)
    {
        var result = $"Owner: {owner}. {description}".Trim();
        if (acceptsOnly)
            result += " An owner response acknowledges receipt only; it does not mean the requested work completed.";
        return result;
    }

    private static void ValidateGatewayPath(string path)
    {
        if (!path.StartsWith("/api/v1/", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains('?') ||
            path.Contains('#') ||
            path.Split('/').Skip(1).Any(segment =>
                Uri.UnescapeDataString(segment) is "." or ".."))
            throw new InvalidDataException("The Gateway tool catalog contains a route outside /api/v1.");
    }

    private static bool IsSupportedMethod(string method) =>
        method is "get" or "post" or "put" or "patch" or "delete";

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(
                $"The Gateway tool catalog is missing a valid '{propertyName}'.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("The Gateway tool catalog exceeded its size limit.");
            buffer.Write(chunk, 0, read);
        }
    }
}
