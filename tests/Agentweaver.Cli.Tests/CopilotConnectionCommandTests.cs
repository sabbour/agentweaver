using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class CopilotConnectionCommandTests
{
    private const string Gateway = "https://gateway.example/";
    private const string Issuer = "https://identity.example/";
    private const string Portal = "https://portal.example/";
    private const string CallbackCookie = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly string[] TargetOptions =
    [
        "--gateway-url", Gateway,
        "--broker-issuer", Issuer,
        "--client-id", "agentweaver-cli",
        "--api-audience", Gateway,
        "--tenant-id", "tenant-1",
    ];

    [Fact]
    public async Task StatusUsesAnUnboundIdentityAndReturnsTheScopedPage()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(JsonResponse(new
        {
            items = new[]
            {
                new
                {
                    connectionId = "b390b940-4141-4d5a-a1e5-4db4ec10a487",
                    revision = 3,
                    scope = "project",
                    scopeId = "project-1",
                    state = "connected",
                    freshUntil = "2026-11-09T12:00:00Z",
                },
            },
            nextAfterConnectionId = (Guid?)null,
        })));
        using var http = new HttpClient(handler);
        var factory = new RecordingGatewayFactory();
        var output = new StringWriter();
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "ambient-project");
        Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "ambient-run");
        try
        {
            var result = await CopilotConnectionCommands.RunAsync(
            [
                "status", "--scope", "project", "--scope-id", "project-1",
                .. TargetOptions,
            ],
            new CliRuntimeOptions(
                GatewayClientFactory: factory,
                Authentication: new RecordingAuthentication(),
                Output: output),
            http,
            output,
            CancellationToken.None);

            Assert.Equal(0, result);
            Assert.Null(factory.Identity?.ProjectId);
            Assert.Null(factory.Identity?.RunId);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/connections/copilot-user/v1/list?scope=project&scopeId=project-1&pageSize=50",
                request.PathAndQuery);
            Assert.Equal("Bearer cli-bearer", request.Authorization);
            Assert.Equal("tenant-1", request.Tenant);
            Assert.Null(request.Cookie);
            Assert.Contains("\"state\": \"connected\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"nextAfterConnectionId\": null", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Fact]
    public async Task ConnectSendsTheLoopbackUriAndOnlyForwardsTheBrokerCookieOnComplete()
    {
        var state = "sealed-state";
        var id = Guid.Parse("b390b940-4141-4d5a-a1e5-4db4ec10a487");
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/begin", StringComparison.Ordinal))
            {
                var response = JsonResponse(new
                {
                    connection = Receipt(id, 1, "pending"),
                    authorizationUri =
                        $"https://github.com/login/oauth/authorize?redirect_uri={Uri.EscapeDataString(Portal + "auth/github/copilot-app/callback")}&state={state}",
                });
                response.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    $"{CopilotConnectionCommandsCookieName}={CallbackCookie}; Path=/; Max-Age=300; Secure; HttpOnly; SameSite=Lax");
                return Task.FromResult(response);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(Receipt(id, 3, "connected")));
            throw new InvalidOperationException("The command sent an unexpected request.");
        });
        using var http = new HttpClient(handler);
        var listeners = new RecordingListenerFactory(
            new OAuthCallback("one-time-code", null),
            new Uri("http://127.0.0.1:49173/oauth/callback"));
        var browser = new RecordingBrowser();
        var factory = new RecordingGatewayFactory();
        var output = new StringWriter();

        var result = await CopilotConnectionCommands.RunAsync(
        [
            "connect", "--scope", "platform",
            .. TargetOptions,
        ],
        new CliRuntimeOptions(
            GatewayClientFactory: factory,
            Authentication: new RecordingAuthentication(),
            Output: output),
        http,
        output,
        CancellationToken.None,
        listeners,
        browser);

        Assert.Equal(0, result);
        Assert.Equal(state, listeners.Listener.ExpectedState);
        Assert.Equal(Issuer, listeners.Listener.ExpectedIssuer);
        Assert.Equal("https://github.com/login/oauth/authorize", browser.AuthorizationUri?.GetLeftPart(UriPartial.Path));
        var authorizationQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(browser.AuthorizationUri!.Query);
        Assert.Equal(Portal + "auth/github/copilot-app/callback", authorizationQuery["redirect_uri"].ToString());
        Assert.Equal("platform", ReadJson(handler.Requests.Single(item => item.PathAndQuery.EndsWith("/begin")).Body!)["scope"]?.GetValue<string>());
        Assert.Equal("http://127.0.0.1:49173/oauth/callback",
            ReadJson(handler.Requests.Single(item => item.PathAndQuery.EndsWith("/begin")).Body!)["cliCallbackUri"]?.GetValue<string>());
        var completeRequest = Assert.Single(handler.Requests,
            request => request.PathAndQuery.EndsWith("/complete", StringComparison.Ordinal));
        Assert.Equal($"{CopilotConnectionCommandsCookieName}={CallbackCookie}", completeRequest.Cookie);
        Assert.Contains("one-time-code", completeRequest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-code", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(state, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CallbackCookie, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests.Where(request => !request.PathAndQuery.EndsWith("/complete", StringComparison.Ordinal)),
            request => Assert.Null(request.Cookie));
    }

    [Fact]
    public async Task ConnectRejectsANonstandardGitHubPortBeforeOpeningTheBrowser()
    {
        var id = Guid.Parse("b390b940-4141-4d5a-a1e5-4db4ec10a487");
        var handler = new RecordingHandler((_, _) =>
        {
            var response = JsonResponse(new
            {
                connection = Receipt(id, 1, "pending"),
                authorizationUri =
                    $"https://github.com:444/login/oauth/authorize?redirect_uri={Uri.EscapeDataString(Gateway + "auth/github/copilot-app/callback")}&state=sealed-state",
            });
            response.Headers.TryAddWithoutValidation(
                "Set-Cookie",
                $"{CopilotConnectionCommandsCookieName}={CallbackCookie}; Path=/; Max-Age=300; Secure; HttpOnly; SameSite=Lax");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var listeners = new RecordingListenerFactory(
            new OAuthCallback("unused", null),
            new Uri("http://127.0.0.1:49173/oauth/callback"));
        var browser = new RecordingBrowser();

        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotConnectionCommands.RunAsync(
        [
            "connect", "--scope", "platform",
            .. TargetOptions,
        ],
        new CliRuntimeOptions(
            GatewayClientFactory: new RecordingGatewayFactory(),
            Authentication: new RecordingAuthentication()),
        http,
        TextWriter.Null,
        CancellationToken.None,
        listeners,
        browser));

        Assert.Null(browser.AuthorizationUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefreshReadsTheCurrentRevisionBeforePostingTheMutation()
    {
        var id = Guid.Parse("b390b940-4141-4d5a-a1e5-4db4ec10a487");
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(Receipt(id, 7, "connected")));
            return Task.FromResult(JsonResponse(Receipt(id, 8, "connected")));
        });
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var result = await CopilotConnectionCommands.RunAsync(
        [
            "refresh", "--connection-id", id.ToString("D"),
            .. TargetOptions,
        ],
        new CliRuntimeOptions(
            GatewayClientFactory: new RecordingGatewayFactory(),
            Authentication: new RecordingAuthentication(),
            Output: output),
        http,
        output,
        CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Collection(handler.Requests,
            status => Assert.Equal($"/api/connections/copilot-user/v1/{id:D}", status.PathAndQuery),
            refresh =>
            {
                Assert.Equal("/api/connections/copilot-user/v1/refresh", refresh.PathAndQuery);
                Assert.Equal(7, ReadJson(refresh.Body!)["expectedRevision"]?.GetValue<long>());
            });
        Assert.Null(handler.Requests[1].Cookie);
    }

    [Fact]
    public async Task DisconnectReadsTheCurrentRevisionBeforePostingTheRevoke()
    {
        var id = Guid.Parse("b390b940-4141-4d5a-a1e5-4db4ec10a487");
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(Receipt(id, 9, "connected")));
            return Task.FromResult(JsonResponse(Receipt(id, 10, "revoked")));
        });
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var result = await CopilotConnectionCommands.RunAsync(
        [
            "disconnect", "--connection-id", id.ToString("D"),
            .. TargetOptions,
        ],
        new CliRuntimeOptions(
            GatewayClientFactory: new RecordingGatewayFactory(),
            Authentication: new RecordingAuthentication(),
            Output: output),
        http,
        output,
        CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Collection(handler.Requests,
            status => Assert.Equal($"/api/connections/copilot-user/v1/{id:D}", status.PathAndQuery),
            revoke =>
            {
                Assert.Equal("/api/connections/copilot-user/v1/revoke", revoke.PathAndQuery);
                Assert.Equal(9, ReadJson(revoke.Body!)["expectedRevision"]?.GetValue<long>());
            });
        Assert.Null(handler.Requests[1].Cookie);
    }

    private const string CopilotConnectionCommandsCookieName = "__Host-agentweaver-copilot-link";

    private static object Receipt(Guid id, long revision, string state) => new
    {
        connectionId = id,
        revision,
        scope = "platform",
        scopeId = "default",
        state,
        freshUntil = "2026-11-09T12:00:00Z",
    };

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static JsonObject ReadJson(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Authorization,
        string? Tenant,
        string? Cookie,
        string? Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CapturedRequest, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                    ? tenants.Single()
                    : null,
                request.Headers.TryGetValues("Cookie", out var cookies)
                    ? cookies.Single()
                    : null,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            _requests.Enqueue(captured);
            return await respond(request, captured);
        }
    }

    private sealed class RecordingAuthentication : ICliAuthentication
    {
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBearerAsync(
            CliIdentity identity, InteractionPolicy interaction, CancellationToken ct) =>
            Task.FromResult("cli-bearer");
    }

    private sealed class RecordingGatewayFactory : IGatewayClientFactory
    {
        public CliIdentity? Identity { get; private set; }

        public IGatewayClient Create(CliIdentity identity)
        {
            Identity = identity;
            return new RecordingGatewayClient(identity);
        }
    }

    private sealed class RecordingGatewayClient(CliIdentity identity) : IGatewayClient
    {
        public CliIdentity Identity { get; } = identity;
        public IReadOnlyList<JsonObject> Calls => [];
        public Task<JsonObject> DiscoverAsync(CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct) =>
            Task.FromResult(new JsonObject { ["actorId"] = "actor-1" });
        public Task<GatewayResponse> InvokeAsync(
            string operationId, GatewayInvocation invocation, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GatewayResponse> InvokeRawAsync(
            string method, string url, byte[]? body, IReadOnlyDictionary<string, string>? headers,
            bool outputRequested, Func<string, ValueTask>? onEventChunk, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBrowser : ICliSystemBrowser
    {
        public Uri? AuthorizationUri { get; private set; }
        public Task OpenAsync(Uri authorizationUri, CancellationToken ct)
        {
            AuthorizationUri = authorizationUri;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingListenerFactory(OAuthCallback callback, Uri redirectUri)
        : IOAuthCallbackListenerFactory
    {
        public RecordingListener Listener { get; } = new(callback, redirectUri);
        public IOAuthCallbackListener Start() => Listener;
    }

    private sealed class RecordingListener(OAuthCallback callback, Uri redirectUri) : IOAuthCallbackListener
    {
        public Uri RedirectUri { get; } = redirectUri;
        public string? ExpectedState { get; private set; }
        public string? ExpectedIssuer { get; private set; }

        public Task<OAuthCallback> WaitAsync(
            string expectedState, string expectedIssuer, CancellationToken ct)
        {
            ExpectedState = expectedState;
            ExpectedIssuer = expectedIssuer;
            return Task.FromResult(callback);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
