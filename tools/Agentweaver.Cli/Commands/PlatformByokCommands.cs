using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli;

internal static class PlatformByokCommands
{
    private const int PageSize = 100;
    private const int MaximumSaveBodyBytes = 32 * 1024;
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private const string PlatformPath = "/api/v1/platform/byok-providers";
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static async Task<int> RunAsync(
        string[] args,
        CliRuntimeOptions runtime,
        HttpClient httpClient,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Length < 2 || args[0] != "byok")
            throw new CliUsageException("Choose `agentweaver platform byok list|show|add|update`.");

        var command = args[1];
        if (command is not ("list" or "show" or "add" or "update"))
            throw new CliUsageException("Choose `list`, `show`, `add`, or `update` for `platform byok`.");

        var hasProviderId = command is "show" or "update";
        var optionStart = hasProviderId ? 3 : 2;
        if (args.Length < optionStart ||
            hasProviderId &&
            (string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("--", StringComparison.Ordinal)))
        {
            throw new CliUsageException(command switch
            {
                "show" => "Usage: agentweaver platform byok show <providerId> [target options]",
                "update" => "Usage: agentweaver platform byok update <providerId> --expected-revision <revision> --name <name> --type <type> --base-url <url> --model <model> [metadata options]",
                "add" => "Usage: agentweaver platform byok add --name <name> --type <type> --base-url <url> --model <model> [metadata options]",
                _ => "Usage: agentweaver platform byok list [target options]",
            });
        }

        var options = CliOptions.Parse(args[optionStart..], new HashSet<string>(StringComparer.Ordinal));
        var commandOptions = command switch
        {
            "list" or "show" => Array.Empty<string>(),
            "add" => SaveOptions,
            _ => SaveOptions.Concat(["expected-revision"]).ToArray(),
        };
        options.RequireOnly(commandOptions.Concat(
            CliConfiguration.TargetOptionNames.Where(name => name is not ("project-id" or "run-id"))).ToArray());

        var providerId = hasProviderId ? args[2] : null;
        var identity = CliConfiguration.ReadIdentity(
            ["projects.admin"],
            options: options,
            unbound: true);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);
        var operations = GatewayContract.Parse(await gateway.DiscoverAsync(ct).ConfigureAwait(false));

        if (command == "list")
        {
            var result = await ListAsync(gateway, operations, ct).ConfigureAwait(false);
            await WriteJsonAsync(output, result).ConfigureAwait(false);
            return 0;
        }

        var operation = command switch
        {
            "show" => ("readPlatformByokProviderDraft", "GET", PlatformPath + "/{providerId}", false),
            "add" => ("createPlatformByokProviderDraft", "POST", PlatformPath, true),
            "update" => ("updatePlatformByokProviderDraft", "PUT", PlatformPath + "/{providerId}", true),
            _ => throw new CliUsageException("Choose `list`, `show`, `add`, or `update` for `platform byok`."),
        };
        var gatewayOperation = RequireOperation(operations, operation.Item1, operation.Item2, operation.Item3, operation.Item4);

        GatewayInvocation invocation;
        Guid? operationId = null;
        if (command is "add" or "update")
        {
            operationId = ReadOperationId(options.Single("operation-id"));
            var expectedRevision = command == "add" ? 0 : ReadExpectedRevision(options.Single("expected-revision"));
            var metadata = ReadMetadata(options);
            invocation = new GatewayInvocation(
                command == "update"
                    ? new Dictionary<string, string>(StringComparer.Ordinal) { ["providerId"] = providerId! }
                    : null,
                Body: SaveBody(operationId.Value, expectedRevision, metadata));
        }
        else
        {
            invocation = new GatewayInvocation(
                PathParameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["providerId"] = providerId! });
        }

        GatewayResponse response;
        try
        {
            response = await InvokeAsync(gateway, operation.Item1, invocation, command, operationId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && operationId is { } replayId)
        {
            await error.WriteLineAsync(UnknownWriteMessage(replayId)).ConfigureAwait(false);
            return 130;
        }
        EnsureSuccess(response, command, operationId);
        var projected = ProjectProvider(response.Body, command == "update" ? providerId : null);
        await WriteJsonAsync(output, projected).ConfigureAwait(false);
        return 0;
    }

    private static readonly string[] SaveOptions =
    [
        "name", "type", "base-url", "model", "wire-api", "azure-api-version",
        "reasoning-effort", "prompt-capacity-tokens", "operation-id",
    ];

    private static async Task<JsonObject> ListAsync(
        IGatewayClient gateway,
        IReadOnlyDictionary<string, GatewayOperation> operations,
        CancellationToken ct)
    {
        const string operationId = "listPlatformByokProviderDrafts";
        RequireOperation(operations, operationId, "GET", PlatformPath, bodyRequired: false);
        var providers = new JsonArray();
        var seenProviderIds = new HashSet<string>(StringComparer.Ordinal);
        string? afterProviderId = null;

        while (true)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
            };
            if (afterProviderId is not null)
                query["afterProviderId"] = afterProviderId;

            var response = await InvokeAsync(
                gateway,
                operationId,
                new GatewayInvocation(Query: query),
                "list",
                operationIdForReplay: null,
                ct: ct).ConfigureAwait(false);
            EnsureSuccess(response, "list", operationId: null);
            if (response.Body is not JsonObject page ||
                page["providers"] is not JsonArray pageProviders ||
                pageProviders.Count > PageSize ||
                !page.ContainsKey("nextAfterProviderId"))
            {
                throw UnexpectedResponse();
            }

            string? lastProviderId = null;
            foreach (var provider in pageProviders)
            {
                var projected = ProjectProvider(provider);
                var currentProviderId = projected["providerId"]!.GetValue<string>();
                if (!seenProviderIds.Add(currentProviderId))
                    throw UnexpectedResponse();
                lastProviderId = currentProviderId;
                providers.Add(projected);
            }

            var nextAfterProviderId = OptionalString(page, "nextAfterProviderId");
            if (nextAfterProviderId is not null &&
                (lastProviderId is null ||
                 !string.Equals(lastProviderId, nextAfterProviderId, StringComparison.Ordinal) ||
                 !seenProviderIds.Contains(nextAfterProviderId)))
            {
                throw UnexpectedResponse();
            }

            if (nextAfterProviderId is null)
                return new JsonObject { ["providers"] = providers };
            afterProviderId = nextAfterProviderId;
        }
    }

    private static async Task<GatewayResponse> InvokeAsync(
        IGatewayClient gateway,
        string operationId,
        GatewayInvocation invocation,
        string command,
        Guid? operationIdForReplay,
        CancellationToken ct)
    {
        try
        {
            return await gateway.InvokeAsync(operationId, invocation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidOperationException(operationIdForReplay is { } replayId
                ? UnknownWriteMessage(replayId)
                : $"The platform BYOK {command} request failed.");
        }
    }

    private static string UnknownWriteMessage(Guid operationId) =>
        $"The write result is unknown. Repeat the same command with --operation-id {operationId:D} and the same fields to replay it.";

    private static void EnsureSuccess(GatewayResponse response, string command, Guid? operationId)
    {
        if (response.Status == 200)
            return;

        var code = OptionalString(response.Body as JsonObject, "code");
        if (response.Status == 409 && code == "model_provider_draft_conflict")
        {
            throw new InvalidOperationException(
                "The provider revision changed or the operation ID was used with different fields. Read the current provider; use a new operation ID for a new update.");
        }
        if (response.Status == 409 && code == "model_provider_draft_metadata_requires_update")
        {
            var unsafeProviderId = OptionalString(response.Body as JsonObject, "id");
            var revision = OptionalPositiveInt64(response.Body as JsonObject, "revision");
            if (unsafeProviderId is not null && ValidProviderId(unsafeProviderId) && revision is { } currentRevision)
            {
                throw new InvalidOperationException(
                    $"Provider {unsafeProviderId} at revision {currentRevision} has metadata that requires an update. Use `platform byok update {unsafeProviderId} --expected-revision {currentRevision}` with corrected metadata.");
            }
            throw new InvalidOperationException("The platform BYOK provider metadata requires an update.");
        }
        if (response.Status >= 500 && operationId is { } replayId)
        {
            throw new InvalidOperationException(UnknownWriteMessage(replayId));
        }
        if (response.Status == 403)
            throw new InvalidOperationException("The Gateway denied platform BYOK access. The account needs current PlatformAdmin authority.");
        if (response.Status == 404)
            throw new InvalidOperationException("The platform BYOK provider was not found.");
        throw new InvalidOperationException($"The platform BYOK {command} request failed with HTTP {response.Status}.");
    }

    private static GatewayOperation RequireOperation(
        IReadOnlyDictionary<string, GatewayOperation> operations,
        string operationId,
        string method,
        string path,
        bool bodyRequired)
    {
        if (!operations.TryGetValue(operationId, out var operation) ||
            operation.Method != method ||
            operation.Path != path ||
            operation.HasRequestBody != bodyRequired ||
            operation.BodyRequired != bodyRequired ||
            operation.MaximumResponseBytes > MaximumPageBytes ||
            bodyRequired && operation.MaximumRequestBytes > MaximumSaveBodyBytes ||
            operation.Parameters.Any(parameter =>
                parameter.Location == "header" &&
                parameter.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The Gateway does not support the expected platform BYOK operation.");
        }

        return operation;
    }

    private static JsonObject SaveBody(Guid operationId, long expectedRevision, ProviderMetadata metadata)
    {
        var reasoningEfforts = new JsonArray();
        foreach (var effort in metadata.ReasoningEfforts)
            reasoningEfforts.Add(effort);

        return new JsonObject
        {
            ["operationId"] = operationId.ToString("D"),
            ["expectedRevision"] = expectedRevision,
            ["name"] = metadata.Name,
            ["type"] = metadata.Type,
            ["base_url"] = metadata.BaseUrl,
            ["model"] = metadata.Model,
            ["wire_api"] = metadata.WireApi,
            ["azure_api_version"] = metadata.AzureApiVersion,
            ["reasoning_efforts"] = reasoningEfforts,
            ["prompt_capacity_tokens"] = metadata.PromptCapacityTokens,
        };
    }

    private static ProviderMetadata ReadMetadata(CliOptions options)
    {
        static string Required(CliOptions parsed, string name)
        {
            var value = parsed.Single(name);
            return string.IsNullOrWhiteSpace(value)
                ? throw new CliUsageException($"Option --{name} requires a non-empty value.")
                : value;
        }

        int? promptCapacityTokens = null;
        var promptCapacityText = options.Single("prompt-capacity-tokens");
        if (promptCapacityText is not null)
        {
            if (!int.TryParse(promptCapacityText, NumberStyles.None, CultureInfo.InvariantCulture, out var promptCapacity) ||
                promptCapacity <= 0)
                throw new CliUsageException("--prompt-capacity-tokens must be a positive integer.");
            promptCapacityTokens = promptCapacity;
        }

        return new ProviderMetadata(
            Required(options, "name"),
            Required(options, "type"),
            Required(options, "base-url"),
            Required(options, "model"),
            options.Single("wire-api"),
            options.Single("azure-api-version"),
            options.Many("reasoning-effort"),
            promptCapacityTokens);
    }

    private static Guid ReadOperationId(string? value)
    {
        if (value is null)
            return Guid.NewGuid();
        if (!Guid.TryParse(value, out var operationId) || operationId == Guid.Empty)
            throw new CliUsageException("--operation-id must be a non-empty GUID.");
        return operationId;
    }

    private static long ReadExpectedRevision(string? value)
    {
        if (value is null ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 1 ||
            revision == long.MaxValue)
        {
            throw new CliUsageException("--expected-revision must be a positive Int64 below Int64.MaxValue.");
        }

        return revision;
    }

    private static JsonObject ProjectProvider(JsonNode? body, string? expectedProviderId = null)
    {
        if (body is not JsonObject provider)
            throw UnexpectedResponse();

        var providerId = RequiredString(provider, "id");
        if (!ValidProviderId(providerId) ||
            expectedProviderId is not null && providerId != expectedProviderId ||
            RequiredString(provider, "scope") != "platform" ||
            provider.TryGetPropertyValue("projectId", out var projectId) && projectId is not null)
        {
            throw UnexpectedResponse();
        }

        var reasoningEfforts = new JsonArray();
        if (provider["reasoning_efforts"] is not JsonArray efforts)
            throw UnexpectedResponse();
        foreach (var effort in efforts)
        {
            if (effort is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null)
                throw UnexpectedResponse();
            reasoningEfforts.Add(text);
        }

        var promptCapacityTokens = provider["prompt_capacity_tokens"];
        if (promptCapacityTokens is not null &&
            (promptCapacityTokens is not JsonValue capacityValue ||
             !capacityValue.TryGetValue<int>(out var capacity) ||
             capacity <= 0))
        {
            throw UnexpectedResponse();
        }

        return new JsonObject
        {
            ["providerId"] = providerId,
            ["name"] = RequiredString(provider, "name"),
            ["type"] = RequiredString(provider, "type"),
            ["baseUrl"] = RequiredString(provider, "base_url"),
            ["model"] = RequiredString(provider, "model"),
            ["wireApi"] = OptionalString(provider, "wire_api"),
            ["azureApiVersion"] = OptionalString(provider, "azure_api_version"),
            ["reasoningEfforts"] = reasoningEfforts,
            ["promptCapacityTokens"] = promptCapacityTokens?.DeepClone(),
            ["revision"] = PositiveInt64(provider, "revision"),
        };
    }

    private static string RequiredString(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<string>(out var result) && result is not null
            ? result
            : throw UnexpectedResponse();

    private static string? OptionalString(JsonObject? value, string name)
    {
        if (value is null || !value.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        return node is JsonValue json && json.TryGetValue<string>(out var result)
            ? result ?? throw UnexpectedResponse()
            : throw UnexpectedResponse();
    }

    private static long PositiveInt64(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<long>(out var result) && result > 0
            ? result
            : throw UnexpectedResponse();

    private static long? OptionalPositiveInt64(JsonObject? value, string name)
    {
        if (value is null || !value.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        return node is JsonValue json && json.TryGetValue<long>(out var result) && result > 0
            ? result
            : null;
    }

    private static bool ValidProviderId(string value) =>
        value.Length is > 0 and <= 128 &&
        value is not ("." or "..") &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static InvalidDataException UnexpectedResponse() =>
        new("The Gateway returned an invalid platform BYOK response.");

    private static Task WriteJsonAsync(TextWriter output, JsonObject result) =>
        output.WriteLineAsync(result.ToJsonString(OutputJson));

    private sealed record ProviderMetadata(
        string Name,
        string Type,
        string BaseUrl,
        string Model,
        string? WireApi,
        string? AzureApiVersion,
        IReadOnlyList<string> ReasoningEfforts,
        int? PromptCapacityTokens);
}
