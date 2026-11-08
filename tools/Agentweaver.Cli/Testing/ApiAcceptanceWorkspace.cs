using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

internal static class ApiAcceptanceWorkspace
{
    private const int MaximumRefPages = 100;
    private const int MaximumRefPageSize = 100;
    private const int MaximumTreeNodes = 25_000;
    private const int MaximumFileBytes = 1024 * 1024;
    private const string SelectionHeader = ProjectTeamSyncContract.SelectionCodeHeader;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WorkspaceJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static async Task<(string Status, string? Failure)> RunAsync(
        AcceptanceManifest manifest,
        IGatewayClient api,
        CliIdentity identity,
        ICliAuthentication authentication,
        ApiTestOptions options,
        List<JsonObject> steps,
        JsonObject observations,
        CancellationToken ct)
    {
        if (options.Interaction != InteractionPolicy.Allow)
        {
            ApiAcceptanceSupport.AddStep(steps, "workspace-selection-interaction", "blocked");
            return ("blocked", "A fresh repository review requires explicit --interactive permission.");
        }
        if (authentication is not INativeRepoAppSelectionHandoff handoff)
        {
            ApiAcceptanceSupport.AddStep(steps, "workspace-selection-handoff", "blocked");
            return ("blocked", "The native sign-in service cannot provide a fresh repository review.");
        }

        JsonObject workspace = (JsonObject)manifest.FixturePlan["workspace"]!;
        string projectId = ApiAcceptanceSupport.GetString(workspace, "projectId")!;
        string expectedRepository = ApiAcceptanceSupport.GetString(workspace, "repositoryFullName")!;
        string expectedRefKind = ApiAcceptanceSupport.GetString(workspace, "refKind")!;
        string expectedRefName = ApiAcceptanceSupport.GetString(workspace, "refName")!;
        string expectedCommit = ApiAcceptanceSupport.GetString(workspace, "commitSha")!;
        string filePath = ApiAcceptanceSupport.GetString(workspace, "filePath")!;
        string expectedDigest = ApiAcceptanceSupport.GetString(workspace, "contentSha256")!;
        long expectedProjectRevision = ApiAcceptanceSupport.TryLong(workspace["expectedProjectRevision"], out var projectRevision)
            ? projectRevision
            : throw new InvalidDataException("The Workspace project revision is invalid.");
        long connectionRevision = ApiAcceptanceSupport.TryLong(workspace["connectionRevision"], out var revision)
            ? revision
            : throw new InvalidDataException("The Workspace connection revision is invalid.");
        long installationId = ApiAcceptanceSupport.TryLong(workspace["installationId"], out var installation)
            ? installation
            : throw new InvalidDataException("The Workspace installation ID is invalid.");
        long repositoryId = ApiAcceptanceSupport.TryLong(workspace["repositoryId"], out var repository)
            ? repository
            : throw new InvalidDataException("The Workspace repository ID is invalid.");
        long expectedByteLength = ApiAcceptanceSupport.TryLong(workspace["expectedByteLength"], out var byteLength)
            ? byteLength
            : throw new InvalidDataException("The Workspace byte length is invalid.");
        string connectionId = ApiAcceptanceSupport.GetString(workspace, "connectionId")!;
        string actorId = ApiAcceptanceSupport.GetString(manifest.Environment, "actorId")!;
        string tenantId = ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId")!;

        try
        {
            _ = await authentication.GetBearerAsync(identity, InteractionPolicy.Allow, ct).ConfigureAwait(false);
            JsonObject openApi = await api.DiscoverAsync(ct).ConfigureAwait(false);
            IReadOnlySet<string> operationIds = ApiAcceptanceJourney.GetOperationIds(openApi);
            string[] requiredOperations =
            [
                "getAuthorizationContext", "getProjectConfiguration",
                "listWorkspaceRepositoryRefs", "readWorkspaceRepositoryTree", "readWorkspaceRepositoryFile",
            ];
            string[] missing = requiredOperations.Where(operation => !operationIds.Contains(operation)).ToArray();
            if (missing.Length > 0)
                throw new WorkspaceJourneyException("blocked", "The live Gateway does not expose every required Workspace read operation.");
            ApiAcceptanceSupport.AddStep(steps, "workspace-live-capabilities", "passed",
                ("requiredOperationCount", JsonValue.Create(requiredOperations.Length)));

            JsonObject authContext = await ApiAcceptanceJourney.ValidateAuthContextAsync(api, ct).ConfigureAwait(false);
            ApiAcceptanceJourney.ValidateAuthContext(authContext, manifest);
            ApiAcceptanceSupport.AddStep(steps, "workspace-current-authorization", "passed");

            JsonObject configuration = await CallJsonAsync(api, "getProjectConfiguration",
                new GatewayInvocation(Path(("projectId", projectId))), "workspace-current-configuration", steps, ct)
                .ConfigureAwait(false);
            JsonObject configBody = configuration["configuration"] as JsonObject
                ?? throw new WorkspaceJourneyException("failed", "The current project configuration is incomplete.");
            JsonObject? sourceControl = configBody["sourceControl"] as JsonObject;
            JsonObject? repositoryConfig = sourceControl?["repository"] as JsonObject;
            string? configuredRepository = repositoryConfig is null
                ? null
                : ApiAcceptanceSupport.GetString(repositoryConfig, "owner") + "/" +
                    ApiAcceptanceSupport.GetString(repositoryConfig, "name");
            if (ApiAcceptanceSupport.GetString(configuration, "projectId") != projectId ||
                ApiAcceptanceSupport.TryLong(configuration["revision"], out var actualProjectRevision) is false ||
                actualProjectRevision != expectedProjectRevision ||
                ApiAcceptanceSupport.GetString(sourceControl ?? [], "authMode") != "githubApp" ||
                ApiAcceptanceSupport.GetString(sourceControl ?? [], "identityConnectionId") != connectionId ||
                !string.Equals(configuredRepository, expectedRepository, StringComparison.Ordinal))
                throw new WorkspaceJourneyException("failed", "The current project configuration does not match the approved Workspace fixture.");
            ApiAcceptanceSupport.AddStep(steps, "workspace-current-configuration", "passed",
                ("projectId", JsonValue.Create(projectId)),
                ("configurationRevision", JsonValue.Create(actualProjectRevision)),
                ("repository", JsonValue.Create(expectedRepository)),
                ("connectionId", JsonValue.Create(connectionId)),
                ("connectionRevision", JsonValue.Create(connectionRevision)));

            SourceControlWorkspaceRef? selectedRef = await FindExpectedRefAsync(
                api, handoff, identity, actorId, tenantId, projectId, connectionId, connectionRevision,
                installationId, repositoryId, expectedRepository, expectedRefKind, expectedRefName, expectedCommit,
                steps, ct).ConfigureAwait(false);
            if (selectedRef is null)
                throw new WorkspaceJourneyException("failed", "The approved repository ref was not found at the expected immutable commit.");

            SourceControlWorkspaceTree tree = await ReadTreeAsync(
                api, handoff, identity, actorId, tenantId, projectId, connectionId, connectionRevision,
                installationId, repositoryId, expectedRepository, expectedCommit, steps, ct).ConfigureAwait(false);
            var matches = tree.Items.Where(item => string.Equals(item.Path, filePath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Kind != SourceControlWorkspaceEntryKind.File)
                throw new WorkspaceJourneyException("failed", "The approved repository path is not one regular file.");
            if (matches[0].Size is { } treeSize && treeSize != expectedByteLength)
                throw new WorkspaceJourneyException("failed", "The approved repository file size does not match the fixture.");

            SourceControlWorkspaceFile file = await ReadFileAsync(
                api, handoff, identity, actorId, tenantId, projectId, connectionId, connectionRevision,
                installationId, repositoryId, expectedRepository, expectedCommit, filePath, steps, ct)
                .ConfigureAwait(false);
            byte[] contentBytes;
            try
            {
                if (file.IsBinary || file.Content is null || file.Size < 0 || file.Size > MaximumFileBytes)
                    throw new WorkspaceJourneyException("failed", "The approved repository file is not a supported bounded text file.");
                contentBytes = StrictUtf8.GetBytes(file.Content);
            }
            catch (EncoderFallbackException)
            {
                throw new WorkspaceJourneyException("failed", "The approved repository file is not valid UTF-8 text.");
            }

            try
            {
                if (!string.Equals(file.Repository, expectedRepository, StringComparison.Ordinal) ||
                    !string.Equals(file.Path, filePath, StringComparison.Ordinal) ||
                    !string.Equals(file.CommitSha, expectedCommit, StringComparison.Ordinal) ||
                    file.Size != expectedByteLength ||
                    contentBytes.LongLength != expectedByteLength ||
                    !string.Equals(Convert.ToHexStringLower(SHA256.HashData(contentBytes)), expectedDigest, StringComparison.Ordinal))
                    throw new WorkspaceJourneyException("failed", "The repository file content does not match the approved digest and size.");

                if (matches[0].BlobSha is { } blobSha &&
                    !string.Equals(ApiAcceptanceSupport.Sha1GitBlobHex(contentBytes), blobSha, StringComparison.Ordinal))
                    throw new WorkspaceJourneyException("failed", "The repository file does not match its commit-pinned Git blob.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contentBytes);
            }

            ApiAcceptanceSupport.AddStep(steps, "workspace-file-verified", "passed",
                ("repository", JsonValue.Create(expectedRepository)),
                ("refKind", JsonValue.Create(expectedRefKind)),
                ("refName", JsonValue.Create(expectedRefName)),
                ("commitSha", JsonValue.Create(expectedCommit)),
                ("filePath", JsonValue.Create(filePath)),
                ("byteLength", JsonValue.Create(expectedByteLength)),
                ("contentSha256", JsonValue.Create(expectedDigest)),
                ("gitBlobShaPresent", JsonValue.Create(matches[0].BlobSha is not null)));
            observations["workspace"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["configurationRevision"] = expectedProjectRevision,
                ["connectionId"] = connectionId,
                ["connectionRevision"] = connectionRevision,
                ["installationId"] = installationId,
                ["repositoryId"] = repositoryId,
                ["repository"] = expectedRepository,
                ["refKind"] = expectedRefKind,
                ["refName"] = expectedRefName,
                ["commitSha"] = expectedCommit,
                ["filePath"] = filePath,
                ["byteLength"] = expectedByteLength,
                ["contentSha256"] = expectedDigest,
            };
            return ("passed", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkspaceJourneyException error)
        {
            ApiAcceptanceSupport.AddStep(steps, "workspace-read-outcome", error.Status);
            return (error.Status, error.Message);
        }
        catch
        {
            ApiAcceptanceSupport.AddStep(steps, "workspace-read-outcome", "blocked");
            return ("blocked", "The Workspace repository read could not be completed.");
        }
    }

    private static async Task<SourceControlWorkspaceRef?> FindExpectedRefAsync(
        IGatewayClient api,
        INativeRepoAppSelectionHandoff handoff,
        CliIdentity identity,
        string actorId,
        string tenantId,
        string projectId,
        string connectionId,
        long connectionRevision,
        long installationId,
        long repositoryId,
        string repository,
        string refKind,
        string refName,
        string commitSha,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 1; pageNumber <= MaximumRefPages; pageNumber++)
        {
            var query = cursor is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["cursor"] = cursor };
            var response = await InvokeSelectedAsync(
                api, handoff, identity,
                CreateIntent(actorId, tenantId, projectId, connectionId, connectionRevision, installationId, repositoryId, repository,
                    "listWorkspaceRepositoryRefs"),
                "listWorkspaceRepositoryRefs",
                new GatewayInvocation(Path(("projectId", projectId)), query, Headers: null),
                $"workspace-refs-page-{pageNumber}", steps, ct).ConfigureAwait(false);
            var page = Deserialize<SourceControlWorkspaceRefsPage>(response, "listWorkspaceRepositoryRefs");
            if (page.Items.IsDefault || page.Items.Length > MaximumRefPageSize ||
                !string.Equals(page.Repository, repository, StringComparison.Ordinal))
                throw new WorkspaceJourneyException("failed", "The live repository ref page does not match the Workspace contract.");

            var expectedKind = refKind == "branch"
                ? SourceControlWorkspaceRefKind.Branch
                : SourceControlWorkspaceRefKind.Tag;
            var matches = page.Items.Where(item =>
                item.Kind == expectedKind &&
                string.Equals(item.Name, refName, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1)
                throw new WorkspaceJourneyException("failed", "The live repository ref page contains duplicate matching refs.");
            if (matches.Length == 1)
            {
                if (!string.Equals(matches[0].CommitSha, commitSha, StringComparison.Ordinal))
                    throw new WorkspaceJourneyException("failed", "The repository ref does not resolve to the expected immutable commit.");
                return matches[0];
            }

            var nextCursor = page.NextCursor;
            if (nextCursor is null) return null;
            if (nextCursor.Length is 0 or > 512 || !seenCursors.Add(nextCursor))
                throw new WorkspaceJourneyException("failed", "The repository ref page repeated an invalid continuation cursor.");
            cursor = nextCursor;
            if (pageNumber == MaximumRefPages)
                throw new WorkspaceJourneyException("blocked", "The repository ref list exceeded the native 100-page safety limit.");
        }
        throw new WorkspaceJourneyException("blocked", "The repository ref list exceeded the native 100-page safety limit.");
    }

    private static async Task<SourceControlWorkspaceTree> ReadTreeAsync(
        IGatewayClient api,
        INativeRepoAppSelectionHandoff handoff,
        CliIdentity identity,
        string actorId,
        string tenantId,
        string projectId,
        string connectionId,
        long connectionRevision,
        long installationId,
        long repositoryId,
        string repository,
        string commitSha,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        var response = await InvokeSelectedAsync(
            api, handoff, identity,
            CreateIntent(actorId, tenantId, projectId, connectionId, connectionRevision, installationId, repositoryId, repository,
                "readWorkspaceRepositoryTree"),
            "readWorkspaceRepositoryTree",
            new GatewayInvocation(
                Path(("projectId", projectId)),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["ref"] = commitSha }),
            "workspace-read-tree", steps, ct).ConfigureAwait(false);
        var tree = Deserialize<SourceControlWorkspaceTree>(response, "readWorkspaceRepositoryTree");
        if (tree.Items.IsDefault || tree.Items.Length > MaximumTreeNodes ||
            !string.Equals(tree.Repository, repository, StringComparison.Ordinal) ||
            !string.Equals(tree.Reference, commitSha, StringComparison.Ordinal) ||
            !string.Equals(tree.CommitSha, commitSha, StringComparison.Ordinal))
            throw new WorkspaceJourneyException("failed", "The live repository tree does not match the expected immutable commit.");
        ApiAcceptanceSupport.AddStep(steps, "workspace-read-tree", "passed",
            ("commitSha", JsonValue.Create(commitSha)),
            ("nodeCount", JsonValue.Create(tree.Items.Length)));
        return tree;
    }

    private static async Task<SourceControlWorkspaceFile> ReadFileAsync(
        IGatewayClient api,
        INativeRepoAppSelectionHandoff handoff,
        CliIdentity identity,
        string actorId,
        string tenantId,
        string projectId,
        string connectionId,
        long connectionRevision,
        long installationId,
        long repositoryId,
        string repository,
        string commitSha,
        string filePath,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        var response = await InvokeSelectedAsync(
            api, handoff, identity,
            CreateIntent(actorId, tenantId, projectId, connectionId, connectionRevision, installationId, repositoryId, repository,
                "readWorkspaceRepositoryFile"),
            "readWorkspaceRepositoryFile",
            new GatewayInvocation(
                Path(("projectId", projectId), ("path", filePath)),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["ref"] = commitSha }),
            "workspace-read-file", steps, ct).ConfigureAwait(false);
        var file = Deserialize<SourceControlWorkspaceFile>(response, "readWorkspaceRepositoryFile");
        ApiAcceptanceSupport.AddStep(steps, "workspace-read-file", "passed",
            ("commitSha", JsonValue.Create(commitSha)),
            ("filePath", JsonValue.Create(filePath)),
            ("byteLength", JsonValue.Create(file.Size)));
        return file;
    }

    private static async Task<GatewayResponse> InvokeSelectedAsync(
        IGatewayClient api,
        INativeRepoAppSelectionHandoff handoff,
        CliIdentity identity,
        NativeRepoAppSelectionIntent intent,
        string operation,
        GatewayInvocation invocation,
        string stepId,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        NativeRepoAppSelection selection;
        try
        {
            selection = await handoff.AcquireRepositorySelectionAsync(
                identity, intent, InteractionPolicy.Allow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ApiAcceptanceSupport.AddStep(steps, stepId, "blocked", ("operation", JsonValue.Create(operation)));
            throw new WorkspaceJourneyException("blocked", "A fresh browser-approved repository selection is not available.");
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SelectionHeader] = selection.Code,
        };
        try
        {
            if (selection.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new WorkspaceJourneyException("blocked", "The repository selection expired before the read.");
            var response = await api.InvokeAsync(operation, invocation with { Headers = headers }, ct).ConfigureAwait(false);
            if (response.Status != 200)
            {
                ApiAcceptanceSupport.AddStep(steps, stepId, response.Status is 401 or 403 ? "blocked" : "failed",
                    ("operation", JsonValue.Create(operation)),
                    ("httpStatus", JsonValue.Create(response.Status)));
                throw new WorkspaceJourneyException(
                    response.Status is 401 or 403 ? "blocked" : "failed",
                    "The Gateway did not complete the approved Workspace read.");
            }
            ApiAcceptanceSupport.AddStep(steps, stepId, "passed", ("operation", JsonValue.Create(operation)));
            return response;
        }
        finally
        {
            headers.Clear();
            selection.Dispose();
        }
    }

    private static async Task<JsonObject> CallJsonAsync(
        IGatewayClient api,
        string operation,
        GatewayInvocation invocation,
        string stepId,
        List<JsonObject> steps,
        CancellationToken ct)
    {
        var response = await api.InvokeAsync(operation, invocation, ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is not JsonObject body)
        {
            ApiAcceptanceSupport.AddStep(steps, stepId, response.Status is 401 or 403 ? "blocked" : "failed",
                ("operation", JsonValue.Create(operation)),
                ("httpStatus", JsonValue.Create(response.Status)));
            throw new WorkspaceJourneyException(
                response.Status is 401 or 403 ? "blocked" : "failed",
                "The Gateway did not return the current project configuration.");
        }
        return body;
    }

    private static T Deserialize<T>(GatewayResponse response, string operation)
    {
        if (response.Status != 200 || response.Body is null)
            throw new WorkspaceJourneyException(
                response.Status is 401 or 403 ? "blocked" : "failed",
                "The Gateway did not return the expected Workspace response.");
        try
        {
            return JsonSerializer.Deserialize<T>(response.Body.ToJsonString(), WorkspaceJson)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new WorkspaceJourneyException("failed", $"The {operation} response does not match its typed Workspace contract.");
        }
    }

    private static NativeRepoAppSelectionIntent CreateIntent(
        string actorId,
        string tenantId,
        string projectId,
        string connectionId,
        long connectionRevision,
        long installationId,
        long repositoryId,
        string repository,
        string operationId) =>
        new(1, actorId, tenantId, projectId, connectionId, connectionRevision,
            installationId, repositoryId, repository, operationId);

    private static IReadOnlyDictionary<string, string> Path(params (string Name, string Value)[] values) =>
        ApiAcceptanceJourney.Path(values);

    private sealed class WorkspaceJourneyException(string status, string message) : Exception(message)
    {
        public string Status { get; } = status;
    }
}
