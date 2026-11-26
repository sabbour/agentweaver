using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli;

internal static class PolicyCommands
{
    private const int MaximumResponseBytes = 5 * 1024 * 1024;
    private const int MaximumInputFileBytes = 64 * 1024 * 1024;
    private const int MaximumDiagnosticCount = 32;
    private const int MaximumDiagnosticCodeLength = 128;
    private const int MaximumDiagnosticMessageLength = 2048;
    private const int MaximumTimestampLength = 33;
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(
        string[] args, CliRuntimeOptions runtime, HttpClient httpClient, TextWriter output, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("list" or "show" or "export" or "history" or "validate" or "apply"))
            throw new CliUsageException("Choose `list`, `show`, `export`, `history`, `validate`, or `apply`.");
        var command = args[0];
        var options = CliOptions.Parse(args[1..], new HashSet<string>(StringComparer.Ordinal));
        if (options.Many("run-id").Count > 0)
            throw new CliUsageException("Policy commands use an unbound identity. Do not set --run-id.");
        var scope = options.Single("scope");
        var projectScope = scope == "project";
        var projectList = projectScope && command == "list";
        var commandOptions = command switch
        {
            "list" => new[] { "scope", "page-size", "cursor" },
            "show" => new[] { "scope", "revision" },
            "export" => new[] { "scope", "revision", "output" },
            "history" => new[] { "scope", "page-size", "cursor" },
            "validate" => new[] { "scope", "input" },
            _ => new[] { "scope", "input", "expected-revision", "reason" },
        };
        options.RequireOnly(commandOptions.Concat(
            CliConfiguration.TargetOptionNames.Where(name =>
                name != "run-id" && (name != "project-id" || projectScope && !projectList))).ToArray());
        if (scope is not ("platform" or "project") ||
            scope == "platform" && command == "list" ||
            projectScope && command is not ("list" or "show" or "history" or "export" or "validate"))
            throw new CliUsageException("Use `--scope platform` for Platform commands or `--scope project` for Project reads.");
        if (!projectScope && options.Many("project-id").Count > 0)
            throw new CliUsageException("Platform Policy commands use an unbound identity. Do not set --project-id.");
        var projectId = projectScope && command is ("show" or "history" or "export" or "validate")
            ? ProjectIdArgument(options.Single("project-id"))
            : null;
        var outputPath = command == "export" ? OutputFilePath(options.Single("output")) : null;
        var inputPath = command is "validate" or "apply"
            ? InputFilePath(options.Single("input"), command)
            : null;
        var expectedRevision = command == "apply"
            ? ExpectedRevision(options.Single("expected-revision"))
            : null;
        var reason = command == "apply" ? ApplyReason(options.Single("reason")) : null;

        var revisionText = options.Single("revision");
        long? revision = null;
        if (revisionText is not null)
        {
            if (revisionText.Length == 0 || revisionText[0] == '0' ||
                revisionText.Any(c => c is < '0' or > '9') ||
                !long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw new CliUsageException("--revision must be a canonical positive decimal Int64.");
            revision = value;
        }
        var pageSizeText = options.Single("page-size");
        var pageSize = 20;
        if (pageSizeText is not null &&
            (pageSizeText.Length == 0 || pageSizeText.Any(c => c is < '0' or > '9') ||
             !int.TryParse(pageSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) ||
             pageSize is < 1 or > 100))
            throw new CliUsageException("--page-size must be an integer from 1 to 100.");
        var cursor = options.Single("cursor");
        if (cursor is not null && !ValidCursor(cursor, projectList ? 4096 : 2048))
            throw new CliUsageException("--cursor must be a non-empty cursor within the API limit.");
        var inputArtifact = inputPath is null
            ? null
            : await ReadInputArtifactAsync(
                inputPath,
                projectScope ? "project" : "platform",
                projectId ?? "default",
                ct).ConfigureAwait(false);
        var validationBody = command == "validate"
            ? new JsonObject { ["artifact"] = inputArtifact!.DeepClone() }
            : null;
        if (projectScope && command == "validate" &&
            GatewayClient.SerializeRequestBody(validationBody!).Length > MaximumResponseBytes)
            throw new PolicyFailureException("The policy validation request exceeds the 5 MiB limit.");
        var artifactHash = command == "apply" ? Hash(inputArtifact!) : null;

        var operationId = projectScope
            ? command switch
            {
                "list" => "listProjectNativeAcsPolicies",
                "history" => "listProjectNativeAcsPolicyRevisions",
                "validate" => "validateProjectNativeAcsPolicy",
                _ => revision is null ? "readProjectNativeAcsPolicy" : "readProjectNativeAcsPolicyRevision",
            }
            : command switch
        {
            "history" => "listPlatformNativeAcsPolicyRevisions",
            "validate" => "validatePlatformNativeAcsPolicy",
            "apply" => "writePlatformNativeAcsPolicy",
            _ => revision is null ? "readPlatformNativeAcsPolicy" : "readPlatformNativeAcsPolicyRevision",
        };
        var path = projectScope
            ? command switch
            {
                "list" => "/api/v1/policies/projects",
                "history" => "/api/v1/policies/projects/{projectId}/revisions",
                "validate" => "/api/v1/policies/projects/{projectId}/validate",
                _ => revision is null
                    ? "/api/v1/policies/projects/{projectId}"
                    : "/api/v1/policies/projects/{projectId}/revisions/{revision}",
            }
            : command switch
        {
            "history" => "/api/v1/policies/platform/revisions",
            "validate" => "/api/v1/policies/platform/validate",
            "apply" => "/api/v1/policies/platform",
            _ => revision is null ? "/api/v1/policies/platform" : "/api/v1/policies/platform/revisions/{revision}",
        };
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pageSizeText is not null)
            query["pageSize"] = pageSizeText;
        if (cursor is not null)
            query["cursor"] = cursor;
        var pathParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId is not null)
            pathParameters["projectId"] = projectId;
        if (revisionText is not null)
            pathParameters["revision"] = revisionText;
        var expectedScopeType = projectScope ? "project" : "platform";
        var expectedScopeId = projectId ?? "default";

        var identity = CliConfiguration.ReadIdentity(
            command == "apply" || projectScope && command == "validate" ? ["projects.admin"] : null,
            options: options,
            unbound: true);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);
        JsonObject result;
        PolicyApplyJournalEntry? applyJournal = null;
        try
        {
            var discovery = await gateway.DiscoverAsync(ct).ConfigureAwait(false);
            var operations = GatewayContract.Parse(discovery);
            if (!operations.TryGetValue(operationId, out var operation) || operation.Path != path ||
                operation.Parameters.Any(p => p.Location == "header" &&
                    p.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)) ||
                !ValidOperation(command, operation) ||
                projectScope && command == "validate" &&
                    !HasProjectValidationRequestSchema(discovery, operation))
                throw new PolicyFailureException("The Gateway does not support this policy operation.");
            GatewayInvocation invocation;
            if (command == "validate")
            {
                invocation = new GatewayInvocation(
                    pathParameters.Count == 0 ? null : pathParameters,
                    Body: validationBody);
            }
            else if (command == "apply")
            {
                applyJournal = await PolicyApplyJournal.PrepareAsync(
                    identity,
                    expectedRevision,
                    artifactHash!,
                    reason!,
                    runtime.PolicyApplyStateDirectory,
                    ct).ConfigureAwait(false);
                invocation = new GatewayInvocation(Body: new JsonObject
                {
                    ["expectedRevision"] = expectedRevision,
                    ["artifact"] = inputArtifact!.DeepClone(),
                    ["idempotencyKey"] = applyJournal.IdempotencyKey,
                    ["reason"] = reason,
                    ["correlationId"] = applyJournal.CorrelationId,
                });
            }
            else
            {
                invocation = new GatewayInvocation(pathParameters.Count == 0 ? null : pathParameters, query);
            }
            var response = await gateway.InvokeAsync(operationId, invocation, ct)
                .ConfigureAwait(false);
            if (command == "validate")
            {
                result = response.Status switch
                {
                    200 => ProjectValidationSuccess(response.Body, projectScope),
                    422 => ProjectValidationFailure(response.Body, projectScope),
                    _ => throw new PolicyFailureException(ValidationErrorMessage(response.Status, response.Body)),
                };
            }
            else if (command == "apply")
            {
                if (response.Status != 200)
                    throw new PolicyFailureException(ApplyErrorMessage(response.Status, response.Body));
                result = ProjectApply(response.Body, expectedRevision, artifactHash!);
            }
            else
            {
                if (response.Status != 200)
                    throw new PolicyFailureException(ErrorMessage(response.Status, response.Body));
                result = command switch
                {
                    "list" => ProjectList(response.Body, pageSize),
                    "history" => ProjectHistory(response.Body, pageSize, expectedScopeType, expectedScopeId),
                    "export" => ProjectExport(response.Body, revision, expectedScopeType, expectedScopeId),
                    _ => ProjectShow(response.Body, revision, expectedScopeType, expectedScopeId),
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PolicyFailureException)
        {
            throw;
        }
        catch (PolicyApplyJournalException exception)
        {
            throw new PolicyFailureException(exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new PolicyFailureException(command == "apply"
                ? "The policy apply failed. Its saved recovery record remains; rerun the same command to recover or complete the request."
                : "The policy request failed.");
        }

        if (command == "export")
        {
            try
            {
                await WriteNewFileAsync(
                    outputPath!,
                    Encoding.UTF8.GetBytes(result.ToJsonString(OutputJson) + Environment.NewLine),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw new PolicyFailureException("The policy export file could not be written.");
            }
            result = ExportSummary(result, outputPath!);
        }

        await output.WriteLineAsync(result.ToJsonString(OutputJson)).ConfigureAwait(false);
        if (applyJournal is not null)
            PolicyApplyJournal.Complete(applyJournal.Path);
        return command == "validate" && !result["isValid"]!.GetValue<bool>() ? 1 : 0;
    }

    private static bool ValidOperation(string command, GatewayOperation operation) =>
        command switch
        {
            "validate" => operation.Method == "POST" && operation.HasRequestBody && operation.BodyRequired &&
                          operation.MaximumRequestBytes <= MaximumResponseBytes &&
                          operation.MaximumResponseBytes <= MaximumResponseBytes,
            "apply" => operation.Method == "PUT" && operation.HasRequestBody && operation.BodyRequired &&
                       operation.MaximumRequestBytes <= MaximumResponseBytes &&
                       operation.MaximumResponseBytes <= MaximumResponseBytes,
            _ => operation.Method == "GET" && !operation.HasRequestBody &&
                 operation.MaximumResponseBytes <= MaximumResponseBytes,
        };

    private static bool HasProjectValidationRequestSchema(JsonNode discovery, GatewayOperation operation) =>
        operation.Document["requestBody"]?["content"]?["application/json"]?["schema"] is JsonObject schema &&
        GatewayContract.String(schema, "$ref") == "#/components/schemas/NativeAcsPolicyValidationRequest" &&
        discovery["components"] is JsonObject components &&
        components["schemas"] is JsonObject schemas &&
        schemas["NativeAcsPolicyValidationRequest"] is JsonObject request &&
        GatewayContract.String(request, "type") == "object" &&
        request["required"] is JsonArray { Count: 1 } required &&
        required[0] is JsonValue field && field.TryGetValue<string>(out var name) && name == "artifact" &&
        request["properties"] is JsonObject { Count: 1 } properties &&
        properties["artifact"] is JsonObject artifact &&
        GatewayContract.String(artifact, "$ref") == "#/components/schemas/NativeAcsArtifact" &&
        schemas["NativeAcsArtifact"] is JsonObject;

    private static string InputFilePath(string? value, string command)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliUsageException($"Policy {command} requires --input <export-file>.");
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliUsageException($"Policy {command} requires a valid input file path.");
        }
    }

    private static long? ExpectedRevision(string? value)
    {
        if (value is null)
            throw new CliUsageException("Policy apply requires --expected-revision <revision|none>.");
        if (value == "none")
            return null;
        if (value.Length == 0 || value[0] == '0' ||
            value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision <= 0)
            throw new CliUsageException("--expected-revision must be `none` or a canonical positive decimal Int64.");
        return revision;
    }

    private static string ApplyReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
            throw new CliUsageException("--reason must be non-blank and at most 1,024 characters.");
        return reason;
    }

    private static async Task<JsonObject> ReadInputArtifactAsync(
        string path,
        string expectedScopeType,
        string expectedScopeId,
        CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumInputFileBytes)
                throw new PolicyFailureException("The policy input file exceeds the 64 MiB limit or is empty.");
            try
            {
                var length = checked((int)stream.Length);
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                if (stream.Length != length)
                    throw UnexpectedInput();
                if (JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) is not JsonObject obj)
                    throw UnexpectedInput();

                var hasCreatedAt = obj.ContainsKey("createdAt");
                var hasUpdatedAt = obj.ContainsKey("updatedAt");
                var expectedFields = hasCreatedAt
                    ? new[] { "artifact", "contentSha256", "createdAt", "policyId", "revision", "scopeId", "scopeType" }
                    : new[] { "artifact", "contentSha256", "policyId", "revision", "scopeId", "scopeType", "updatedAt" };
                if (hasCreatedAt == hasUpdatedAt ||
                    obj.Count != expectedFields.Length ||
                    obj.Any(pair => !expectedFields.Contains(pair.Key, StringComparer.Ordinal)))
                    throw UnexpectedInput();
                var revision = PositiveNumber(obj, "revision");
                _ = Timestamp(obj, hasCreatedAt ? "createdAt" : "updatedAt");
                if (obj["artifact"] is not JsonObject artifact ||
                    artifact.Count != 3 ||
                    artifact.Any(pair => pair.Key is not ("contentSha256" or "files" or "manifestBytes")) ||
                    artifact["files"] is not JsonArray files ||
                    files.Any(item => item is not JsonObject file ||
                        file.Count != 2 || file.Any(pair => pair.Key is not ("relativePath" or "bytes"))))
                    throw UnexpectedInput();
                var exported = ProjectExport(obj, hasCreatedAt ? revision : null, expectedScopeType, expectedScopeId);
                return (JsonObject)exported["artifact"]!.DeepClone();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                throw UnexpectedInput();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PolicyFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new PolicyFailureException("The policy export file is invalid or could not be read.");
        }
    }

    private static string OutputFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliUsageException("Policy export requires --output <new-file>.");
        var path = Path.GetFullPath(value);
        if (File.Exists(path) || Directory.Exists(path))
            throw new CliUsageException("The policy export output file already exists.");
        return path;
    }

    private static JsonObject ProjectShow(
        JsonNode? body, long? requestedRevision, string expectedScopeType = "platform", string expectedScopeId = "default")
    {
        if (body is not JsonObject obj)
            throw UnexpectedResponse();
        var policyId = PolicyId(obj, expectedScopeType, expectedScopeId);
        var revision = PositiveNumber(obj, "revision");
        var hash = Hash(obj);
        var timeField = requestedRevision is null ? "updatedAt" : "createdAt";
        var time = Timestamp(obj, timeField);
        if (requestedRevision is not null && revision != requestedRevision ||
            obj["artifact"] is not JsonObject artifact ||
            artifact["manifestBytes"] is not JsonArray || artifact["files"] is not JsonArray ||
            Hash(artifact) != hash)
            throw UnexpectedResponse();
        return new JsonObject
        {
            ["scopeType"] = expectedScopeType, ["scopeId"] = expectedScopeId, ["policyId"] = policyId,
            ["revision"] = revision, ["contentSha256"] = hash, [timeField] = time,
        };
    }

    private static JsonObject ProjectExport(
        JsonNode? body, long? requestedRevision, string expectedScopeType = "platform", string expectedScopeId = "default")
    {
        if (body is not JsonObject obj)
            throw UnexpectedResponse();
        var policyId = PolicyId(obj, expectedScopeType, expectedScopeId);
        var revision = PositiveNumber(obj, "revision");
        var hash = Hash(obj);
        var timeField = requestedRevision is null ? "updatedAt" : "createdAt";
        var time = Timestamp(obj, timeField);
        if (requestedRevision is not null && revision != requestedRevision ||
            obj["artifact"] is not JsonObject artifact ||
            artifact["manifestBytes"] is not JsonArray manifestBytes ||
            manifestBytes.Count == 0 ||
            artifact["files"] is not JsonArray files ||
            files.Count > 64 ||
            Hash(artifact) != hash)
            throw UnexpectedResponse();
        foreach (var item in files)
        {
            if (item is not JsonObject file ||
                file["relativePath"] is not JsonValue path ||
                !path.TryGetValue<string>(out var relativePath) ||
                string.IsNullOrEmpty(relativePath) ||
                file["bytes"] is not JsonArray)
                throw UnexpectedResponse();
        }

        NativeAcsArtifact artifactValue;
        string computedHash;
        try
        {
            artifactValue = JsonSerializer.Deserialize<NativeAcsArtifact>(artifact.ToJsonString(), OutputJson)
                ?? throw UnexpectedResponse();
            if (artifactValue.ManifestBytes.IsDefaultOrEmpty)
                throw UnexpectedResponse();
            computedHash = NativeAcsArtifactHash.Compute(artifactValue);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw UnexpectedResponse();
        }
        if (!string.Equals(artifactValue.ContentSha256, hash, StringComparison.Ordinal) ||
            !string.Equals(computedHash, hash, StringComparison.Ordinal))
            throw UnexpectedResponse();

        obj.Remove("artifact");
        return new JsonObject
        {
            ["scopeType"] = expectedScopeType,
            ["scopeId"] = expectedScopeId,
            ["policyId"] = policyId,
            ["revision"] = revision,
            ["contentSha256"] = hash,
            ["artifact"] = artifact,
            [timeField] = time,
        };
    }

    private static JsonObject ExportSummary(JsonObject exported, string outputPath)
    {
        var timeField = exported.ContainsKey("updatedAt") ? "updatedAt" : "createdAt";
        var scopeType = String(exported, "scopeType");
        var scopeId = String(exported, "scopeId");
        return new JsonObject
        {
            ["scopeType"] = scopeType,
            ["scopeId"] = scopeId,
            ["policyId"] = PolicyId(exported, scopeType, scopeId),
            ["revision"] = PositiveNumber(exported, "revision"),
            ["contentSha256"] = Hash(exported),
            [timeField] = Timestamp(exported, timeField),
            ["output"] = outputPath,
        };
    }

    private static JsonObject ProjectApply(JsonNode? body, long? expectedRevision, string expectedHash)
    {
        if (body is not JsonObject obj)
            throw UnexpectedResponse();
        var hasCommittedFields =
            obj.ContainsKey("revision") || obj.ContainsKey("contentSha256") ||
            obj.ContainsKey("auditId") || obj.ContainsKey("outboxId");
        var hasPendingFields =
            obj.ContainsKey("preparationId") || obj.ContainsKey("proposedContentSha256") ||
            obj.ContainsKey("state") || obj.ContainsKey("expectedHeadPolicyId") ||
            obj.ContainsKey("expectedHeadRevision") || obj.ContainsKey("expectedHeadContentSha256") ||
            obj.ContainsKey("checkpoint") || obj.ContainsKey("createdAt") ||
            obj.ContainsKey("governanceDecisionId") || obj.ContainsKey("governanceDecisionHash");
        if (hasCommittedFields == hasPendingFields)
            throw UnexpectedResponse();

        CheckScope(obj);
        var policyId = PolicyId(obj);
        if (obj["replayed"] is not JsonValue replayedValue ||
            !replayedValue.TryGetValue<bool>(out var replayed))
            throw UnexpectedResponse();

        if (hasCommittedFields)
        {
            var revision = PositiveNumber(obj, "revision");
            var hash = Hash(obj);
            var nextRevision = expectedRevision is null
                ? 1
                : expectedRevision == long.MaxValue
                    ? throw UnexpectedResponse()
                    : expectedRevision.Value + 1;
            if (revision != nextRevision || hash != expectedHash ||
                !Guid.TryParse(String(obj, "auditId"), out _) ||
                !Guid.TryParse(String(obj, "outboxId"), out _))
                throw UnexpectedResponse();

            return new JsonObject
            {
                ["scopeType"] = "platform",
                ["scopeId"] = "default",
                ["policyId"] = policyId,
                ["revision"] = revision,
                ["contentSha256"] = hash,
                ["replayed"] = replayed,
            };
        }

        if (!Guid.TryParse(String(obj, "preparationId"), out var preparationId) || preparationId == Guid.Empty)
            throw UnexpectedResponse();
        var proposedHash = Hash(obj, "proposedContentSha256");
        if (proposedHash != expectedHash || String(obj, "state") != "pending")
            throw UnexpectedResponse();
        var createdAt = Timestamp(obj, "createdAt");

        var expectedHeadPolicyId = OptionalStringValue(obj, "expectedHeadPolicyId");
        var expectedHeadRevision = !obj.ContainsKey("expectedHeadRevision") || obj["expectedHeadRevision"] is null
            ? (long?)null
            : PositiveNumber(obj, "expectedHeadRevision");
        var expectedHeadHash = !obj.ContainsKey("expectedHeadContentSha256") || obj["expectedHeadContentSha256"] is null
            ? null
            : Hash(obj, "expectedHeadContentSha256");
        if (expectedRevision is null)
        {
            if (expectedHeadPolicyId is not null || expectedHeadRevision is not null || expectedHeadHash is not null)
                throw UnexpectedResponse();
        }
        else if (expectedHeadPolicyId != policyId ||
                 expectedHeadRevision != expectedRevision.Value ||
                 expectedHeadHash is null)
        {
            throw UnexpectedResponse();
        }

        var checkpoint = OptionalStringValue(obj, "checkpoint");
        var decisionIdText = OptionalStringValue(obj, "governanceDecisionId");
        var decisionHash = !obj.ContainsKey("governanceDecisionHash") || obj["governanceDecisionHash"] is null
            ? null
            : Hash(obj, "governanceDecisionHash");
        if ((decisionIdText is null) != (decisionHash is null))
            throw UnexpectedResponse();
        string? normalizedDecisionId = null;
        if (decisionIdText is not null)
        {
            if (!Guid.TryParse(decisionIdText, out var decisionId) || decisionId == Guid.Empty)
                throw UnexpectedResponse();
            normalizedDecisionId = decisionId.ToString("D");
        }

        return new JsonObject
        {
            ["scopeType"] = "platform",
            ["scopeId"] = "default",
            ["policyId"] = policyId,
            ["preparationId"] = preparationId.ToString("D"),
            ["state"] = "pending",
            ["expectedHeadPolicyId"] = expectedHeadPolicyId,
            ["expectedHeadRevision"] = expectedHeadRevision,
            ["expectedHeadContentSha256"] = expectedHeadHash,
            ["proposedContentSha256"] = proposedHash,
            ["checkpoint"] = checkpoint,
            ["createdAt"] = createdAt,
            ["replayed"] = replayed,
            ["governanceDecisionId"] = normalizedDecisionId,
            ["governanceDecisionHash"] = decisionHash,
        };
    }

    private static JsonObject ProjectValidationSuccess(JsonNode? body, bool projectScope)
    {
        if (body is not JsonObject obj ||
            obj["isValid"] is not JsonValue isValidValue ||
            !isValidValue.TryGetValue<bool>(out var isValid) || !isValid ||
            obj["diagnostics"] is not JsonArray diagnostics)
            throw UnexpectedResponse();
        return new JsonObject { ["isValid"] = true, ["diagnostics"] = ProjectDiagnostics(diagnostics, projectScope) };
    }

    private static JsonObject ProjectValidationFailure(JsonNode? body, bool projectScope)
    {
        if (body is not JsonObject obj ||
            obj["code"] is not JsonValue codeValue ||
            !codeValue.TryGetValue<string>(out var code) || code != "native_acs_policy_invalid" ||
            obj["diagnostics"] is not JsonArray diagnostics)
            throw UnexpectedResponse();
        return new JsonObject { ["isValid"] = false, ["diagnostics"] = ProjectDiagnostics(diagnostics, projectScope) };
    }

    private static JsonArray ProjectDiagnostics(JsonArray diagnostics, bool projectScope)
    {
        if (diagnostics.Count > MaximumDiagnosticCount)
            throw UnexpectedResponse();
        var projected = new JsonArray();
        foreach (var item in diagnostics)
        {
            if (item is not JsonObject diagnostic)
                throw UnexpectedResponse();
            var code = String(diagnostic, "code");
            var message = String(diagnostic, "message");
            if (code.Length is 0 or > MaximumDiagnosticCodeLength ||
                message.Length is 0 or > MaximumDiagnosticMessageLength)
                throw UnexpectedResponse();
            var relativePath = OptionalString(diagnostic, "relativePath");
            if (projectScope && relativePath is JsonValue path &&
                (!path.TryGetValue<string>(out var text) || !IsCanonicalRelativePath(text)))
                relativePath = null;
            projected.Add(new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["relativePath"] = relativePath,
                ["line"] = OptionalInt32(diagnostic, "line"),
                ["column"] = OptionalInt32(diagnostic, "column"),
            });
        }
        return projected;
    }

    private static bool IsCanonicalRelativePath(string path)
    {
        if (path.Length > 256 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl))
            return false;
        return path.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
    }

    private static JsonNode? OptionalString(JsonObject obj, string name) =>
        OptionalStringValue(obj, name) is { } text ? JsonValue.Create(text) : null;

    private static string? OptionalStringValue(JsonObject obj, string name) =>
        !obj.ContainsKey(name) || obj[name] is null
            ? null
            : obj[name] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : throw UnexpectedResponse();

    private static JsonNode? OptionalInt32(JsonObject obj, string name) =>
        !obj.ContainsKey(name) || obj[name] is null
            ? null
            : obj[name] is JsonValue value && value.TryGetValue<int>(out var number)
                ? JsonValue.Create(number)
                : throw UnexpectedResponse();

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }

    private static JsonObject ProjectList(JsonNode? body, int pageSize)
    {
        if (body is not JsonObject obj || obj.Count != 2 ||
            obj["items"] is not JsonArray items || items.Count > pageSize ||
            !obj.ContainsKey("nextCursor"))
            throw UnexpectedResponse();
        var next = obj["nextCursor"] is null ? null : String(obj, "nextCursor");
        if (next is not null && !ValidCursor(next, 4096))
            throw UnexpectedResponse();

        var projected = new JsonArray();
        string? previous = null;
        foreach (var item in items)
        {
            if (item is not JsonObject row || row.Count != 6 ||
                String(row, "scopeType") != "project")
                throw UnexpectedResponse();
            var scopeId = ProjectScopeId(row);
            if (previous is not null && string.CompareOrdinal(scopeId, previous) <= 0)
                throw UnexpectedResponse();
            previous = scopeId;
            projected.Add(new JsonObject
            {
                ["scopeType"] = "project",
                ["scopeId"] = scopeId,
                ["policyId"] = PolicyId(row, "project", scopeId),
                ["revision"] = PositiveNumber(row, "revision"),
                ["contentSha256"] = Hash(row),
                ["updatedAt"] = Timestamp(row, "updatedAt"),
            });
        }
        return new JsonObject { ["scopeType"] = "project", ["items"] = projected, ["nextCursor"] = next };
    }

    private static JsonObject ProjectHistory(
        JsonNode? body, int pageSize, string expectedScopeType = "platform", string expectedScopeId = "default")
    {
        if (body is not JsonObject obj || !obj.ContainsKey("policyId") ||
            !obj.ContainsKey("snapshotRevision") || !obj.ContainsKey("nextCursor") ||
            obj["items"] is not JsonArray items || items.Count > pageSize)
            throw UnexpectedResponse();
        CheckScope(obj, expectedScopeType, expectedScopeId);
        var policyId = obj["policyId"] is null ? null : PolicyId(obj, expectedScopeType, expectedScopeId);
        var snapshot = obj["snapshotRevision"] is null ? (long?)null : PositiveNumber(obj, "snapshotRevision");
        var next = obj["nextCursor"] is null ? null : String(obj, "nextCursor");
        if (next is not null && !ValidCursor(next) ||
            (policyId is null) != (snapshot is null) ||
            snapshot is null && (items.Count != 0 || next is not null))
            throw UnexpectedResponse();
        var projected = new JsonArray();
        long? previous = null;
        foreach (var item in items)
        {
            if (item is not JsonObject row)
                throw UnexpectedResponse();
            var revision = PositiveNumber(row, "revision");
            if (revision > snapshot || previous is not null && revision >= previous)
                throw UnexpectedResponse();
            previous = revision;
            projected.Add(new JsonObject
            {
                ["revision"] = revision, ["contentSha256"] = Hash(row), ["createdAt"] = Timestamp(row, "createdAt"),
            });
        }
        return new JsonObject
        {
            ["scopeType"] = expectedScopeType, ["scopeId"] = expectedScopeId, ["policyId"] = policyId,
            ["snapshotRevision"] = snapshot, ["items"] = projected, ["nextCursor"] = next,
        };
    }

    private static void CheckScope(JsonObject obj, string expectedScopeType = "platform", string expectedScopeId = "default")
    {
        if (String(obj, "scopeType") != expectedScopeType || String(obj, "scopeId") != expectedScopeId)
            throw UnexpectedResponse();
    }

    private static string PolicyId(
        JsonObject obj, string expectedScopeType = "platform", string expectedScopeId = "default")
    {
        CheckScope(obj, expectedScopeType, expectedScopeId);
        var id = String(obj, "policyId");
        if (id.Length is 0 or > 128 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw UnexpectedResponse();
        return id;
    }

    private static string Hash(JsonObject obj, string name = "contentSha256")
    {
        var hash = String(obj, name);
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw UnexpectedResponse();
        return hash;
    }

    private static long PositiveNumber(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value || !value.TryGetValue<long>(out var number) || number <= 0)
            throw UnexpectedResponse();
        return number;
    }

    private static string Timestamp(JsonObject obj, string name)
    {
        var text = String(obj, name);
        if (text.Length > MaximumTimestampLength)
            throw UnexpectedResponse();
        var parseText = text.EndsWith('Z') ? text[..^1] + "+00:00" : text;
        if (!DateTimeOffset.TryParseExact(
                parseText, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw UnexpectedResponse();
        return text;
    }

    private static string String(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw UnexpectedResponse();

    private static bool ValidCursor(string value) =>
        ValidCursor(value, 2048);

    private static bool ValidCursor(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string ProjectIdArgument(string? value)
    {
        if (!Guid.TryParseExact(value, "N", out var projectId))
            throw new CliUsageException("Project Policy reads require --project-id <32-character ID>.");
        return projectId.ToString("N");
    }

    private static string ProjectScopeId(JsonObject obj)
    {
        var value = String(obj, "scopeId");
        if (!Guid.TryParseExact(value, "N", out var projectId) ||
            !string.Equals(projectId.ToString("N"), value, StringComparison.Ordinal))
            throw UnexpectedResponse();
        return value;
    }

    private static string ErrorMessage(int status, JsonNode? body)
    {
        if (status == 403)
            return "The policy request was denied (HTTP 403).";
        var code = (body as JsonObject)?["code"] is JsonValue value &&
            value.TryGetValue<string>(out var text) ? text : null;
        var known = (status, code) is
            (404, "native_acs_policy_not_found") or
            (500, "native_acs_policy_corrupt") or
            (503, "native_acs_policy_source_unavailable") or
            (400, "native_acs_policy_read_request_invalid") or
            (400, "native_acs_policy_scope_list_request_invalid") or
            (400, "native_acs_policy_history_request_invalid") or
            (400, "native_acs_policy_revision_request_invalid");
        return known ? $"The policy request failed (HTTP {status}, {code})." :
            $"The policy request failed (HTTP {status}).";
    }

    private static string ValidationErrorMessage(int status, JsonNode? body)
    {
        if (status == 403)
            return "The policy validation was denied (HTTP 403).";
        var code = (body as JsonObject)?["code"] is JsonValue value &&
            value.TryGetValue<string>(out var text) ? text : null;
        var known = (status, code) is
            (400, "native_acs_policy_invalid") or
            (429, "native_acs_validation_rate_limited") or
            (503, "native_acs_dependency_unavailable");
        return known ? $"The policy validation failed (HTTP {status}, {code})." :
            $"The policy validation failed (HTTP {status}).";
    }

    private static string ApplyErrorMessage(int status, JsonNode? body)
    {
        var code = (body as JsonObject)?["code"] is JsonValue value &&
            value.TryGetValue<string>(out var text) ? text : null;
        if (status == 403)
            return "The policy apply was denied (HTTP 403). The saved recovery record remains; rerun the same command after authorization is restored.";
        if (status == 409)
            return "The Platform Policy write conflicted (HTTP 409). Read the current revision before choosing a new --expected-revision; the saved recovery record remains.";
        if (status == 503 && code == "native_acs_policy_writes_unavailable")
            return "Platform Policy writes are disabled on this deployment (native_acs_policy_writes_unavailable). The saved recovery record remains; rerun the same command when writes are enabled.";
        if (status == 422)
            return "The Platform Policy artifact was rejected (HTTP 422). Validate the artifact before applying it; the saved recovery record remains.";
        return $"The policy apply failed (HTTP {status}). The saved recovery record remains; rerun the same command to recover or complete the request.";
    }

    private static PolicyFailureException UnexpectedResponse() => new("The Gateway returned an invalid policy response.");
    private static PolicyFailureException UnexpectedInput() => new("The policy export file is invalid or could not be read.");
    private sealed class PolicyFailureException(string message) : InvalidOperationException(message);
}
