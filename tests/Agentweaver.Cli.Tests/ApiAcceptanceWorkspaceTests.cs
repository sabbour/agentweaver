using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli.Tests;

public sealed class ApiAcceptanceWorkspaceTests
{
    private const string ActorId = "b9a2c6e7-9c2a-4b6d-8a11-344c4c6c6123";
    private const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string ProjectId = "3b80de8b-b44a-40c3-8d85-7cddab95b171";
    private const string Repository = "octo/widget";
    private const string ConnectionId = "connection_1";
    private const string CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FilePath = "docs/overview.txt";
    private const string FileContent = "workspace-private-content-canary: café\n";
    private const string Issuer = "https://identity.example/";
    private const string SelectionHeader = "X-Agentweaver-Team-Sync-Selection";

    [Fact]
    public async Task WorkspaceReadUsesFreshSelectionPerReadAndDoesNotPersistPrivateContent()
    {
        var fixture = CreateFixture();
        var gateway = new WorkspaceGateway(fixture.Identity, FileContent);
        var authentication = new WorkspaceAuthentication();
        List<JsonObject> steps = [];
        JsonObject observations = new();

        var result = await ApiAcceptanceWorkspace.RunAsync(
            fixture.Manifest,
            gateway,
            fixture.Identity,
            authentication,
            new ApiTestOptions("", "", Interaction: InteractionPolicy.Allow),
            steps,
            observations,
            CancellationToken.None);

        Assert.Equal("passed", result.Status);
        Assert.Null(result.Failure);
        Assert.Equal(1, authentication.BearerCalls);
        Assert.Equal(
            ["listWorkspaceRepositoryRefs", "readWorkspaceRepositoryTree", "readWorkspaceRepositoryFile"],
            authentication.Intents.Select(intent => intent.OperationId));
        Assert.Equal(3, gateway.WorkspaceReads.Count);
        Assert.Equal(authentication.SelectionCodes,
            gateway.WorkspaceReads.Select(call => call.Invocation.Headers![SelectionHeader]));
        Assert.All(authentication.Intents, intent =>
        {
            Assert.Equal(ActorId, intent.ActorId);
            Assert.Equal(TenantId, intent.TenantId);
            Assert.Equal(ProjectId, intent.ProjectId);
            Assert.Equal(ConnectionId, intent.ConnectionId);
            Assert.Equal(1, intent.ConnectionRevision);
        });

        string evidence = ApiAcceptanceSupport.WorkspaceResult(
            "passed", null, steps, observations, null).ToJsonString();
        Assert.DoesNotContain(FileContent, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(string.Join("", authentication.SelectionCodes), evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceRefPagingRejectsRepeatedCursor()
    {
        var fixture = CreateFixture();
        var gateway = new WorkspaceGateway(fixture.Identity, FileContent) { RepeatRefCursor = true };
        var authentication = new WorkspaceAuthentication();

        var result = await ApiAcceptanceWorkspace.RunAsync(
            fixture.Manifest,
            gateway,
            fixture.Identity,
            authentication,
            new ApiTestOptions("", "", Interaction: InteractionPolicy.Allow),
            [],
            new JsonObject(),
            CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("repeated", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, gateway.RefPageCount);
        Assert.Equal(2, authentication.Intents.Count);
        Assert.DoesNotContain(gateway.Invocations, call =>
            call.Operation is "readWorkspaceRepositoryTree" or "readWorkspaceRepositoryFile");
    }

    [Fact]
    public async Task WorkspaceRefPagingStopsAtOneHundredPages()
    {
        var fixture = CreateFixture();
        var gateway = new WorkspaceGateway(fixture.Identity, FileContent) { ReturnUniqueEmptyRefPages = true };
        var authentication = new WorkspaceAuthentication();

        var result = await ApiAcceptanceWorkspace.RunAsync(
            fixture.Manifest,
            gateway,
            fixture.Identity,
            authentication,
            new ApiTestOptions("", "", Interaction: InteractionPolicy.Allow),
            [],
            new JsonObject(),
            CancellationToken.None);

        Assert.Equal("blocked", result.Status);
        Assert.Contains("100-page", result.Failure, StringComparison.Ordinal);
        Assert.Equal(100, gateway.RefPageCount);
        Assert.Equal(100, authentication.Intents.Count);
        Assert.DoesNotContain(gateway.Invocations, call =>
            call.Operation is "readWorkspaceRepositoryTree" or "readWorkspaceRepositoryFile");
    }

    [Fact]
    public async Task WorkspaceReadBlocksBeforeAuthenticationOrNetworkWithoutInteractivePermission()
    {
        var fixture = CreateFixture();
        var gateway = new WorkspaceGateway(fixture.Identity, FileContent);
        var authentication = new WorkspaceAuthentication();

        var result = await ApiAcceptanceWorkspace.RunAsync(
            fixture.Manifest,
            gateway,
            fixture.Identity,
            authentication,
            new ApiTestOptions("", ""),
            [],
            new JsonObject(),
            CancellationToken.None);

        Assert.Equal("blocked", result.Status);
        Assert.Contains("--interactive", result.Failure, StringComparison.Ordinal);
        Assert.Equal(0, authentication.BearerCalls);
        Assert.Empty(authentication.Intents);
        Assert.Equal(0, gateway.DiscoveryCalls);
        Assert.Empty(gateway.Invocations);
    }

    [Fact]
    public async Task WorkspaceDryRunDoesNotAuthenticateCallGatewayReserveOrWriteEvidence()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"workspace-dry-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "workspace.json");
            string outputPath = Path.Combine(directory, "evidence.jsonl");
            File.WriteAllText(Path.Combine(directory, "source.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "images.json"), "{}");
            File.WriteAllText(inputPath, WorkspaceManifest(directory).ToJsonString());

            var authentication = new WorkspaceAuthentication();
            var clients = new CountingGatewayClientFactory();
            var deploymentEvidence = new CountingDeploymentEvidenceProvider();
            using var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(clients, authentication, deploymentEvidence, output);

            int exitCode = await runner.RunAsync(
                new ApiTestOptions(inputPath, outputPath, DryRun: true, Interaction: InteractionPolicy.Allow),
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("Dry run only", output.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
            Assert.Equal(0, authentication.BearerCalls);
            Assert.Empty(authentication.Intents);
            Assert.Equal(0, clients.CreateCalls);
            Assert.Equal(0, deploymentEvidence.PrepareCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRunReservesEvidenceBeforeRepositoryReads()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"workspace-output-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string outputPath = Path.Combine(directory, "evidence.jsonl");
        try
        {
            string inputPath = Path.Combine(directory, "workspace.json");
            File.WriteAllText(Path.Combine(directory, "source.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "images.json"), "{}");
            File.WriteAllText(inputPath, WorkspaceManifest(directory).ToJsonString());

            var fixture = CreateFixture();
            bool collisionRejected = false;
            var gateway = new WorkspaceGateway(fixture.Identity, FileContent)
            {
                OnInvoke = () =>
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using var other = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                    });
                    collisionRejected = true;
                },
            };
            var authentication = new WorkspaceAuthentication();
            using var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(
                new SingleGatewayClientFactory(gateway),
                authentication,
                new WorkspaceDeploymentEvidenceProvider(),
                output);

            int exitCode = await runner.RunAsync(
                new ApiTestOptions(inputPath, outputPath, Interaction: InteractionPolicy.Allow),
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.True(collisionRejected);
            Assert.Equal(3, gateway.WorkspaceReads.Count);
            Assert.Equal("passed", JsonNode.Parse(File.ReadAllText(outputPath))!["featureStatus"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceReservationFailureDoesNotReadOrReportPassed(bool existingFile)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"workspace-reservation-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string outputPath = Path.Combine(directory, "evidence.jsonl");
        try
        {
            string inputPath = Path.Combine(directory, "workspace.json");
            File.WriteAllText(Path.Combine(directory, "source.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "images.json"), "{}");
            File.WriteAllText(inputPath, WorkspaceManifest(directory).ToJsonString());
            if (existingFile)
                File.WriteAllText(outputPath, "existing evidence");
            else
                Directory.CreateDirectory(outputPath);

            var authentication = new WorkspaceAuthentication();
            var clients = new CountingGatewayClientFactory();
            var deploymentEvidence = new CountingDeploymentEvidenceProvider();
            using var output = new StringWriter();
            var runner = new ApiAcceptanceRunner(clients, authentication, deploymentEvidence, output);

            int exitCode = await runner.RunAsync(
                new ApiTestOptions(inputPath, outputPath, Interaction: InteractionPolicy.Allow),
                CancellationToken.None);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("\"featureStatus\":\"failed\"", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"featureStatus\":\"passed\"", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, deploymentEvidence.PrepareCalls);
            Assert.Equal(0, clients.CreateCalls);
            Assert.Equal(0, authentication.BearerCalls);
            Assert.Empty(authentication.Intents);
            if (existingFile)
                Assert.Equal("existing evidence", File.ReadAllText(outputPath));
            else
                Assert.True(Directory.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceValidationFailureStillWritesBlockedEvidence()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, $"workspace-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "workspace.json");
            string outputPath = Path.Combine(directory, "evidence.jsonl");
            File.WriteAllText(Path.Combine(directory, "source.json"), "{}");
            File.WriteAllText(inputPath, WorkspaceManifest(directory).ToJsonString());

            var clients = new CountingGatewayClientFactory();
            var deploymentEvidence = new CountingDeploymentEvidenceProvider();
            var runner = new ApiAcceptanceRunner(
                clients, new WorkspaceAuthentication(), deploymentEvidence, new StringWriter());

            int exitCode = await runner.RunAsync(
                new ApiTestOptions(inputPath, outputPath, DryRun: true), CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal("blocked", JsonNode.Parse(File.ReadAllText(outputPath))!["featureStatus"]!.GetValue<string>());
            Assert.Equal(0, clients.CreateCalls);
            Assert.Equal(0, deploymentEvidence.PrepareCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NativeRepositorySelectionAcceptsDotPrefixedNamesButRejectsDotSegments()
    {
        NativeRepoAppSelectionIntent intent = new(
            1, ActorId, TenantId, ProjectId, ConnectionId, 1, 22, 33,
            "octo/.github", "readWorkspaceRepositoryFile");

        Assert.True(NativeRepoAppSelectionIntentValidator.IsValid(intent));
        Assert.True(NativeRepoAppSelectionIntentValidator.IsValid(intent with { RepositoryFullName = "octo/widget." }));
        Assert.False(NativeRepoAppSelectionIntentValidator.IsValid(intent with { RepositoryFullName = "octo/." }));
        Assert.False(NativeRepoAppSelectionIntentValidator.IsValid(intent with { RepositoryFullName = "octo/.." }));
    }

    private static WorkspaceFixture CreateFixture()
    {
        byte[] content = Encoding.UTF8.GetBytes(FileContent);
        string contentHash = Convert.ToHexStringLower(SHA256.HashData(content));
        var environment = new JsonObject
        {
            ["baseUrl"] = "https://gateway.example",
            ["issuer"] = Issuer,
            ["audience"] = "https://gateway.example",
            ["actorId"] = ActorId,
            ["tenantId"] = TenantId,
        };
        var workspace = new JsonObject
        {
            ["projectId"] = ProjectId,
            ["expectedProjectRevision"] = 4L,
            ["connectionId"] = ConnectionId,
            ["connectionRevision"] = 1L,
            ["installationId"] = 22L,
            ["repositoryId"] = 33L,
            ["repositoryFullName"] = Repository,
            ["refKind"] = "branch",
            ["refName"] = "main",
            ["commitSha"] = CommitSha,
            ["filePath"] = FilePath,
            ["expectedByteLength"] = content.LongLength,
            ["contentSha256"] = contentHash,
        };
        var fixturePlan = new JsonObject { ["workspace"] = workspace.DeepClone() };
        var root = new JsonObject
        {
            ["journey"] = "workspace-repository-read",
            ["environment"] = environment.DeepClone(),
            ["fixturePlan"] = fixturePlan.DeepClone(),
        };
        var target = new CliTarget(
            new Uri("https://gateway.example/"),
            new Uri(Issuer),
            "agentweaver-cli",
            "https://gateway.example",
            TenantId);
        var identity = new CliIdentity(target, ["openid", "api.read", "offline_access", "projects.admin"]);
        var manifest = new AcceptanceManifest(
            root, environment, new JsonObject(), fixturePlan,
            "workspace-repository-read", "workspace.json", AppContext.BaseDirectory);
        CryptographicOperations.ZeroMemory(content);
        return new WorkspaceFixture(manifest, identity);
    }

    private static JsonObject WorkspaceManifest(string directory)
    {
        WorkspaceFixture fixture = CreateFixture();
        JsonObject workspace = (JsonObject)fixture.Manifest.FixturePlan["workspace"]!.DeepClone();
        return new JsonObject
        {
            ["executionIntent"] = "execute",
            ["journey"] = "workspace-repository-read",
            ["environment"] = fixture.Manifest.Environment.DeepClone(),
            ["pins"] = new JsonObject
            {
                ["context"] = "test-context",
                ["namespace"] = "agentweaver",
                ["providerReceipt"] = new JsonObject
                {
                    ["configMapName"] = "provider-catalog",
                    ["sourceCommit"] = CommitSha,
                    ["uid"] = "test-uid",
                    ["resourceVersion"] = "1",
                },
                ["runtimeReceipt"] = new JsonObject { ["revision"] = 1 },
                ["storageReceipt"] = new JsonObject { ["shareId"] = "test-share" },
                ["sourceReceipt"] = new JsonObject
                {
                    ["sourceSha"] = CommitSha,
                    ["sourceTree"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                },
            },
            ["deploymentEvidence"] = new JsonObject
            {
                ["manifestPath"] = Path.Combine(directory, "source.json"),
                ["imageManifestPath"] = Path.Combine(directory, "images.json"),
            },
            ["fixturePlan"] = new JsonObject { ["workspace"] = workspace },
        };
    }

    private sealed record WorkspaceFixture(AcceptanceManifest Manifest, CliIdentity Identity);

    private sealed class WorkspaceAuthentication : ICliAuthentication, INativeRepoAppSelectionHandoff
    {
        public int BearerCalls { get; private set; }
        public List<NativeRepoAppSelectionIntent> Intents { get; } = [];
        public List<string> SelectionCodes { get; } = [];

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            BearerCalls++;
            Assert.Equal(InteractionPolicy.Allow, interaction);
            return Task.FromResult("controlled-bearer");
        }

        public Task<NativeRepoAppSelection> AcquireRepositorySelectionAsync(
            CliIdentity identity,
            NativeRepoAppSelectionIntent intent,
            InteractionPolicy interaction,
            CancellationToken ct)
        {
            Assert.Equal(InteractionPolicy.Allow, interaction);
            Intents.Add(intent);
            string code = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Intents.Count.ToString())));
            SelectionCodes.Add(code);
            return Task.FromResult(new NativeRepoAppSelection(
                intent.ActorId, intent.TenantId, intent.ProjectId, intent.OperationId, code,
                intent.ConnectionId, intent.ConnectionRevision, intent.InstallationId, intent.RepositoryId,
                intent.RepositoryFullName, DateTimeOffset.UtcNow.AddMinutes(2)));
        }

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, ActorId, identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, ActorId, identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class WorkspaceGateway(CliIdentity identity, string fileContent) : IGatewayClient
    {
        public bool RepeatRefCursor { get; init; }
        public bool ReturnUniqueEmptyRefPages { get; init; }
        public Action? OnInvoke { get; init; }
        public int DiscoveryCalls { get; private set; }
        public int RefPageCount { get; private set; }
        private bool _invokeCallbackCalled;
        public List<WorkspaceCall> Invocations { get; } = [];
        public List<WorkspaceCall> WorkspaceReads =>
            Invocations.Where(call => call.Operation is
                "listWorkspaceRepositoryRefs" or "readWorkspaceRepositoryTree" or "readWorkspaceRepositoryFile").ToList();
        public CliIdentity Identity => identity;
        public IReadOnlyList<JsonObject> Calls => [];

        public Task<JsonObject> DiscoverAsync(CancellationToken ct)
        {
            DiscoveryCalls++;
            JsonObject paths = new();
            foreach (string operation in new[]
            {
                "getAuthorizationContext", "getProjectConfiguration",
                "listWorkspaceRepositoryRefs", "readWorkspaceRepositoryTree", "readWorkspaceRepositoryFile",
            })
                paths[$"/{operation}"] = new JsonObject { ["get"] = new JsonObject { ["operationId"] = operation } };
            return Task.FromResult(new JsonObject { ["paths"] = paths });
        }

        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) =>
            Task.FromResult(new JsonObject
            {
                ["contractVersion"] = 1L,
                ["issuer"] = Issuer,
                ["actorId"] = ActorId,
                ["tenantId"] = TenantId,
                ["boundProjectId"] = null,
                ["boundRunId"] = null,
                ["effectiveAuthority"] = new JsonArray(),
            });

        public Task<GatewayResponse> InvokeAsync(
            string operationId, GatewayInvocation invocation, CancellationToken ct)
        {
            if (!_invokeCallbackCalled)
            {
                _invokeCallbackCalled = true;
                OnInvoke?.Invoke();
            }
            IReadOnlyDictionary<string, string>? headers = invocation.Headers is null
                ? null
                : new Dictionary<string, string>(invocation.Headers, StringComparer.OrdinalIgnoreCase);
            Invocations.Add(new WorkspaceCall(operationId, invocation with { Headers = headers }));

            JsonObject body = operationId switch
            {
                "getProjectConfiguration" => new JsonObject
                {
                    ["projectId"] = ProjectId,
                    ["revision"] = 4L,
                    ["configuration"] = new JsonObject
                    {
                        ["sourceControl"] = new JsonObject
                        {
                            ["authMode"] = "githubApp",
                            ["identityConnectionId"] = ConnectionId,
                            ["repository"] = new JsonObject { ["owner"] = "octo", ["name"] = "widget" },
                        },
                    },
                },
                "listWorkspaceRepositoryRefs" => RefsPage(invocation),
                "readWorkspaceRepositoryTree" => JsonNode.Parse(
                    $$"""{"repository":"{{Repository}}","reference":"{{CommitSha}}","commitSha":"{{CommitSha}}","items":[{"path":"{{FilePath}}","kind":"file","size":{{Encoding.UTF8.GetByteCount(fileContent)}}, "blobSha":"{{ApiAcceptanceSupport.Sha1GitBlobHex(Encoding.UTF8.GetBytes(fileContent))}}"}]}""")!.AsObject(),
                "readWorkspaceRepositoryFile" => JsonNode.Parse(
                    JsonSerializer.Serialize(new
                    {
                        repository = Repository,
                        path = FilePath,
                        commitSha = CommitSha,
                        content = fileContent,
                        isBinary = false,
                        language = "text",
                        size = Encoding.UTF8.GetByteCount(fileContent),
                    }))!.AsObject(),
                _ => throw new InvalidOperationException($"Unexpected operation: {operationId}"),
            };
            return Task.FromResult(new GatewayResponse(200, body, new Dictionary<string, string>(), new JsonObject()));
        }

        public Task<GatewayResponse> InvokeRawAsync(
            string method, string url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
            bool outputRequested, Func<string, ValueTask>? onEventChunk, CancellationToken ct) =>
            throw new NotSupportedException();

        private JsonObject RefsPage(GatewayInvocation invocation)
        {
            RefPageCount++;
            if (RepeatRefCursor || ReturnUniqueEmptyRefPages)
            {
                string? cursor = RepeatRefCursor ? "repeat" : $"cursor-{RefPageCount}";
                return new JsonObject
                {
                    ["repository"] = Repository,
                    ["defaultBranch"] = "main",
                    ["items"] = new JsonArray(),
                    ["nextCursor"] = cursor,
                };
            }

            return new JsonObject
            {
                ["repository"] = Repository,
                ["defaultBranch"] = "main",
                ["items"] = new JsonArray(new JsonObject
                {
                    ["name"] = "main",
                    ["kind"] = "branch",
                    ["commitSha"] = CommitSha,
                    ["isDefault"] = true,
                }),
                ["nextCursor"] = null,
            };
        }
    }

    private sealed record WorkspaceCall(string Operation, GatewayInvocation Invocation);

    private sealed class CountingGatewayClientFactory : IGatewayClientFactory
    {
        public int CreateCalls { get; private set; }

        public IGatewayClient Create(CliIdentity identity)
        {
            CreateCalls++;
            throw new InvalidOperationException("Dry-run must not create a Gateway client.");
        }
    }

    private sealed class SingleGatewayClientFactory(IGatewayClient gateway) : IGatewayClientFactory
    {
        public IGatewayClient Create(CliIdentity identity) => gateway;
    }

    private sealed class WorkspaceDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
    {
        public Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct) =>
            Task.FromResult(new JsonObject
            {
                ["source"] = new JsonObject
                {
                    ["sourceSha"] = new string('a', 40),
                    ["sourceTree"] = new string('b', 40),
                },
            });
    }

    private sealed class CountingDeploymentEvidenceProvider : IAcceptanceDeploymentEvidenceProvider
    {
        public int PrepareCalls { get; private set; }

        public Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Dry-run must not inspect live deployment evidence.");
        }
    }
}
