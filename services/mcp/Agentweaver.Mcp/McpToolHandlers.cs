using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Agentweaver.Mcp;

public sealed class McpToolHandlers(
    GatewayToolCatalog catalog,
    GatewayApiClient gateway)
{
    private const int MaximumOwnerResponseBytes = 8 * 1024 * 1024;
    private static readonly Dictionary<string, HttpMethod> Methods = new(StringComparer.Ordinal)
    {
        ["GET"] = HttpMethod.Get,
        ["POST"] = HttpMethod.Post,
        ["PUT"] = HttpMethod.Put,
        ["PATCH"] = HttpMethod.Patch,
        ["DELETE"] = HttpMethod.Delete,
    };

    public async ValueTask<ListToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        var operations = await catalog.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        return new ListToolsResult { Tools = operations.Select(operation => operation.Tool).ToList() };
    }

    public async ValueTask<CallToolResult> CallAsync(
        CallToolRequestParams request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(McpServiceOptions.GatewayRequestTimeout);
        var requestToken = requestDeadline.Token;

        IReadOnlyList<GatewayToolOperation> operations;
        try
        {
            operations = await catalog.GetOperationsAsync(requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("gateway_timeout", "The Gateway tool catalog request timed out.");
        }
        catch (HttpRequestException)
        {
            return ErrorResult("gateway_unavailable", "The Gateway tool catalog is unavailable.");
        }
        catch (InvalidDataException)
        {
            return ErrorResult("gateway_contract_invalid", "The Gateway tool catalog is invalid.");
        }
        catch (JsonException)
        {
            return ErrorResult("gateway_contract_invalid", "The Gateway tool catalog is invalid.");
        }

        if (request.Name is null)
            return ErrorResult("unknown_tool", "The requested tool is not in the Gateway catalog.");
        var operation = operations.FirstOrDefault(candidate => candidate.Tool.Name == request.Name);
        if (operation is null)
            return ErrorResult("unknown_tool", "The requested tool is not in the Gateway catalog.");

        var arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
        if (!GatewayJsonSchemaValidator.IsValid(
                JsonSerializer.SerializeToElement(arguments),
                operation.InputSchema) ||
            !TryBuildRequest(operation, arguments, out var pathAndQuery, out var body,
                out var tenantSelector, out var idempotencyKey))
            return ErrorResult("invalid_arguments", "The tool arguments do not match the advertised input schema.");

        if (!Methods.TryGetValue(operation.Method, out var method))
            return ErrorResult("gateway_contract_invalid", "The Gateway operation uses an unsupported method.");

        try
        {
            using var response = await gateway.SendAsync(
                context,
                method.Method,
                pathAndQuery,
                body,
                tenantSelector,
                idempotencyKey,
                ifMatch: null,
                requestToken).ConfigureAwait(false);
            var rawBody = await ReadOwnerBodyAsync(response, requestToken).ConfigureAwait(false);
            return OwnerResult(response.StatusCode, rawBody, operation.AcceptsOnly);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("gateway_timeout", "The Gateway request timed out.");
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorResult("unauthenticated", "The current MCP bearer is no longer valid.");
        }
        catch (HttpRequestException)
        {
            return ErrorResult("gateway_unavailable", "The Gateway could not complete the request.");
        }
        catch (ArgumentException exception)
        {
            return ErrorResult("invalid_arguments", exception.Message);
        }
        catch (InvalidDataException)
        {
            return ErrorResult("gateway_response_too_large", "The Gateway response exceeded its size limit.");
        }
    }

    private static bool TryBuildRequest(
        GatewayToolOperation operation,
        IReadOnlyDictionary<string, JsonElement> arguments,
        out string pathAndQuery,
        out JsonElement? body,
        out string? tenantSelector,
        out string? idempotencyKey)
    {
        pathAndQuery = string.Empty;
        body = null;
        tenantSelector = null;
        idempotencyKey = null;

        var acceptedNames = operation.Parameters
            .Select(parameter => parameter.InputName)
            .ToHashSet(StringComparer.Ordinal);
        if (arguments.Keys.Any(name => !acceptedNames.Contains(name)) ||
            operation.Parameters.Any(parameter =>
                parameter.Required &&
                (!arguments.TryGetValue(parameter.InputName, out var value) ||
                 value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)))
            return false;

        var path = operation.PathTemplate;
        foreach (var parameter in operation.Parameters.Where(parameter => parameter.Location == "path"))
        {
            if (!arguments.TryGetValue(parameter.InputName, out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;
            var placeholder = "{" + parameter.ApiName + "}";
            if (!path.Contains(placeholder, StringComparison.Ordinal))
                return false;
            path = path.Replace(
                placeholder,
                Uri.EscapeDataString(value.GetString()!),
                StringComparison.Ordinal);
        }
        if (path.Contains('{') || path.Contains('}'))
            return false;

        var query = new List<string>();
        foreach (var parameter in operation.Parameters.Where(parameter => parameter.Location == "query"))
        {
            if (!arguments.TryGetValue(parameter.InputName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (!TryGetScalar(value, out var queryValue))
                return false;
            query.Add(
                Uri.EscapeDataString(parameter.ApiName) + "=" + Uri.EscapeDataString(queryValue));
        }
        if (query.Count > 0)
            path += "?" + string.Join("&", query);

        if (arguments.TryGetValue("body", out var bodyValue))
            body = bodyValue;
        if (!TryReadOptionalString(arguments, "tenantSelector", out tenantSelector) ||
            !TryReadOptionalString(arguments, "idempotencyKey", out idempotencyKey))
            return false;
        pathAndQuery = path;
        return true;
    }

    private static bool TryReadOptionalString(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string name,
        out string? result)
    {
        if (!arguments.TryGetValue(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            result = null;
            return true;
        }
        result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return result is not null;
    }

    private static bool TryGetScalar(JsonElement value, out string result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result = value.GetString()!;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                result = value.GetRawText();
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    private static CallToolResult OwnerResult(
        HttpStatusCode statusCode,
        string rawBody,
        bool acceptsOnly)
    {
        JsonNode? ownerResponse;
        try
        {
            ownerResponse = rawBody.Length == 0 ? null : JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            ownerResponse = JsonValue.Create(rawBody);
        }

        var acceptedFalse = ownerResponse is JsonObject responseObject &&
            responseObject["accepted"] is JsonValue acceptedValue &&
            acceptedValue.TryGetValue<bool>(out var accepted) &&
            !accepted;
        var isError = (int)statusCode >= 400 || acceptedFalse;
        var message = acceptedFalse
            ? "The owning service rejected the request."
            : acceptsOnly || statusCode == HttpStatusCode.Accepted
                ? $"The owning service returned HTTP {(int)statusCode}; acceptance is not completion."
                : $"The owning service returned HTTP {(int)statusCode}.";
        var text = rawBody.Length == 0 ? message : message + "\n" + rawBody;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = new JsonObject
            {
                ["status"] = (int)statusCode,
                ["ownerResponse"] = ownerResponse,
            },
            IsError = isError,
        };
    }

    private static CallToolResult ErrorResult(string code, string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = new JsonObject { ["error"] = code },
            IsError = true,
        };

    private static async Task<string> ReadOwnerBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumOwnerResponseBytes)
            throw new InvalidDataException("The Gateway response exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var body = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(body.ToArray());
            if (body.Length + read > MaximumOwnerResponseBytes)
                throw new InvalidDataException("The Gateway response exceeded its size limit.");
            body.Write(chunk, 0, read);
        }
    }
}
