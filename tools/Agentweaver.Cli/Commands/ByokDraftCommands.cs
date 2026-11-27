using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli;

internal static class ByokDraftCommands
{
    private const int PageSize = 100;
    private const int MaximumSaveBodyBytes = 64 * 1024;
    private const int MaximumPageBytes = 8 * 1024 * 1024;
    private const string PlatformPath = "/api/v1/platform/byok-providers";
    private const string ProjectPath = "/api/v1/projects/{projectId}/byok-providers";
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string[] SaveOptions =
    [
        "name", "type", "base-url", "model", "wire-api", "azure-api-version",
        "reasoning-effort", "prompt-capacity-tokens", "operation-id",
        "rate-card-id", "rate-card-version",
    ];

    internal static async Task<int> RunAsync(
        string[] args,
        CliRuntimeOptions runtime,
        HttpClient httpClient,
        TextWriter output,
        TextWriter error,
        bool projectScope,
        CancellationToken ct)
    {
        if (args.Length < 2 || args[0] != "byok")
            throw new CliUsageException("Choose a BYOK command.");

        var scopeName = projectScope ? "project" : "platform";
        var command = args[1];
        if (!SupportedCommands(projectScope).Contains(command, StringComparer.Ordinal))
            throw new CliUsageException($"Unknown {scopeName} BYOK command: {command}.");

        var hasProviderId = command is "show" or "update" or "set-key" or "recheck-key" or "disable-key" or "activate";
        var hasRateCardIdAndVersion = command == "save-prices";
        var positionalCount = hasRateCardIdAndVersion ? 2 : hasProviderId ? 1 : 0;
        var optionStart = 2 + positionalCount;
        if (args.Length < optionStart ||
            args.Skip(2).Take(positionalCount).Any(value =>
                string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new CliUsageException(Usage(scopeName, command));
        }

        var optionArgs = args[optionStart..];
        if (optionArgs.Any(arg => arg.StartsWith("--credential-status=", StringComparison.Ordinal)))
            throw new CliUsageException(Usage(scopeName, command));

        CliOptions options;
        try
        {
            options = CliOptions.Parse(
                optionArgs,
                new HashSet<string>(StringComparer.Ordinal) { "from-stdin", "credential-status" });
        }
        catch (CliUsageException) when (optionArgs.Contains("--credential-status", StringComparer.Ordinal))
        {
            throw new CliUsageException(Usage(scopeName, command));
        }

        var commandOptions = command switch
        {
            "list" => [],
            "show" => ["credential-status", "operation-id"],
            "prices" => ["region", "search"],
            "save-prices" => ["source", "input"],
            "add" => SaveOptions,
            "update" => SaveOptions.Concat(["expected-revision"]).ToArray(),
            "set-key" => ["from-stdin", "expected-revision", "operation-id"],
            "recheck-key" => ["expected-revision", "expected-credential-revision", "operation-id"],
            "disable-key" => ["expected-revision", "expected-credential-revision", "operation-id"],
            "activate" => ["expected-revision", "expected-settings-revision"],
            "deactivate" => ["expected-settings-revision"],
            _ => throw new CliUsageException($"Unknown {scopeName} BYOK command: {command}."),
        };
        var targetOptions = CliConfiguration.TargetOptionNames.Where(name =>
            projectScope ? name != "run-id" : name is not ("project-id" or "run-id"));
        options.RequireOnly(commandOptions.Concat(targetOptions).ToArray());

        var credentialStatus = command == "show" && options.HasFlag("credential-status");
        var rawOperationId = command == "show" ? options.Single("operation-id") : null;
        if (credentialStatus && rawOperationId is not null)
            throw new CliUsageException(Usage(scopeName, command));
        Guid? lookupOperationId = rawOperationId is not null
            ? RequiredOperationId(rawOperationId)
            : null;
        var identity = CliConfiguration.ReadIdentity(
            ["projects.admin"],
            options: options,
            unbound: !projectScope,
            runless: projectScope);
        var projectId = projectScope
            ? identity.ProjectId
                ?? throw new CliUsageException("Set --project-id or AGENTWEAVER_PROJECT_ID for project BYOK.")
            : null;
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);
        var document = await gateway.DiscoverAsync(ct).ConfigureAwait(false);
        var operations = GatewayContract.Parse(document);
        var contracts = BuildContracts(projectScope, projectId, command, args, credentialStatus, lookupOperationId is not null);
        foreach (var contract in contracts)
            RequireContract(document, operations, contract, scopeName);

        if (command == "list")
        {
            var result = await ListAsync(
                gateway, projectScope ? ProjectPath : PlatformPath,
                projectScope ? "project" : "platform", projectId, scopeName, ct).ConfigureAwait(false);
            await WriteJsonAsync(output, result).ConfigureAwait(false);
            return 0;
        }

        if (command == "set-key")
        {
            return await RunSetKeyAsync(
                gateway, runtime, output, error, projectScope, projectId, args[2], options, ct)
                .ConfigureAwait(false);
        }

        var invocation = BuildInvocation(command, projectScope, projectId, args, options, out var operationId, lookupOperationId);
        var receiptOperation = contractForCommand(contracts, command);
        GatewayResponse response;
        try
        {
            response = await InvokeAsync(
                gateway, receiptOperation.OperationId, invocation, scopeName, command, operationId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync(
                operationId is { } id
                    ? UnknownWriteMessage(id)
                    : "Operation cancelled.").ConfigureAwait(false);
            return 130;
        }

        EnsureSuccess(response, scopeName, command, operationId,
            command == "show" ? args[2] : null, credentialStatus, lookupOperationId is not null);
        if ((credentialStatus || lookupOperationId is not null) && response.Status != 200)
            throw UnexpectedResponse();
        var projected = command switch
        {
            "show" when credentialStatus => ProjectCredentialStatus(
                response.Body, projectScope ? "project" : "platform", projectId, args[2]),
            "show" when lookupOperationId is { } opId => ProjectOperationStatus(
                response.Body, projectScope ? "project" : "platform", projectId, args[2], opId),
            "show" or "add" or "update" => ProjectProvider(
                response.Body,
                projectScope ? "project" : "platform",
                projectId,
                command == "show" || command == "update" ? args[2] : null),
            "prices" => ProjectPriceLookup(response.Body),
            "save-prices" => ProjectRateCard(response.Body, args[2], args[3]),
            "recheck-key" or "disable-key" => ProjectCredentialReceipt(
                response.Body, projectScope ? "project" : "platform", projectId, args[2], operationId!.Value,
                command == "recheck-key" ? "published" : "disabled"),
            "activate" or "deactivate" => ProjectSwitchReceipt(
                response.Body, command == "activate" ? args[2] : null),
            _ => throw new InvalidOperationException($"Unknown {scopeName} BYOK command: {command}."),
        };
        await WriteJsonAsync(output, projected).ConfigureAwait(false);
        return 0;
    }

    private static string[] SupportedCommands(bool projectScope) =>
        projectScope
            ? ["list", "show", "add", "update", "prices", "set-key", "recheck-key", "disable-key"]
            : ["list", "show", "add", "update", "save-prices", "set-key", "recheck-key", "disable-key", "activate", "deactivate"];

    private static string Usage(string scope, string command) =>
        command switch
        {
            "prices" => $"Usage: agentweaver {scope} byok prices --project-id <projectId> --region <region> --search <meter-text>",
            "save-prices" => $"Usage: agentweaver {scope} byok save-prices <id> <version> --source manual|azure-retail --input <file.json>",
            "set-key" => scope == "project"
                ? "Usage: agentweaver project byok set-key <providerId> --project-id <projectId> --from-stdin --expected-revision <providerRevision> --operation-id <uuid>"
                : "Usage: agentweaver platform byok set-key <providerId> --from-stdin --expected-revision <providerRevision> --operation-id <uuid>",
            "activate" => $"Usage: agentweaver {scope} byok activate <providerId> --expected-revision <providerRevision> --expected-settings-revision <settingsRevision>",
            "deactivate" => $"Usage: agentweaver {scope} byok deactivate --expected-settings-revision <settingsRevision>",
            "recheck-key" => $"Usage: agentweaver {scope} byok recheck-key <providerId> --expected-revision <providerRevision> --expected-credential-revision <credentialRevision> --operation-id <uuid>",
            "disable-key" => $"Usage: agentweaver {scope} byok disable-key <providerId> --expected-revision <providerRevision> --expected-credential-revision <credentialRevision> --operation-id <uuid>",
            "show" => scope == "project"
                ? "Usage: agentweaver project byok show <providerId> --project-id <projectId> [--credential-status | --operation-id <uuid>] [target options]"
                : "Usage: agentweaver platform byok show <providerId> [--credential-status | --operation-id <uuid>] [target options]",
            "update" => $"Usage: agentweaver {scope} byok update <providerId> --expected-revision <revision> --name <name> --type <type> --base-url <url> --model <model> [metadata options]",
            "add" => $"Usage: agentweaver {scope} byok add --name <name> --type <type> --base-url <url> --model <model> [metadata options]",
            _ => $"Usage: agentweaver {scope} byok {command} [target options]",
        };

    private static IReadOnlyList<OperationContract> BuildContracts(
        bool projectScope,
        string? projectId,
        string command,
        string[] args,
        bool credentialStatus = false,
        bool operationStatus = false)
    {
        var prefix = projectScope ? "Project" : "Platform";
        var path = projectScope ? ProjectPath : PlatformPath;
        return command switch
        {
            "list" => [Operation($"list{prefix}ByokProviderDrafts", "GET", path, null, null,
                [..ProjectPathParameters(projectScope), "pageSize", "afterProviderId"],
                ["200:ModelProviderDraftPage"], 4 * 1024 * 1024)],
            "show" when credentialStatus => [Operation($"read{prefix}ByokProviderCredential", "GET",
                path + "/{providerId}/credential", null, null,
                [..ProjectPathParameters(projectScope), "providerId"],
                ["200:ModelProviderCredentialStatusResponse"], 16 * 1024)],
            "show" when operationStatus => [Operation($"read{prefix}ByokProviderCredentialOperation", "GET",
                path + "/{providerId}/credential/operations/{operationId}", null,
                "ModelProviderCredentialOperationStatusResponse",
                [..ProjectPathParameters(projectScope), "providerId", "operationId"],
                ["200:ModelProviderCredentialOperationStatusResponse"], 16 * 1024)],
            "show" => [Operation($"read{prefix}ByokProviderDraft", "GET", path + "/{providerId}", null, null,
                [..ProjectPathParameters(projectScope), "providerId"], ["200:ModelProviderDraft"], 32 * 1024)],
            "add" => [Operation($"create{prefix}ByokProviderDraft", "POST", path,
                projectScope ? "ModelProviderDraftProjectCreateRequest" : "ModelProviderDraftPlatformCreateRequest",
                "ModelProviderDraft", ProjectPathParameters(projectScope), ["200:ModelProviderDraft"], 32 * 1024, 32 * 1024)],
            "update" => [Operation($"update{prefix}ByokProviderDraft", "PUT", path + "/{providerId}",
                "ModelProviderDraftSaveRequest", "ModelProviderDraft",
                [..ProjectPathParameters(projectScope), "providerId"], ["200:ModelProviderDraft"], 32 * 1024, 32 * 1024)],
            "prices" => [Operation("searchProjectAzureRetailPrices", "GET",
                "/api/v1/projects/{projectId}/model-pricing/azure-retail", null, null,
                ["projectId", "region", "search"], ["200:AzureRetailPriceLookupResult"], 8 * 1024 * 1024)],
            "save-prices" => ReadPricingSource(args) switch
            {
                "manual" => [Operation("savePlatformManualRuntimeRateCard", "PUT",
                    "/api/v1/platform/model-pricing/rate-cards/{id}/{version}",
                    "ManualRuntimeRateCardWriteRequest", "RuntimeRateCard", ["id", "version"],
                    ["200:RuntimeRateCard", "201:RuntimeRateCard"], 256 * 1024, 64 * 1024)],
                "azure-retail" => [Operation("savePlatformAzureRetailRuntimeRateCard", "PUT",
                    "/api/v1/platform/model-pricing/azure-retail/rate-cards/{id}/{version}",
                    "AzureRetailRuntimeRateCardWriteRequest", "AzureRetailRuntimeRateCard", ["id", "version"],
                    ["200:AzureRetailRuntimeRateCard", "201:AzureRetailRuntimeRateCard"], 256 * 1024, 64 * 1024)],
                _ => throw new CliUsageException("--source must be manual or azure-retail."),
            },
            "set-key" =>
            [
                Operation($"read{prefix}ByokProviderDraft", "GET", path + "/{providerId}",
                    null, "ModelProviderDraft", [..ProjectPathParameters(projectScope), "providerId"],
                    ["200:ModelProviderDraft"], 32 * 1024),
                Operation($"read{prefix}ByokProviderCredentialOperation", "GET",
                    path + "/{providerId}/credential/operations/{operationId}", null,
                    "ModelProviderCredentialOperationStatusResponse",
                    [..ProjectPathParameters(projectScope), "providerId", "operationId"],
                    ["200:ModelProviderCredentialOperationStatusResponse"], 16 * 1024),
                Operation($"set{prefix}ByokProviderCredential", "PUT",
                    path + "/{providerId}/credential", "ModelProviderCredentialPutRequest",
                    "ModelProviderCredentialOperationResponse",
                    [..ProjectPathParameters(projectScope), "providerId", "If-Match", "Idempotency-Key"],
                    ["200:ModelProviderCredentialOperationResponse"], 16 * 1024, 16 * 1024,
                    requestMediaTypes: ["application/octet-stream"]),
            ],
            "recheck-key" => [Operation($"set{prefix}ByokProviderCredential", "PUT",
                path + "/{providerId}/credential", "ModelProviderCredentialPutRequest",
                "ModelProviderCredentialOperationResponse",
                [..ProjectPathParameters(projectScope), "providerId", "If-Match", "Idempotency-Key"],
                ["200:ModelProviderCredentialOperationResponse"], 16 * 1024, 16 * 1024)],
            "disable-key" => [Operation($"disable{prefix}ByokProviderCredential", "DELETE",
                path + "/{providerId}/credential", "ModelProviderCredentialDeleteRequest",
                "ModelProviderCredentialOperationResponse",
                [..ProjectPathParameters(projectScope), "providerId", "operationId", "expectedProviderRevision",
                    "expectedCredentialRevision", "cancel"],
                ["200:ModelProviderCredentialOperationResponse"], 16 * 1024, 8 * 1024,
                bodyRequired: false)],
            "activate" => [Operation("activatePlatformByokProviderDraft", "POST",
                PlatformPath + "/{providerId}/activate", "ActivatePlatformModelProviderRequest",
                "PlatformModelDefaultSwitchResult", ["providerId"],
                ["200:PlatformModelDefaultSwitchResult"], 8 * 1024, 8 * 1024)],
            "deactivate" => [Operation("deactivatePlatformByokProviderDraft", "POST",
                PlatformPath + "/deactivate", "DeactivatePlatformModelProviderRequest",
                "PlatformModelDefaultSwitchResult", [],
                ["200:PlatformModelDefaultSwitchResult"], 8 * 1024, 8 * 1024)],
            _ => throw new CliUsageException($"Unknown {(projectScope ? "project" : "platform")} BYOK command: {command}."),
        };
    }

    private static OperationContract contractForCommand(
        IReadOnlyList<OperationContract> contracts,
        string command) =>
        contracts.Single();

    private static string[] ProjectPathParameters(bool projectScope) =>
        projectScope ? ["projectId"] : [];

    private static OperationContract Operation(
        string operationId,
        string method,
        string path,
        string? requestSchema,
        string? responseSchema,
        string[] parameterNames,
        string[] responses,
        int maximumResponseBytes,
        int maximumRequestBytes = 1024 * 1024,
        bool? bodyRequired = null,
        string[]? requestMediaTypes = null) =>
        new(operationId, method, path, requestSchema, responseSchema, parameterNames, responses,
            maximumRequestBytes, maximumResponseBytes, bodyRequired ?? requestSchema is not null,
            requestMediaTypes);

    private static void RequireContract(
        JsonObject document,
        IReadOnlyDictionary<string, GatewayOperation> operations,
        OperationContract contract,
        string scopeName)
    {
        if (!operations.TryGetValue(contract.OperationId, out var operation))
            throw ContractDrift(scopeName);
        if (operation.Method != contract.Method)
            throw ContractDrift(scopeName);
        if (operation.Path != contract.Path)
            throw ContractDrift(scopeName);
        if (operation.HasRequestBody != (contract.RequestSchema is not null))
            throw ContractDrift(scopeName);
        if (operation.BodyRequired != contract.BodyRequired)
            throw ContractDrift(scopeName);
        if (operation.MaximumRequestBytes != contract.MaximumRequestBytes ||
            operation.MaximumResponseBytes != contract.MaximumResponseBytes)
            throw ContractDrift(scopeName);
        if (operation.Parameters.Count(parameter =>
                parameter.Location == "header" && parameter.Name == "X-Agentweaver-Tenant" && !parameter.Required) != 1)
            throw ContractMismatch();
        var operationParameters = operation.Parameters
            .Where(parameter => !(parameter.Location == "header" && parameter.Name == "X-Agentweaver-Tenant" && !parameter.Required))
            .ToArray();
        if (!operationParameters.Select(parameter => parameter.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(contract.ParameterNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContractMismatch();
        foreach (var parameter in operationParameters)
            RequireParameterShape(contract, parameter);
        if (operation.Parameters.Any(parameter =>
            parameter.Location == "header" &&
            parameter.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)))
            throw ContractMismatch();

        if (contract.RequestSchema is not null &&
            GatewayContract.String(
                operation.Document["requestBody"]?["content"]?["application/json"]?["schema"] as JsonObject
                    ?? new JsonObject(), "$ref") != "#/components/schemas/" + contract.RequestSchema)
            throw ContractMismatch();
        if (contract.RequestMediaTypes is { Length: > 0 } &&
            contract.RequestMediaTypes.Any(mediaType =>
                operation.RequestBodyMediaTypes is null ||
                !operation.RequestBodyMediaTypes.Contains(mediaType, StringComparer.Ordinal)))
            throw ContractMismatch();

        var responseObject = operation.Document["responses"] as JsonObject;
        var requireJsonOnlySuccessResponse = contract.OperationId is
            "readPlatformByokProviderCredentialOperation" or "readProjectByokProviderCredentialOperation";
        if (requireJsonOnlySuccessResponse &&
            !operation.ResponseMediaTypes.SequenceEqual(["application/json"], StringComparer.Ordinal))
            throw ContractMismatch();
        foreach (var expected in contract.Responses)
        {
            var separator = expected.IndexOf(':');
            var status = expected[..separator];
            var schema = expected[(separator + 1)..];
            var content = responseObject?[status]?["content"] as JsonObject;
            if (requireJsonOnlySuccessResponse &&
                (content is null || content.Count != 1 || !content.ContainsKey("application/json")))
                throw ContractMismatch();
            if (GatewayContract.String(
                    content?["application/json"]?["schema"] as JsonObject
                        ?? new JsonObject(), "$ref") != "#/components/schemas/" + schema)
                throw ContractMismatch();
        }

        var schemas = document["components"]?["schemas"] as JsonObject
            ?? throw ContractDrift(scopeName);
        if (contract.RequestSchema is not null)
            RequireSchema(schemas, contract.RequestSchema);
        foreach (var schema in contract.Responses.Select(value => value[(value.IndexOf(':') + 1)..]).Distinct(StringComparer.Ordinal))
            RequireSchema(schemas, schema);
        RequireNestedSchemas(schemas, contract.RequestSchema);
        foreach (var schema in contract.Responses.Select(value => value[(value.IndexOf(':') + 1)..]).Distinct(StringComparer.Ordinal))
            RequireNestedSchemas(schemas, schema);
    }

    private static void RequireSchema(JsonObject schemas, string name)
    {
        if (schemas[name] is not JsonObject schema || schema["properties"] is not JsonObject properties)
            throw ContractMismatch();

        var expected = SchemaProperties(name);
        var actualProperties = properties.Select(item => item.Key)
            .Where(key => !(name == "ModelProviderDraftPage" && key == "activeProviderRevision"));
        if (!actualProperties.Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Properties.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            schema["required"] is not JsonArray required ||
            !required.Select(value => value?.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Required.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            AllowsAdditionalProperties(name, schema["additionalProperties"]))
            throw ContractMismatch();
        if (name.StartsWith("ModelProviderDraft", StringComparison.Ordinal) &&
            name is not ("ModelProviderDraft" or "ModelProviderDraftPage"))
        {
            RequireNullableString(properties, "rate_card_id");
            RequireNullableString(properties, "rate_card_version");
            RequireDependentRequired(schema, "rate_card_id", "rate_card_version");
            RequireDependentRequired(schema, "rate_card_version", "rate_card_id");
        }
        RequireFieldShapes(properties, name);
    }

    private static void RequireParameterShape(OperationContract contract, GatewayParameter parameter)
    {
        var isPath = contract.Path.Contains("{" + parameter.Name + "}", StringComparison.Ordinal);
        var isHeader = parameter.Name is "If-Match" or "Idempotency-Key";
        var expectedLocation = isPath ? "path" : isHeader ? "header" : "query";
        var expectedRequired = isPath ||
            contract.OperationId == "searchProjectAzureRetailPrices" &&
            parameter.Name is "region" or "search";
        var expectedType = contract.OperationId.Contains("ByokProviderDrafts", StringComparison.Ordinal) &&
                           parameter.Name == "pageSize"
            ? "integer"
            : "string";
        if (parameter.Location != expectedLocation ||
            parameter.Required != expectedRequired ||
            GatewayContract.String(parameter.Schema, "type") != expectedType)
            throw ContractMismatch();
    }

    private static SchemaInventory SchemaProperties(string name) =>
        name switch
        {
            "ModelProviderDraftPlatformCreateRequest" => new(
                ["operationId", "expectedRevision", "name", "type", "base_url", "model", "wire_api",
                    "azure_api_version", "reasoning_efforts", "prompt_capacity_tokens", "rate_card_id",
                    "rate_card_version", "reference", "targetInstallationId"],
                ["operationId", "expectedRevision", "name", "type", "base_url", "model"]),
            "ModelProviderDraftProjectCreateRequest" => new(
                ["operationId", "expectedRevision", "name", "type", "base_url", "model", "wire_api",
                    "azure_api_version", "reasoning_efforts", "prompt_capacity_tokens", "rate_card_id",
                    "rate_card_version"],
                ["operationId", "expectedRevision", "name", "type", "base_url", "model"]),
            "ModelProviderDraftSaveRequest" => new(
                ["operationId", "expectedRevision", "name", "type", "base_url", "model", "wire_api",
                    "azure_api_version", "reasoning_efforts", "prompt_capacity_tokens", "rate_card_id",
                    "rate_card_version"],
                ["operationId", "expectedRevision", "name", "type", "base_url", "model"]),
            "ModelProviderCredentialPutRequest" => new(
                ["operationId", "expectedProviderRevision", "expectedCredentialRevision", "apiKey",
                    "recoverySecretVersion", "recheckExistingVersion"],
                ["operationId", "expectedProviderRevision"]),
            "ModelProviderCredentialDeleteRequest" => new(
                ["operationId", "expectedProviderRevision", "expectedCredentialRevision", "cancel"],
                ["operationId"]),
            "ActivatePlatformModelProviderRequest" => new(
                ["expectedSettingsRevision", "expectedProviderRevision"],
                ["expectedSettingsRevision", "expectedProviderRevision"]),
            "DeactivatePlatformModelProviderRequest" => new(
                ["expectedSettingsRevision"], ["expectedSettingsRevision"]),
            "ManualRuntimeRateCardWriteRequest" => new(
                ["runtimeRateCardSchemaVersion", "rateCard", "provenance"],
                ["runtimeRateCardSchemaVersion", "rateCard", "provenance"]),
            "AzureRetailRuntimeRateCardWriteRequest" => new(
                ["region", "modelId", "input", "output", "cachedInput"],
                ["region", "modelId", "input", "output"]),
            "ModelProviderDraft" => new(
                ["id", "name", "type", "base_url", "model", "wire_api", "azure_api_version",
                    "reasoning_efforts", "prompt_capacity_tokens", "scope", "projectId", "state", "revision",
                    "configurationHash", "has_api_key", "is_active", "hasCredential", "reference",
                    "rate_card_id", "rate_card_version", "egress"],
                ["id", "name", "type", "base_url", "model", "wire_api", "azure_api_version",
                    "reasoning_efforts", "prompt_capacity_tokens", "scope", "projectId", "state", "revision",
                    "configurationHash", "rate_card_id", "rate_card_version",
                    "has_api_key", "is_active", "hasCredential"]),
            "ModelProviderEgressStatus" => new(
                ["host", "port", "destinationState", "canApprove", "deliveryStatus"],
                ["host", "port", "destinationState", "canApprove", "deliveryStatus"]),
            "ModelProviderDraftPage" => new(
                ["switchMode", "activeProviderId", "settingsRevision", "providers", "nextAfterProviderId"],
                ["switchMode", "activeProviderId", "settingsRevision", "providers", "nextAfterProviderId"]),
            "ModelProviderCredentialOperationResponse" => new(
                ["operationId", "status", "scope", "projectId", "providerId", "providerRevision",
                    "credentialRevision", "credentialVersion", "configurationHash", "providerState"],
                ["operationId", "status", "scope", "providerId"]),
            "ModelProviderCredentialOperationStatusResponse" => new(
                ["operationId", "scope", "projectId", "providerId", "status", "providerRevision", "hasCredential"],
                ["operationId", "scope", "projectId", "providerId", "status"]),
            "ModelProviderCredentialStatusResponse" => new(
                ["scope", "projectId", "providerId", "providerRevision", "state", "credentialRevision"],
                ["scope", "projectId", "providerId", "providerRevision", "state", "credentialRevision"]),
            "PlatformModelDefaultSwitchResult" => new(
                ["mode", "activeProviderId", "settingsRevision"],
                ["mode", "activeProviderId", "settingsRevision"]),
            "AzureRetailPriceLookupResult" => new(
                ["region", "sourceUrl", "retrievedOn", "items"],
                ["region", "sourceUrl", "retrievedOn", "items"]),
            "RuntimeRateCard" or "AzureRetailRuntimeRateCard" => new(
                ["runtimeRateCardSchemaVersion", "rateCard", "provenance"],
                ["runtimeRateCardSchemaVersion", "rateCard", "provenance"]),
            "RuntimeRateCardBody" => new(
                ["id", "version", "meterSource", "unit", "nanoUnitsPerUnit", "modelMultipliers"],
                ["id", "version", "meterSource", "unit", "nanoUnitsPerUnit", "modelMultipliers"]),
            "ManualRuntimeRateCardProvenance" => new(
                ["source", "sourceUrl", "retrievedOn"],
                ["source", "sourceUrl", "retrievedOn"]),
            "AzureRetailRuntimeRateCardProvenance" => new(
                ["source", "region", "meterIds"],
                ["source", "region", "meterIds"]),
            "AzureRetailMeterSelection" => new(
                ["meterId", "expectedUsdPer1M"],
                ["meterId", "expectedUsdPer1M"]),
            "AzureRetailMeterCandidate" => new(
                ["meterId", "meterName", "productName", "skuName", "unitOfMeasure", "currencyCode",
                    "retailPrice", "tierMinimumUnits", "usdPer1M"],
                ["meterId", "meterName", "productName", "skuName", "unitOfMeasure", "currencyCode",
                    "retailPrice", "tierMinimumUnits", "usdPer1M"]),
            _ => throw ContractMismatch(),
        };

    private static void RequireNestedSchemas(JsonObject schemas, string? root)
    {
        var nested = root switch
        {
            "ManualRuntimeRateCardWriteRequest" or "RuntimeRateCard" =>
                new[] { "RuntimeRateCardBody", "ManualRuntimeRateCardProvenance" },
            "AzureRetailRuntimeRateCardWriteRequest" => ["AzureRetailMeterSelection"],
            "AzureRetailRuntimeRateCard" =>
                ["RuntimeRateCardBody", "AzureRetailRuntimeRateCardProvenance"],
            "AzureRetailPriceLookupResult" => ["AzureRetailMeterCandidate"],
            "ModelProviderDraft" => ["ModelProviderEgressStatus"],
            _ => [],
        };
        foreach (var name in nested)
            RequireSchema(schemas, name);
    }

    private static bool AllowsAdditionalProperties(string name, JsonNode? additionalProperties) =>
        name == "ModelProviderDraftPage"
            ? additionalProperties is not null
            : additionalProperties is not JsonValue value ||
              !value.TryGetValue<bool>(out var allowed) ||
              allowed;

    private static void RequireNullableString(JsonObject properties, string name)
    {
        if (properties[name]?["anyOf"] is not JsonArray alternatives ||
            alternatives.Count != 2 ||
            GatewayContract.String(alternatives[0] as JsonObject ?? new JsonObject(), "type") != "string" ||
            GatewayContract.String(alternatives[1] as JsonObject ?? new JsonObject(), "type") != "null")
            throw ContractMismatch();
    }

    private static void RequireDependentRequired(
        JsonObject schema, string property, string requiredProperty)
    {
        if (schema["dependentRequired"]?[property] is not JsonArray dependencies ||
            dependencies.Count != 1 ||
            dependencies[0]?.GetValue<string>() != requiredProperty)
            throw ContractMismatch();
    }

    private static void RequireFieldShapes(JsonObject properties, string name)
    {
        switch (name)
        {
            case "ModelProviderDraftPlatformCreateRequest":
            case "ModelProviderDraftProjectCreateRequest":
            case "ModelProviderDraftSaveRequest":
                RequireType(properties, "operationId", "string", "uuid");
                RequireType(properties, "expectedRevision", "integer");
                RequireConstraint(properties, "expectedRevision", "format", "int64");
                foreach (var field in new[] { "name", "type", "base_url", "model" })
                    RequireType(properties, field, "string");
                if (name is "ModelProviderDraftPlatformCreateRequest" or "ModelProviderDraftProjectCreateRequest")
                    RequireConstraint(properties, "expectedRevision", "const", 0);
                else
                {
                    RequireConstraint(properties, "expectedRevision", "minimum", 1);
                    RequireConstraint(properties, "expectedRevision", "maximum", long.MaxValue - 1);
                }
                RequireNullableType(properties, "wire_api", "string");
                RequireNullableType(properties, "azure_api_version", "string");
                RequireArrayType(properties, "reasoning_efforts", "string");
                RequireNullableType(properties, "prompt_capacity_tokens", "integer");
                RequireNullableType(properties, "rate_card_id", "string");
                RequireNullableType(properties, "rate_card_version", "string");
                if (name == "ModelProviderDraftPlatformCreateRequest")
                {
                    RequireType(properties, "reference", "string");
                    RequireType(properties, "targetInstallationId", "string", "uuid");
                }
                break;
            case "ModelProviderCredentialPutRequest":
                RequireType(properties, "operationId", "string", "uuid");
                RequireType(properties, "expectedProviderRevision", "integer");
                RequireConstraint(properties, "expectedProviderRevision", "format", "int64");
                RequireConstraint(properties, "expectedProviderRevision", "minimum", 1);
                RequireConstraint(properties, "expectedProviderRevision", "maximum", long.MaxValue - 1);
                RequireType(properties, "expectedCredentialRevision", "integer");
                RequireConstraint(properties, "expectedCredentialRevision", "format", "int64");
                RequireConstraint(properties, "expectedCredentialRevision", "minimum", 0);
                RequireConstraint(properties, "expectedCredentialRevision", "maximum", long.MaxValue - 1);
                RequireNullableType(properties, "apiKey", "string");
                RequireNullableType(properties, "recoverySecretVersion", "string");
                RequireNullableType(properties, "recheckExistingVersion", "boolean");
                break;
            case "ModelProviderCredentialDeleteRequest":
                RequireType(properties, "operationId", "string", "uuid");
                RequireNullableType(properties, "expectedProviderRevision", "integer");
                RequireNullableType(properties, "expectedCredentialRevision", "integer");
                RequireNullableType(properties, "cancel", "boolean");
                break;
            case "ModelProviderCredentialOperationResponse":
                RequireType(properties, "operationId", "string", "uuid");
                RequireType(properties, "status", "string");
                RequireType(properties, "scope", "string");
                RequireNullableType(properties, "projectId", "string");
                RequireType(properties, "providerId", "string");
                RequireNullableType(properties, "providerRevision", "integer");
                RequireNullableType(properties, "credentialRevision", "integer");
                RequireNullableType(properties, "credentialVersion", "string");
                RequireNullableType(properties, "configurationHash", "string");
                RequireNullableType(properties, "providerState", "string");
                break;
            case "ModelProviderCredentialOperationStatusResponse":
                RequireType(properties, "operationId", "string", "uuid");
                RequireType(properties, "scope", "string");
                RequireEnum(properties, "scope", ["platform", "project"]);
                RequireNullableType(properties, "projectId", "string");
                RequireType(properties, "providerId", "string");
                RequireType(properties, "status", "string");
                RequireEnum(properties, "status",
                    ["pending", "publication_pending", "published", "quarantined", "cancelled", "disable_pending", "disabled"]);
                RequireNullableType(properties, "providerRevision", "integer");
                RequireNullableType(properties, "hasCredential", "boolean");
                break;
            case "ModelProviderCredentialStatusResponse":
                RequireType(properties, "scope", "string");
                RequireEnum(properties, "scope", ["platform", "project"]);
                RequireNullableType(properties, "projectId", "string");
                RequireType(properties, "providerId", "string");
                RequireType(properties, "providerRevision", "integer", "int64");
                RequireConstraint(properties, "providerRevision", "minimum", 1);
                RequireType(properties, "state", "string");
                RequireEnum(properties, "state", ["not_set", "enabled", "disabled", "pending", "quarantined"]);
                RequireType(properties, "credentialRevision", "integer", "int64");
                RequireConstraint(properties, "credentialRevision", "minimum", 0);
                break;
            case "ModelProviderDraft":
                foreach (var field in new[] { "id", "name", "type", "base_url", "model", "scope",
                             "state", "configurationHash" })
                    RequireType(properties, field, "string");
                foreach (var field in new[] { "wire_api", "azure_api_version", "prompt_capacity_tokens",
                             "projectId", "rate_card_id", "rate_card_version" })
                    RequireNullableType(properties, field, field == "prompt_capacity_tokens" ? "integer" : "string");
                RequireType(properties, "reference", "string");
                RequireArrayType(properties, "reasoning_efforts", "string");
                RequireType(properties, "revision", "integer");
                RequireType(properties, "has_api_key", "boolean");
                RequireType(properties, "is_active", "boolean");
                RequireType(properties, "hasCredential", "boolean");
                RequireNullableReference(properties, "egress", "ModelProviderEgressStatus");
                break;
            case "ModelProviderEgressStatus":
                RequireType(properties, "host", "string");
                RequireType(properties, "port", "integer");
                RequireNullableType(properties, "destinationState", "string");
                RequireType(properties, "canApprove", "boolean");
                RequireType(properties, "deliveryStatus", "string");
                break;
            case "ModelProviderDraftPage":
                RequireNullableType(properties, "switchMode", "string");
                RequireNullableType(properties, "activeProviderId", "string");
                if (properties.ContainsKey("activeProviderRevision"))
                    RequireNullableType(properties, "activeProviderRevision", "integer");
                RequireNullableType(properties, "settingsRevision", "integer");
                RequireArrayReference(properties, "providers", "ModelProviderDraft");
                RequireNullableType(properties, "nextAfterProviderId", "string");
                break;
            case "ActivatePlatformModelProviderRequest":
            case "DeactivatePlatformModelProviderRequest":
                RequireType(properties, "expectedSettingsRevision", "integer");
                if (name == "ActivatePlatformModelProviderRequest")
                    RequireType(properties, "expectedProviderRevision", "integer");
                break;
            case "PlatformModelDefaultSwitchResult":
                RequireType(properties, "mode", "string");
                RequireNullableType(properties, "activeProviderId", "string");
                RequireType(properties, "settingsRevision", "integer");
                break;
            case "AzureRetailPriceLookupResult":
                RequireType(properties, "region", "string");
                RequireType(properties, "sourceUrl", "string", "uri");
                RequireType(properties, "retrievedOn", "string", "date-time");
                RequireArrayReference(properties, "items", "AzureRetailMeterCandidate");
                break;
            case "AzureRetailMeterCandidate":
                foreach (var field in new[] { "meterId", "meterName", "productName", "skuName",
                             "unitOfMeasure", "currencyCode" })
                    RequireType(properties, field, "string");
                foreach (var field in new[] { "retailPrice", "tierMinimumUnits", "usdPer1M" })
                    RequireNullableType(properties, field, "number");
                break;
            case "ManualRuntimeRateCardWriteRequest":
            case "RuntimeRateCard":
                RequireType(properties, "runtimeRateCardSchemaVersion", "integer");
                RequireConstraint(properties, "runtimeRateCardSchemaVersion", "enum", 1);
                RequireReference(properties, "rateCard", "RuntimeRateCardBody");
                RequireReference(properties, "provenance", "ManualRuntimeRateCardProvenance");
                break;
            case "AzureRetailRuntimeRateCardWriteRequest":
                RequireType(properties, "region", "string");
                RequireConstraint(properties, "region", "maxLength", 63);
                RequireConstraint(properties, "region", "pattern", "^[a-z][a-z0-9]{0,62}$");
                RequireType(properties, "modelId", "string");
                RequireReference(properties, "input", "AzureRetailMeterSelection");
                RequireReference(properties, "output", "AzureRetailMeterSelection");
                RequireReference(properties, "cachedInput", "AzureRetailMeterSelection");
                break;
            case "AzureRetailRuntimeRateCard":
                RequireType(properties, "runtimeRateCardSchemaVersion", "integer");
                RequireConstraint(properties, "runtimeRateCardSchemaVersion", "enum", 1);
                RequireReference(properties, "rateCard", "RuntimeRateCardBody");
                RequireReference(properties, "provenance", "AzureRetailRuntimeRateCardProvenance");
                break;
            case "RuntimeRateCardBody":
                RequireType(properties, "id", "string");
                RequireType(properties, "version", "string");
                RequireType(properties, "meterSource", "string");
                RequireConstraint(properties, "meterSource", "enum", "byok.tokens");
                RequireType(properties, "unit", "string");
                RequireConstraint(properties, "unit", "enum", "USD");
                RequireType(properties, "nanoUnitsPerUnit", "number");
                RequireConstraint(properties, "nanoUnitsPerUnit", "enum", 1_000_000);
                if (properties["modelMultipliers"] is not JsonObject multipliers ||
                    GatewayContract.String(multipliers, "type") != "object" ||
                    GatewayContract.String(multipliers["additionalProperties"] as JsonObject ?? new(), "type") != "number")
                    throw ContractMismatch();
                break;
            case "ManualRuntimeRateCardProvenance":
                RequireType(properties, "source", "string");
                RequireConstraint(properties, "source", "enum", "manual");
                RequireType(properties, "sourceUrl", "string", "uri");
                RequireType(properties, "retrievedOn", "string", "date-time");
                break;
            case "AzureRetailRuntimeRateCardProvenance":
                RequireType(properties, "source", "string");
                RequireConstraint(properties, "source", "enum", "azure-retail-prices");
                RequireType(properties, "region", "string");
                RequireArrayType(properties, "meterIds", "string");
                break;
            case "AzureRetailMeterSelection":
                RequireType(properties, "meterId", "string");
                RequireType(properties, "expectedUsdPer1M", "number");
                RequireConstraint(properties, "expectedUsdPer1M", "exclusiveMinimum", 0);
                break;
        }
    }

    private static void RequireType(
        JsonObject properties, string name, string type, string? format = null)
    {
        var schema = properties[name] as JsonObject ?? throw ContractMismatch();
        if (GatewayContract.String(schema, "type") != type ||
            format is not null && GatewayContract.String(schema, "format") != format)
            throw ContractMismatch();
    }

    private static void RequireNullableType(JsonObject properties, string name, string type)
    {
        if (properties[name]?["anyOf"] is not JsonArray alternatives ||
            alternatives.Count != 2 ||
            alternatives[0] is not JsonObject value ||
            alternatives[1] is not JsonObject @null ||
            GatewayContract.String(value, "type") != type ||
            GatewayContract.String(@null, "type") != "null")
            throw ContractMismatch();
    }

    private static void RequireNullableReference(JsonObject properties, string name, string schemaName)
    {
        if (properties[name]?["anyOf"] is not JsonArray alternatives ||
            alternatives.Count != 2 ||
            alternatives[0] is not JsonObject value ||
            alternatives[1] is not JsonObject @null ||
            GatewayContract.String(value, "$ref") != "#/components/schemas/" + schemaName ||
            GatewayContract.String(@null, "type") != "null")
            throw ContractMismatch();
    }

    private static void RequireReference(JsonObject properties, string name, string schemaName)
    {
        if (GatewayContract.String(properties[name] as JsonObject ?? new(), "$ref") !=
            "#/components/schemas/" + schemaName)
            throw ContractMismatch();
    }

    private static void RequireArrayReference(JsonObject properties, string name, string schemaName)
    {
        if (GatewayContract.String(properties[name] as JsonObject ?? new(), "type") != "array" ||
            GatewayContract.String(
                properties[name]?["items"] as JsonObject ?? new(), "$ref") !=
            "#/components/schemas/" + schemaName)
            throw ContractMismatch();
    }

    private static void RequireArrayType(JsonObject properties, string name, string type)
    {
        if (GatewayContract.String(properties[name] as JsonObject ?? new(), "type") != "array" ||
            GatewayContract.String(properties[name]?["items"] as JsonObject ?? new(), "type") != type)
            throw ContractMismatch();
    }

    private static void RequireEnum(JsonObject properties, string name, string[] expected)
    {
        if (properties[name]?["enum"] is not JsonArray values ||
            values.Count != expected.Length ||
            values.Any(value => value is not JsonValue json || !json.TryGetValue<string>(out var text) || text is null) ||
            !values.Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContractMismatch();
    }

    private static void RequireConstraint(
        JsonObject properties, string name, string key, string expected) =>
        RequireConstraint(properties, name, key, JsonValue.Create(expected));

    private static void RequireConstraint(
        JsonObject properties, string name, string key, long expected) =>
        RequireConstraint(properties, name, key, JsonValue.Create(expected));

    private static void RequireConstraint(
        JsonObject properties, string name, string key, JsonNode? expected)
    {
        if (key == "enum")
        {
            if (properties[name]?[key] is not JsonArray values ||
                values.Count != 1 ||
                !JsonNode.DeepEquals(values[0], expected))
                throw ContractMismatch();
            return;
        }
        if (!JsonNode.DeepEquals(properties[name]?[key], expected))
            throw ContractMismatch();
    }

    private static InvalidDataException ContractDrift(string scopeName) =>
        ContractMismatch();

    private static InvalidDataException ContractMismatch() =>
        new("BYOK_CONTRACT_MISMATCH");

    private static async Task<JsonObject> ListAsync(
        IGatewayClient gateway,
        string path,
        string expectedScope,
        string? projectId,
        string scopeName,
        CancellationToken ct)
    {
        var operationId = $"list{(projectId is null ? "Platform" : "Project")}ByokProviderDrafts";
        var providers = new JsonArray();
        var seenProviderIds = new HashSet<string>(StringComparer.Ordinal);
        string? afterProviderId = null;
        string? switchMode = null;
        string? activeProviderId = null;
        long? settingsRevision = null;
        var switchObserved = false;
        while (true)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
            };
            if (afterProviderId is not null)
                query["afterProviderId"] = afterProviderId;

            var response = await InvokeAsync(
                gateway, operationId,
                new GatewayInvocation(
                    projectId is null ? null :
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["projectId"] = projectId },
                    query),
                scopeName, "list", null, ct).ConfigureAwait(false);
            EnsureSuccess(response, scopeName, "list", null);
            if (response.Body is not JsonObject page ||
                page["providers"] is not JsonArray pageProviders ||
                pageProviders.Count > PageSize ||
                !page.ContainsKey("nextAfterProviderId"))
                throw UnexpectedResponse();
            var pageSwitchMode = OptionalString(page, "switchMode");
            var pageActiveProviderId = OptionalString(page, "activeProviderId");
            var pageSettingsRevision = OptionalPositiveInt64(page, "settingsRevision");
            if (!switchObserved)
            {
                switchMode = pageSwitchMode;
                activeProviderId = pageActiveProviderId;
                settingsRevision = pageSettingsRevision;
                switchObserved = true;
            }
            else if (pageSwitchMode != switchMode ||
                     pageActiveProviderId != activeProviderId ||
                     pageSettingsRevision != settingsRevision)
            {
                throw UnexpectedResponse();
            }

            string? lastProviderId = null;
            foreach (var provider in pageProviders)
            {
                var projected = ProjectProvider(provider, expectedScope, projectId);
                var currentProviderId = projected["providerId"]!.GetValue<string>();
                if (!seenProviderIds.Add(currentProviderId))
                    throw UnexpectedResponse();
                lastProviderId = currentProviderId;
                providers.Add(projected);
            }

            var nextAfterProviderId = OptionalString(page, "nextAfterProviderId");
            if (nextAfterProviderId is not null &&
                (lastProviderId is null || lastProviderId != nextAfterProviderId ||
                 !seenProviderIds.Contains(nextAfterProviderId)))
                throw UnexpectedResponse();
            if (nextAfterProviderId is null)
                return new JsonObject
                {
                    ["switchMode"] = switchMode,
                    ["activeProviderId"] = activeProviderId,
                    ["settingsRevision"] = settingsRevision,
                    ["providers"] = providers,
                };
            afterProviderId = nextAfterProviderId;
        }
    }

    private static GatewayInvocation BuildInvocation(
        string command,
        bool projectScope,
        string? projectId,
        string[] args,
        CliOptions options,
        out Guid? operationId,
        Guid? lookupOperationId = null)
    {
        operationId = null;
        var path = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId is not null)
            path["projectId"] = projectId;

        switch (command)
        {
            case "show":
                path["providerId"] = args[2];
                if (lookupOperationId is { } opId)
                    path["operationId"] = opId.ToString("D");
                return new GatewayInvocation(path);
            case "add":
            case "update":
                if (command == "update")
                    path["providerId"] = args[2];
                operationId = ReadOperationId(options.Single("operation-id"));
                var expectedRevision = command == "add"
                    ? 0
                    : ReadPositiveRevision(options.Single("expected-revision"), "--expected-revision");
                var attachment = ReadRateCardAttachment(options);
                return new GatewayInvocation(
                    path,
                    Body: SaveBody(operationId.Value, expectedRevision, ReadMetadata(options), attachment));
            case "prices":
                var region = RequiredOption(options, "region");
                var search = RequiredOption(options, "search");
                return new GatewayInvocation(path,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["region"] = region,
                        ["search"] = search,
                    });
            case "save-prices":
                path["id"] = args[2];
                path["version"] = args[3];
                var input = RequiredOption(options, "input");
                var body = ReadJsonFile(input);
                ValidatePricingRequest(ReadPricingSource(args), args[2], args[3], body);
                return new GatewayInvocation(path, Body: body);
            case "recheck-key":
                path["providerId"] = args[2];
                operationId = RequiredOperationId(options.Single("operation-id"));
                return new GatewayInvocation(path, Body: new JsonObject
                {
                    ["operationId"] = operationId.Value.ToString("D"),
                    ["expectedProviderRevision"] = ReadPositiveRevision(
                        options.Single("expected-revision"), "--expected-revision"),
                    ["expectedCredentialRevision"] = ReadPositiveRevision(
                        options.Single("expected-credential-revision"), "--expected-credential-revision"),
                    ["recheckExistingVersion"] = true,
                });
            case "disable-key":
                path["providerId"] = args[2];
                operationId = RequiredOperationId(options.Single("operation-id"));
                return new GatewayInvocation(path, Body: new JsonObject
                {
                    ["operationId"] = operationId.Value.ToString("D"),
                    ["expectedProviderRevision"] = ReadPositiveRevision(
                        options.Single("expected-revision"), "--expected-revision"),
                    ["expectedCredentialRevision"] = ReadPositiveRevision(
                        options.Single("expected-credential-revision"), "--expected-credential-revision"),
                    ["cancel"] = false,
                });
            case "activate":
                path["providerId"] = args[2];
                return new GatewayInvocation(path, Body: new JsonObject
                {
                    ["expectedProviderRevision"] = ReadPositiveRevision(
                        options.Single("expected-revision"), "--expected-revision"),
                    ["expectedSettingsRevision"] = ReadPositiveRevision(
                        options.Single("expected-settings-revision"), "--expected-settings-revision"),
                });
            case "deactivate":
                return new GatewayInvocation(path, Body: new JsonObject
                {
                    ["expectedSettingsRevision"] = ReadPositiveRevision(
                        options.Single("expected-settings-revision"), "--expected-settings-revision"),
                });
            default:
                throw new CliUsageException($"Unknown BYOK command: {command}.");
        }
    }

    private static string ReadPricingSource(string[] args)
    {
        var index = Array.FindIndex(args, value => value == "--source");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    }

    private static JsonObject ReadJsonFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new CliUsageException("--input must name an existing JSON file.");
            if (file.Length > MaximumSaveBodyBytes)
                throw new CliUsageException("--input exceeds the 65536-byte request limit.");
            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CliUsageException("--input must contain a JSON object.");
            return JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        }
        catch (JsonException)
        {
            throw new CliUsageException("--input must contain valid JSON.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliUsageException("--input could not be read.");
        }
        catch (IOException)
        {
            throw new CliUsageException("--input could not be read.");
        }
    }

    private static void ValidatePricingRequest(string source, string id, string version, JsonObject body)
    {
        if (source == "manual")
        {
            RequireExactProperties(body,
                ["runtimeRateCardSchemaVersion", "rateCard", "provenance"]);
            if (PositiveInt(body, "runtimeRateCardSchemaVersion") != 1 ||
                body["rateCard"] is not JsonObject rateCard ||
                body["provenance"] is not JsonObject provenance)
                throw new CliUsageException("--input does not match the manual pricing request schema.");
            RequireRateCardBody(rateCard, id, version);
            RequireExactProperties(provenance, ["source", "sourceUrl", "retrievedOn"]);
            if (RequiredString(provenance, "source") != "manual" ||
                !TryReadCredentialFreeHttpsUrl(RequiredString(provenance, "sourceUrl"), out _) ||
                !DateTimeOffset.TryParse(
                    RequiredString(provenance, "retrievedOn"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
                throw new CliUsageException("--input contains an invalid manual pricing provenance.");
            return;
        }

        if (source == "azure-retail")
        {
            if (body["cachedInput"] is null)
                RequireExactProperties(body, ["region", "modelId", "input", "output"]);
            else
                RequireExactProperties(body, ["region", "modelId", "input", "output", "cachedInput"]);
            var region = RequiredString(body, "region");
            if (id != $"azure-retail-prices.standard.{region}" ||
                !ValidRegion(region) ||
                body["input"] is not JsonObject input ||
                body["output"] is not JsonObject output ||
                (body["cachedInput"] is not null && body["cachedInput"] is not JsonObject))
                throw new CliUsageException(
                    "--input region must match an azure-retail-prices.standard.<region> rate-card ID.");
            RequireString(body, "modelId");
            if (!ValidMeterValue(RequiredString(body, "modelId"), 256))
                throw new CliUsageException("--input contains an invalid Azure Retail model ID.");
            RequireMeterSelection(input);
            RequireMeterSelection(output);
            if (body["cachedInput"] is JsonObject cachedInput)
                RequireMeterSelection(cachedInput);
            return;
        }

        throw new CliUsageException("--source must be manual or azure-retail.");
    }

    private static void RequireRateCardBody(JsonObject rateCard, string id, string version)
    {
        RequireExactProperties(rateCard,
            ["id", "version", "meterSource", "unit", "nanoUnitsPerUnit", "modelMultipliers"]);
        if (!ValidRateCardToken(RequiredString(rateCard, "id"), 128) ||
            !ValidRateCardToken(RequiredString(rateCard, "version"), 64) ||
            RequiredString(rateCard, "id") != id ||
            RequiredString(rateCard, "version") != version ||
            RequiredString(rateCard, "meterSource") != "byok.tokens" ||
            RequiredString(rateCard, "unit") != "USD" ||
            RequiredNumber(rateCard, "nanoUnitsPerUnit") != 1_000_000 ||
            ProjectNumberObject(rateCard, "modelMultipliers").Count == 0)
            throw new CliUsageException("--input does not match the selected rate-card ID and version.");
    }

    private static void RequireMeterSelection(JsonObject selection)
    {
        RequireExactProperties(selection, ["meterId", "expectedUsdPer1M"]);
        if (!ValidMeterValue(RequiredString(selection, "meterId"), 128) ||
            RequiredNumber(selection, "expectedUsdPer1M") <= 0)
            throw new CliUsageException("--input contains an invalid Azure Retail meter selection.");
    }

    private static bool ValidRateCardToken(string value, int maximumLength) =>
        value.Length > 0 && value.Length <= maximumLength &&
        (char.IsAsciiLetterOrDigit(value[0])) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool ValidRegion(string value) =>
        value.Length is > 0 and <= 63 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool ValidMeterValue(string value, int maximumLength) =>
        value.Length > 0 && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static void RequireString(JsonObject value, string name)
    {
        if (RequiredString(value, name).Length == 0)
            throw new CliUsageException($"--input field {name} must be a non-empty string.");
    }

    private static void RequireExactProperties(JsonObject value, string[] expected)
    {
        if (!value.Select(item => item.Key).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new CliUsageException("--input contains unexpected or missing pricing fields.");
    }

    private static RateCardAttachment ReadRateCardAttachment(CliOptions options)
    {
        var id = options.Single("rate-card-id");
        var version = options.Single("rate-card-version");
        if ((id is null) != (version is null))
            throw new CliUsageException("--rate-card-id and --rate-card-version must be specified together.");
        if (id is not null && (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version)))
            throw new CliUsageException("--rate-card-id and --rate-card-version require non-empty values.");
        return new(id?.Trim(), version?.Trim());
    }

    private static JsonObject SaveBody(
        Guid operationId,
        long expectedRevision,
        ProviderMetadata metadata,
        RateCardAttachment attachment)
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
            ["rate_card_id"] = attachment.Id,
            ["rate_card_version"] = attachment.Version,
        };
    }

    private static ProviderMetadata ReadMetadata(CliOptions options)
    {
        int? promptCapacityTokens = null;
        var promptCapacityText = options.Single("prompt-capacity-tokens");
        if (promptCapacityText is not null &&
            (!int.TryParse(promptCapacityText, NumberStyles.None, CultureInfo.InvariantCulture, out var promptCapacity) ||
             promptCapacity <= 0))
            throw new CliUsageException("--prompt-capacity-tokens must be a positive integer.");
        if (promptCapacityText is not null)
            promptCapacityTokens = int.Parse(promptCapacityText, CultureInfo.InvariantCulture);
        return new ProviderMetadata(
            RequiredOption(options, "name"),
            RequiredOption(options, "type"),
            RequiredOption(options, "base-url"),
            RequiredOption(options, "model"),
            options.Single("wire-api"),
            options.Single("azure-api-version"),
            options.Many("reasoning-effort"),
            promptCapacityTokens);
    }

    private static string RequiredOption(CliOptions options, string name)
    {
        var value = options.Single(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new CliUsageException($"Option --{name} requires a non-empty value.")
            : value;
    }

    private static Guid RequiredOperationId(string? value)
    {
        if (value is null)
            throw new CliUsageException("--operation-id is required.");
        return ReadOperationId(value);
    }

    private static Guid ReadOperationId(string? value)
    {
        if (value is null)
            return Guid.NewGuid();
        if (!Guid.TryParse(value, out var operationId) || operationId == Guid.Empty)
            throw new CliUsageException("--operation-id must be a non-empty GUID.");
        return operationId;
    }

    private static long ReadPositiveRevision(string? value, string option)
    {
        if (value is null ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 1 || revision == long.MaxValue)
            throw new CliUsageException($"{option} must be a positive Int64 below Int64.MaxValue.");
        return revision;
    }

    private static async Task<GatewayResponse> InvokeAsync(
        IGatewayClient gateway,
        string operationId,
        GatewayInvocation invocation,
        string scopeName,
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
                : $"The {scopeName} BYOK {command} request failed.");
        }
    }

    private static string UnknownWriteMessage(Guid operationId) =>
        $"The write result is unknown. Repeat the same command with --operation-id {operationId:D} and the same fields to replay it.";

    private static void EnsureSuccess(
        GatewayResponse response,
        string scopeName,
        string command,
        Guid? operationId,
        string? expectedProviderId = null,
        bool credentialStatus = false,
        bool operationStatus = false)
    {
        if (response.Status is >= 200 and < 300)
            return;

        var code = response.Body is JsonObject problem
            ? GatewayContract.String(problem, "code")
            : null;
        if (response.Status == 409)
        {
            if (code == "credential_write_quarantined")
                throw new InvalidOperationException("BYOK_QUARANTINED");
            if (command == "show" && !credentialStatus && !operationStatus &&
                code == "model_provider_draft_metadata_requires_update" &&
                response.Body is JsonObject repair &&
                ValidProviderId(GatewayContract.String(repair, "id") ?? "") &&
                GatewayContract.String(repair, "id") == expectedProviderId &&
                TryReadPositiveInt64(repair, "revision", out var revision))
                throw new InvalidOperationException(
                    $"BYOK_METADATA_REPAIR_REQUIRED providerId={expectedProviderId} revision={revision}");
            throw new InvalidOperationException("BYOK_CONFLICT");
        }
        if (response.Status == 503)
        {
            if (code == "credential_publication_pending")
                throw new InvalidOperationException("BYOK_PENDING_PUBLICATION");
            if (code == "credential_vault_unavailable")
                throw new InvalidOperationException("BYOK_VAULT_UNAVAILABLE");
            throw new InvalidOperationException(
                command is "recheck-key" or "disable-key"
                    ? "BYOK_PENDING_PUBLICATION"
                    : "BYOK_UNAVAILABLE");
        }
        if (response.Status == 403)
            throw new InvalidOperationException("BYOK_FORBIDDEN");
        if (response.Status == 404)
            throw new InvalidOperationException("BYOK_NOT_FOUND");
        if (response.Status >= 500 && operationId is not null)
            throw new InvalidOperationException(UnknownWriteMessage(operationId.Value));
        throw new InvalidOperationException($"BYOK_REQUEST_FAILED_{response.Status}");
    }

    private static JsonObject ProjectCredentialStatus(
        JsonNode? body,
        string expectedScope,
        string? expectedProjectId,
        string expectedProviderId)
    {
        if (body is not JsonObject status || status.Count != 6)
            throw UnexpectedResponse();
        var scope = RequiredString(status, "scope");
        if (!status.TryGetPropertyValue("projectId", out var projectIdNode))
            throw UnexpectedResponse();
        string? projectId;
        if (expectedProjectId is null)
        {
            if (projectIdNode is not null)
                throw UnexpectedResponse();
            projectId = null;
        }
        else
        {
            projectId = RequiredString(status, "projectId");
            if (!string.Equals(projectId, expectedProjectId, StringComparison.Ordinal))
                throw UnexpectedResponse();
        }

        var providerId = RequiredString(status, "providerId");
        if (!string.Equals(scope, expectedScope, StringComparison.Ordinal) ||
            !string.Equals(providerId, expectedProviderId, StringComparison.Ordinal))
            throw UnexpectedResponse();

        var providerRevision = PositiveInt64(status, "providerRevision");
        var state = RequiredString(status, "state");
        var credentialRevision = NonNegativeInt64(status, "credentialRevision");
        var validState = state switch
        {
            "not_set" => credentialRevision == 0,
            "enabled" or "disabled" => credentialRevision >= 1,
            "pending" or "quarantined" => true,
            _ => false,
        };
        if (!validState)
            throw UnexpectedResponse();

        return new JsonObject
        {
            ["scope"] = scope,
            ["projectId"] = projectId,
            ["providerId"] = providerId,
            ["providerRevision"] = providerRevision,
            ["state"] = state,
            ["credentialRevision"] = credentialRevision,
        };
    }

    private static readonly HashSet<string> AllowedOperationStatusProperties = new(StringComparer.Ordinal)
    {
        "operationId", "scope", "projectId", "providerId", "status", "providerRevision", "hasCredential",
    };

    private static JsonObject ProjectOperationStatus(
        JsonNode? body,
        string expectedScope,
        string? expectedProjectId,
        string expectedProviderId,
        Guid expectedOperationId)
    {
        if (body is not JsonObject statusObj ||
            statusObj.Any(property => !AllowedOperationStatusProperties.Contains(property.Key)))
            throw UnexpectedResponse();

        if (!Guid.TryParse(RequiredString(statusObj, "operationId"), out var operationId) ||
            operationId == Guid.Empty ||
            operationId != expectedOperationId)
            throw UnexpectedResponse();

        var scope = RequiredString(statusObj, "scope");
        if (!string.Equals(scope, expectedScope, StringComparison.Ordinal))
            throw UnexpectedResponse();

        if (!statusObj.TryGetPropertyValue("projectId", out var projectIdNode))
            throw UnexpectedResponse();
        string? projectId;
        if (expectedProjectId is null)
        {
            if (projectIdNode is not null)
                throw UnexpectedResponse();
            projectId = null;
        }
        else
        {
            projectId = RequiredString(statusObj, "projectId");
            if (!string.Equals(projectId, expectedProjectId, StringComparison.Ordinal))
                throw UnexpectedResponse();
        }

        var providerId = RequiredString(statusObj, "providerId");
        if (!string.Equals(providerId, expectedProviderId, StringComparison.Ordinal))
            throw UnexpectedResponse();

        var status = RequiredString(statusObj, "status");
        long? providerRevision = null;
        if (statusObj.TryGetPropertyValue("providerRevision", out var revisionNode) && revisionNode is not null)
        {
            if (revisionNode is not JsonValue revisionValue ||
                !revisionValue.TryGetValue<long>(out var revision) ||
                revision <= 0)
                throw UnexpectedResponse();
            providerRevision = revision;
        }

        bool? hasCredential = null;
        if (statusObj.TryGetPropertyValue("hasCredential", out var credentialNode) && credentialNode is not null)
        {
            if (credentialNode is not JsonValue credentialValue ||
                !credentialValue.TryGetValue<bool>(out var credentialFlag))
                throw UnexpectedResponse();
            hasCredential = credentialFlag;
        }

        var valid = status switch
        {
            "published" => providerRevision is > 0 && hasCredential == true,
            "disabled" => providerRevision is > 0 && hasCredential is null,
            "pending" or "publication_pending" or "quarantined" or "cancelled" or "disable_pending" =>
                providerRevision is null && hasCredential is null,
            _ => false,
        };
        if (!valid)
            throw UnexpectedResponse();

        return new JsonObject
        {
            ["operationId"] = operationId.ToString("D"),
            ["scope"] = scope,
            ["projectId"] = projectId,
            ["providerId"] = providerId,
            ["status"] = status,
            ["providerRevision"] = providerRevision,
            ["hasCredential"] = hasCredential,
        };
    }

    private static JsonObject ProjectProvider(
        JsonNode? body,
        string expectedScope,
        string? projectId,
        string? expectedProviderId = null)
    {
        if (body is not JsonObject provider)
            throw UnexpectedResponse();
        var providerId = RequiredString(provider, "id");
        if (!ValidProviderId(providerId) ||
            expectedProviderId is not null && providerId != expectedProviderId ||
            RequiredString(provider, "scope") != expectedScope ||
            OptionalString(provider, "projectId") != projectId)
            throw UnexpectedResponse();

        var reasoningEfforts = CopyStringArray(provider, "reasoning_efforts");
        var promptCapacityTokens = provider["prompt_capacity_tokens"];
        if (promptCapacityTokens is not null &&
            (promptCapacityTokens is not JsonValue capacityValue ||
             !capacityValue.TryGetValue<int>(out var capacity) || capacity <= 0))
            throw UnexpectedResponse();

        var result = new JsonObject
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
            ["rateCardId"] = OptionalString(provider, "rate_card_id"),
            ["rateCardVersion"] = OptionalString(provider, "rate_card_version"),
            ["revision"] = PositiveInt64(provider, "revision"),
        };
        if (provider.ContainsKey("reference"))
        {
            var reference = RequiredString(provider, "reference");
            if (expectedScope != "platform" || !ByokImportOverlay.ValidReference(reference))
                throw UnexpectedResponse();
            result["reference"] = reference;
        }
        return result;
    }

    private static JsonObject ProjectPriceLookup(JsonNode? body)
    {
        if (body is not JsonObject lookup)
            throw UnexpectedResponse();
        var items = new JsonArray();
        if (lookup["items"] is not JsonArray candidates)
            throw UnexpectedResponse();
        foreach (var candidate in candidates)
        {
            if (candidate is not JsonObject value)
                throw UnexpectedResponse();
            items.Add(new JsonObject
            {
                ["meterId"] = RequiredString(value, "meterId"),
                ["meterName"] = RequiredString(value, "meterName"),
                ["productName"] = RequiredString(value, "productName"),
                ["skuName"] = RequiredString(value, "skuName"),
                ["unitOfMeasure"] = RequiredString(value, "unitOfMeasure"),
                ["currencyCode"] = RequiredString(value, "currencyCode"),
                ["retailPrice"] = NullableNumber(value, "retailPrice"),
                ["tierMinimumUnits"] = NullableNumber(value, "tierMinimumUnits"),
                ["usdPer1M"] = NullableNumber(value, "usdPer1M"),
            });
        }
        return new JsonObject
        {
            ["region"] = RequiredString(lookup, "region"),
            ["sourceUrl"] = SanitizeUrlForOutput(RequiredString(lookup, "sourceUrl")),
            ["retrievedOn"] = RequiredString(lookup, "retrievedOn"),
            ["items"] = items,
        };
    }

    private static JsonObject ProjectRateCard(JsonNode? body, string expectedId, string expectedVersion)
    {
        if (body is not JsonObject receipt ||
            receipt["rateCard"] is not JsonObject rateCard ||
            receipt["provenance"] is not JsonObject provenance ||
            RequiredString(rateCard, "id") != expectedId ||
            RequiredString(rateCard, "version") != expectedVersion)
            throw UnexpectedResponse();
        var projectedCard = new JsonObject
        {
            ["id"] = RequiredString(rateCard, "id"),
            ["version"] = RequiredString(rateCard, "version"),
            ["meterSource"] = RequiredString(rateCard, "meterSource"),
            ["unit"] = RequiredString(rateCard, "unit"),
            ["nanoUnitsPerUnit"] = RequiredNumber(rateCard, "nanoUnitsPerUnit"),
            ["modelMultipliers"] = ProjectNumberObject(rateCard, "modelMultipliers"),
        };
        if (RequiredString(rateCard, "meterSource") != "byok.tokens" ||
            RequiredString(rateCard, "unit") != "USD" ||
            RequiredNumber(rateCard, "nanoUnitsPerUnit") != 1_000_000)
            throw UnexpectedResponse();
        var projectedProvenance = RequiredString(provenance, "source") switch
        {
            "manual" => new JsonObject
            {
                ["source"] = "manual",
                ["sourceUrl"] = SanitizeUrlForOutput(RequiredString(provenance, "sourceUrl")),
                ["retrievedOn"] = RequiredString(provenance, "retrievedOn"),
            },
            "azure-retail-prices" => new JsonObject
            {
                ["source"] = "azure-retail-prices",
                ["region"] = RequiredString(provenance, "region"),
                ["meterIds"] = CopyStringArray(provenance, "meterIds"),
            },
            _ => throw UnexpectedResponse(),
        };
        return new JsonObject
        {
            ["runtimeRateCardSchemaVersion"] = PositiveInt(receipt, "runtimeRateCardSchemaVersion") == 1
                ? 1
                : throw UnexpectedResponse(),
            ["rateCard"] = projectedCard,
            ["provenance"] = projectedProvenance,
        };
    }

    private static JsonObject ProjectCredentialReceipt(
        JsonNode? body,
        string expectedScope,
        string? expectedProjectId,
        string expectedProviderId,
        Guid expectedOperationId,
        string expectedStatus)
    {
        if (body is not JsonObject receipt ||
            !Guid.TryParse(RequiredString(receipt, "operationId"), out var operationId) ||
            operationId != expectedOperationId ||
            RequiredString(receipt, "scope") != expectedScope ||
            OptionalString(receipt, "projectId") != expectedProjectId ||
            RequiredString(receipt, "providerId") != expectedProviderId)
            throw UnexpectedResponse();
        var status = RequiredString(receipt, "status");
        if (status is "quarantined")
            throw new InvalidOperationException("BYOK_QUARANTINED");
        if (status is "publication_pending")
            throw new InvalidOperationException("BYOK_PENDING_PUBLICATION");
        if (status != expectedStatus)
            throw UnexpectedResponse();
        var providerRevision = OptionalPositiveInt64(receipt, "providerRevision");
        var credentialRevision = OptionalPositiveInt64(receipt, "credentialRevision");
        var providerState = OptionalString(receipt, "providerState");
        if (providerState is not (null or "Draft" or "Available"))
            throw UnexpectedResponse();
        return new JsonObject
        {
            ["operationId"] = operationId.ToString("D"),
            ["status"] = status,
            ["scope"] = expectedScope,
            ["projectId"] = expectedProjectId,
            ["providerId"] = expectedProviderId,
            ["providerRevision"] = providerRevision,
            ["credentialRevision"] = credentialRevision,
            ["providerState"] = providerState,
        };
    }

    private static JsonObject ProjectSwitchReceipt(JsonNode? body, string? expectedActiveProviderId)
    {
        if (body is not JsonObject receipt)
            throw UnexpectedResponse();
        var mode = RequiredString(receipt, "mode");
        var activeProviderId = OptionalString(receipt, "activeProviderId");
        if (expectedActiveProviderId is not null &&
                (mode != "active" || activeProviderId != expectedActiveProviderId) ||
            expectedActiveProviderId is null &&
                (mode != "explicitHosted" || activeProviderId is not null))
            throw UnexpectedResponse();
        return new JsonObject
        {
            ["mode"] = mode,
            ["activeProviderId"] = activeProviderId,
            ["settingsRevision"] = PositiveInt64(receipt, "settingsRevision"),
        };
    }

    private static JsonArray CopyStringArray(JsonObject value, string name)
    {
        if (value[name] is not JsonArray source)
            throw UnexpectedResponse();
        var result = new JsonArray();
        foreach (var node in source)
        {
            if (node is not JsonValue item || !item.TryGetValue<string>(out var text) || text is null)
                throw UnexpectedResponse();
            result.Add(text);
        }
        return result;
    }

    private static JsonObject ProjectNumberObject(JsonObject value, string name)
    {
        if (value[name] is not JsonObject source || source.Count == 0)
            throw UnexpectedResponse();
        var result = new JsonObject();
        foreach (var property in source)
        {
            var number = RequiredNumber(source, property.Key);
            if (number <= 0)
                throw UnexpectedResponse();
            result[property.Key] = number;
        }
        return result;
    }

    private static decimal RequiredNumber(JsonObject value, string name) =>
        value[name] is JsonValue number && number.TryGetValue<decimal>(out var result)
            ? result
            : throw UnexpectedResponse();

    private static JsonNode? NullableNumber(JsonObject value, string name)
    {
        var node = value[name];
        return node is null
            ? null
            : node is JsonValue number &&
              (number.TryGetValue<decimal>(out _) || number.TryGetValue<double>(out var result) && double.IsFinite(result))
                ? node.DeepClone()
                : throw UnexpectedResponse();
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

    private static long NonNegativeInt64(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<long>(out var result) && result >= 0
            ? result
            : throw UnexpectedResponse();

    private static bool TryReadPositiveInt64(JsonObject value, string name, out long result)
    {
        result = 0;
        return value[name] is JsonValue json &&
               json.TryGetValue<long>(out result) &&
               result > 0;
    }

    private static long? OptionalPositiveInt64(JsonObject value, string name)
    {
        var node = value[name];
        if (node is null)
            return null;
        return node is JsonValue json && json.TryGetValue<long>(out var result) && result > 0
            ? result
            : throw UnexpectedResponse();
    }

    private static int PositiveInt(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<int>(out var result) && result > 0
            ? result
            : throw UnexpectedResponse();

    private static string SanitizeUrlForOutput(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
            throw UnexpectedResponse();
        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = "",
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool TryReadCredentialFreeHttpsUrl(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
            return false;
        uri = candidate;
        return true;
    }

    private static bool ValidProviderId(string value) =>
        value.Length is > 0 and <= 128 &&
        value is not ("." or "..") &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static InvalidDataException UnexpectedResponse() =>
        new("The Gateway returned an invalid BYOK response.");

    private static async Task<int> RunSetKeyAsync(
        IGatewayClient gateway,
        CliRuntimeOptions runtime,
        TextWriter output,
        TextWriter error,
        bool projectScope,
        string? projectId,
        string providerId,
        CliOptions options,
        CancellationToken ct)
    {
        var scopeName = projectScope ? "project" : "platform";
        var prefix = projectScope ? "Project" : "Platform";
        if (!options.HasFlag("from-stdin"))
            throw new CliUsageException(Usage(scopeName, "set-key"));
        var redirected = runtime.StandardInputRedirected ?? Console.IsInputRedirected;
        if (!redirected)
            throw new CliUsageException("Pipe the API key to redirected stdin using --from-stdin.");

        var expectedRevision = ReadPositiveRevision(options.Single("expected-revision"), "--expected-revision");
        var operationId = RequiredOperationId(options.Single("operation-id"));
        var pathParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["providerId"] = providerId,
        };
        if (projectScope)
            pathParams["projectId"] = projectId!;

        // Verify target provider revision and operation status before reading stdin.
        var draftResponse = await gateway.InvokeAsync(
            $"read{prefix}ByokProviderDraft",
            new GatewayInvocation(pathParams),
            ct).ConfigureAwait(false);
        EnsureSuccess(draftResponse, scopeName, "show", operationId, providerId);
        var draft = ProjectProvider(draftResponse.Body, projectScope ? "project" : "platform", projectId, providerId);
        var currentRevision = draft["revision"]?.GetValue<long>() ?? 0;
        if (currentRevision != expectedRevision)
            throw new InvalidOperationException("BYOK_CONFLICT");

        var statusPathParams = new Dictionary<string, string>(pathParams, StringComparer.Ordinal)
        {
            ["operationId"] = operationId.ToString("D"),
        };
        var existingStatus = await gateway.InvokeAsync(
            $"read{prefix}ByokProviderCredentialOperation",
            new GatewayInvocation(statusPathParams),
            ct).ConfigureAwait(false);
        if (existingStatus.Status == 200)
        {
            var projected = ProjectOperationStatus(
                existingStatus.Body, scopeName, projectId, providerId, operationId);
            var statusText = projected["status"]!.GetValue<string>();
            await error.WriteLineAsync(
                $"Operation {operationId:D} already exists with status '{statusText}'; no key was read or submitted.")
                .ConfigureAwait(false);
            return 1;
        }
        if (existingStatus.Status != 404)
        {
            EnsureSuccess(existingStatus, scopeName, "set-key", operationId, providerId);
            return 1;
        }

        byte[]? keyBytes = null;
        try
        {
            var stdin = runtime.StandardInput ?? Console.OpenStandardInput();
            keyBytes = await ReadUtf8SecretFromStdinAsync(stdin, ct).ConfigureAwait(false);

            var putHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["If-Match"] = expectedRevision.ToString(CultureInfo.InvariantCulture),
                ["Idempotency-Key"] = operationId.ToString("D"),
            };

            GatewayResponse? putResponse = null;
            try
            {
                putResponse = await gateway.InvokeOctetStreamAsync(
                    $"set{prefix}ByokProviderCredential",
                    new GatewayInvocation(pathParams, Headers: putHeaders),
                    keyBytes,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await error.WriteLineAsync(UnknownWriteMessage(operationId)).ConfigureAwait(false);
                return 130;
            }
            catch (Exception ex) when (ex is GatewayTransportException or InvalidDataException or JsonException)
            {
                return await RecoverSetKeyFromStatusAsync(
                    gateway, prefix, scopeName, projectId, providerId, operationId, statusPathParams, output, error, ct)
                    .ConfigureAwait(false);
            }

            if (putResponse.Status >= 500)
            {
                return await RecoverSetKeyFromStatusAsync(
                    gateway, prefix, scopeName, projectId, providerId, operationId, statusPathParams, output, error, ct)
                    .ConfigureAwait(false);
            }

            EnsureSuccess(putResponse, scopeName, "set-key", operationId, providerId);
            var projected = ProjectCredentialReceipt(
                putResponse.Body, projectScope ? "project" : "platform", projectId, providerId, operationId, "published");
            await WriteJsonAsync(output, projected).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (keyBytes is not null)
                CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static async Task<int> RecoverSetKeyFromStatusAsync(
        IGatewayClient gateway,
        string prefix,
        string scopeName,
        string? projectId,
        string providerId,
        Guid operationId,
        IReadOnlyDictionary<string, string> statusPathParams,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        try
        {
            var recovery = await gateway.InvokeAsync(
                $"read{prefix}ByokProviderCredentialOperation",
                new GatewayInvocation(statusPathParams),
                ct).ConfigureAwait(false);
            if (recovery.Status == 200)
            {
                var projected = ProjectOperationStatus(
                    recovery.Body, scopeName, projectId, providerId, operationId);
                var status = projected["status"]!.GetValue<string>();
                if (status == "published")
                {
                    await WriteJsonAsync(output, new JsonObject
                    {
                        ["operationId"] = projected["operationId"]!.DeepClone(),
                        ["status"] = status,
                        ["scope"] = projected["scope"]!.DeepClone(),
                        ["projectId"] = projected["projectId"]?.DeepClone(),
                        ["providerId"] = projected["providerId"]!.DeepClone(),
                        ["providerRevision"] = projected["providerRevision"]!.DeepClone(),
                    }).ConfigureAwait(false);
                    return 0;
                }
                await error.WriteLineAsync(
                    $"Operation {operationId:D} status is '{status}'. Do not resend the key.")
                    .ConfigureAwait(false);
                return 1;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync(UnknownWriteMessage(operationId)).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (ex is GatewayTransportException or InvalidDataException or JsonException)
        {
        }

        await error.WriteLineAsync(
            $"Operation {operationId:D} result is unknown. Check operation status before retrying; do not resend the key.")
            .ConfigureAwait(false);
        return 1;
    }

    private static async Task<byte[]> ReadUtf8SecretFromStdinAsync(Stream stdin, CancellationToken ct)
    {
        const int maxSecretBytes = 8 * 1024;
        const int maxRawBytes = maxSecretBytes + 2; // Allow one trailing CRLF before trimming.
        var buffer = new byte[maxRawBytes + 1];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stdin.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }

            if (total > maxRawBytes)
                throw new CliUsageException("The API key exceeds the 8192-byte limit.");

            var length = total;
            if (length >= 2 && buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
                length -= 2;
            else if (length >= 1 && buffer[length - 1] == (byte)'\n')
                length -= 1;

            if (length == 0)
                throw new CliUsageException("An API key is required on stdin.");
            if (length > maxSecretBytes)
                throw new CliUsageException("The API key exceeds the 8192-byte limit.");

            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            string decoded;
            try
            {
                decoded = strictUtf8.GetString(buffer, 0, length);
            }
            catch (DecoderFallbackException)
            {
                throw new CliUsageException("The API key must be valid UTF-8 text.");
            }
            if (string.IsNullOrWhiteSpace(decoded))
                throw new CliUsageException("The API key cannot be whitespace only.");

            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

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

    private sealed record RateCardAttachment(string? Id, string? Version);
    private sealed record SchemaInventory(string[] Properties, string[] Required);
    private sealed record OperationContract(
        string OperationId,
        string Method,
        string Path,
        string? RequestSchema,
        string? ResponseSchema,
        string[] ParameterNames,
        string[] Responses,
        int MaximumRequestBytes,
        int MaximumResponseBytes,
        bool BodyRequired,
        string[]? RequestMediaTypes = null);
}
