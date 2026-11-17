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
        if (args.Length == 0 || args[0] is not ("show" or "export" or "history" or "validate" or "apply"))
            throw new CliUsageException("Choose `show`, `export`, `history`, `validate`, or `apply`.");
        var command = args[0];
        var options = CliOptions.Parse(args[1..], new HashSet<string>(StringComparer.Ordinal));
        if (options.Many("project-id").Count > 0 || options.Many("run-id").Count > 0)
            throw new CliUsageException("Policy commands use an unbound identity. Do not set --project-id or --run-id.");
        var commandOptions = command switch
        {
            "show" => new[] { "scope", "revision" },
            "export" => new[] { "scope", "revision", "output" },
            "history" => new[] { "scope", "page-size", "cursor" },
            "validate" => new[] { "scope", "input" },
            _ => new[] { "scope", "input", "expected-revision", "reason" },
        };
        options.RequireOnly(commandOptions.Concat(
            CliConfiguration.TargetOptionNames.Where(name => name is not ("project-id" or "run-id"))).ToArray());
        if (options.Single("scope") != "platform")
            throw new CliUsageException("Policy commands require --scope platform.");
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
        if (cursor is not null && !ValidCursor(cursor))
            throw new CliUsageException("--cursor must be a non-empty history cursor.");
        var inputArtifact = inputPath is null
            ? null
            : await ReadInputArtifactAsync(inputPath, ct, requirePlatform: command == "apply").ConfigureAwait(false);
        var artifactHash = command == "apply" ? Hash(inputArtifact!) : null;

        var operationId = command switch
        {
            "history" => "listPlatformNativeAcsPolicyRevisions",
            "validate" => "validatePlatformNativeAcsPolicy",
            "apply" => "writePlatformNativeAcsPolicy",
            _ => revision is null ? "readPlatformNativeAcsPolicy" : "readPlatformNativeAcsPolicyRevision",
        };
        var path = command switch
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
        var pathParameters = revisionText is null ? null :
            new Dictionary<string, string>(StringComparer.Ordinal) { ["revision"] = revisionText };

        var identity = CliConfiguration.ReadIdentity(
            command == "apply" ? ["projects.admin"] : null,
            options: options,
            unbound: true);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication))
            .Create(identity);
        JsonObject result;
        PolicyApplyJournalEntry? applyJournal = null;
        try
        {
            var operations = GatewayContract.Parse(await gateway.DiscoverAsync(ct).ConfigureAwait(false));
            if (!operations.TryGetValue(operationId, out var operation) || operation.Path != path ||
                operation.Parameters.Any(p => p.Location == "header" &&
                    p.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)) ||
                !ValidOperation(command, operation))
                throw new PolicyFailureException("The Gateway does not support this policy operation.");
            GatewayInvocation invocation;
            if (command == "validate")
            {
                invocation = new GatewayInvocation(Body: new JsonObject { ["artifact"] = inputArtifact!.DeepClone() });
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
                invocation = new GatewayInvocation(pathParameters, query);
            }
            var response = await gateway.InvokeAsync(operationId, invocation, ct)
                .ConfigureAwait(false);
            if (command == "validate")
            {
                result = response.Status switch
                {
                    200 => ProjectValidationSuccess(response.Body),
                    422 => ProjectValidationFailure(response.Body),
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
                    "history" => ProjectHistory(response.Body, pageSize),
                    "export" => ProjectExport(response.Body, revision),
                    _ => ProjectShow(response.Body, revision),
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
        CancellationToken ct,
        bool requirePlatform = false)
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
                if (requirePlatform &&
                    (String(obj, "scopeType") != "platform" || String(obj, "scopeId") != "default"))
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
                var exported = ProjectExport(obj, hasCreatedAt ? revision : null);
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

    private static JsonObject ProjectShow(JsonNode? body, long? requestedRevision)
    {
        if (body is not JsonObject obj)
            throw UnexpectedResponse();
        var policyId = PolicyId(obj);
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
            ["scopeType"] = "platform", ["scopeId"] = "default", ["policyId"] = policyId,
            ["revision"] = revision, ["contentSha256"] = hash, [timeField] = time,
        };
    }

    private static JsonObject ProjectExport(JsonNode? body, long? requestedRevision)
    {
        if (body is not JsonObject obj)
            throw UnexpectedResponse();
        var policyId = PolicyId(obj);
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
            ["scopeType"] = "platform",
            ["scopeId"] = "default",
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
        return new JsonObject
        {
            ["scopeType"] = "platform",
            ["scopeId"] = "default",
            ["policyId"] = PolicyId(exported),
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
        CheckScope(obj);
        var policyId = PolicyId(obj);
        var revision = PositiveNumber(obj, "revision");
        var hash = Hash(obj);
        var nextRevision = expectedRevision is null
            ? 1
            : expectedRevision == long.MaxValue
                ? throw UnexpectedResponse()
                : expectedRevision.Value + 1;
        if (revision != nextRevision || hash != expectedHash ||
            !Guid.TryParse(String(obj, "auditId"), out _) ||
            !Guid.TryParse(String(obj, "outboxId"), out _) ||
            obj["replayed"] is not JsonValue replayedValue ||
            !replayedValue.TryGetValue<bool>(out var replayed))
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

    private static JsonObject ProjectValidationSuccess(JsonNode? body)
    {
        if (body is not JsonObject obj ||
            obj["isValid"] is not JsonValue isValidValue ||
            !isValidValue.TryGetValue<bool>(out var isValid) || !isValid ||
            obj["diagnostics"] is not JsonArray diagnostics)
            throw UnexpectedResponse();
        return new JsonObject { ["isValid"] = true, ["diagnostics"] = ProjectDiagnostics(diagnostics) };
    }

    private static JsonObject ProjectValidationFailure(JsonNode? body)
    {
        if (body is not JsonObject obj ||
            obj["code"] is not JsonValue codeValue ||
            !codeValue.TryGetValue<string>(out var code) || code != "native_acs_policy_invalid" ||
            obj["diagnostics"] is not JsonArray diagnostics)
            throw UnexpectedResponse();
        return new JsonObject { ["isValid"] = false, ["diagnostics"] = ProjectDiagnostics(diagnostics) };
    }

    private static JsonArray ProjectDiagnostics(JsonArray diagnostics)
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
            projected.Add(new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["relativePath"] = OptionalString(diagnostic, "relativePath"),
                ["line"] = OptionalInt32(diagnostic, "line"),
                ["column"] = OptionalInt32(diagnostic, "column"),
            });
        }
        return projected;
    }

    private static JsonNode? OptionalString(JsonObject obj, string name) =>
        !obj.ContainsKey(name) || obj[name] is null
            ? null
            : obj[name] is JsonValue value && value.TryGetValue<string>(out var text)
                ? JsonValue.Create(text)
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

    private static JsonObject ProjectHistory(JsonNode? body, int pageSize)
    {
        if (body is not JsonObject obj || !obj.ContainsKey("policyId") ||
            !obj.ContainsKey("snapshotRevision") || !obj.ContainsKey("nextCursor") ||
            obj["items"] is not JsonArray items || items.Count > pageSize)
            throw UnexpectedResponse();
        CheckScope(obj);
        var policyId = obj["policyId"] is null ? null : PolicyId(obj);
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
            ["scopeType"] = "platform", ["scopeId"] = "default", ["policyId"] = policyId,
            ["snapshotRevision"] = snapshot, ["items"] = projected, ["nextCursor"] = next,
        };
    }

    private static void CheckScope(JsonObject obj)
    {
        if (String(obj, "scopeType") != "platform" || String(obj, "scopeId") != "default")
            throw UnexpectedResponse();
    }

    private static string PolicyId(JsonObject obj)
    {
        CheckScope(obj);
        var id = String(obj, "policyId");
        if (id.Length is 0 or > 128 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw UnexpectedResponse();
        return id;
    }

    private static string Hash(JsonObject obj)
    {
        var hash = String(obj, "contentSha256");
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
        value.Length is > 0 and <= 2048 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

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
