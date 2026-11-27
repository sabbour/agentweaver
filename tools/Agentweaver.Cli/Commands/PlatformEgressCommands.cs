using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

internal static class PlatformEgressCommands
{
    private const string OperationId = "listPlatformEgressDestinations";
    private const string OperationPath = "/api/v1/platform/egress/destinations";
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 100;
    private const int MaximumCursorLength = 256;
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly string[] PageKeys = ["destinations", "nextCursor"];
    private static readonly string[] RowKeys =
    [
        "destinationId", "requestId", "host", "port", "state", "origin", "scope",
        "projectId", "tenantId", "providerId", "providerRevision", "requestedBy", "requestedAt",
    ];
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
    };

    internal static async Task<int> RunAsync(
        string[] args,
        CliRuntimeOptions runtime,
        HttpClient httpClient,
        TextWriter output,
        CancellationToken ct)
    {
        if (args.Length < 3 || args[0] != "egress" || args[1] != "provider-requests" || args[2] != "list")
            throw new CliUsageException(
                "Usage: agentweaver platform egress provider-requests list [--limit <1-100>] [--cursor <cursor>]");

        var options = CliOptions.Parse(args[3..], new HashSet<string>(StringComparer.Ordinal));
        if (options.Many("project-id").Count > 0 || options.Many("run-id").Count > 0)
            throw new CliUsageException(
                "Platform provider host request reads use an unbound identity. Do not set --project-id or --run-id.");
        options.RequireOnly(["limit", "cursor",
            .. CliConfiguration.TargetOptionNames.Where(name => name is not ("project-id" or "run-id"))]);

        var limitValue = DefaultLimit;
        if (options.Single("limit") is { } limit)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out limitValue) ||
                limitValue is < 1 or > MaximumLimit)
                throw new CliUsageException("--limit must be an integer from 1 to 100.");
        }

        var cursor = options.Single("cursor");
        if (cursor is not null && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorLength))
            throw new CliUsageException("--cursor must be a nonempty string of at most 256 characters.");

        var identity = CliConfiguration.ReadIdentity(["projects.admin"], options: options, unbound: true);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);

        JsonObject document;
        try
        {
            document = await gateway.DiscoverAsync(ct, recordEvidence: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GatewayTransportException exception) when (
            exception.InnerException is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Gateway request timed out.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidDataException(
                "The live Gateway does not support the platform provider host request list contract.");
        }

        var operations = GatewayContract.Parse(document);
        if (!operations.TryGetValue(OperationId, out var operation) ||
            operation.Method != "GET" ||
            operation.Path != OperationPath ||
            operation.HasRequestBody ||
            !RequiresBearerOnly(operation.Document["security"] ?? document["security"]) ||
            operation.Parameters.Any(parameter =>
                parameter.Location == "header" &&
                parameter.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)) ||
            !HasExplicit200JsonResponse(operation.Document["responses"]) ||
            operation.Document["x-agentweaver-max-response-bytes"] is not JsonValue maxBytesNode ||
            !maxBytesNode.TryGetValue<int>(out var advertisedMaxBytes) ||
            advertisedMaxBytes is <= 0 or > MaximumResponseBytes ||
            operation.MaximumResponseBytes is <= 0 or > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The live Gateway does not support the platform provider host request list contract.");
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state"] = "requested",
            ["pageSize"] = limitValue.ToString(CultureInfo.InvariantCulture),
        };
        if (cursor is not null)
            query["cursor"] = cursor;

        GatewayResponse response;
        try
        {
            response = await gateway.InvokeAsync(
                OperationId,
                new GatewayInvocation(Query: query),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GatewayTransportException exception) when (
            exception.InnerException is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Gateway request timed out.");
        }
        catch (CliAuthNeedsLoginException)
        {
            throw new InvalidOperationException(
                "Clear AGENTWEAVER_PROJECT_ID and AGENTWEAVER_RUN_ID, then sign in with `agentweaver auth login --scope projects.admin` before listing platform provider host requests.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The caller is not authorized to list platform provider host requests.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The Gateway returned a malformed platform provider host request page.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidOperationException("The platform provider host request list failed.");
        }

        if (response.Status != 200)
            throw new InvalidOperationException(ErrorMessage(response.Status, response.Body, cursor is not null));
        if (response.Evidence["contentType"]?.GetValue<string>() != "application/json")
            throw new InvalidDataException("The Gateway returned a malformed platform provider host request page.");

        var projected = ProjectPage(response.Body, limitValue);
        await output.WriteLineAsync(projected.ToJsonString(OutputJson)).ConfigureAwait(false);
        return 0;
    }

    private static JsonObject ProjectPage(JsonNode? body, int limitValue)
    {
        if (body is not JsonObject page ||
            !HasExactKeys(page, PageKeys) ||
            page["destinations"] is not JsonArray destinations ||
            destinations.Count > limitValue ||
            !NullableCursor(page["nextCursor"]))
        {
            throw new InvalidDataException("The Gateway returned a malformed platform provider host request page.");
        }

        var requests = new JsonArray();
        foreach (var node in destinations)
        {
            if (node is not JsonObject row ||
                !HasExactKeys(row, RowKeys) ||
                !NonEmptyUuid(row["destinationId"]) ||
                !NonEmptyUuid(row["requestId"]) ||
                !NonEmptyBoundedString(row["host"], 253, out var host) ||
                !Integer(row["port"], 1, 65535, out var portValue) ||
                PlatformEgressHostValidator.Validate(host, (int)portValue) is not null ||
                String(row, "state") != "requested" ||
                String(row, "origin") != "modelProvider" ||
                String(row, "scope") is not ("platform" or "project") ||
                !ValidProjectId(row["scope"]!.GetValue<string>(), row["projectId"]) ||
                !NonEmptyBoundedString(row["tenantId"], 256, out _) ||
                !NonEmptyBoundedString(row["providerId"], 128, out _) ||
                !Integer(row["providerRevision"], 1, long.MaxValue, out _) ||
                !NonEmptyBoundedString(row["requestedBy"], 256, out _) ||
                !Timestamp(row["requestedAt"]))
            {
                throw new InvalidDataException("The Gateway returned a malformed platform provider host request page.");
            }

            requests.Add(new JsonObject
            {
                ["host"] = row["host"]!.DeepClone(),
                ["port"] = row["port"]!.DeepClone(),
                ["scope"] = row["scope"]!.DeepClone(),
                ["tenantId"] = row["tenantId"]!.DeepClone(),
                ["projectId"] = row["projectId"]?.DeepClone(),
                ["providerId"] = row["providerId"]!.DeepClone(),
                ["providerRevision"] = row["providerRevision"]!.DeepClone(),
                ["requestedAt"] = row["requestedAt"]!.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["requests"] = requests,
            ["nextCursor"] = page["nextCursor"]?.DeepClone(),
        };
    }

    private static bool ValidProjectId(string scope, JsonNode? projectId) =>
        scope == "platform"
            ? projectId is null
            : NonEmptyBoundedString(projectId, 32, out _);

    private static bool HasExactKeys(JsonObject node, string[] expected) =>
        node.Count == expected.Length && expected.All(node.ContainsKey);

    private static string? String(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool NonEmptyBoundedString(JsonNode? node, int maxLength, out string text)
    {
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var candidate) &&
            !string.IsNullOrWhiteSpace(candidate) &&
            candidate.Length <= maxLength)
        {
            text = candidate;
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static bool NonEmptyUuid(JsonNode? node) =>
        node is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        Guid.TryParseExact(text, "D", out var parsed) &&
        parsed != Guid.Empty;

    private static bool NullableCursor(JsonNode? node) =>
        node is null ||
        node is JsonValue value &&
        value.TryGetValue<string>(out var cursor) &&
        !string.IsNullOrWhiteSpace(cursor) &&
        cursor.Length <= MaximumCursorLength;

    private static readonly string[] Rfc3339Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.fzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
    ];

    private static bool Timestamp(JsonNode? node)
    {
        return node is JsonValue value &&
               value.TryGetValue<string>(out var text) &&
               DateTimeOffset.TryParseExact(
                   text,
                   Rfc3339Formats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out _);
    }

    private static bool Integer(JsonNode? node, long minimum, long maximum, out long value)
    {
        value = default;
        return node is JsonValue scalar &&
               scalar.TryGetValue<long>(out value) &&
               value >= minimum &&
               value <= maximum;
    }

    private static bool RequiresBearerOnly(JsonNode? security)
    {
        return security is JsonArray { Count: 1 } requirements &&
               requirements[0] is JsonObject { Count: 1 } requirement &&
               requirement.First().Key.Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
               requirement.TryGetPropertyValue(requirement.First().Key, out var scopes) &&
               scopes is JsonArray;
    }

    private static bool HasExplicit200JsonResponse(JsonNode? node) =>
        node is JsonObject responses &&
        responses["200"] is JsonObject okResponse &&
        okResponse["content"] is JsonObject content &&
        content.Any(entry => GatewayContract.NormalizeMediaType(entry.Key) == "application/json");

    private static string ErrorMessage(int status, JsonNode? body, bool hasCursor)
    {
        if (hasCursor && status == 400 && body is JsonObject json &&
            String(json, "code") == "invalid_pagination")
            return "The pagination cursor is invalid. Rerun without --cursor to start from the first page.";

        return status switch
        {
            400 => "The Gateway rejected the platform provider host request query (400).",
            401 => "Sign-in is required to list platform provider host requests (401).",
            403 => "The caller is not authorized to list platform provider host requests (403).",
            503 => "Platform provider host request verification is unavailable (503).",
            _ => $"The platform provider host request list failed with HTTP {status}.",
        };
    }
}
