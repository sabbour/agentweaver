using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class RunEgressCommandTests
{
    private const string TargetRunId = "run/a b#";
    private const string Cursor = "next/+ =?cursor";
    private static readonly string[] Target =
    [
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--client-id", "cli-tests",
        "--api-audience", "agentweaver",
        "--tenant-id", "tenant-1",
    ];

    [Fact]
    public async Task EgressUsesExactGetPathQueryHeadersAndDefaultCredential()
    {
        var fixture = new Fixture();

        Assert.Equal(0, await fixture.Run(TargetRunId, "--limit", "25", "--cursor", Cursor));

        Assert.Equal(HttpMethod.Get, fixture.EgressMethod);
        Assert.Equal(
            "/api/v1/runs/run%2Fa%20b%23/egress?cursor=next%2F%2B%20%3D%3Fcursor&limit=25",
            fixture.EgressPathAndQuery);
        Assert.Equal("safe-bearer", fixture.EgressBearer);
        Assert.Equal("tenant-1", fixture.EgressTenant);
        Assert.Null(fixture.EgressProjectAuthorization);
        Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identities.Single().Scopes);
        Assert.All(fixture.Authentication.Interactions, policy => Assert.Equal(InteractionPolicy.Never, policy));
        var page = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(["coverage", "delayedUntil", "items", "nextCursor", "runId"],
            page.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(TargetRunId, page["runId"]!.GetValue<string>());
        Assert.Equal("Unavailable", page["coverage"]!.GetValue<string>());
        Assert.Null(page["delayedUntil"]);
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal("next-page", page["nextCursor"]!.GetValue<string>());
        Assert.Contains("coverage is unavailable", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EgressOmitsDefaultLimitAndPreservesAnEmptyPage()
    {
        var fixture = new Fixture { Body = Page("[]", "null") };

        Assert.Equal(0, await fixture.Run(TargetRunId));

        Assert.Equal("/api/v1/runs/run%2Fa%20b%23/egress", fixture.EgressPathAndQuery);
        var page = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Empty(page["items"]!.AsArray());
        Assert.Null(page["nextCursor"]);
    }

    [Fact]
    public async Task AdminScopeSelectsUnboundProjectsAdminCredential()
    {
        var fixture = new Fixture();

        Assert.Equal(0, await fixture.Run(TargetRunId, "--scope", "projects.admin"));

        var identity = fixture.Authentication.Identities.Single();
        Assert.Null(identity.ProjectId);
        Assert.Null(identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access", "projects.admin"], identity.Scopes);
    }

    [Fact]
    public async Task OpenApiVerbMismatchStopsBeforeAuthenticationOrEgress()
    {
        var fixture = new Fixture { WrongVerb = true };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Output.ToString());
        Assert.Empty(fixture.Authentication.Identities);
        Assert.Equal(0, fixture.EgressRequestCount);
        Assert.Equal(1, fixture.RequestCount);
        Assert.Contains("does not support", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOperationAndSecondaryAuthorizationHeaderAreRejectedBeforeAuth()
    {
        foreach (var missing in new[] { true, false })
        {
            var fixture = new Fixture { MissingOperation = missing, SecondaryAuthorization = !missing };
            Assert.Equal(1, await fixture.Run(TargetRunId));
            Assert.Empty(fixture.Authentication.Identities);
            Assert.Equal(0, fixture.EgressRequestCount);
            Assert.Empty(fixture.Output.ToString());
        }
    }

    [Fact]
    public async Task SecondarySecurityRequirementIsRejectedBeforeAuth()
    {
        var fixture = new Fixture { SecondarySecurity = true };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Authentication.Identities);
        Assert.Equal(0, fixture.EgressRequestCount);
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "101")]
    [InlineData("--scope", "projects.orchestrator")]
    [InlineData("--project-id", "project-1")]
    [InlineData("--run-id", "run-1")]
    public async Task InvalidArgumentsFailBeforeNetwork(string option, string value)
    {
        var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run(TargetRunId, option, value));

        Assert.Equal(0, fixture.RequestCount);
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task ApiErrorsLeaveStdoutEmptyAndExitOne(int status)
    {
        var fixture = new Fixture
        {
            Status = (HttpStatusCode)status,
            Body = status == 404
                ? """{"code":"run_not_found","detail":"private-canary"}"""
                : """{"code":"private_error","detail":"private-canary"}""",
        };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("private-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        if (status == 404)
            Assert.Contains("run_not_found", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownServerCodeStaysOffStderr()
    {
        var fixture = new Fixture
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"code":"private_opaque_id_123","detail":"private-canary"}""",
        };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("private_opaque_id_123", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"runId":"different","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null}""")]
    [InlineData("""{"runId":"run/a b#","coverage":"Unavailable","delayedUntil":null,"items":[{"private":"canary"}],"nextCursor":null}""")]
    [InlineData("""{"runId":"run/a b#","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null,"private":"canary"}""")]
    public async Task MalformedResponseLeavesStdoutEmpty(string body)
    {
        var fixture = new Fixture { Body = body };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("canary", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailureIsRedactedAndCancellationReturns130()
    {
        var fixture = new Fixture { TransportFailure = true };
        Assert.Equal(1, await fixture.Run(TargetRunId));
        Assert.Empty(fixture.Output.ToString());
        Assert.DoesNotContain("transport-canary", fixture.Error.ToString(), StringComparison.Ordinal);

        fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(130, await fixture.Run(cancellation.Token, TargetRunId));
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("cancelled", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingCachedCredentialPrintsSignInInstruction()
    {
        var fixture = new Fixture { MissingCredential = true };

        Assert.Equal(1, await fixture.Run(TargetRunId));

        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("agentweaver auth login", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("request failed", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    private static string Page(string items, string nextCursor) =>
        $$"""{"runId":"{{TargetRunId}}","coverage":"Unavailable","delayedUntil":null,"items":{{items}},"nextCursor":{{nextCursor}}}""";

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly StringWriter Output = new();
        public readonly StringWriter Error = new();
        public readonly Authentication Authentication = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = Page(
            """[{"host":"api.example","address":null,"port":443,"verdict":"allowed","firstSeen":"2026-11-20T10:00:00Z","lastSeen":"2026-11-20T10:00:01+00:00","count":2}]""",
            "\"next-page\"");
        public bool WrongVerb;
        public bool MissingOperation;
        public bool SecondaryAuthorization;
        public bool SecondarySecurity;
        public bool TransportFailure;
        public bool MissingCredential;
        public int RequestCount;
        public int EgressRequestCount;
        public HttpMethod? EgressMethod;
        public string? EgressPathAndQuery;
        public string? EgressBearer;
        public string? EgressTenant;
        public string? EgressProjectAuthorization;

        public async Task<int> Run(params string[] args) =>
            await Run(CancellationToken.None, args).ConfigureAwait(false);

        public async Task<int> Run(CancellationToken ct, params string[] args)
        {
            Authentication.MissingCredential = MissingCredential;
            return await CliApplication.RunAsync(
                ["run", "egress", .. args, .. Target],
                new CliRuntimeOptions(
                    Authentication: Authentication,
                    HttpClient: new HttpClient(this, disposeHandler: false),
                    Output: Output,
                    Error: Error),
                ct).ConfigureAwait(false);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
            {
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    OpenApi()));
            }
            if (path == "/api/v1/authorization/context")
            {
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    """{"contractVersion":1,"issuer":"https://identity.example/","actorId":"actor-1","tenantId":"tenant-1","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}"""));
            }

            EgressRequestCount++;
            EgressMethod = request.Method;
            EgressPathAndQuery = request.RequestUri.PathAndQuery;
            EgressBearer = request.Headers.Authorization?.Parameter;
            EgressTenant = request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                ? tenant.Single()
                : null;
            EgressProjectAuthorization = request.Headers.TryGetValues(
                "X-Agentweaver-Project-Authorization", out var projectAuthorization)
                ? projectAuthorization.Single()
                : null;
            if (TransportFailure)
                throw new HttpRequestException("transport-canary");
            return Task.FromResult(Json(Status, Body,
                Status == HttpStatusCode.OK ? "application/json" : "application/problem+json"));
        }

        private string OpenApi()
        {
            var type = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!;
            var emitted = type.GetMethod("CreateDocument", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null);
            var document = JsonSerializer.SerializeToNode(
                emitted,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var path = document["paths"]!["/api/v1/runs/{runId}/egress"]!.AsObject();
            if (WrongVerb)
            {
                path["post"] = path["get"]!.DeepClone();
                path.Remove("get");
            }
            else if (MissingOperation)
            {
                path.Remove("get");
            }
            else if (SecondaryAuthorization)
            {
                path["get"]!["parameters"]!.AsArray().Add(JsonNode.Parse(
                    """{"name":"X-Agentweaver-Project-Authorization","in":"header","required":true,"schema":{"type":"string"}}"""));
            }
            else if (SecondarySecurity)
            {
                path["get"]!["security"] = JsonNode.Parse(
                    """[{"Bearer":[],"X-Agentweaver-Secondary":[]}]""");
            }
            return document.ToJsonString();
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    private sealed class Authentication : ICliAuthentication
    {
        public readonly List<CliIdentity> Identities = [];
        public readonly List<InteractionPolicy> Interactions = [];

        public Task<string> GetBearerAsync(
            CliIdentity identity,
            InteractionPolicy interaction,
            CancellationToken ct)
        {
            if (!Identities.Contains(identity))
                Identities.Add(identity);
            Interactions.Add(interaction);
            return MissingCredential
                ? throw new CliAuthNeedsLoginException("cached session missing")
                : Task.FromResult("safe-bearer");
        }

        public bool MissingCredential { get; set; }

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
