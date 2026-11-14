using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

/// <summary>
/// Reads the configured project repository through one fresh, single-use Workspace selection for each call.
/// </summary>
internal static partial class WorkspaceCommands
{
    private const string SelectionHeader = "X-Agentweaver-Team-Sync-Selection";
    private const string RefsOperation = "listWorkspaceRepositoryRefs";
    private const string TreeOperation = "readWorkspaceRepositoryTree";
    private const string FileOperation = "readWorkspaceRepositoryFile";
    private const string AccessDeniedMessage = "Sign-in was canceled or access was denied.";
    private const int MaximumRefsPerPage = 100;
    private const int MaximumTreeNodes = 25_000;
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly TimeSpan MinimumSelectionLifetime = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<string> TreeKinds =
        new(StringComparer.Ordinal) { "directory", "file", "symbolicLink", "submodule", "unsupported" };
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
        var request = Parse(args);
        var identity = CliConfiguration.ReadIdentity(["projects.admin"], options: request.Options, unbound: true);
        var authentication = runtime.Authentication;
        var handoff = runtime.RepositorySelection ?? authentication as INativeRepoAppSelectionHandoff;
        if (authentication is null || handoff is null)
            throw new InvalidOperationException("This CLI build cannot start a Workspace repository review.");

        _ = await authentication.GetBearerAsync(identity, InteractionPolicy.Allow, ct).ConfigureAwait(false);
        var gateway = (runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, authentication)).Create(identity);
        var operations = GatewayContract.Parse(await gateway.DiscoverAsync(ct).ConfigureAwait(false));
        if (!operations.ContainsKey("getAuthorizationContext") ||
            !operations.ContainsKey("getProjectConfiguration") ||
            !operations.ContainsKey(request.OperationId))
            throw new InvalidOperationException("The Gateway does not support Workspace repository review.");

        var context = await gateway.ValidateAuthorizationContextAsync(ct).ConfigureAwait(false);
        var actorId = GatewayContract.String(context, "actorId") ?? string.Empty;
        var tenantId = GatewayContract.String(context, "tenantId") ?? string.Empty;
        var (connectionId, repositoryFullName) = await ReadProjectRepositoryAsync(
            gateway, request.ProjectId, ct).ConfigureAwait(false);

        var intent = NativeRepoAppSelectionIntent.ForProjectRepository(
            actorId, tenantId, request.ProjectId, connectionId, repositoryFullName, request.OperationId);
        await error.WriteLineAsync(
            $"Approve the repository review in your browser: {repositoryFullName}, {request.OperationId}.")
            .ConfigureAwait(false);
        NativeRepoAppSelection selection;
        try
        {
            selection = await handoff.AcquireRepositorySelectionAsync(
                identity, intent, InteractionPolicy.Allow, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            !string.Equals(exception.Message, AccessDeniedMessage, StringComparison.Ordinal))
        {
            await error.WriteLineAsync(
                $"Check that the project repository {repositoryFullName} is visible to the GitHub App installation and that the connection is current.")
                .ConfigureAwait(false);
            throw;
        }

        GatewayResponse response;
        JsonObject selectionSummary;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (selection)
        {
            try
            {
                if (selection.Version != 2 ||
                    !string.Equals(selection.RepositoryFullName, repositoryFullName, StringComparison.Ordinal) ||
                    !string.Equals(selection.OperationId, request.OperationId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The Identity Broker returned an unexpected Workspace selection.");
                if (selection.ExpiresAt <= DateTimeOffset.UtcNow + MinimumSelectionLifetime)
                    throw new InvalidOperationException("The Workspace selection expired before use.");
                selectionSummary = new JsonObject
                {
                    ["version"] = selection.Version,
                    ["connectionRevision"] = selection.ConnectionRevision,
                    ["installationId"] = selection.InstallationId,
                    ["repositoryId"] = selection.RepositoryId,
                    ["repositoryFullName"] = selection.RepositoryFullName,
                    ["expiresAt"] = selection.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                };
                headers[SelectionHeader] = selection.Code;
                response = await gateway.InvokeAsync(
                    request.OperationId,
                    new GatewayInvocation(request.PathParameters, request.Query, null, headers),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                headers.Clear();
            }
        }

        RequireSuccess(response);
        var result = request.Command switch
        {
            "refs" => ValidateRefs(response.Body, repositoryFullName, request.Cursor),
            "tree" => ValidateTree(response.Body, repositoryFullName, request.Reference!),
            _ => await ReadFileAsync(response.Body, repositoryFullName, request, ct).ConfigureAwait(false),
        };
        var document = new JsonObject
        {
            ["status"] = "passed",
            ["operation"] = request.OperationId,
            ["selection"] = selectionSummary,
            ["result"] = result,
            ["evidence"] = response.Evidence.DeepClone(),
        };
        await output.WriteLineAsync(document.ToJsonString(OutputJson)).ConfigureAwait(false);
        return 0;
    }

    private static WorkspaceRequest Parse(string[] args)
    {
        if (args.Length == 0)
            throw new CliUsageException("Choose `refs`, `tree`, or `file`.");
        var command = args[0];
        var (operationId, commandOptions) = command switch
        {
            "refs" => (RefsOperation, new[] { "project", "cursor" }),
            "tree" => (TreeOperation, new[] { "project", "ref" }),
            "file" => (FileOperation, new[] { "project", "ref", "path", "output" }),
            _ => throw new CliUsageException($"Unknown Workspace command: {command}"),
        };

        var parsed = CliOptions.Parse(args[1..], new HashSet<string>(StringComparer.Ordinal) { "interactive" });
        if (parsed.Many("project-id").Count > 0 || parsed.Many("run-id").Count > 0)
            throw new CliUsageException("Workspace commands use an unbound identity. Use --project, not --project-id or --run-id.");
        parsed.RequireOnly(commandOptions
            .Append("interactive")
            .Concat(CliConfiguration.TargetOptionNames.Where(name => name is not ("project-id" or "run-id")))
            .ToArray());
        if (!parsed.HasFlag("interactive"))
            throw new CliUsageException("A fresh Workspace repository selection requires --interactive.");

        var projectValue = parsed.Single("project")
            ?? throw new CliUsageException("Workspace commands require --project <project-id>.");
        if (!Guid.TryParseExact(projectValue, "D", out var project))
            throw new CliUsageException("--project must be a project GUID.");
        var projectId = project.ToString("D");

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = 1;
        string? reference = null;
        string? filePath = null;
        string? outputPath = null;
        if (command == "refs" && parsed.Single("cursor") is { } cursorValue)
        {
            if (!CursorPattern().IsMatch(cursorValue))
                throw new CliUsageException("--cursor must be an integer from 2 to 10000.");
            cursor = int.Parse(cursorValue, NumberStyles.None, CultureInfo.InvariantCulture);
            query["cursor"] = cursorValue;
        }
        if (command is "tree" or "file")
        {
            var referenceValue = parsed.Single("ref");
            if (referenceValue is null || !CommitShaPattern().IsMatch(referenceValue))
                throw new CliUsageException("--ref must be a 40-character hexadecimal commit SHA.");
            reference = referenceValue.ToLowerInvariant();
            query["ref"] = reference;
        }
        var pathParameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["projectId"] = projectId };
        if (command == "file")
        {
            filePath = parsed.Single("path");
            if (filePath is null || !WorkspaceFilePath.IsSafe(filePath))
                throw new CliUsageException(
                    "--path must be a repository file path with no leading or trailing '/', no '\\', no empty, '.', or '..' segment, and no encoded separator.");
            pathParameters["path"] = filePath;
            if (parsed.Single("output") is { } outputValue)
            {
                outputPath = Path.GetFullPath(outputValue);
                if (File.Exists(outputPath) || Directory.Exists(outputPath))
                    throw new IOException("The output file already exists.");
            }
        }

        return new WorkspaceRequest(
            command, operationId, parsed, projectId, cursor, reference, filePath, outputPath, pathParameters, query);
    }

    private static async Task<(string ConnectionId, string RepositoryFullName)> ReadProjectRepositoryAsync(
        IGatewayClient gateway,
        string projectId,
        CancellationToken ct)
    {
        var response = await gateway.InvokeAsync(
            "getProjectConfiguration",
            new GatewayInvocation(new Dictionary<string, string>(StringComparer.Ordinal) { ["projectId"] = projectId }),
            ct).ConfigureAwait(false);
        RequireSuccess(response);
        var sourceControl = (response.Body as JsonObject)?["configuration"]?["sourceControl"] as JsonObject;
        var repository = sourceControl?["repository"] as JsonObject;
        var authMode = sourceControl is null ? null : GatewayContract.String(sourceControl, "authMode");
        var connectionId = sourceControl is null ? null : GatewayContract.String(sourceControl, "identityConnectionId");
        var owner = repository is null ? null : GatewayContract.String(repository, "owner");
        var name = repository is null ? null : GatewayContract.String(repository, "name");
        if (!string.Equals(authMode, "githubApp", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(connectionId) ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("The project does not use a GitHub App repository connection.");
        return (connectionId, owner + "/" + name);
    }

    private static void RequireSuccess(GatewayResponse response)
    {
        if (response.Status == (int)HttpStatusCode.OK)
            return;
        var code = response.Body is JsonObject problem ? GatewayContract.String(problem, "code") : null;
        throw new InvalidOperationException(code is null
            ? $"The Gateway request failed with HTTP status {response.Status}."
            : $"The Gateway request failed with HTTP status {response.Status} ({code}).");
    }

    private static JsonNode ValidateRefs(JsonNode? body, string repositoryFullName, int requestCursor)
    {
        if (body is not JsonObject page ||
            !SameText(page, "repository", repositoryFullName) ||
            page["items"] is not JsonArray items ||
            items.Count > MaximumRefsPerPage)
            throw UnexpectedResponse();
        foreach (var node in items)
        {
            if (node is not JsonObject item ||
                string.IsNullOrEmpty(GatewayContract.String(item, "name")) ||
                GatewayContract.String(item, "kind") is not ("branch" or "tag") ||
                !IsCommitSha(GatewayContract.String(item, "commitSha")))
                throw UnexpectedResponse();
        }
        if (page["nextCursor"] is { } next)
        {
            if (next is not JsonValue value ||
                !value.TryGetValue<string>(out var cursor) ||
                !CursorPattern().IsMatch(cursor) ||
                int.Parse(cursor, NumberStyles.None, CultureInfo.InvariantCulture) <= requestCursor)
                throw UnexpectedResponse();
        }
        return page.DeepClone();
    }

    private static JsonNode ValidateTree(JsonNode? body, string repositoryFullName, string reference)
    {
        if (body is not JsonObject tree ||
            !SameText(tree, "repository", repositoryFullName) ||
            !SameText(tree, "reference", reference) ||
            !SameText(tree, "commitSha", reference) ||
            tree["items"] is not JsonArray items ||
            items.Count > MaximumTreeNodes)
            throw UnexpectedResponse();
        foreach (var node in items)
        {
            if (node is not JsonObject item ||
                string.IsNullOrEmpty(GatewayContract.String(item, "path")) ||
                GatewayContract.String(item, "kind") is not { } kind ||
                !TreeKinds.Contains(kind) ||
                item["blobSha"] is { } blob && !IsCommitSha(blob is JsonValue blobValue &&
                    blobValue.TryGetValue<string>(out var blobSha) ? blobSha : null) ||
                item["size"] is { } size && !(size is JsonValue sizeValue &&
                    sizeValue.TryGetValue<long>(out var length) && length >= 0))
                throw UnexpectedResponse();
        }
        return tree.DeepClone();
    }

    private static async Task<JsonNode> ReadFileAsync(
        JsonNode? body,
        string repositoryFullName,
        WorkspaceRequest request,
        CancellationToken ct)
    {
        if (body is not JsonObject file ||
            !SameText(file, "repository", repositoryFullName) ||
            !string.Equals(GatewayContract.String(file, "path"), request.FilePath, StringComparison.Ordinal) ||
            !SameText(file, "commitSha", request.Reference!) ||
            file["size"] is not JsonValue sizeValue ||
            !sizeValue.TryGetValue<int>(out var size) ||
            size is < 0 or > MaximumFileBytes ||
            file["isBinary"] is not JsonValue binaryValue ||
            !binaryValue.TryGetValue<bool>(out var isBinary))
            throw UnexpectedResponse();
        string? content = null;
        try
        {
            if (file["content"] is { } contentNode &&
                !(contentNode is JsonValue contentValue && contentValue.TryGetValue(out content)))
                throw UnexpectedResponse();
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json rejects JSON strings that hold invalid UTF-16, such as a lone surrogate.
            throw UnexpectedResponse();
        }
        if (isBinary || content is null)
            throw new InvalidOperationException("The file is binary and cannot be read.");

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            throw UnexpectedResponse();
        }
        if (bytes.Length != size)
            throw UnexpectedResponse();

        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"blob {bytes.Length}\0"));
        var blob = new byte[header.Length + bytes.Length];
        header.CopyTo(blob, 0);
        bytes.CopyTo(blob, header.Length);
#pragma warning disable CA5350 // Git object identity uses SHA-1; it is compared, not used for security.
        var gitBlobSha1 = Convert.ToHexStringLower(SHA1.HashData(blob));
#pragma warning restore CA5350
        if (request.OutputPath is { } outputPath)
            await WriteNewFileAsync(outputPath, bytes, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["path"] = request.FilePath,
            ["commitSha"] = GatewayContract.String(file, "commitSha")!.ToLowerInvariant(),
            ["size"] = size,
            ["isBinary"] = false,
            ["language"] = file["language"] is JsonValue language && language.TryGetValue<string>(out var name) ? name : null,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["gitBlobSha1"] = gitBlobSha1,
            ["output"] = request.OutputPath,
        };
    }

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

    // GitHub owner and repository names, and hexadecimal SHAs, are not case-sensitive.
    private static bool SameText(JsonObject node, string key, string expected) =>
        string.Equals(GatewayContract.String(node, key), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsCommitSha(string? value) => value is not null && CommitShaPattern().IsMatch(value);

    private static InvalidDataException UnexpectedResponse() =>
        new("The Gateway returned an unexpected Workspace response.");

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    [GeneratedRegex("^(?:[2-9]|[1-9][0-9]{1,3}|10000)$", RegexOptions.CultureInvariant)]
    private static partial Regex CursorPattern();

    private sealed record WorkspaceRequest(
        string Command,
        string OperationId,
        CliOptions Options,
        string ProjectId,
        int Cursor,
        string? Reference,
        string? FilePath,
        string? OutputPath,
        IReadOnlyDictionary<string, string> PathParameters,
        IReadOnlyDictionary<string, string> Query);
}
