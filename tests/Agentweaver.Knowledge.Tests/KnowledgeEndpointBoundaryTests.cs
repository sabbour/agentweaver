using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

[Collection("Knowledge PostgreSQL")]
public sealed class KnowledgeEndpointBoundaryTests(KnowledgePostgresFixture postgres)
{
    private static readonly string TestIssuer = "https://identity.test/";

    [Fact]
    public async Task EndpointUsesFreshOwnerAuthorizationAndForwardsOriginalCallerToken()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "boundary",
                    importance = "medium",
                    tags = new[] { "boundary" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-create-1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            ["/api/authorization/context", "/api/projects/project-a/runs/run-a/selection"],
            owner.Paths.ToArray());
        Assert.All(owner.AuthorizationHeaders, header => Assert.Equal("Bearer caller-token", header));
        Assert.All(owner.CacheControlHeaders, header => Assert.Null(header));
    }

    [Fact]
    public async Task EndpointAcceptsUnboundCallerWithCurrentOwnerAuthority()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "unbound owner",
                    importance = "medium",
                    tags = new[] { "unbound" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "unbound-token");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-unbound-owner");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            ["/api/authorization/context", "/api/projects/project-a/runs/run-a/selection"],
            owner.Paths.ToArray());
    }

    [Theory]
    [InlineData("missing-issuer-token")]
    [InlineData("cross-issuer-token")]
    [InlineData("duplicate-bound-token")]
    [InlineData("cross-identity-token")]
    [InlineData("wrong-raw-issuer-token")]
    public async Task EndpointRejectsCallerBoundsWithoutIssuerProvenance(string token)
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "must not be written",
                    importance = "medium",
                    tags = new[] { "invalid-issuer" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-invalid-issuer-" + token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("invalid_authority_response", await response.Content.ReadAsStringAsync());
        Assert.Equal(["/api/authorization/context"], owner.Paths.ToArray());
    }

    [Fact]
    public async Task EndpointDeniesCurrentOwnerWithoutWriteAndEnforcesContextBounds()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        using var write = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "denied",
                    importance = "medium",
                    tags = new[] { "boundary" }
                },
                options: JsonOptions)
        };
        write.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        write.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-denied-1");
        using var denied = await client.SendAsync(write);
        using var contextRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/context?maxItems=21");
        contextRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var bounded = await client.SendAsync(contextRequest);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bounded.StatusCode);
        Assert.Contains("context_budget_exceeded", await bounded.Content.ReadAsStringAsync());
        Assert.Equal(2, owner.Paths.Count(path => path == "/api/authorization/context"));
        Assert.Single(owner.Paths, path => path.EndsWith("/selection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptedEffectReceiptIsNoStoreRedactedAndPendingDeliveryDoesNotUndoPromotion()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        using var create = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "proposal",
                    type = "architectural",
                    title = "Private proposal",
                    content = "private-proposal-content",
                    rationale = "private-proposal-rationale",
                    importance = "medium",
                    tags = new[] { "receipt-test" }
                },
                options: JsonOptions)
        };
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        create.Headers.TryAddWithoutValidation("Idempotency-Key", "receipt-proposal");
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var proposalId = createdDocument.RootElement.GetProperty("record").GetProperty("recordId").GetGuid();

        using var promote = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/proposals/{proposalId:D}/promote")
        {
            Content = JsonContent.Create(new { expectedRevision = 1 }, options: JsonOptions)
        };
        promote.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        promote.Headers.TryAddWithoutValidation("Idempotency-Key", "receipt-promote");
        using var promoted = await client.SendAsync(promote);
        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
        using var promotedDocument = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = promotedDocument.RootElement.GetProperty("outboxEventId").GetGuid();
        Assert.Equal("PENDING", promotedDocument.RootElement.GetProperty("delivery").GetString());
        Assert.Equal("events_audience_required",
            promotedDocument.RootElement.GetProperty("deliveryCode").GetString());
        Assert.Equal("actor-a",
            promotedDocument.RootElement.GetProperty("requiredAudienceSubject").GetString());
        Assert.Equal("events-tests",
            promotedDocument.RootElement.GetProperty("requiredAudience").GetString());

        using var read = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        read.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var response = await client.SendAsync(read);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("private-proposal-content", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-proposal-rationale", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caller-token", body, StringComparison.Ordinal);
        Assert.Contains("\"receiptId\"", body, StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"actor-a\"", body, StringComparison.Ordinal);

        var revokedOwner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: false);
        await using var revokedApp = await CreateAppAsync(database, revokedOwner);
        using var revokedClient = revokedApp.GetTestClient();
        using var revokedRead = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        revokedRead.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var revokedResponse = await revokedClient.SendAsync(revokedRead);
        Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);
        Assert.Equal("no-store", revokedResponse.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(receiptId.ToString("D"), await revokedResponse.Content.ReadAsStringAsync());

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var status = new NpgsqlCommand(
            $"SELECT delivered_at IS NULL FROM \"{database.Options.Schema}\".outbox_events WHERE id = @id",
            connection);
        status.Parameters.AddWithValue("id", receiptId);
        Assert.True((bool)(await status.ExecuteScalarAsync())!);
    }

    private async Task<WebApplication> CreateAppAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase database,
        FakeProjectsOwnerHandler owner,
        HttpMessageHandler? eventsHandler = null)
    {
        var provider = database.Provider;
        var registration = new ProviderRegistration(
            provider.Descriptor,
            Enabled: true,
            database.Options.OptionsRevision,
            database.Options.OptionsSchemaVersion);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)]).Value);
        var runtimeOptions = new KnowledgeRuntimeOptions(
            new Uri("https://identity.test/"),
            "knowledge-tests",
            new Uri("https://projects.test/"),
            new Uri("https://events.test/"),
            "events-tests",
            database.Options,
            MaximumContextCandidates: 100,
            DefaultContextItems: 20,
            DefaultContextTokens: 1000);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(new PostgresOutbox(database.DataSource, database.Options.Schema));
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(new ProviderResolver(catalog));
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<IMemoryProvider>(provider);
        builder.Services.AddSingleton<IReadOnlyDictionary<string, IMemoryProvider>>(
            new Dictionary<string, IMemoryProvider>(StringComparer.Ordinal)
            {
                [NativePostgresMemoryProvider.ProviderId] = provider
            });
        builder.Services.AddSingleton<ProjectsConfigClient>(services => new ProjectsConfigClient(
            new HttpClient(owner) { BaseAddress = runtimeOptions.ProjectsConfigBaseAddress },
            services.GetRequiredService<IHttpContextAccessor>(),
            runtimeOptions));
        builder.Services.AddSingleton(new HttpClient(eventsHandler ?? new UnauthorizedEventsHandler())
        {
            BaseAddress = runtimeOptions.EventsBaseAddress
        });
        builder.Services.AddSingleton<AcceptedEffectRelay>();
        builder.Services.AddScoped<KnowledgeProviderBindingService>();
        builder.Services.AddSingleton<MemoryContextCompiler>();
        builder.Services.AddScoped<KnowledgeApplicationService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKnowledgeEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeProjectsOwnerHandler(
        NativePostgresMemoryOptions options,
        bool writeAllowed,
        bool bindCaller = true) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly ConcurrentQueue<string?> _authorizationHeaders = new();
        private readonly ConcurrentQueue<string?> _cacheControlHeaders = new();

        public IReadOnlyCollection<string> Paths => _paths;
        public IReadOnlyCollection<string?> AuthorizationHeaders => _authorizationHeaders;
        public IReadOnlyCollection<string?> CacheControlHeaders => _cacheControlHeaders;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _paths.Enqueue(path);
            _authorizationHeaders.Enqueue(request.Headers.Authorization?.ToString());
            _cacheControlHeaders.Enqueue(request.Headers.CacheControl?.ToString());
            object payload = path switch
            {
                "/api/authorization/context" => Authority(),
                "/api/projects/project-a/runs/run-a/selection" => Selection(),
                _ => throw new InvalidOperationException($"Unexpected Projects & Config route '{path}'.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            });
        }

        private ProjectAuthorizationContextResponse Authority()
        {
            var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadProjects, 1));
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadRunSelection, 1));
            if (writeAllowed)
                permissions.Add(new ProjectAuthorizationPermissionGrant(
                    ProjectAuthorizationPermission.WriteProjects, 1));
            return new ProjectAuthorizationContextResponse(
                1,
                "https://identity.test/",
                "actor-a",
                "tenant-a",
                1,
                bindCaller ? "project-a" : null,
                bindCaller ? "run-a" : null,
                [new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    permissions.ToImmutable())]);
        }

        private ProjectRunSelectionResponse Selection() =>
            new(
                "project-a",
                "run-a",
                1,
                1,
                1,
                "context-v1",
                [new EffectiveProviderSelection(
                    ProviderCardinality.Exclusive,
                    ProviderSeam.Memory,
                    [new EffectiveProviderCandidate(
                        ProviderSeam.Memory,
                        NativePostgresMemoryProvider.ProviderId,
                        NativePostgresMemoryProvider.AdapterVersion.ToString(),
                        options.OptionsSchemaVersion,
                        options.OptionsRevision,
                        ProviderHostingPattern.RemoteService,
                        MemoryProviderCapabilities.All.ToImmutableArray(),
                        MemoryProviderCapabilities.All.ToImmutableArray())])],
                new ProjectRunLimitSnapshot(1000));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = AuthenticationHeaderValue.TryParse(
                Request.Headers.Authorization.ToString(), out var authorization)
                ? authorization.Parameter
                : null;
            var subjectIssuer = token == "missing-issuer-token" ? string.Empty : TestIssuer;
            var projectIssuer = token == "cross-issuer-token" ? "https://other-identity.test/" : TestIssuer;
            var claims = new List<Claim>
            {
                IssuedClaim("sub", "actor-a", subjectIssuer),
                IssuedClaim("tenant_id", "tenant-a", TestIssuer)
            };
            if (token != "unbound-token")
            {
                claims.Add(IssuedClaim("project_id", "project-a", projectIssuer));
                claims.Add(IssuedClaim("run_id", "run-a", TestIssuer));
            }
            if (token == "duplicate-bound-token")
                claims.Add(IssuedClaim("project_id", "project-a", TestIssuer));
            if (token == "wrong-raw-issuer-token")
                claims.Add(IssuedClaim("iss", "https://other-identity.test/", TestIssuer));

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            if (token == "cross-identity-token")
                principal.AddIdentity(new ClaimsIdentity(
                    [
                        IssuedClaim("project_id", "project-a", TestIssuer),
                        IssuedClaim("run_id", "run-a", TestIssuer)
                    ],
                    Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }

        private static Claim IssuedClaim(string type, string value, string issuer) =>
            new(type, value, ClaimValueTypes.String, issuer);
    }

    private sealed class UnauthorizedEventsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
