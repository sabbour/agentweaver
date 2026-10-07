using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentweaver.Gateway;

internal static partial class GatewayOpenApi
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static object CreateDocument()
    {
        var paths = GatewayRouteCatalog.Routes
            .GroupBy(route => NormalizePath(route.PublicPath), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (object)group.ToDictionary(
                    route => route.Method.ToLowerInvariant(),
                    CreateOperation,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "Agentweaver Gateway API",
                version = "1.0.0",
                description =
                    "Versioned browser, CLI, and first-party MCP entry. Requests and responses are delegated " +
                    "to the named owner API; owner status codes and JSON bodies are preserved. A 202 response " +
                    "means accepted by the owner, not completed.",
            },
            paths,
            components = new
            {
                securitySchemes = new
                {
                    Bearer = new
                    {
                        type = "http",
                        scheme = "bearer",
                        bearerFormat = "JWT",
                        description = "Identity Broker access token for the Gateway audience.",
                    },
                },
                schemas = new
                {
                    OwnerJson = new { },
                    Problem = new
                    {
                        type = "object",
                        required = new[] { "title", "status", "code" },
                        properties = new
                        {
                            type = new { type = "string" },
                            title = new { type = "string" },
                            status = new { type = "integer" },
                            detail = new { type = "string" },
                            code = new { type = "string" },
                        },
                    },
                },
            },
            security = new[] { new Dictionary<string, string[]> { ["Bearer"] = [] } },
        };
    }

    private static object CreateOperation(GatewayRoute route)
    {
        var parameters = PathParameterRegex().Matches(route.PublicPath)
            .Select(match => (object)new
            {
                name = match.Groups["name"].Value,
                @in = "path",
                required = true,
                schema = PathParameterSchema(match.Groups["constraint"].Value),
            })
            .Concat(route.QueryParameters.Select(name => (object)new
            {
                name,
                @in = "query",
                required = false,
                schema = QuerySchema(name),
            }))
            .Append(new
            {
                name = "X-Agentweaver-Tenant",
                @in = "header",
                required = false,
                schema = new { type = "string" },
            })
            .ToArray();

        var responses = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["200"] = route.IsRunEventStream
                ? EventStreamResponse()
                : JsonResponse("Owner response; returned by the owning service."),
            ["default"] = new
            {
                description =
                    "Owner-defined status and response are passed through unchanged, including structured errors.",
            },
            ["401"] = ProblemResponse("The access token is missing, invalid, or expired."),
            ["403"] = ProblemResponse("The Gateway or owning service denied the request."),
            ["502"] = ProblemResponse("The owner is unavailable, redirected, or returned an invalid contract."),
            ["504"] = ProblemResponse("The finite owner request timed out."),
        };
        if (route.IsRunEventStream)
        {
            responses["400"] = ProblemResponse("The event cursor is invalid or ambiguous.");
        }
        if (route.Method == "POST" && !route.AcceptsOnly)
        {
            responses["201"] = JsonResponse("Created by the owner; body and status are preserved.");
        }
        if (route.AcceptsOnly)
        {
            responses["202"] = JsonResponse(
                "Accepted by the owner only; this is not evidence of downstream completion.");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = route.OperationId,
            ["summary"] = route.Summary,
            ["description"] =
                $"Owner: {route.Owner}. The Gateway forwards the validated bearer and does not infer identity " +
                "from request bodies or caller-controlled identity headers. Owner authorization and JSON " +
                "contracts remain authoritative.",
            ["security"] = new[] { new Dictionary<string, string[]> { ["Bearer"] = [] } },
            ["parameters"] = parameters,
            ["requestBody"] = route.HasJsonBody
                ? new
                {
                    required = true,
                    content = new Dictionary<string, object>
                    {
                        ["application/json"] = new
                        {
                            schema = new Dictionary<string, object> { ["$ref"] = "#/components/schemas/OwnerJson" },
                        },
                    },
                }
                : null,
            ["responses"] = responses,
            ["x-agentweaver-owner"] = route.Owner.ToString(),
            ["x-agentweaver-owner-path"] = route.OwnerPath,
            ["x-agentweaver-accepted-only"] = route.AcceptsOnly,
            ["x-agentweaver-stream"] = route.IsRunEventStream ? "server-sent-events" : null,
        };
    }

    private static object JsonResponse(string description) =>
        new
        {
            description,
            content = new Dictionary<string, object>
            {
                ["application/json"] = new
                {
                    schema = new Dictionary<string, object> { ["$ref"] = "#/components/schemas/OwnerJson" },
                },
            },
        };

    private static object ProblemResponse(string description) =>
        new
        {
            description,
            content = new Dictionary<string, object>
            {
                ["application/problem+json"] = new
                {
                    schema = new Dictionary<string, object> { ["$ref"] = "#/components/schemas/Problem" },
                },
            },
        };

    private static object EventStreamResponse() =>
        new
        {
            description =
                "Ordered committed session events. Each SSE id is the journal cursor and can be supplied as " +
                "Last-Event-ID when reconnecting. Current project read authority is checked before each event.",
            content = new Dictionary<string, object>
            {
                ["text/event-stream"] = new
                {
                    schema = new { type = "string" },
                },
            },
        };

    private static object PathParameterSchema(string constraint) => constraint switch
    {
        "guid" => new { type = "string", format = "uuid" },
        _ => new { type = "string" },
    };

    private static object QuerySchema(string name) => name switch
    {
        "limit" or "page" or "pageSize" or "maxItems" or "maxTokens" or "maximumEvents" or
            "maximumDurationSeconds" or "revision" => new { type = "integer" },
        "includeInactive" => new { type = "boolean" },
        _ => new { type = "string" },
    };

    private static string NormalizePath(string path) =>
        PathParameterRegex().Replace(
            path,
            match => "{" + match.Groups["name"].Value + "}");

    [GeneratedRegex(
        @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?::(?<constraint>[^}]+))?\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathParameterRegex();
}
