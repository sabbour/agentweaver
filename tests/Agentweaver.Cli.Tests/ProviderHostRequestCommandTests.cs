using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class ProviderHostRequestCommandTests
{
    private const string Cursor = "next/+ =?cursor";
    private const string SecretCanary = "secret_canary_value";
    private static readonly string[] Target =
    [
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--client-id", "cli-tests",
        "--api-audience", "agentweaver",
        "--tenant-id", "tenant-1",
    ];

    [Fact]
    public async Task ListUsesExactGetPathQueryHeadersUnboundScopesAndProjectsNarrowJson()
    {
        var fixture = new Fixture
        {
            Body = Page(
                $$"""
                [
                  {
                    "destinationId": "11111111-1111-1111-1111-111111111111",
                    "requestId": "22222222-2222-2222-2222-222222222222",
                    "host": "api.openai.com",
                    "port": 443,
                    "state": "requested",
                    "origin": "modelProvider",
                    "scope": "platform",
                    "projectId": null,
                    "tenantId": "tenant-a",
                    "providerId": "prov-1",
                    "providerRevision": 2,
                    "requestedBy": "user-1",
                    "requestedAt": "2026-11-27T10:00:00Z"
                  },
                  {
                    "destinationId": "33333333-3333-3333-3333-333333333333",
                    "requestId": "44444444-4444-4444-4444-444444444444",
                    "host": "eastus.api.cognitive.microsoft.com",
                    "port": 443,
                    "state": "requested",
                    "origin": "modelProvider",
                    "scope": "project",
                    "projectId": "proj-9",
                    "tenantId": "tenant-b",
                    "providerId": "prov-2",
                    "providerRevision": 5,
                    "requestedBy": "user-2",
                    "requestedAt": "2026-11-27T10:01:00+02:00"
                  }
                ]
                """,
                "\"next-page\""),
        };

        Assert.Equal(0, await fixture.Run("--limit", "25", "--cursor", Cursor));

        Assert.Equal(HttpMethod.Get, fixture.ListMethod);
        Assert.Equal(
            "/api/v1/platform/egress/destinations?cursor=next%2F%2B%20%3D%3Fcursor&pageSize=25&state=requested",
            fixture.ListPathAndQuery);
        Assert.Equal("safe-bearer", fixture.ListBearer);
        Assert.Equal("tenant-1", fixture.ListTenant);
        Assert.Null(fixture.ListProjectAuthorization);
        var identity = fixture.Authentication.Identities.Single();
        Assert.Null(identity.ProjectId);
        Assert.Null(identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access", "projects.admin"], identity.Scopes);
        Assert.All(fixture.Authentication.Interactions, policy => Assert.Equal(InteractionPolicy.Never, policy));

        var outputJson = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(["nextCursor", "requests"],
            outputJson.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("next-page", outputJson["nextCursor"]!.GetValue<string>());
        var requests = outputJson["requests"]!.AsArray();
        Assert.Equal(2, requests.Count);

        var first = requests[0]!.AsObject();
        Assert.Equal(
            ["host", "port", "projectId", "providerId", "providerRevision", "requestedAt", "scope", "tenantId"],
            first.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("api.openai.com", first["host"]!.GetValue<string>());
        Assert.Equal(443, first["port"]!.GetValue<int>());
        Assert.Equal("platform", first["scope"]!.GetValue<string>());
        Assert.Equal("tenant-a", first["tenantId"]!.GetValue<string>());
        Assert.Null(first["projectId"]);
        Assert.Equal("prov-1", first["providerId"]!.GetValue<string>());
        Assert.Equal(2, first["providerRevision"]!.GetValue<int>());
        Assert.Equal("2026-11-27T10:00:00Z", first["requestedAt"]!.GetValue<string>());

        var second = requests[1]!.AsObject();
        Assert.Equal("eastus.api.cognitive.microsoft.com", second["host"]!.GetValue<string>());
        Assert.Equal("project", second["scope"]!.GetValue<string>());
        Assert.Equal("tenant-b", second["tenantId"]!.GetValue<string>());
        Assert.Equal("proj-9", second["projectId"]!.GetValue<string>());

        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user-1", fixture.Output.ToString(), StringComparison.Ordinal);

        var listCall = Assert.Single(fixture.Calls);
        Assert.Equal("listPlatformEgressDestinations", listCall["operationId"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", listCall["responseBody"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", listCall["query"]?["cursor"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListDefaultsPageSizeTo50AndPreservesEmptyPageWithCursor()
    {
        var fixture = new Fixture { Body = Page("[]", "\"cursor-2\"") };

        Assert.Equal(0, await fixture.Run());

        Assert.Equal("/api/v1/platform/egress/destinations?pageSize=50&state=requested", fixture.ListPathAndQuery);
        var outputJson = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Empty(outputJson["requests"]!.AsArray());
        Assert.Equal("cursor-2", outputJson["nextCursor"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListIgnoresEnvironmentProjectAndRunBindingsAndRejectsExplicitFlags()
    {
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "env-project");
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "env-run");

            var envFixture = new Fixture { Body = Page("[]", "null") };
            Assert.Equal(0, await envFixture.Run());
            var identity = envFixture.Authentication.Identities.Single();
            Assert.Null(identity.ProjectId);
            Assert.Null(identity.RunId);

            foreach (var flag in new[] { "--project-id", "--run-id" })
            {
                var flagFixture = new Fixture();
                Assert.Equal(1, await flagFixture.Run(flag, "bound-1"));
                Assert.Empty(flagFixture.Output.ToString());
                Assert.Equal(0, flagFixture.RequestCount);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Theory]
    [InlineData("egress")]
    [InlineData("egress", "provider-requests")]
    [InlineData("egress", "provider-requests", "other")]
    public async Task InvalidSubcommandRoutingFailsWithUsage(params string[] subArgs)
    {
        var fixture = new Fixture();
        Assert.Equal(1, await fixture.RunRaw([.. subArgs, .. Target]));
        Assert.Empty(fixture.Output.ToString());
        Assert.Equal(0, fixture.RequestCount);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "101")]
    [InlineData("--limit", "-1")]
    [InlineData("--limit", "abc")]
    [InlineData("--cursor", "")]
    [InlineData("--cursor", "   ")]
    public async Task InvalidPaginationOptionsFailBeforeDiscovery(string option, string value)
    {
        var fixture = new Fixture();
        Assert.Equal(1, await fixture.Run(option, value));
        Assert.Empty(fixture.Output.ToString());
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task OversizedCursorOptionFailsBeforeDiscovery()
    {
        var fixture = new Fixture();
        Assert.Equal(1, await fixture.Run("--cursor", new string('a', 257)));
        Assert.Empty(fixture.Output.ToString());
        Assert.Equal(0, fixture.RequestCount);
    }

    [Theory]
    [InlineData(DiscoveryMismatch.MissingOperation)]
    [InlineData(DiscoveryMismatch.WrongVerb)]
    [InlineData(DiscoveryMismatch.WrongPath)]
    [InlineData(DiscoveryMismatch.HasRequestBody)]
    [InlineData(DiscoveryMismatch.SecondarySecurity)]
    [InlineData(DiscoveryMismatch.ProjectAuthorizationHeader)]
    [InlineData(DiscoveryMismatch.Missing200Response)]
    [InlineData(DiscoveryMismatch.Wrong200MediaType)]
    [InlineData(DiscoveryMismatch.MissingMaxResponseBytes)]
    [InlineData(DiscoveryMismatch.ExcessiveMaxResponseBytes)]
    [InlineData(DiscoveryMismatch.ZeroMaxResponseBytes)]
    public async Task DiscoveryMismatchStopsBeforeAuthenticationOrDispatch(DiscoveryMismatch mismatch)
    {
        var fixture = new Fixture { Mismatch = mismatch };

        Assert.Equal(1, await fixture.Run());

        Assert.Empty(fixture.Output.ToString());
        Assert.Empty(fixture.Authentication.Identities);
        Assert.Equal(0, fixture.ListRequestCount);
        Assert.Equal(1, fixture.RequestCount);
    }

    [Theory]
    [InlineData(400, true, """{"code":"invalid_pagination"}""",
        "The pagination cursor is invalid. Rerun without --cursor to start from the first page.")]
    [InlineData(400, false, """{"code":"invalid_pagination"}""",
        "The Gateway rejected the platform provider host request query (400).")]
    [InlineData(401, false, """{"code":"unauthorized"}""",
        "Sign-in is required to list platform provider host requests (401).")]
    [InlineData(403, false, """{"code":"forbidden"}""",
        "The caller is not authorized to list platform provider host requests (403).")]
    [InlineData(503, false, """{"code":"egress_proof_unavailable"}""",
        "Platform provider host request verification is unavailable (503).")]
    public async Task HttpFailureStatusesPrintFixedMessagesAndKeepStdoutEmpty(
        int status, bool hasCursor, string body, string expectedMessage)
    {
        var fixture = new Fixture { Status = (HttpStatusCode)status, Body = body };
        string[] args = hasCursor ? ["--cursor", Cursor] : [];

        Assert.Equal(1, await fixture.Run(args));

        Assert.Empty(fixture.Output.ToString());
        Assert.Equal(expectedMessage + fixture.Error.NewLine, fixture.Error.ToString());
        var listCall = Assert.Single(fixture.Calls, call =>
            call["operationId"]?.GetValue<string>() == "listPlatformEgressDestinations");
        Assert.Equal("[REDACTED]", listCall["responseBody"]?.GetValue<string>());
    }

    [Fact]
    public async Task MissingCredentialPrintsSignInInstruction()
    {
        var fixture = new Fixture { MissingCredential = true };

        Assert.Equal(1, await fixture.Run());

        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("AGENTWEAVER_PROJECT_ID", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("AGENTWEAVER_RUN_ID", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("agentweaver auth login --scope projects.admin", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryAndAuthFailuresRecordNoEvidenceWithCanaries()
    {
        var discoveryFailFixture = new Fixture { DiscoveryFailureBody = "discovery-failure-canary" };
        Assert.Equal(1, await discoveryFailFixture.Run());
        Assert.Empty(discoveryFailFixture.Output.ToString());
        Assert.Empty(discoveryFailFixture.Calls);
        Assert.DoesNotContain("discovery-failure-canary", discoveryFailFixture.Error.ToString(), StringComparison.Ordinal);

        var authFailFixture = new Fixture { AuthFailureBody = "auth-failure-canary" };
        Assert.Equal(1, await authFailFixture.Run());
        Assert.Empty(authFailFixture.Output.ToString());
        Assert.Empty(authFailFixture.Calls);
        Assert.DoesNotContain("auth-failure-canary", authFailFixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("part-a&cursor=part-b", "part-a%26cursor%3Dpart-b")]
    [InlineData("""{"a":1,"a":2}""", "%7B%22a%22%3A1%2C%22a%22%3A2%7D")]
    public async Task CursorWithDuplicateKeySyntaxPassesUnchangedAndStaysRedactedInEvidence(
        string duplicateCursor,
        string escapedCursor)
    {
        var fixture = new Fixture
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"code":"invalid_pagination"}""",
        };

        Assert.Equal(1, await fixture.Run("--cursor", duplicateCursor));

        Assert.Equal(
            $"/api/v1/platform/egress/destinations?cursor={escapedCursor}&pageSize=50&state=requested",
            fixture.ListPathAndQuery);
        Assert.Equal(
            "The pagination cursor is invalid. Rerun without --cursor to start from the first page." + fixture.Error.NewLine,
            fixture.Error.ToString());
        var call = Assert.Single(fixture.Calls);
        Assert.Equal("[REDACTED]", call["query"]?["cursor"]?.GetValue<string>());
        Assert.DoesNotContain(duplicateCursor, call.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("part-a", call.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("part-b", call.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidContentTypeAndOversizedResponseFailWithoutOutput()
    {
        var badTypeFixture = new Fixture { ResponseMediaType = "text/plain", Body = "plain-canary" };
        Assert.Equal(1, await badTypeFixture.Run());
        Assert.Empty(badTypeFixture.Output.ToString());
        Assert.DoesNotContain("plain-canary", badTypeFixture.Error.ToString(), StringComparison.Ordinal);

        var oversizedFixture = new Fixture
        {
            Body = $$"""{"destinations":[],"nextCursor":"{{new string('x', 513 * 1024)}}"}""",
        };
        Assert.Equal(1, await oversizedFixture.Run());
        Assert.Empty(oversizedFixture.Output.ToString());
        var listCall = Assert.Single(oversizedFixture.Calls, call =>
            call["operationId"]?.GetValue<string>() == "listPlatformEgressDestinations");
        Assert.Equal("[REDACTED]", listCall["error"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"destinations":[]}""")]
    [InlineData("""{"destinations":[],"nextCursor":null,"extra":1}""")]
    [InlineData("""{"destinations":[],"destinations":[],"nextCursor":null}""")]
    [InlineData("""{"destinations":[],"nextCursor":""}""")]
    [InlineData("""{"destinations":[],"nextCursor":"   "}""")]
    public async Task MalformedPageEnvelopeFailsWithEmptyStdout(string body)
    {
        var fixture = new Fixture { Body = body };

        Assert.Equal(1, await fixture.Run());

        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("""{"destinationId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"requestId":"not-a-guid"}""")]
    [InlineData("""{"host":"10.0.0.1"}""")]
    [InlineData("""{"host":"vault.azure.net"}""")]
    [InlineData("""{"host":"service.local"}""")]
    [InlineData("""{"port":80}""")]
    [InlineData("""{"state":"approved"}""")]
    [InlineData("""{"origin":"manual"}""")]
    [InlineData("""{"scope":"platform","projectId":"proj-1"}""")]
    [InlineData("""{"scope":"project","projectId":null}""")]
    [InlineData("""{"scope":"project","projectId":""}""")]
    [InlineData("""{"tenantId":""}""")]
    [InlineData("""{"providerId":""}""")]
    [InlineData("""{"providerRevision":0}""")]
    [InlineData("""{"requestedBy":""}""")]
    [InlineData("""{"requestedAt":"2026-11-27T10:00:00"}""")]
    [InlineData("""{"requestedAt":"2026-11-27T10:00:00 Z"}""")]
    [InlineData("""{"requestedAt":"2026-11-27 10:00:00Z"}""")]
    public async Task MalformedRowFieldsFailWithEmptyStdout(string patchJson)
    {
        var row = ValidRow();
        foreach (var (key, value) in JsonNode.Parse(patchJson)!.AsObject())
            row[key] = value?.DeepClone();

        var fixture = new Fixture { Body = Page($"[{row.ToJsonString()}]", "null") };

        Assert.Equal(1, await fixture.Run());

        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task ExtraMissingOrDuplicateRowKeysAndExcessRowsFail()
    {
        var extraRow = ValidRow();
        extraRow["extra"] = "bad";
        var extraFixture = new Fixture { Body = Page($"[{extraRow.ToJsonString()}]", "null") };
        Assert.Equal(1, await extraFixture.Run());
        Assert.Empty(extraFixture.Output.ToString());

        var missingRow = ValidRow();
        missingRow.Remove("requestedBy");
        var missingFixture = new Fixture { Body = Page($"[{missingRow.ToJsonString()}]", "null") };
        Assert.Equal(1, await missingFixture.Run());
        Assert.Empty(missingFixture.Output.ToString());

        var rawRow = ValidRow().ToJsonString();
        var duplicateRow = rawRow[..^1] + ",\"host\":\"api.openai.com\"}";
        var duplicateFixture = new Fixture { Body = Page($"[{duplicateRow}]", "null") };
        Assert.Equal(1, await duplicateFixture.Run());
        Assert.Empty(duplicateFixture.Output.ToString());

        var twoRowsFixture = new Fixture
        {
            Body = Page($"[{ValidRow().ToJsonString()},{ValidRow().ToJsonString()}]", "null"),
        };
        Assert.Equal(1, await twoRowsFixture.Run("--limit", "1"));
        Assert.Empty(twoRowsFixture.Output.ToString());
    }

    [Fact]
    public async Task CallerCancellationExits130AndInternalTimeoutsExit1()
    {
        var cancelFixture = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(130, await cancelFixture.Run(cts.Token));
        Assert.Empty(cancelFixture.Output.ToString());
        Assert.Contains("cancelled", cancelFixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);

        var discoveryTimeoutFixture = new Fixture
        {
            DelayDiscovery = TimeSpan.FromMilliseconds(200),
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        Assert.Equal(1, await discoveryTimeoutFixture.Run());
        Assert.Empty(discoveryTimeoutFixture.Output.ToString());
        Assert.Equal("The Gateway request timed out." + discoveryTimeoutFixture.Error.NewLine,
            discoveryTimeoutFixture.Error.ToString());

        var invokeTimeoutFixture = new Fixture
        {
            DelayList = TimeSpan.FromMilliseconds(200),
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        Assert.Equal(1, await invokeTimeoutFixture.Run());
        Assert.Empty(invokeTimeoutFixture.Output.ToString());
        Assert.Equal("The Gateway request timed out." + invokeTimeoutFixture.Error.NewLine,
            invokeTimeoutFixture.Error.ToString());
    }

    [Fact]
    public async Task TransportFailureKeepsCanaryOutOfStderrAndDiagnostics()
    {
        var fixture = new Fixture { TransportFailure = true };

        Assert.Equal(1, await fixture.Run("--cursor", SecretCanary));

        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain(SecretCanary, fixture.Error.ToString(), StringComparison.Ordinal);
        var listCall = Assert.Single(fixture.Calls, call =>
            call["operationId"]?.GetValue<string>() == "listPlatformEgressDestinations");
        Assert.Equal("[REDACTED]", listCall["error"]?.GetValue<string>());
        Assert.Equal("[REDACTED]", listCall["query"]?["cursor"]?.GetValue<string>());
    }

    private static JsonObject ValidRow() => new()
    {
        ["destinationId"] = "11111111-1111-1111-1111-111111111111",
        ["requestId"] = "22222222-2222-2222-2222-222222222222",
        ["host"] = "api.openai.com",
        ["port"] = 443,
        ["state"] = "requested",
        ["origin"] = "modelProvider",
        ["scope"] = "platform",
        ["projectId"] = null,
        ["tenantId"] = "tenant-a",
        ["providerId"] = "prov-1",
        ["providerRevision"] = 1,
        ["requestedBy"] = "user-1",
        ["requestedAt"] = "2026-11-27T10:00:00Z",
    };

    private static string Page(string destinations, string nextCursor) =>
        $$"""{"destinations":{{destinations}},"nextCursor":{{nextCursor}}}""";

    public enum DiscoveryMismatch
    {
        None,
        MissingOperation,
        WrongVerb,
        WrongPath,
        HasRequestBody,
        SecondarySecurity,
        ProjectAuthorizationHeader,
        Missing200Response,
        Wrong200MediaType,
        MissingMaxResponseBytes,
        ExcessiveMaxResponseBytes,
        ZeroMaxResponseBytes,
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private GatewayClient? _lastClient;
        public readonly StringWriter Output = new();
        public readonly StringWriter Error = new();
        public readonly Authentication Authentication = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? ResponseMediaType;
        public string Body = Page("[]", "null");
        public DiscoveryMismatch Mismatch = DiscoveryMismatch.None;
        public string? DiscoveryFailureBody;
        public string? AuthFailureBody;
        public bool TransportFailure;
        public bool MissingCredential;
        public TimeSpan? DelayDiscovery;
        public TimeSpan? DelayList;
        public TimeSpan? RequestTimeout;
        public int RequestCount;
        public int ListRequestCount;
        public HttpMethod? ListMethod;
        public string? ListPathAndQuery;
        public string? ListBearer;
        public string? ListTenant;
        public string? ListProjectAuthorization;

        public IReadOnlyList<JsonObject> Calls => _lastClient?.Calls ?? [];

        public Task<int> Run(params string[] args) => Run(CancellationToken.None, args);

        public Task<int> Run(CancellationToken ct, params string[] args) =>
            RunRaw(["egress", "provider-requests", "list", .. args, .. Target], ct);

        public async Task<int> RunRaw(string[] platformArgs, CancellationToken ct = default)
        {
            Authentication.MissingCredential = MissingCredential;
            var httpClient = new HttpClient(this, disposeHandler: false);
            var factory = new TestGatewayClientFactory(
                httpClient,
                Authentication,
                RequestTimeout,
                client => _lastClient = client);
            return await CliApplication.RunAsync(
                ["platform", .. platformArgs],
                new CliRuntimeOptions(
                    Authentication: Authentication,
                    GatewayClientFactory: factory,
                    HttpClient: httpClient,
                    Output: Output,
                    Error: Error),
                ct).ConfigureAwait(false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
            {
                if (DelayDiscovery is { } delay)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (DiscoveryFailureBody is not null)
                    return JsonResponse(HttpStatusCode.InternalServerError, DiscoveryFailureBody, "application/json");
                return JsonResponse(HttpStatusCode.OK, BuildOpenApi(), "application/json");
            }

            if (path == "/api/v1/authorization/context")
            {
                if (AuthFailureBody is not null)
                    return JsonResponse(HttpStatusCode.Forbidden, AuthFailureBody, "application/json");
                return JsonResponse(HttpStatusCode.OK, """
                {
                  "contractVersion": 1,
                  "issuer": "https://identity.example/",
                  "tenantId": "tenant-1",
                  "actorId": "actor-1",
                  "membershipRevision": 1,
                  "boundProjectId": null,
                  "boundRunId": null,
                  "effectiveAuthority": []
                }
                """, "application/json");
            }

            ListRequestCount++;
            ListMethod = request.Method;
            ListPathAndQuery = request.RequestUri.PathAndQuery;
            ListBearer = request.Headers.Authorization?.Parameter;
            ListTenant = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                ? tenants.SingleOrDefault()
                : null;
            ListProjectAuthorization = request.Headers.TryGetValues("X-Agentweaver-Project-Authorization", out var projectAuth)
                ? projectAuth.SingleOrDefault()
                : null;

            if (DelayList is { } listDelay)
                await Task.Delay(listDelay, cancellationToken).ConfigureAwait(false);
            if (TransportFailure)
                throw new HttpRequestException($"Transport failed for {SecretCanary}");

            return JsonResponse(Status, Body, ResponseMediaType ?? "application/json");
        }

        private string BuildOpenApi()
        {
            var paths = new JsonObject
            {
                ["/api/v1/authorization/context"] = new JsonObject
                {
                    ["get"] = new JsonObject
                    {
                        ["operationId"] = "getAuthorizationContext",
                        ["responses"] = new JsonObject
                        {
                            ["200"] = new JsonObject
                            {
                                ["content"] = new JsonObject
                                {
                                    ["application/json"] = new JsonObject(),
                                },
                            },
                        },
                    },
                },
            };

            if (Mismatch != DiscoveryMismatch.MissingOperation)
            {
                var operation = new JsonObject
                {
                    ["operationId"] = "listPlatformEgressDestinations",
                    ["parameters"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "state", ["in"] = "query", ["required"] = false, ["schema"] = new JsonObject { ["type"] = "string" } },
                        new JsonObject { ["name"] = "pageSize", ["in"] = "query", ["required"] = false, ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } },
                        new JsonObject { ["name"] = "cursor", ["in"] = "query", ["required"] = false, ["schema"] = new JsonObject { ["type"] = "string" } },
                    },
                    ["responses"] = Mismatch switch
                    {
                        DiscoveryMismatch.Missing200Response => new JsonObject
                        {
                            ["201"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["application/json"] = new JsonObject() },
                            },
                        },
                        DiscoveryMismatch.Wrong200MediaType => new JsonObject
                        {
                            ["200"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["application/octet-stream"] = new JsonObject() },
                            },
                        },
                        _ => new JsonObject
                        {
                            ["200"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["application/json"] = new JsonObject() },
                            },
                        },
                    },
                };

                if (Mismatch == DiscoveryMismatch.HasRequestBody)
                {
                    operation["requestBody"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject() },
                    };
                }
                if (Mismatch == DiscoveryMismatch.SecondarySecurity)
                {
                    operation["security"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["Bearer"] = new JsonArray(),
                            ["Other"] = new JsonArray(),
                        },
                    };
                }
                if (Mismatch == DiscoveryMismatch.ProjectAuthorizationHeader)
                {
                    operation["parameters"]!.AsArray().Add(new JsonObject
                    {
                        ["name"] = "X-Agentweaver-Project-Authorization",
                        ["in"] = "header",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["type"] = "string" },
                    });
                }
                if (Mismatch != DiscoveryMismatch.MissingMaxResponseBytes)
                {
                    operation["x-agentweaver-max-response-bytes"] = Mismatch switch
                    {
                        DiscoveryMismatch.ExcessiveMaxResponseBytes => (512 * 1024) + 1,
                        DiscoveryMismatch.ZeroMaxResponseBytes => 0,
                        _ => 512 * 1024,
                    };
                }

                var opPath = Mismatch == DiscoveryMismatch.WrongPath
                    ? "/api/v1/platform/egress/other"
                    : "/api/v1/platform/egress/destinations";
                var opMethod = Mismatch == DiscoveryMismatch.WrongVerb ? "post" : "get";
                paths[opPath] = new JsonObject { [opMethod] = operation };
            }

            var root = new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["security"] = new JsonArray { new JsonObject { ["Bearer"] = new JsonArray() } },
                ["components"] = new JsonObject
                {
                    ["securitySchemes"] = new JsonObject
                    {
                        ["Bearer"] = new JsonObject
                        {
                            ["type"] = "http",
                            ["scheme"] = "bearer",
                        },
                    },
                },
                ["paths"] = paths,
            };
            return root.ToJsonString();
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body, string mediaType) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
    }

    private sealed class TestGatewayClientFactory(
        HttpClient httpClient,
        ICliAuthentication authentication,
        TimeSpan? requestTimeout,
        Action<GatewayClient> onCreate) : IGatewayClientFactory
    {
        public IGatewayClient Create(CliIdentity identity)
        {
            var client = requestTimeout is { } timeout
                ? new GatewayClient(httpClient, identity, authentication, timeout)
                : new GatewayClient(httpClient, identity, authentication);
            onCreate(client);
            return client;
        }
    }

    private sealed class Authentication : ICliAuthentication
    {
        public readonly List<CliIdentity> Identities = [];
        public readonly List<InteractionPolicy> Interactions = [];
        public bool MissingCredential;

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            Identities.Add(identity);
            Interactions.Add(interaction);
            if (MissingCredential)
                throw new CliAuthNeedsLoginException("Sign in required.");
            return Task.FromResult("safe-bearer");
        }
    }
}
