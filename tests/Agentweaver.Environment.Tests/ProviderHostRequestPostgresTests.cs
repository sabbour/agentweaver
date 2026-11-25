using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class ProviderHostRequestPostgresTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    private DbContextOptions<EnvironmentDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(database.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema))
            .Options;

    [Fact]
    public async Task ReceiveAsyncIsIdempotentRejectsConflictingPayloadAndEnforcesImmutability()
    {
        await EnvironmentMigrator.VerifyMigrationsAppliedAsync(database.DataSource, CreateDbOptions());

        await using var db = new EnvironmentDbContext(CreateDbOptions());
        var store = new ProviderHostRequestStore(db, TimeProvider.System, new FakeProofClient());

        var requestId = Guid.NewGuid();
        var opId = Guid.NewGuid();
        var hash = new string('a', 64);
        var host = $"idemp-{Guid.NewGuid():N}.example.com";
        var dispatch = new ProviderHostRequestDispatch(
            requestId, "tenant-1", ModelProviderBindingScope.Platform, null,
            "byok-" + opId.ToString("N"), 1, hash, opId, "actor-1", host, 443);

        var first = await store.ReceiveAsync(dispatch, CancellationToken.None);
        Assert.True(first.Created);
        Assert.Equal(PlatformEgressDestinationState.Requested, first.Receipt.DestinationState);
        Assert.Equal(host, first.Receipt.Host);
        Assert.Equal(443, first.Receipt.Port);

        var replay = await store.ReceiveAsync(dispatch, CancellationToken.None);
        Assert.False(replay.Created);
        Assert.Equal(first.Receipt, replay.Receipt);

        var conflicting = dispatch with { Host = $"other-{Guid.NewGuid():N}.example.com" };
        var conflict = await Assert.ThrowsAsync<ProviderHostRequestConflictException>(
            () => store.ReceiveAsync(conflicting, CancellationToken.None));
        Assert.Equal("host_request_conflict", conflict.Code);

        var invalidHost = dispatch with { RequestId = Guid.NewGuid(), Host = "127.0.0.1" };
        await Assert.ThrowsAsync<ProviderHostRequestValidationException>(
            () => store.ReceiveAsync(invalidHost, CancellationToken.None));

        var updateErr = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE environment.provider_host_request_receipts SET host = 'tampered.example' WHERE request_id = {requestId}"));
        Assert.Equal("55000", updateErr.SqlState);

        var deleteErr = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM environment.provider_host_request_receipts WHERE request_id = {requestId}"));
        Assert.Equal("55000", deleteErr.SqlState);
    }

    [Fact]
    public async Task RequestedDestinationsEndpointPagesSkipsStaleAdvancesEmptyPageCursorAndFailsClosedOnUnavailableProof()
    {
        await using var db = new EnvironmentDbContext(CreateDbOptions());
        var proofClient = new FakeProofClient();
        var store = new ProviderHostRequestStore(db, TimeProvider.System, proofClient);

        // Seed 3 distinct Requested hosts; mark the first two stale so page 1 returns empty [] with a nextCursor.
        var seeded = new List<ProviderHostRequestReceipt>();
        for (var i = 0; i < 3; i++)
        {
            var opId = Guid.NewGuid();
            var dispatch = new ProviderHostRequestDispatch(
                Guid.NewGuid(), "tenant-queue", ModelProviderBindingScope.Platform, null,
                "byok-" + opId.ToString("N"), 1, new string('b', 64), opId, "actor-queue",
                $"queue-{i}-{Guid.NewGuid():N}.example.com", 443);
            var res = await store.ReceiveAsync(dispatch, CancellationToken.None);
            seeded.Add(res.Receipt);
            await Task.Delay(5);
        }

        proofClient.SetCurrent(seeded[0].RequestId, false);
        proofClient.SetCurrent(seeded[1].RequestId, false);
        proofClient.SetCurrent(seeded[2].RequestId, true);

        var projectsClient = new FakeProjectsConfigClient();
        using var host = await StartTestServerAsync(db, proofClient, projectsClient);
        var http = host.GetTestClient();

        // 1. Project-bound or non-PlatformAdmin caller is denied (403).
        projectsClient.AllowPlatformAdmin = false;
        using (var forbiddenReq = new HttpRequestMessage(HttpMethod.Get, "/api/platform/egress/destinations"))
        {
            forbiddenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
            using var forbiddenResp = await http.SendAsync(forbiddenReq);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
            Assert.True(forbiddenResp.Headers.CacheControl?.NoStore);
        }

        projectsClient.AllowPlatformAdmin = true;

        // 2. Unavailable proof returns 503 with no cursor advance.
        proofClient.Unavailable = true;
        using (var unavailableReq = new HttpRequestMessage(HttpMethod.Get, "/api/platform/egress/destinations?pageSize=2"))
        {
            unavailableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
            using var unavailableResp = await http.SendAsync(unavailableReq);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailableResp.StatusCode);
            Assert.True(unavailableResp.Headers.CacheControl?.NoStore);
        }

        // 3. Page 1 examines 2 stale rows: returns empty destinations yet advances nextCursor past the last examined row.
        proofClient.Unavailable = false;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        string? nextCursor = null;
        var seenPage2 = false;
        for (var pageIndex = 0; pageIndex < 20; pageIndex++)
        {
            var url = nextCursor is null
                ? "/api/platform/egress/destinations?pageSize=2&state=requested"
                : $"/api/platform/egress/destinations?pageSize=2&state=requested&cursor={Uri.EscapeDataString(nextCursor)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            var rawJson = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(rawJson);
            foreach (var item in doc.RootElement.GetProperty("destinations").EnumerateArray())
            {
                Assert.Equal("requested", item.GetProperty("state").GetString());
                Assert.Equal("modelProvider", item.GetProperty("origin").GetString());
                Assert.Contains(item.GetProperty("scope").GetString(), new[] { "platform", "project" });
            }
            var page = JsonSerializer.Deserialize<PlatformEgressDestinationPage>(rawJson, jsonOptions);
            Assert.NotNull(page);
            Assert.DoesNotContain(page!.Destinations, d => d.RequestId == seeded[0].RequestId || d.RequestId == seeded[1].RequestId);
            if (page.Destinations.Any(d => d.RequestId == seeded[2].RequestId))
                seenPage2 = true;
            nextCursor = page.NextCursor;
            if (nextCursor is null)
                break;
        }
        Assert.True(seenPage2);
    }

    private static async Task<IHost> StartTestServerAsync(
        EnvironmentDbContext db,
        IProjectsHostRequestProofClient proofClient,
        IProjectsConfigClient projectsClient)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(proofClient);
        builder.Services.AddSingleton(projectsClient);
        builder.Services.AddScoped<ProviderHostRequestStore>();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPlatformEgressDestinationEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "actor-queue")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeProofClient : IProjectsHostRequestProofClient
    {
        private readonly Dictionary<Guid, bool> map = new();
        public bool Unavailable { get; set; }

        public void SetCurrent(Guid requestId, bool isCurrent) => map[requestId] = isCurrent;

        public Task<IReadOnlyDictionary<Guid, bool>?> VerifyCurrentAsync(
            IReadOnlyList<ProviderHostRequestProofItem> items,
            CancellationToken cancellationToken)
        {
            if (Unavailable)
                return Task.FromResult<IReadOnlyDictionary<Guid, bool>?>(null);
            IReadOnlyDictionary<Guid, bool> result = items.ToDictionary(
                item => item.RequestId,
                item => map.TryGetValue(item.RequestId, out var current) && current);
            return Task.FromResult<IReadOnlyDictionary<Guid, bool>?>(result);
        }
    }

    private sealed class FakeProjectsConfigClient : IProjectsConfigClient
    {
        public bool AllowPlatformAdmin { get; set; } = true;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            var authority = AllowPlatformAdmin
                ? ImmutableArray.Create(new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Platform,
                    "default",
                    [new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults, 1)]))
                : ImmutableArray<EffectiveProjectAuthorization>.Empty;

            return Task.FromResult(new ProjectAuthorizationContextResponse(
                ProjectAuthorizationContextContract.CurrentVersion,
                "https://issuer.example/",
                "actor-queue",
                "tenant-queue",
                1,
                null,
                null,
                authority));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller, string projectId, string runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
