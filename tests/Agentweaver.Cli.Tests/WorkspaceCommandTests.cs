using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class WorkspaceCommandTests
{
    private const string ActorId = "11111111-1111-4111-8111-111111111111";
    private const string TenantId = "22222222-2222-4222-8222-222222222222";
    private const string ProjectId = "33333333-3333-4333-8333-333333333333";
    private const string ConnectionId = "connection_1";
    private const string Repository = "octo/repo";
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string SelectionCode = "f00dcafe0123456789abcdef0123456789abcdef0123456789abcdef0badc0de";
    private const string FileContent = "hello workspace\n";
    private static readonly string[] TargetOptions =
    [
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--client-id", "agentweaver-cli",
        "--api-audience", "https://gateway.example/",
        "--tenant-id", TenantId,
    ];

    [Fact]
    public async Task RefsBuildsVersion2IntentFromAuthorizationContextAndProjectConfiguration()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(0, result);
        var intent = Assert.Single(fixture.Handoff.Intents);
        Assert.Equal(2, intent.Version);
        Assert.Equal(ActorId, intent.ActorId);
        Assert.Equal(TenantId, intent.TenantId);
        Assert.Equal(ProjectId, intent.ProjectId);
        Assert.Equal(ConnectionId, intent.ConnectionId);
        Assert.Equal(Repository, intent.RepositoryFullName);
        Assert.Equal("listWorkspaceRepositoryRefs", intent.OperationId);
        Assert.Null(intent.ConnectionRevision);
        Assert.Null(intent.InstallationId);
        Assert.Null(intent.RepositoryId);
        Assert.Equal(InteractionPolicy.Allow, fixture.Authentication.Interactions[0]);
        Assert.All(fixture.Authentication.Interactions.Skip(1), interaction => Assert.Equal(InteractionPolicy.Never, interaction));
        Assert.Equal(InteractionPolicy.Allow, Assert.Single(fixture.Handoff.Interactions));
        Assert.Null(fixture.Handoff.Identities.Single().ProjectId);
        Assert.Equal(
            ["openid", "api.read", "offline_access", "projects.admin"],
            fixture.Handoff.Identities.Single().Scopes);
    }

    [Fact]
    public async Task RefsSendsSelectionHeaderOnceAndKeepsOnlySafeSelectionIdentity()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive", "--cursor", "2");

        Assert.Equal(0, result);
        var workspace = Assert.Single(fixture.Handler.WorkspaceRequests);
        Assert.Equal(SelectionCode, workspace.SelectionCode);
        Assert.Equal("?cursor=2", workspace.Query);
        Assert.Single(fixture.Handler.Requests, request => request.SelectionCode is not null);
        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal("passed", output["status"]!.GetValue<string>());
        Assert.Equal("listWorkspaceRepositoryRefs", output["operation"]!.GetValue<string>());
        var selection = output["selection"]!.AsObject();
        Assert.Equal(
            ["connectionRevision", "expiresAt", "installationId", "repositoryFullName", "repositoryId", "version"],
            selection.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.Equal(2, selection["version"]!.GetValue<int>());
        Assert.Equal(7, selection["connectionRevision"]!.GetValue<long>());
        Assert.Equal(70, selection["installationId"]!.GetValue<long>());
        Assert.Equal(700, selection["repositoryId"]!.GetValue<long>());
        Assert.Equal(Repository, selection["repositoryFullName"]!.GetValue<string>());
        Assert.NotNull(output["evidence"]);
        Assert.DoesNotContain(SelectionCode, fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SelectionCode, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Approve the repository review in your browser: octo/repo, listWorkspaceRepositoryRefs.",
            fixture.Error.ToString(),
            StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => fixture.Handoff.Selections.Single().Code);
    }

    [Fact]
    public async Task RefsTreeFileReadsRequestedCommitAndBoundedFile()
    {
        var fixture = new Fixture();

        Assert.Equal(0, await fixture.RunAsync("refs", "--project", ProjectId, "--interactive"));
        var refs = JsonNode.Parse(fixture.TakeOutput())!["result"]!["items"]!.AsArray();
        var commit = refs.Single(item => item!["name"]!.GetValue<string>() == "main")!["commitSha"]!.GetValue<string>();

        Assert.Equal(0, await fixture.RunAsync("tree", "--project", ProjectId, "--ref", commit.ToUpperInvariant(), "--interactive"));
        var tree = JsonNode.Parse(fixture.TakeOutput())!["result"]!.AsObject();
        Assert.Equal(commit, tree["commitSha"]!.GetValue<string>());
        var blobSha = tree["items"]!.AsArray()
            .Single(item => item!["path"]!.GetValue<string>() == "docs/a.md")!["blobSha"]!.GetValue<string>();

        Assert.Equal(0, await fixture.RunAsync(
            "file", "--project", ProjectId, "--ref", commit, "--path", "docs/a.md", "--interactive"));
        var fileOutput = fixture.TakeOutput();
        var file = JsonNode.Parse(fileOutput)!["result"]!.AsObject();
        var bytes = Encoding.UTF8.GetBytes(FileContent);
        Assert.Equal(bytes.Length, file["size"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file["sha256"]!.GetValue<string>());
        Assert.Equal(blobSha, file["gitBlobSha1"]!.GetValue<string>());
        Assert.Null(file["output"]);
        Assert.DoesNotContain(FileContent.Trim(), fileOutput, StringComparison.Ordinal);

        Assert.Equal(
            ["listWorkspaceRepositoryRefs", "readWorkspaceRepositoryTree", "readWorkspaceRepositoryFile"],
            fixture.Handoff.Intents.Select(intent => intent.OperationId));
        Assert.Equal(3, fixture.Handoff.Selections.Count);
        Assert.All(fixture.Handler.WorkspaceRequests.Skip(1), request => Assert.Equal($"?ref={commit}", request.Query));
    }

    [Fact]
    public async Task MissingInteractiveFailsBeforeAnyNetworkCall()
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync("refs", "--project", ProjectId);

        Assert.Equal(1, result);
        Assert.Contains("A fresh Workspace repository selection requires --interactive.", fixture.Error.ToString(), StringComparison.Ordinal);
        fixture.AssertNoNetworkOrApproval();
    }

    [Theory]
    [InlineData("--project-id", ProjectId)]
    [InlineData("--run-id", "run-1")]
    [InlineData("--scope", "projects.admin")]
    [InlineData("--header", "X-Agentweaver-Team-Sync-Selection=code")]
    public async Task ProjectAndRunBindingOptionsAreRejected(string option, string value)
    {
        var fixture = new Fixture();

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive", option, value);

        Assert.Equal(1, result);
        fixture.AssertNoNetworkOrApproval();
        if (option is "--project-id" or "--run-id")
            Assert.Contains("Workspace commands use an unbound identity.", fixture.Error.ToString(), StringComparison.Ordinal);
        else
            Assert.Contains($"Unknown option: {option}", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tree", "--ref", "abc123")]
    [InlineData("file", "--path", "..")]
    [InlineData("file", "--path", "/a.md")]
    [InlineData("file", "--path", "docs\\a.md")]
    [InlineData("file", "--path", "docs%2Fa.md")]
    [InlineData("file", "--path", "docs%2fa.md")]
    [InlineData("file", "--path", "docs%5Ca.md")]
    [InlineData("file", "--path", "docs%252Fa.md")]
    [InlineData("file", "--path", "%2E%2E/a.md")]
    [InlineData("file", "--path", "docs/%2e/a.md")]
    [InlineData("refs", "--cursor", "1")]
    [InlineData("refs", "--cursor", "01")]
    [InlineData("refs", "--cursor", "10001")]
    [InlineData("refs", "--project", "not-a-guid")]
    public async Task InvalidRefCursorAndPathFailBeforeNetwork(string command, string option, string value)
    {
        var fixture = new Fixture();
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["--project"] = ProjectId };
        if (command is "tree" or "file")
            arguments["--ref"] = CommitSha;
        if (command == "file")
            arguments["--path"] = "docs/a.md";
        arguments[option] = value;

        var result = await fixture.RunAsync(
            [command, "--interactive", .. arguments.SelectMany(item => new[] { item.Key, item.Value })]);

        Assert.Equal(1, result);
        fixture.AssertNoNetworkOrApproval();
    }

    [Fact]
    public async Task ExpiredSelectionIsDisposedAndNotSent()
    {
        var fixture = new Fixture();
        fixture.Handoff.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("The Workspace selection expired before use.", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Handler.WorkspaceRequests);
        Assert.Throws<ObjectDisposedException>(() => fixture.Handoff.Selections.Single().Code);
    }

    [Fact]
    public async Task MismatchedSelectionNameIsRejected()
    {
        var fixture = new Fixture();
        fixture.Handoff.RepositoryFullName = "Octo/Repo";

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("unexpected Workspace selection", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Handler.WorkspaceRequests);
        Assert.Throws<ObjectDisposedException>(() => fixture.Handoff.Selections.Single().Code);
    }

    [Fact]
    public async Task WrongTenantContextFailsBeforeSelection()
    {
        var fixture = new Fixture();
        fixture.Handler.ContextTenantId = "44444444-4444-4444-8444-444444444444";

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Empty(fixture.Handoff.Intents);
        Assert.Empty(fixture.Handler.WorkspaceRequests);
    }

    [Theory]
    [InlineData("""{"authMode":"userToken","identityConnectionId":"connection_1","repository":{"owner":"octo","name":"repo"}}""")]
    [InlineData("""{"authMode":"githubApp","repository":{"owner":"octo","name":"repo"}}""")]
    [InlineData("""{"authMode":"githubApp","identityConnectionId":"connection_1","repository":{"owner":"octo"}}""")]
    public async Task NonGitHubAppProjectFailsBeforeSelection(string sourceControl)
    {
        var fixture = new Fixture();
        fixture.Handler.SourceControl = sourceControl;

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("The project does not use a GitHub App repository connection.", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Handoff.Intents);
        Assert.Empty(fixture.Handler.WorkspaceRequests);
    }

    [Theory]
    [InlineData("The Identity Broker could not complete sign-in.", true)]
    [InlineData("Sign-in was canceled or access was denied.", false)]
    public async Task HandoffFailureDoesNotSendWorkspaceRequest(string message, bool expectsHint)
    {
        var fixture = new Fixture();
        fixture.Handoff.Failure = new InvalidOperationException(message);

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains(message, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(expectsHint, fixture.Error.ToString().Contains(
            "Check that the project repository octo/repo is visible to the GitHub App installation",
            StringComparison.Ordinal));
        Assert.Empty(fixture.Handler.WorkspaceRequests);
    }

    [Fact]
    public async Task TreeEchoMismatchIsRejected()
    {
        var fixture = new Fixture();
        fixture.Handler.TreeBody = TreeBody(commitSha: new string('b', 40));

        var result = await fixture.RunAsync("tree", "--project", ProjectId, "--ref", CommitSha, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("The Gateway returned an unexpected Workspace response.", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("hello", 4)]
    [InlineData("\\u00e9", 1)]
    [InlineData("\\ud800", 3)]
    public async Task FileSizeOrUtf8MismatchIsRejected(string jsonContent, int size)
    {
        var fixture = new Fixture();
        fixture.Handler.FileBody = FileBody("docs/a.md", jsonContent, size);

        var result = await fixture.RunAsync(
            "file", "--project", ProjectId, "--ref", CommitSha, "--path", "docs/a.md", "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("The Gateway returned an unexpected Workspace response.", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task BinaryFileFailsAndWritesNoOutput()
    {
        var fixture = new Fixture();
        fixture.Handler.FileBody = $$"""{"repository":"{{Repository}}","path":"docs/a.md","commitSha":"{{CommitSha}}","content":null,"isBinary":true,"language":null,"size":4}""";
        var directory = CreateTestDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "a.md");

            var result = await fixture.RunAsync(
                "file", "--project", ProjectId, "--ref", CommitSha, "--path", "docs/a.md",
                "--output", outputPath, "--interactive");

            Assert.Equal(1, result);
            Assert.Contains("The file is binary and cannot be read.", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileOutputUsesCreateNewAndRefusesExistingFile()
    {
        var directory = CreateTestDirectory();
        try
        {
            var existing = Path.Combine(directory, "existing.md");
            await File.WriteAllTextAsync(existing, "keep");
            var refused = new Fixture();

            var refusedResult = await refused.RunAsync(
                "file", "--project", ProjectId, "--ref", CommitSha, "--path", "docs/a.md",
                "--output", existing, "--interactive");

            Assert.Equal(1, refusedResult);
            Assert.Contains("The output file already exists.", refused.Error.ToString(), StringComparison.Ordinal);
            refused.AssertNoNetworkOrApproval();
            Assert.Equal("keep", await File.ReadAllTextAsync(existing));

            var created = Path.Combine(directory, "created.md");
            var fixture = new Fixture();
            var result = await fixture.RunAsync(
                "file", "--project", ProjectId, "--ref", CommitSha, "--path", "docs/a.md",
                "--output", created, "--interactive");

            Assert.Equal(0, result);
            Assert.Equal(Encoding.UTF8.GetBytes(FileContent), await File.ReadAllBytesAsync(created));
            Assert.Equal(Path.GetFullPath(created), JsonNode.Parse(fixture.Output.ToString())!["result"]!["output"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NestedPathKeepsSlashSeparators()
    {
        var fixture = new Fixture();
        fixture.Handler.FileBody = FileBody("docs/a b.md", "hello workspace\\n", Encoding.UTF8.GetByteCount(FileContent));

        var result = await fixture.RunAsync(
            "file", "--project", ProjectId, "--ref", CommitSha, "--path", "docs/a b.md", "--interactive");

        Assert.Equal(0, result);
        var request = Assert.Single(fixture.Handler.WorkspaceRequests);
        Assert.EndsWith("/files/docs/a%20b.md", request.RawPath, StringComparison.Ordinal);
        Assert.DoesNotContain("%2F", request.RawPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionIsDisposedWhenGatewayFails()
    {
        var fixture = new Fixture();
        fixture.Handler.WorkspaceStatus = HttpStatusCode.InternalServerError;

        var result = await fixture.RunAsync("refs", "--project", ProjectId, "--interactive");

        Assert.Equal(1, result);
        Assert.Contains("HTTP status 500", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Single(fixture.Handler.WorkspaceRequests);
        Assert.Throws<ObjectDisposedException>(() => fixture.Handoff.Selections.Single().Code);
    }

    private static string TreeBody(string commitSha = CommitSha)
    {
        var bytes = Encoding.UTF8.GetBytes(FileContent);
        return $$"""
            {"repository":"{{Repository}}","reference":"{{commitSha}}","commitSha":"{{commitSha}}","items":[
              {"path":"docs","kind":"directory","size":null,"blobSha":null},
              {"path":"docs/a.md","kind":"file","size":{{bytes.Length}},"blobSha":"{{GitBlobSha1(bytes)}}"}
            ]}
            """;
    }

    private static string FileBody(string path, string jsonContent, int size) =>
        $$"""{"repository":"{{Repository}}","path":"{{path}}","commitSha":"{{CommitSha}}","content":"{{jsonContent}}","isBinary":false,"language":"markdown","size":{{size}}}""";

    private static string GitBlobSha1(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
#pragma warning disable CA5350
        return Convert.ToHexStringLower(SHA1.HashData(header.Concat(bytes).ToArray()));
#pragma warning restore CA5350
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "workspace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class Fixture
    {
        public WorkspaceGatewayHandler Handler { get; } = new();
        public FakeAuthentication Authentication { get; } = new();
        public FakeHandoff Handoff { get; } = new();
        public StringWriter Output { get; private set; } = new();
        public StringWriter Error { get; } = new();

        public Task<int> RunAsync(params string[] arguments)
        {
            var http = new HttpClient(Handler);
            return CliApplication.RunAsync(
                ["workspace", .. arguments, .. TargetOptions],
                new CliRuntimeOptions(
                    HttpClient: http,
                    Authentication: Authentication,
                    Output: Output,
                    Error: Error,
                    RepositorySelection: Handoff));
        }

        public string TakeOutput()
        {
            var value = Output.ToString();
            Output = new StringWriter();
            return value;
        }

        public void AssertNoNetworkOrApproval()
        {
            Assert.Empty(Handler.Requests);
            Assert.Empty(Handoff.Intents);
            Assert.Empty(Authentication.Interactions);
        }
    }

    private sealed class FakeAuthentication : ICliAuthentication
    {
        public List<InteractionPolicy> Interactions { get; } = [];

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            Interactions.Add(interaction);
            return Task.FromResult("admin-bearer");
        }
    }

    private sealed class FakeHandoff : INativeRepoAppSelectionHandoff
    {
        public List<NativeRepoAppSelectionIntent> Intents { get; } = [];
        public List<CliIdentity> Identities { get; } = [];
        public List<InteractionPolicy> Interactions { get; } = [];
        public List<NativeRepoAppSelection> Selections { get; } = [];
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? RepositoryFullName { get; set; }
        public Exception? Failure { get; set; }

        public Task<NativeRepoAppSelection> AcquireRepositorySelectionAsync(
            CliIdentity identity,
            NativeRepoAppSelectionIntent intent,
            InteractionPolicy interaction,
            CancellationToken ct)
        {
            Intents.Add(intent);
            Identities.Add(identity);
            Interactions.Add(interaction);
            if (Failure is not null)
                throw Failure;
            var selection = new NativeRepoAppSelection(
                2, intent.ActorId, intent.TenantId, intent.ProjectId, intent.OperationId, SelectionCode,
                intent.ConnectionId, 7, 70, 700, RepositoryFullName ?? intent.RepositoryFullName,
                ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10));
            Selections.Add(selection);
            return Task.FromResult(selection);
        }
    }

    private sealed record RequestSnapshot(string RawPath, string Query, string? SelectionCode);

    private sealed class WorkspaceGatewayHandler : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        public IEnumerable<RequestSnapshot> WorkspaceRequests =>
            Requests.Where(request => request.RawPath.Contains("/source-control/", StringComparison.Ordinal));
        public string ContextTenantId { get; set; } = TenantId;
        public string SourceControl { get; set; } =
            """{"authMode":"githubApp","identityConnectionId":"connection_1","repository":{"owner":"octo","name":"repo"}}""";
        public string RefsBody { get; set; } =
            $$"""{"repository":"{{Repository}}","defaultBranch":"main","items":[{"name":"main","kind":"branch","commitSha":"{{CommitSha}}","isDefault":true},{"name":"v1.0.0","kind":"tag","commitSha":"{{new string('c', 40)}}","isDefault":false}],"nextCursor":"3"}""";
        public string TreeBody { get; set; } = WorkspaceCommandTests.TreeBody();
        public string FileBody { get; set; } = WorkspaceCommandTests.FileBody(
            "docs/a.md", "hello workspace\\n", Encoding.UTF8.GetByteCount(FileContent));
        public HttpStatusCode WorkspaceStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new RequestSnapshot(
                uri.AbsolutePath,
                uri.Query,
                request.Headers.TryGetValues("X-Agentweaver-Team-Sync-Selection", out var codes) ? codes.Single() : null));
            var path = uri.AbsolutePath;
            if (path == "/openapi/v1.json")
                return Json(HttpStatusCode.OK, OpenApi);
            if (path == "/api/v1/authorization/context")
                return Json(HttpStatusCode.OK, $$"""
                    {"contractVersion":1,"issuer":"https://identity.example/","actorId":"{{ActorId}}","tenantId":"{{ContextTenantId}}","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}
                    """);
            if (path == $"/api/v1/projects/{ProjectId}/configuration")
                return Json(HttpStatusCode.OK, $$$"""
                    {"projectId":"{{{ProjectId}}}","revision":4,"configuration":{"sourceControl":{{{SourceControl}}}}}
                    """);
            if (WorkspaceStatus != HttpStatusCode.OK && path.Contains("/source-control/", StringComparison.Ordinal))
                return Json(WorkspaceStatus, """{"type":"about:blank","title":"workspace_unavailable","status":500,"code":"workspace_unavailable"}""");
            if (path == $"/api/v1/projects/{ProjectId}/source-control/refs")
                return Json(HttpStatusCode.OK, RefsBody);
            if (path == $"/api/v1/projects/{ProjectId}/source-control/tree")
                return Json(HttpStatusCode.OK, TreeBody);
            if (path.StartsWith($"/api/v1/projects/{ProjectId}/source-control/files/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, FileBody);
            return Json(HttpStatusCode.NotFound, """{"code":"not_found"}""");
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private const string SelectionHeaderParameter =
            """{"name":"X-Agentweaver-Team-Sync-Selection","in":"header","required":true,"schema":{"type":"string","minLength":64,"maxLength":256}}""";
        private const string TenantHeaderParameter =
            """{"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}""";
        private const string ProjectParameter =
            """{"name":"projectId","in":"path","required":true,"schema":{"type":"string","format":"uuid"}}""";
        private const string RefParameter =
            """{"name":"ref","in":"query","required":true,"schema":{"type":"string","pattern":"^[a-fA-F0-9]{40}$"}}""";
        private const string JsonResponse =
            """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}""";

        private static readonly string OpenApi = $$$"""
            {
              "openapi":"3.1.0",
              "security":[{"Bearer":[]}],
              "components":{"securitySchemes":{"Bearer":{"type":"http","scheme":"bearer"} } },
              "paths":{
                "/api/v1/authorization/context":{"get":{"operationId":"getAuthorizationContext","parameters":[{{{TenantHeaderParameter}}}],"responses":{{{JsonResponse}}}}},
                "/api/v1/projects/{projectId}/configuration":{"get":{"operationId":"getProjectConfiguration","parameters":[{{{ProjectParameter}}},{{{TenantHeaderParameter}}}],"responses":{{{JsonResponse}}}}},
                "/api/v1/projects/{projectId}/source-control/refs":{"get":{"operationId":"listWorkspaceRepositoryRefs","parameters":[{{{ProjectParameter}}},{"name":"cursor","in":"query","required":false,"schema":{"type":"string","pattern":"^(?:[2-9]|[1-9][0-9]{1,3}|10000)$"}},{{{TenantHeaderParameter}}},{{{SelectionHeaderParameter}}}],"responses":{{{JsonResponse}}}}},
                "/api/v1/projects/{projectId}/source-control/tree":{"get":{"operationId":"readWorkspaceRepositoryTree","parameters":[{{{ProjectParameter}}},{{{RefParameter}}},{{{TenantHeaderParameter}}},{{{SelectionHeaderParameter}}}],"responses":{{{JsonResponse}}}}},
                "/api/v1/projects/{projectId}/source-control/files/{path}":{"get":{"operationId":"readWorkspaceRepositoryFile","x-agentweaver-owner-path":"/api/projects/{projectId}/source-control/files/{**path}","parameters":[{{{ProjectParameter}}},{"name":"path","in":"path","required":true,"schema":{"type":"string"}},{{{RefParameter}}},{{{TenantHeaderParameter}}},{{{SelectionHeaderParameter}}}],"responses":{{{JsonResponse}}}}}
              }
            }
            """;
    }
}
