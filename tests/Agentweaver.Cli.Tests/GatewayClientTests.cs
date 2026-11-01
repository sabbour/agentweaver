using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli.Tests;

public sealed class GatewayClientTests
{
    private static readonly CliIdentity RunIdentity = new(
        new CliTarget(
            new Uri("https://gateway.example/"),
            new Uri("https://identity.example/"),
            "agentweaver-cli",
            "https://gateway.example/",
            "tenant-1"),
        ["openid", "api.read", "offline_access", "projects.orchestrator"],
        "project-1",
        "run-1");
    private static readonly CliIdentity UnboundOrchestratorIdentity = RunIdentity with
    {
        Scopes = ["openid", "api.read", "offline_access", "projects.orchestrator"],
        ProjectId = null,
        RunId = null,
    };

    [Fact]
    public async Task DiscoveryAndInvocationUseTheLiveCatalogAndSeparateBoundCredentials()
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var client = new GatewayClient(http, RunIdentity, authentication);

        var catalog = await client.DiscoverAsync(CancellationToken.None);
        Assert.Equal("3.1.0", catalog["openapi"]!.GetValue<string>());
        Assert.Contains(
            catalog["paths"]!.AsObject().Select(item => item.Key),
            path => path.Contains("/projects/{projectId}/casting/proposals", StringComparison.Ordinal));

        var response = await client.InvokeAsync(
            "formulateCastingProposal",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = "project-1" },
                new Dictionary<string, string> { ["runId"] = "run-1", ["count"] = "2" },
                JsonNode.Parse("""{"projectId":"project-1","runId":"run-1"}"""),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["idempotency-key"] = "cli-test-1" }),
            CancellationToken.None);

        Assert.Equal(201, response.Status);
        Assert.Equal("created", response.Body?["result"]?.GetValue<string>());
        Assert.Contains("formulateCastingProposal", client.Calls
            .Select(call => call["operationId"]?.GetValue<string>())
            .Where(value => value is not null));
        Assert.Contains("run-bearer", authentication.RequestedBearers);
        Assert.Contains("writer-bearer", authentication.RequestedBearers);
        Assert.Contains(handler.Requests, request =>
            request.Path.Contains("/projects/project-1/casting/proposals", StringComparison.Ordinal) &&
            request.Query == "?count=2&runId=run-1" &&
            request.Authorization == "Bearer run-bearer" &&
            request.ProjectAuthorization == "Bearer writer-bearer" &&
            request.Tenant == "tenant-1");
    }

    [Theory]
    [InlineData("createP1InvocationSetup", "POST")]
    [InlineData("readP1InvocationSetup", "GET")]
    public async Task P1SetupOperationsInvokeWithUnboundAdminIdentity(string operationId, string method)
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var error = new StringWriter();
        var result = await CliApplication.RunAsync(
        [
            "api", "invoke", operationId,
            "--path", "invocationId=2a2eb07e-85be-4c99-9b1d-1d3c8b923825",
            "--scope", "projects.admin",
            "--gateway-url", "https://gateway.example/",
            "--broker-issuer", "https://identity.example/",
            "--client-id", "agentweaver-cli",
            "--api-audience", "https://gateway.example/",
            "--tenant-id", "tenant-1",
        ],
        new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Error: error));

        Assert.True(result == 0, error.ToString());
        var request = Assert.Single(handler.Requests,
            item => item.Path == "/api/v1/p1/invocations/2a2eb07e-85be-4c99-9b1d-1d3c8b923825/setup");
        Assert.Equal(method, request.Method);
        Assert.Equal("tenant-1", request.Tenant);
        Assert.Contains("writer-bearer", authentication.RequestedBearers);
        Assert.DoesNotContain("run-bearer", authentication.RequestedBearers);
    }

    [Fact]
    public async Task CompositeSetupScopesAreRejectedBeforeProtectedRequest()
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var error = new StringWriter();
        var result = await CliApplication.RunAsync(
        [
            "api", "invoke", "createP1InvocationSetup",
            "--path", "invocationId=2a2eb07e-85be-4c99-9b1d-1d3c8b923825",
            "--scope", "projects.admin",
            "--scope", "projects.orchestrator",
            "--gateway-url", "https://gateway.example/",
            "--broker-issuer", "https://identity.example/",
            "--client-id", "agentweaver-cli",
            "--api-audience", "https://gateway.example/",
            "--tenant-id", "tenant-1",
        ],
        new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Error: error));

        Assert.Equal(1, result);
        Assert.Contains("Project scopes do not match", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests, request => request.Path == "/openapi/v1.json");
        Assert.Empty(authentication.RequestedBearers);
    }

    [Fact]
    public async Task ApiInvocationAcceptsTwoMiBJsonBody()
    {
        var bodyPath = Path.GetFullPath($"api-large-body-{Guid.NewGuid():N}.json");
        var bodyText = "{\"payload\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";
        await File.WriteAllTextAsync(bodyPath, bodyText, new UTF8Encoding(false));
        try
        {
            var handler = new TestGatewayHandler();
            using var http = new HttpClient(handler);
            var error = new StringWriter();
            var result = await CliApplication.RunAsync(
            [
                "api", "invoke", "formulateCastingProposal",
                "--path", "projectId=project-1",
                "--query", "runId=run-1",
                "--header", "Idempotency-Key=large-body-test",
                "--body", "@" + bodyPath,
                "--scope", "projects.orchestrator",
                "--gateway-url", "https://gateway.example/",
                "--broker-issuer", "https://identity.example/",
                "--client-id", "agentweaver-cli",
                "--api-audience", "https://gateway.example/",
                "--tenant-id", "tenant-1",
                "--project-id", "project-1",
                "--run-id", "run-1",
            ],
            new CliRuntimeOptions(
                HttpClient: http,
                Authentication: new TestAuthentication(),
                Error: error));

            Assert.True(result == 0, error.ToString());
            var request = Assert.Single(handler.Requests, request =>
                request.Path.Contains("/projects/project-1/casting/proposals", StringComparison.Ordinal));
            Assert.Equal(bodyText, request.Body);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(bodyPath);
        }
    }

    [Fact]
    public async Task UnboundOrchestratorCanCreateRunWithProjectsOwnerAuthorityEvidence()
    {
        var handler = new TestGatewayHandler { OwnerPermission = "acceptRunSelection" };
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var client = new GatewayClient(http, UnboundOrchestratorIdentity, authentication);
        await client.DiscoverAsync(CancellationToken.None);

        var response = await client.InvokeAsync(
            "createProjectRun",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = "project-1" },
                Headers: new Dictionary<string, string> { ["Idempotency-Key"] = "create-run-1" },
                Body: JsonNode.Parse("""{"firstMessage":"hello","expectedProjectConfigRevision":1}""")),
            CancellationToken.None);

        Assert.Equal(201, response.Status);
        Assert.Contains("run-bearer", authentication.RequestedBearers);
        Assert.Contains(handler.Requests, request =>
            request.Path == "/api/v1/projects/project-1/runs" &&
            request.Authorization == "Bearer run-bearer" &&
            request.Tenant == "tenant-1" &&
            request.IdempotencyKey == "create-run-1" &&
            request.Body == """{"firstMessage":"hello","expectedProjectConfigRevision":1}""");
        Assert.DoesNotContain("writer-bearer", authentication.RequestedBearers);
    }

    [Fact]
    public async Task UnboundOrchestratorCanCancelRunWithProjectsOwnerCancelPermissionEvidence()
    {
        var handler = new TestGatewayHandler { OwnerPermission = "cancelRun" };
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var client = new GatewayClient(http, UnboundOrchestratorIdentity, authentication);
        await client.DiscoverAsync(CancellationToken.None);

        var response = await client.InvokeAsync(
            "cancelProjectRun",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = "project-1", ["runId"] = "run-1" },
                Headers: new Dictionary<string, string> { ["Idempotency-Key"] = "cancel-run-1" },
                Body: JsonNode.Parse("""{"reason":"cancel test"}""")),
            CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Contains("run-bearer", authentication.RequestedBearers);
        Assert.Contains(handler.Requests, request =>
            request.Path == "/api/v1/projects/project-1/runs/run-1/cancel" &&
            request.Authorization == "Bearer run-bearer" &&
            request.Tenant == "tenant-1" &&
            request.IdempotencyKey == "cancel-run-1" &&
            request.Body == """{"reason":"cancel test"}""");
        Assert.DoesNotContain("writer-bearer", authentication.RequestedBearers);
    }

    [Fact]
    public void ProjectRunBindingMustRemainPairedForUnboundOrchestratorScopes()
    {
        Assert.Throws<ArgumentException>(() =>
            CliConfiguration.ValidateIdentity(UnboundOrchestratorIdentity with { ProjectId = "project-1" }));
        Assert.Throws<ArgumentException>(() =>
            CliConfiguration.ValidateIdentity(UnboundOrchestratorIdentity with { RunId = "run-1" }));
    }

    [Fact]
    public async Task ApiOperationsOnlyRequireTheGatewayUrl()
    {
        var names = new[]
        {
            "AGENTWEAVER_GATEWAY_URL",
            "AGENTWEAVER_BROKER_ISSUER",
            "AGENTWEAVER_API_AUDIENCE",
            "AGENTWEAVER_TENANT_ID",
            "AGENTWEAVER_PROJECT_ID",
            "AGENTWEAVER_RUN_ID",
            "AGENTWEAVER_CLIENT_ID",
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_GATEWAY_URL", "https://gateway.example/");
            foreach (var name in names.Skip(1))
                Environment.SetEnvironmentVariable(name, null);

            var handler = new TestGatewayHandler();
            using var http = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();
            var result = await CliApplication.RunAsync(
                [
                    "api", "operations",
                    "--gateway-url", "https://flag.gateway.example/",
                    "--broker-issuer", "not a URL",
                    "--client-id", "",
                    "--api-audience", "",
                    "--tenant-id", "",
                    "--project-id", "",
                    "--run-id", "",
                ],
                new CliRuntimeOptions(HttpClient: http, Output: output, Error: error));

            Assert.Equal(0, result);
            Assert.Contains("formulateCastingProposal", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://flag.gateway.example", request.Origin);
            Assert.Equal("/openapi/v1.json", request.Path);
            Assert.Null(request.Authorization);
            Assert.Null(request.Tenant);

            var descriptionOutput = new StringWriter();
            var description = await CliApplication.RunAsync(
                ["api", "describe", "formulateCastingProposal"],
                new CliRuntimeOptions(HttpClient: http, Output: descriptionOutput, Error: error));
            Assert.Equal(0, description);
            Assert.Contains("Idempotency-Key", descriptionOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, item =>
            {
                Assert.Equal("/openapi/v1.json", item.Path);
                Assert.Null(item.Authorization);
                Assert.Null(item.Tenant);
            });

            var invocation = await CliApplication.RunAsync(
                ["api", "invoke", "formulateCastingProposal"],
                new CliRuntimeOptions(HttpClient: http, Error: error));
            Assert.Equal(1, invocation);
            Assert.Contains("AGENTWEAVER_BROKER_ISSUER", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task UnadvertisedParametersAreRejectedBeforeAnyProtectedRequest()
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());
        await client.DiscoverAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.InvokeAsync(
                "formulateCastingProposal",
                new GatewayInvocation(
                    new Dictionary<string, string> { ["projectId"] = "project-1" },
                    new Dictionary<string, string> { ["runId"] = "run-1", ["surprise"] = "x" },
                    JsonNode.Parse("""{"projectId":"project-1","runId":"run-1"}"""),
                    new Dictionary<string, string> { ["Idempotency-Key"] = "cli-test-1" }),
                CancellationToken.None));

        Assert.Contains("not advertised", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.Equal("/openapi/v1.json", handler.Requests[0].Path);
    }

    [Fact]
    public async Task BearerSchemeNameMatchesCaseInsensitively()
    {
        var handler = new TestGatewayHandler { LowercaseBearerScheme = true };
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());

        var catalog = await client.DiscoverAsync(CancellationToken.None);
        Assert.Equal("bearer", catalog["components"]!["securitySchemes"]!["bearer"]!["scheme"]!.GetValue<string>());
        Assert.Contains("formulateCastingProposal", catalog["paths"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBodyRequiresJsonMediaTypeInTheLiveContract()
    {
        var handler = new TestGatewayHandler { UnsupportedRequestBodyMediaType = true };
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DiscoverAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal("/openapi/v1.json", handler.Requests[0].Path);
    }

    [Fact]
    public async Task BinaryAndEventStreamResponsesAreBoundedAndStreamed()
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());
        await client.DiscoverAsync(CancellationToken.None);

        var download = await client.InvokeAsync(
            "downloadFile",
            new GatewayInvocation(
                new Dictionary<string, string> { ["fileId"] = "file-1" }),
            CancellationToken.None);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, download.BinaryBody);
        Assert.Null(download.Body);

        var chunks = new List<string>();
        var stream = await client.InvokeAsync(
            "streamEvents",
            new GatewayInvocation
            {
                OnEventChunk = chunk =>
                {
                    chunks.Add(chunk);
                    return ValueTask.CompletedTask;
                },
            },
            CancellationToken.None);
        Assert.Equal(200, stream.Status);
        Assert.Contains("data: first", string.Concat(chunks), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationStopsEventStreamAndIsPreserved()
    {
        var handler = new TestGatewayHandler();
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());
        await client.DiscoverAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.InvokeAsync(
                "streamEvents",
                new GatewayInvocation
                {
                    OnEventChunk = _ =>
                    {
                        cancellation.Cancel();
                        return ValueTask.CompletedTask;
                    },
                },
                cancellation.Token));
    }

    [Fact]
    public void PlainTextRedactionProtectsAccessAndSubscriptionKeys()
    {
        var redacted = GatewayClient.RedactText(
            "accessKey=plain-access-secret; subscriptionKey=plain-subscription-secret");

        Assert.DoesNotContain("plain-access-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-subscription-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("accessKey=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("subscriptionKey=[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventStreamCliOutputRedactsSecretsSplitAcrossChunksWithoutChangingSseFraming()
    {
        var names = new[]
        {
            "AGENTWEAVER_GATEWAY_URL",
            "AGENTWEAVER_BROKER_ISSUER",
            "AGENTWEAVER_API_AUDIENCE",
            "AGENTWEAVER_TENANT_ID",
            "AGENTWEAVER_PROJECT_ID",
            "AGENTWEAVER_RUN_ID",
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        using var cancellation = new CancellationTokenSource();
        var handler = new TestGatewayHandler
        {
            EventStreamContent = new BlockingContent(
                "text/event-stream",
                "data: Bearer split-secret-",
                "canary-value\n\n"),
        };
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_GATEWAY_URL", "https://gateway.example/");
            Environment.SetEnvironmentVariable("AGENTWEAVER_BROKER_ISSUER", "https://identity.example/");
            Environment.SetEnvironmentVariable("AGENTWEAVER_API_AUDIENCE", "https://gateway.example/");
            Environment.SetEnvironmentVariable("AGENTWEAVER_TENANT_ID", "tenant-1");
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "project-1");
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "run-1");

            var running = CliApplication.RunAsync(
                ["api", "invoke", "streamEvents", "--scope", "projects.orchestrator"],
                new CliRuntimeOptions(
                    HttpClient: http,
                    Authentication: new TestAuthentication(),
                    Output: output,
                    Error: error),
                cancellation.Token);

            await WaitForTextAsync(output, "data: Bearer [REDACTED]\n\n");
            Assert.DoesNotContain("split-secret-canary-value", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("data: Bearer [REDACTED]\n\n", output.ToString(), StringComparison.Ordinal);
            cancellation.Cancel();
            Assert.Equal(130, await running);
            Assert.DoesNotContain("split-secret-canary-value", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task EventStreamExceedsRequestDeadlineButFiniteBodiesStillTimeOut()
    {
        var streamHandler = new TestGatewayHandler
        {
            EventStreamContent = new BlockingContent("text/event-stream", "data: open\n\n"),
        };
        using var streamHttp = new HttpClient(streamHandler);
        var streamClient = new GatewayClient(
            streamHttp,
            RunIdentity,
            new TestAuthentication(),
            TimeSpan.FromMilliseconds(100));
        await streamClient.DiscoverAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var chunkReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamRequest = streamClient.InvokeAsync(
            "streamEvents",
            new GatewayInvocation
            {
                OnEventChunk = _ =>
                {
                    chunkReceived.TrySetResult();
                    return ValueTask.CompletedTask;
                },
            },
            cancellation.Token);

        await chunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(streamRequest.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamRequest);

        var finiteHandler = new TestGatewayHandler
        {
            ResponseContent = new BlockingContent("application/json", """{"result":"pending"}"""),
        };
        using var finiteHttp = new HttpClient(finiteHandler);
        var finiteClient = new GatewayClient(
            finiteHttp,
            RunIdentity,
            new TestAuthentication(),
            TimeSpan.FromMilliseconds(100));
        await finiteClient.DiscoverAsync(CancellationToken.None);
        var timeout = await Assert.ThrowsAsync<GatewayTransportException>(() =>
            finiteClient.InvokeAsync(
                "formulateCastingProposal",
                new GatewayInvocation(
                    new Dictionary<string, string> { ["projectId"] = "project-1" },
                    new Dictionary<string, string> { ["runId"] = "run-1", ["count"] = "2" },
                    JsonNode.Parse("""{"projectId":"project-1","runId":"run-1"}"""),
                    new Dictionary<string, string> { ["Idempotency-Key"] = "cli-timeout" }),
                CancellationToken.None));
        Assert.Contains("timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestAndResponseEvidenceRedactsSecretsButInvocationReturnsTransientBody()
    {
        var handler = new TestGatewayHandler();
        handler.ResponseBody = """{"access_token":"response-secret","message":"Bearer visible-secret"}""";
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());
        await client.DiscoverAsync(CancellationToken.None);

        var response = await client.InvokeAsync(
            "formulateCastingProposal",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = "project-1" },
                new Dictionary<string, string> { ["runId"] = "run-1", ["count"] = "2" },
                JsonNode.Parse("""{"projectId":"project-1","runId":"run-1","refresh_token":"request-secret"}"""),
                new Dictionary<string, string> { ["Idempotency-Key"] = "cli-test-1" }),
            CancellationToken.None);

        var evidence = response.Evidence.ToJsonString() + string.Join("", client.Calls.Select(call => call.ToJsonString()));
        Assert.DoesNotContain("request-secret", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("response-secret", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("visible-secret", evidence, StringComparison.Ordinal);
        Assert.Equal("response-secret", response.Body?["access_token"]?.GetValue<string>());
    }

    [Fact]
    public async Task EmbeddedJsonAndExecutionProviderKeysAreRedactedButTransientResponseStaysRaw()
    {
        var handler = new TestGatewayHandler
        {
            ResponseBody = """{"content":"Knowledge content {\"access_token\":\"embeddedJSON-response-canary\",\"execution_key\":\"execution-key-response-canary\",\"provider_key\":\"provider-key-response-canary\"}"}""",
        };
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());
        await client.DiscoverAsync(CancellationToken.None);

        var response = await client.InvokeAsync(
            "formulateCastingProposal",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = "project-1" },
                new Dictionary<string, string> { ["runId"] = "run-1", ["count"] = "2" },
                JsonNode.Parse("""{"projectId":"project-1","runId":"run-1","content":"Knowledge content {\"access_token\":\"embeddedJSON-request-canary\",\"execution_key\":\"execution-key-request-canary\",\"provider_key\":\"provider-key-request-canary\"}"}"""),
                new Dictionary<string, string> { ["Idempotency-Key"] = "redaction-test" }),
            CancellationToken.None);

        var evidence = response.Evidence.ToJsonString() + string.Join("", client.Calls.Select(call => call.ToJsonString()));
        foreach (var secret in new[]
        {
            "embeddedJSON-request-canary", "execution-key-request-canary", "provider-key-request-canary",
            "embeddedJSON-response-canary", "execution-key-response-canary", "provider-key-response-canary",
        })
            Assert.DoesNotContain(secret, evidence, StringComparison.Ordinal);
        Assert.Contains("embeddedJSON-response-canary", response.Body?["content"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateOperationIdsFailDiscoveryBeforeInvocation()
    {
        var handler = new TestGatewayHandler { DuplicateOperationId = true };
        using var http = new HttpClient(handler);
        var client = new GatewayClient(http, RunIdentity, new TestAuthentication());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DiscoverAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void GatewayMustBeAnExactHttpsOriginOrLoopbackFixture()
    {
        using var http = new HttpClient(new TestGatewayHandler());
        var remoteHttpIdentity = RunIdentity with
        {
            Target = RunIdentity.Target with { Gateway = new Uri("http://gateway.example/") },
        };
        Assert.Throws<ArgumentException>(() => new GatewayClient(http, remoteHttpIdentity));
    }

    private sealed class TestAuthentication : ICliAuthentication
    {
        public List<string> RequestedBearers { get; } = [];

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            _ = CliAuthIdentity.Create(identity);
            var token = identity.Scopes.Contains("projects.admin", StringComparer.Ordinal)
                ? "writer-bearer"
                : "run-bearer";
            RequestedBearers.Add(token);
            return Task.FromResult(token);
        }
    }

    private sealed class TestGatewayHandler : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        public bool DuplicateOperationId { get; init; }
        public bool LowercaseBearerScheme { get; init; }
        public bool UnsupportedRequestBodyMediaType { get; init; }
        public string? OwnerPermission { get; init; }
        public HttpContent? EventStreamContent { get; init; }
        public HttpContent? ResponseContent { get; init; }
        public string ResponseBody { get; set; } = """{"result":"created"}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.RequestUri.GetLeftPart(UriPartial.Authority),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Agentweaver-Project-Authorization", out var projectAuth)
                    ? projectAuth.Single()
                    : null,
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                    ? tenant.Single()
                    : null,
                request.Headers.TryGetValues("Idempotency-Key", out var idempotencyKey)
                    ? idempotencyKey.Single()
                    : null,
                body));

            if (request.RequestUri.AbsolutePath == "/openapi/v1.json")
            {
                var openApi = OpenApi.Replace(
                    "\"operationId\":\"streamEvents\"",
                    $"\"operationId\":\"{(DuplicateOperationId ? "formulateCastingProposal" : "streamEvents")}\"",
                    StringComparison.Ordinal);
                if (LowercaseBearerScheme)
                    openApi = openApi.Replace("\"Bearer\"", "\"bearer\"", StringComparison.Ordinal);
                if (UnsupportedRequestBodyMediaType)
                {
                    openApi = openApi.Replace(
                        "\"requestBody\":{\"required\":true,\"content\":{\"application/json\":",
                        "\"requestBody\":{\"required\":true,\"content\":{\"text/plain\":",
                        StringComparison.Ordinal);
                }
                return Json(HttpStatusCode.OK, openApi);
            }

            if (request.RequestUri.AbsolutePath == "/api/v1/authorization/context")
            {
                var isAdmin = request.Headers.Authorization?.Parameter == "writer-bearer";
                return Json(HttpStatusCode.OK, OwnerPermission is null
                    ? AuthorizationContext(isAdmin)
                    : AuthorizationContext(OwnerPermission));
            }

            if (request.RequestUri.AbsolutePath.StartsWith("/api/v1/p1/invocations/", StringComparison.Ordinal) &&
                request.RequestUri.AbsolutePath.EndsWith("/setup", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ResponseBody);

            if (request.RequestUri.AbsolutePath == "/api/v1/files/file-1")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0, 1, 2, 255])
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                    },
                };
            }

            if (request.RequestUri.AbsolutePath == "/api/v1/events")
            {
                if (EventStreamContent is not null)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = EventStreamContent };
                var content = new StringContent("data: first\n\n", Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            if (ResponseContent is not null)
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = ResponseContent };
            if (request.RequestUri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ResponseBody);
            return Json(HttpStatusCode.Created, ResponseBody);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private static string AuthorizationContext(bool admin) =>
            $$"""
              {
                "contractVersion": 1,
                "issuer": "https://identity.example/",
                "actorId": "actor-1",
                "tenantId": "tenant-1",
                "membershipRevision": 1,
                "boundProjectId": {{(admin ? "null" : "\"project-1\"")}},
                "boundRunId": {{(admin ? "null" : "\"run-1\"")}},
                "effectiveAuthority": []
              }
              """;

        private static string AuthorizationContext(string permission) =>
            $$"""
              {
                "contractVersion": 1,
                "issuer": "https://identity.example/",
                "actorId": "actor-1",
                "tenantId": "tenant-1",
                "membershipRevision": 1,
                "boundProjectId": null,
                "boundRunId": null,
                "effectiveAuthority": [
                  {
                    "resourceType": "project",
                    "resourceId": "project-1",
                    "permissions": {{(permission == "acceptRunSelection"
                        ? """[{"permission":"readRunSelection","roleRevision":7},{"permission":"acceptRunSelection","roleRevision":7}]"""
                        : """[{"permission":"readProjects","roleRevision":7},{"permission":"cancelRun","roleRevision":7}]""")}}
                  }
                ]
              }
              """;

        private const string OpenApi = """
        {
          "openapi":"3.1.0",
          "security":[{"Bearer":[]}],
          "components":{"securitySchemes":{"Bearer":{"type":"http","scheme":"bearer"}}},
          "paths":{
          "/api/v1/p1/invocations/{invocationId}/setup":{
            "post":{
              "operationId":"createP1InvocationSetup",
              "security":[{"Bearer":[]}],
              "parameters":[
                {"name":"invocationId","in":"path","required":true,"schema":{"type":"string","format":"uuid"}},
                {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}
              ],
              "responses":{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}
            },
            "get":{
              "operationId":"readP1InvocationSetup",
              "security":[{"Bearer":[]}],
              "parameters":[
                {"name":"invocationId","in":"path","required":true,"schema":{"type":"string","format":"uuid"}},
                {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}
              ],
              "responses":{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}
            }
          },
          "/api/v1/authorization/context":{
              "get":{
                "operationId":"getAuthorizationContext",
                "security":[{"Bearer":[]}],
                "parameters":[{"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}],
                "responses":{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}
              }
            },
            "/api/v1/projects/{projectId}/casting/proposals":{
              "post":{
                "operationId":"formulateCastingProposal",
                "x-agentweaver-max-request-body-bytes":3145728,
                "security":[{"Bearer":[]}],
                "parameters":[
                  {"name":"projectId","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"runId","in":"query","required":true,"schema":{"type":"string"}},
                  {"name":"count","in":"query","required":false,"schema":{"type":"integer","minimum":1,"maximum":2}},
                  {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}},
                  {"name":"X-Agentweaver-Project-Authorization","in":"header","required":true,"schema":{"type":"string"}},
                  {"name":"Idempotency-Key","in":"header","required":true,"schema":{"type":"string","minLength":1,"maxLength":128,"pattern":"^[A-Za-z0-9._:-]+$"}}
                ],
                "requestBody":{"required":true,"content":{"application/json":{"schema":{"type":"object"}}}},
                "responses":{"201":{"content":{"application/json":{"schema":{"type":"object"}}}}}
              }
            },
            "/api/v1/files/{fileId}":{
              "get":{
                "operationId":"downloadFile",
                "security":[{"Bearer":[]}],
                "parameters":[
                  {"name":"fileId","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}
                ],
                "responses":{"200":{"content":{"application/octet-stream":{"schema":{"type":"string","format":"binary"}}}}}
              }
            },
            "/api/v1/events":{
              "get":{
                "operationId":"streamEvents",
                "security":[{"Bearer":[]}],
                "parameters":[{"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}],
                "responses":{"200":{"content":{"text/event-stream":{"schema":{"type":"string"}}}}}
              }
            },
            "/api/v1/projects/{projectId}/runs":{
              "post":{
                "operationId":"createProjectRun",
                "security":[{"Bearer":[]}],
                "parameters":[
                  {"name":"projectId","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}},
                  {"name":"Idempotency-Key","in":"header","required":true,"schema":{"type":"string","minLength":1,"maxLength":128}}
                ],
                "requestBody":{"required":true,"content":{"application/json":{"schema":{"type":"object"}}}},
                "responses":{"201":{"content":{"application/json":{"schema":{"type":"object"}}}}}
              }
            },
            "/api/v1/projects/{projectId}/runs/{runId}/cancel":{
              "post":{
                "operationId":"cancelProjectRun",
                "security":[{"Bearer":[]}],
                "parameters":[
                  {"name":"projectId","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"runId","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}},
                  {"name":"Idempotency-Key","in":"header","required":true,"schema":{"type":"string","minLength":1,"maxLength":128}}
                ],
                "requestBody":{"required":true,"content":{"application/json":{"schema":{"type":"object"}}}},
                "responses":{"200":{"content":{"application/json":{"schema":{"type":"object"}}}}}
              }
            }
          }
        }
        """;
    }

    private sealed record RequestSnapshot(
        string Method,
        string Path,
        string Query,
        string Origin,
        string? Authorization,
        string? ProjectAuthorization,
        string? Tenant,
        string? IdempotencyKey,
        string? Body);

    private static async Task WaitForTextAsync(StringWriter writer, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!writer.ToString().Contains(expected, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains(expected, writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class BlockingContent : HttpContent
    {
        private readonly string[] _chunks;

        public BlockingContent(string mediaType, params string[] chunks)
        {
            _chunks = chunks;
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingReadStream(_chunks));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingReadStream(string[] chunks) : Stream
    {
        private int _nextChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_nextChunk >= chunks.Length)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(chunks[_nextChunk++]);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }
}
