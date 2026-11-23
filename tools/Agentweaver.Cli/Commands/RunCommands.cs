using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

internal static class RunCommands
{
    private const string OperationId = "readRunEgress";
    private const string OperationPath = "/api/v1/runs/{runId}/egress";
    private const int MaximumItems = 100;
    private const int MaximumCursorLength = 2048;
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
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Length == 0 || args[0] != "egress")
            throw new CliUsageException("Choose `agentweaver run egress <runId>`.");
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            throw new CliUsageException("Usage: agentweaver run egress <runId>");

        var runId = args[1];
        var options = CliOptions.Parse(args[2..], new HashSet<string>(StringComparer.Ordinal));
        if (options.Many("project-id").Count > 0 || options.Many("run-id").Count > 0)
            throw new CliUsageException("Run egress reads use an unbound identity. Do not set --project-id or --run-id.");
        options.RequireOnly(["limit", "cursor", "scope",
            .. CliConfiguration.TargetOptionNames.Where(name => name is not ("project-id" or "run-id"))]);

        var scope = options.Single("scope");
        if (scope is not null && scope != "projects.admin")
            throw new CliUsageException("--scope must be `projects.admin` when supplied.");
        var requestedScopes = scope is null ? null : new[] { "projects.admin" };

        var limit = options.Single("limit");
        if (limit is not null &&
            (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var limitValue) ||
             limitValue is < 1 or > MaximumItems))
            throw new CliUsageException("--limit must be an integer from 1 to 100.");

        var identity = CliConfiguration.ReadIdentity(requestedScopes, options: options, unbound: true);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);
        var document = await gateway.DiscoverAsync(ct).ConfigureAwait(false);
        var operations = GatewayContract.Parse(document);
        if (!operations.TryGetValue(OperationId, out var operation) ||
            operation.Method != "GET" ||
            operation.Path != OperationPath ||
            operation.HasRequestBody ||
            !RequiresBearerOnly(operation.Document["security"] ?? document["security"]) ||
            operation.Parameters.Any(parameter =>
                parameter.Location == "header" &&
                parameter.Name.Equals(
                    "X-Agentweaver-Project-Authorization",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The live Gateway does not support the run egress read contract.");
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (limit is not null)
            query["limit"] = limit;
        if (options.Single("cursor") is { } cursor)
            query["cursor"] = cursor;
        GatewayResponse response;
        try
        {
            response = await gateway.InvokeAsync(
                OperationId,
                new GatewayInvocation(
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = runId },
                    query),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CliAuthNeedsLoginException)
        {
            throw new InvalidOperationException(scope == "projects.admin"
                ? "Sign in with `agentweaver auth login --scope projects.admin` before reading run egress."
                : "Sign in with `agentweaver auth login` before reading run egress.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidOperationException("The run egress request failed.");
        }

        if (response.Status != 200)
            throw new InvalidOperationException(ErrorMessage(response.Status, response.Body));
        var page = ParsePage(response.Body, runId);
        await output.WriteLineAsync(page.ToJsonString(OutputJson)).ConfigureAwait(false);
        if (page["coverage"]!.GetValue<string>() == "Unavailable")
        {
            await error.WriteLineAsync(
                "Egress coverage is unavailable; the page contains only stored attributed flows.")
                .ConfigureAwait(false);
        }
        return 0;
    }

    private static JsonObject ParsePage(JsonNode? body, string expectedRunId)
    {
        if (body is not JsonObject page ||
            !HasExactKeys(page, ["runId", "coverage", "delayedUntil", "items", "nextCursor"]) ||
            String(page, "runId") != expectedRunId ||
            String(page, "coverage") != "Unavailable" ||
            !NullableTimestamp(page["delayedUntil"]) ||
            page["items"] is not JsonArray items ||
            items.Count > MaximumItems ||
            !NullableCursor(page["nextCursor"]))
        {
            throw new InvalidDataException("The Gateway returned a malformed run egress page.");
        }

        var normalizedItems = new JsonArray();
        foreach (var node in items)
        {
            if (node is not JsonObject item ||
                !HasExactKeys(item, ["host", "address", "port", "verdict", "firstSeen", "lastSeen", "count"]) ||
                !NullableString(item["host"]) ||
                !NullableString(item["address"]) ||
                !Integer(item["port"], 1, 65535, out _) ||
                String(item, "verdict") is not ("allowed" or "denied") ||
                !Timestamp(item["firstSeen"], out _) ||
                !Timestamp(item["lastSeen"], out _) ||
                !Integer(item["count"], 1, long.MaxValue, out _))
            {
                throw new InvalidDataException("The Gateway returned a malformed run egress page.");
            }
            normalizedItems.Add(item.DeepClone());
        }

        return new JsonObject
        {
            ["runId"] = page["runId"]!.DeepClone(),
            ["coverage"] = page["coverage"]!.DeepClone(),
            ["delayedUntil"] = page["delayedUntil"]?.DeepClone(),
            ["items"] = normalizedItems,
            ["nextCursor"] = page["nextCursor"]?.DeepClone(),
        };
    }

    private static bool HasExactKeys(JsonObject node, string[] expected)
    {
        if (node.Count != expected.Length)
            return false;
        return expected.All(node.ContainsKey);
    }

    private static string? String(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool NullableString(JsonNode? node) =>
        node is null || node is JsonValue value && value.TryGetValue<string>(out _);

    private static bool NullableTimestamp(JsonNode? node) =>
        node is null || Timestamp(node, out _);

    private static bool NullableCursor(JsonNode? node) =>
        node is null ||
        node is JsonValue value && value.TryGetValue<string>(out var cursor) &&
        cursor.Length > 0 && cursor.Length <= MaximumCursorLength;

    private static bool Timestamp(JsonNode? node, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return node is JsonValue value &&
               value.TryGetValue<string>(out var text) &&
               text.Length is > 0 and <= 33 &&
               text == text.Trim() &&
               text.Contains('T', StringComparison.Ordinal) &&
               (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-') &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
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

    private static string ErrorMessage(int status, JsonNode? body)
    {
        var code = status == 404 &&
                   body is JsonObject json &&
                   GatewayContract.String(json, "code") == "run_not_found"
            ? " (run_not_found)"
            : string.Empty;
        return status switch
        {
            400 => $"The Gateway rejected the run egress request (400){code}.",
            401 => $"Sign-in is required for the run egress request (401){code}.",
            403 => $"The caller cannot read this run's egress (403){code}.",
            404 => $"The run was not found (404){code}.",
            503 => $"Stored run egress is unavailable (503){code}.",
            _ => $"The run egress request failed with HTTP {status}{code}.",
        };
    }
}
