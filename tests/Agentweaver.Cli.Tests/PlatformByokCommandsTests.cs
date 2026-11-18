using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class PlatformByokCommandsTests
{
    private const string ProviderId = "byok-platform-0001";
    private const string OperationId = "f1b62270-3320-414c-9c2a-803493e50131";
    private static readonly string[] TargetOptions =
    [
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--api-audience", "https://gateway.example/",
        "--tenant-id", "tenant-1",
    ];

    [Fact]
    public async Task ListReadsEveryCursorPageAndReturnsOnlyMetadata()
    {
        using var fixture = new Fixture();
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "ambient-project");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run");
        fixture.Respond = request =>
        {
            if (request.Query.Contains("afterProviderId=" + ProviderId, StringComparison.Ordinal))
                return JsonResponse(Page([Provider("byok-platform-0002", "Second")], null));
            return JsonResponse(Page([Provider(ProviderId, "First", secret: "list-secret-canary")], ProviderId));
        };

        try
        {
            Assert.Equal(0, await fixture.Run("list"));

            var requests = fixture.Requests;
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request => Assert.Contains("pageSize=100", request.Query, StringComparison.Ordinal));
            Assert.DoesNotContain("afterProviderId=", requests[0].Query, StringComparison.Ordinal);
            Assert.Contains("afterProviderId=" + ProviderId, requests[1].Query, StringComparison.Ordinal);
            var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            var providers = result["providers"]!.AsArray();
            Assert.Equal(["byok-platform-0001", "byok-platform-0002"],
                providers.Select(provider => provider!["providerId"]!.GetValue<string>()));
            Assert.DoesNotContain("list-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hasCredential", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("state", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Contains("projects.admin", fixture.Authentication.Identity.Scopes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Fact]
    public async Task ListRejectsARepeatedProviderCursor()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => JsonResponse(Page([Provider(ProviderId, "First")], ProviderId));

        Assert.Equal(1, await fixture.Run("list"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("invalid platform BYOK response", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowUsesProviderIdAndKeepsTheNameAsDisplayText()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(ProviderId, "Mutable display name", secret: "response-secret-canary")),
        };

        Assert.Equal(0, await fixture.Run("show", ProviderId));

        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId, fixture.Requests.Single().Path);
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(ProviderId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Mutable display name", result["name"]!.GetValue<string>());
        Assert.DoesNotContain("response-secret-canary", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddUsesTheApiProviderIdAndSendsOnlyMetadata()
    {
        const string apiGeneratedId = "byok-api-generated-42";
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(apiGeneratedId, "Staging endpoint")),
        };

        Assert.Equal(0, await fixture.Run(
            "add",
            "--name", "Staging endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-a",
            "--wire-api", "responses",
            "--reasoning-effort", "low",
            "--reasoning-effort", "high",
            "--operation-id", OperationId));

        var request = fixture.Requests.Single();
        Assert.Equal(HttpMethod.Post.Method, request.Method);
        Assert.Equal("/api/v1/platform/byok-providers", request.Path);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal(
            new[]
            {
                "azure_api_version", "base_url", "expectedRevision", "model", "name",
                "operationId", "prompt_capacity_tokens", "reasoning_efforts", "type", "wire_api",
            },
            body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(OperationId, body["operationId"]!.GetValue<string>());
        Assert.Equal(0, body["expectedRevision"]!.GetValue<int>());
        Assert.Equal(["low", "high"], body["reasoning_efforts"]!.AsArray()
            .Select(effort => effort!.GetValue<string>()));
        Assert.DoesNotContain("key", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pricing", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("budget", request.Body, StringComparison.OrdinalIgnoreCase);
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(apiGeneratedId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Staging endpoint", result["name"]!.GetValue<string>());
        Assert.DoesNotContain("has_api_key", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("is_active", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRequiresRevisionAndKeepsAProviderIdAfterRename()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(Provider(ProviderId, "Renamed endpoint", revision: 2)),
        };

        Assert.Equal(1, await fixture.Run(
            "update", ProviderId,
            "--name", "Renamed endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b"));
        Assert.Empty(fixture.Requests);
        Assert.Contains("--expected-revision", fixture.Error.ToString(), StringComparison.Ordinal);

        fixture.Error.GetStringBuilder().Clear();
        Assert.Equal(0, await fixture.Run(
            "update", ProviderId,
            "--expected-revision", "1",
            "--name", "Renamed endpoint",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b",
            "--operation-id", OperationId));

        var request = fixture.Requests.Single();
        Assert.Equal(HttpMethod.Put.Method, request.Method);
        Assert.Equal("/api/v1/platform/byok-providers/" + ProviderId, request.Path);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal(1, body["expectedRevision"]!.GetValue<long>());
        Assert.Equal("Renamed endpoint", body["name"]!.GetValue<string>());
        var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(ProviderId, result["providerId"]!.GetValue<string>());
        Assert.Equal("Renamed endpoint", result["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReusingAnExplicitOperationIdReplaysTheSameWrite()
    {
        using var fixture = new Fixture
        {
            Respond = _ => throw new HttpRequestException("The response was lost."),
        };
        var arguments = new[]
        {
            "add", "--name", "Replayable provider", "--type", "openai",
            "--base-url", "https://models.example/", "--model", "deployment-a",
            "--operation-id", OperationId,
        };

        Assert.Equal(1, await fixture.Run(arguments));
        Assert.Contains(OperationId, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same command", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Error.GetStringBuilder().Clear();
        fixture.Respond = _ => JsonResponse(Provider("byok-from-api", "Replayable provider"));
        Assert.Equal(0, await fixture.Run(arguments));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(fixture.Requests[0].Body, fixture.Requests[1].Body);
        Assert.Equal(OperationId, JsonNode.Parse(fixture.Requests[1].Body)!["operationId"]!.GetValue<string>());
        Assert.Equal("byok-from-api",
            JsonNode.Parse(fixture.Output.ToString())!["providerId"]!.GetValue<string>());
    }

    [Fact]
    public async Task CanceledWriteShowsItsOperationIdForReplay()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        Assert.Equal(130, await fixture.Run(
            cancellation.Token,
            "add",
            "--name", "Canceled provider",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-a"));

        Assert.Single(fixture.Requests);
        var generatedOperationId =
            JsonNode.Parse(fixture.Requests.Single().Body)!["operationId"]!.GetValue<string>();
        Assert.True(Guid.TryParse(generatedOperationId, out var parsedOperationId));
        Assert.NotEqual(Guid.Empty, parsedOperationId);
        Assert.Contains(generatedOperationId, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("write result is unknown", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeMetadataConflictShowsOnlyProviderIdAndRevision()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                """{"code":"model_provider_draft_metadata_requires_update","id":"byok-legacy-0001","revision":7,"base_url":"https://bad.example/?api_key=secret-canary"}""",
                HttpStatusCode.Conflict),
        };

        Assert.Equal(1, await fixture.Run("show", "byok-legacy-0001"));

        Assert.Contains("byok-legacy-0001", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("revision 7", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task StaleRevisionFailsWithoutRetry()
    {
        using var fixture = new Fixture
        {
            Respond = _ => JsonResponse(
                """{"code":"model_provider_draft_conflict"}""",
                HttpStatusCode.Conflict),
        };

        Assert.Equal(1, await fixture.Run(
            "update", ProviderId,
            "--expected-revision", "1",
            "--name", "Changed",
            "--type", "openai",
            "--base-url", "https://models.example/",
            "--model", "deployment-b",
            "--operation-id", OperationId));

        Assert.Single(fixture.Requests);
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("revision changed", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlatformCommandsRejectProjectSelectors()
    {
        using var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run("list", "--project-id", "project-1"));

        Assert.Empty(fixture.Requests);
        Assert.Contains("Unknown option: --project-id", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    private static string Page(IReadOnlyList<JsonObject> providers, string? nextAfterProviderId) =>
        new JsonObject
        {
            ["providers"] = new JsonArray(providers.Select(provider => (JsonNode?)provider.DeepClone()).ToArray()),
            ["nextAfterProviderId"] = nextAfterProviderId,
        }.ToJsonString();

    private static JsonObject Provider(string id, string name, long revision = 1, string? secret = null) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = "openai",
            ["base_url"] = "https://models.example/",
            ["model"] = "deployment-a",
            ["wire_api"] = "responses",
            ["azure_api_version"] = null,
            ["reasoning_efforts"] = new JsonArray("low"),
            ["prompt_capacity_tokens"] = null,
            ["scope"] = "platform",
            ["projectId"] = null,
            ["state"] = "Draft",
            ["revision"] = revision,
            ["configurationHash"] = "abc123",
            ["has_api_key"] = false,
            ["is_active"] = false,
            ["hasCredential"] = false,
            ["api_key"] = secret,
            ["credentialReference"] = secret,
        };

    private static HttpResponseMessage JsonResponse(
        JsonObject body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        JsonResponse(body.ToJsonString(), status);

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Fixture : HttpMessageHandler, IGatewayClientFactory, IDisposable
    {
        private readonly HttpClient _http;

        public Fixture()
        {
            _http = new HttpClient(this, disposeHandler: false);
        }

        public List<Request> Requests { get; } = [];
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public Authentication Authentication { get; } = new();
        public Func<Request, HttpResponseMessage>? Respond { get; set; }

        public IGatewayClient Create(CliIdentity identity) =>
            new GatewayClient(_http, identity, Authentication);

        public Task<int> Run(params string[] args) =>
            Run(CancellationToken.None, args);

        public Task<int> Run(CancellationToken cancellationToken, params string[] args) =>
            CliApplication.RunAsync(
                ["platform", "byok", .. args, .. TargetOptions],
                new CliRuntimeOptions(
                    GatewayClientFactory: this,
                    Authentication: Authentication,
                    Output: Output,
                    Error: Error),
                cancellationToken);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
            {
                var openApi = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!;
                var emitted = openApi.GetMethod("CreateDocument", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, null);
                return JsonResponse(JsonSerializer.Serialize(emitted, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            if (path == "/api/v1/authorization/context")
            {
                return JsonResponse(
                    """{"contractVersion":1,"issuer":"https://identity.example/","actorId":"actor-1","tenantId":"tenant-1","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}""");
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var captured = new Request(request.Method.Method, request.RequestUri.PathAndQuery, path, body);
            Requests.Add(captured);
            return Respond?.Invoke(captured) ??
                   JsonResponse("{}", HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _http.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record Request(string Method, string PathAndQuery, string Path, string Body)
    {
        public string Query => PathAndQuery.Contains('?')
            ? PathAndQuery[(PathAndQuery.IndexOf('?') + 1)..]
            : string.Empty;
    }

    private sealed class Authentication : ICliAuthentication
    {
        public CliIdentity? Identity { get; private set; }

        public Task<string> GetBearerAsync(
            CliIdentity identity,
            InteractionPolicy interaction,
            CancellationToken cancellationToken)
        {
            Identity = identity;
            return Task.FromResult("safe-bearer");
        }

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CliIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
