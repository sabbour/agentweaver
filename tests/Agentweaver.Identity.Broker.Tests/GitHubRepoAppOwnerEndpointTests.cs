using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class GitHubRepoAppOwnerEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private FakeIdentityProvider _fakeIdp = null!;
    private HttpClient _fakeIdpClient = null!;
    private IdentityBrokerWebApplicationFactory _factory = null!;
    private HttpClient _broker = null!;
    private (string PfxPath, string Password) _signingCertificate;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority)
        };
        _signingCertificate = TestSigningCertificate.Create();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                settings["IdentityBroker__GitHubRepoApp__OAuthClientId"] = "repo-app-client";
                settings["IdentityBroker__GitHubRepoApp__OAuthClientSecret"] = "repo-app-client-secret";
                settings["IdentityBroker__GitHubRepoApp__CallbackUri"] =
                    "https://broker.test.local/auth/github/repo-app/callback";
                settings["IdentityBroker__GitHubRepoApp__AppId"] = "123";
                settings["IdentityBroker__GitHubRepoApp__AppSlug"] = "agentweaver";
                settings["IdentityBroker__GitHubRepoApp__PrivateKeySecretId"] = "github-app-key";
                settings["IdentityBroker__GitHubRepoApp__PrivateKeySecretVersion"] = "key-version-1";
            },
            configureServices: services =>
            {
                services.RemoveAll<ISecretVersionWriter>();
                services.AddSingleton<ISecretVersionWriter, UnusedSecretVersionWriter>();
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption, UnusedSecretRedemption>();
                foreach (var clientName in new[]
                         {
                             "github-repo-app-oauth",
                             "github-repo-app-api",
                             "github-repo-app-installation-api"
                         })
                    services.AddHttpClient(clientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new NoExternalHttpHandler());
            });
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local")
        });
    }

    public async Task DisposeAsync()
    {
        _broker.Dispose();
        _fakeIdpClient.Dispose();
        await _factory.DisposeAsync();
        await _fakeIdp.DisposeAsync();
        if (File.Exists(_signingCertificate.PfxPath))
            File.Delete(_signingCertificate.PfxPath);
        if (Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
    }

    [Fact]
    public async Task StatusAndDisconnectUseOwnerCookieAntiforgeryAndExpectedRevision()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local")
        });
        var anonymousStatus = await anonymous.GetAsync("/auth/github/repo-app/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);

        var (_, challenge) = Pkce.Create();
        _ = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            _broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            string.Join(' ', IdentityBrokerWebApplicationFactory.TestClientScopes),
            challenge);

        var initialStatus = await _broker.GetAsync("/auth/github/repo-app/status");
        Assert.Equal(HttpStatusCode.OK, initialStatus.StatusCode);
        var noConnection = await initialStatus.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_connected", noConnection.GetProperty("state").GetString());
        Assert.Equal("not_connected", noConnection.GetProperty("localReadiness").GetString());

        const string connectionId = "connection-owner-endpoint-test";
        const long connectionRevision = 4;
        Guid ownerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            var owner = await db.Users.SingleAsync();
            ownerId = owner.Id;
            db.RepoAppConnections.Add(new RepoAppConnectionRecord
            {
                ConnectionId = connectionId,
                OwnerId = ownerId,
                GitHubLogin = "connected-owner",
                AccessTokenSecretId = "private-access-secret-id",
                AccessTokenSecretVersion = "private-access-secret-version",
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                RefreshTokenSecretId = "private-refresh-secret-id",
                RefreshTokenSecretVersion = "private-refresh-secret-version",
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                ConnectionRevision = connectionRevision,
                CredentialRevision = 2,
                State = RepoAppConnectionState.Connected,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.RepoAppInstallations.Add(new RepoAppInstallationRecord
            {
                ConnectionId = connectionId,
                InstallationId = 456,
                AccountLogin = "octo",
                AccountType = "Organization",
                RepositorySelection = "selected",
                ConnectionRevision = connectionRevision,
                AddedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var dataSource = NpgsqlDataSource.Create(_factory.ConnectionStringForRestartTest);
        await using (var db = new IdentityBrokerDbContext(
            new DbContextOptionsBuilder<IdentityBrokerDbContext>()
                .UseNpgsql(dataSource)
                .Options))
        {
            var connectedStatus = await _broker.GetAsync("/auth/github/repo-app/status");
            var connectedBody = await connectedStatus.Content.ReadAsStringAsync();
            using var connectedJson = JsonDocument.Parse(connectedBody);
            var connected = connectedJson.RootElement;
            Assert.Equal("connected", connected.GetProperty("state").GetString());
            Assert.Equal("access_token_available", connected.GetProperty("localReadiness").GetString());
            Assert.Equal(connectionRevision, connected.GetProperty("connectionRevision").GetInt64());
            Assert.DoesNotContain("private-access-secret-id", connectedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("private-refresh-secret-id", connectedBody, StringComparison.Ordinal);

            var csrfResponse = await _broker.GetAsync("/auth/github/repo-app/csrf");
            var csrf = await csrfResponse.Content.ReadFromJsonAsync<JsonElement>();
            var input = new
            {
                connectionId,
                expectedConnectionRevision = connectionRevision
            };
            var withoutCsrf = await _broker.PostAsJsonAsync("/auth/github/repo-app/disconnect", input);
            Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
            Assert.Equal(
                RepoAppConnectionState.Connected,
                (await db.RepoAppConnections.AsNoTracking().SingleAsync()).State);

            using var disconnect = new HttpRequestMessage(
                HttpMethod.Post, "/auth/github/repo-app/disconnect")
            {
                Content = JsonContent.Create(input)
            };
            disconnect.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("csrf_token").GetString());
            var disconnectedResponse = await _broker.SendAsync(disconnect);
            Assert.Equal(HttpStatusCode.OK, disconnectedResponse.StatusCode);
            var disconnected = await disconnectedResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("revoked", disconnected.GetProperty("state").GetString());
            Assert.Equal("reauthorization_required", disconnected.GetProperty("localReadiness").GetString());
            Assert.Equal(connectionRevision, disconnected.GetProperty("connectionRevision").GetInt64());

            var persisted = await db.RepoAppConnections.AsNoTracking().SingleAsync();
            Assert.Equal(RepoAppConnectionState.Revoked, persisted.State);
            Assert.Equal(3, persisted.CredentialRevision);
            Assert.Null(persisted.RefreshLeaseId);
            Assert.NotNull((await db.RepoAppInstallations.AsNoTracking().SingleAsync()).RevokedAt);

            using var retry = new HttpRequestMessage(
                HttpMethod.Post, "/auth/github/repo-app/disconnect")
            {
                Content = JsonContent.Create(input)
            };
            retry.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("csrf_token").GetString());
            Assert.Equal(HttpStatusCode.OK, (await _broker.SendAsync(retry)).StatusCode);

            using var stale = new HttpRequestMessage(
                HttpMethod.Post, "/auth/github/repo-app/disconnect")
            {
                Content = JsonContent.Create(new
                {
                    connectionId,
                    expectedConnectionRevision = connectionRevision + 1
                })
            };
            stale.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("csrf_token").GetString());
            var staleResponse = await _broker.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
            var staleError = await staleResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("connection_revision_conflict", staleError.GetProperty("error").GetString());
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            Assert.Equal(ownerId, (await db.RepoAppConnections.SingleAsync()).OwnerId);
        }
    }

    private sealed class UnusedSecretVersionWriter : ISecretVersionWriter
    {
        public Task<SecretRef> WriteVersionAsync(
            string secretId,
            SecretCredential credential,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Status and disconnect must not write secrets.");
    }

    private sealed class UnusedSecretRedemption : ISecretRedemption
    {
        public Task<SecretCredential> RedeemAsync(
            SecretRedemptionRequest request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Status and disconnect must not redeem secrets.");
    }

    private sealed class NoExternalHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Status and disconnect must not call GitHub.");
    }
}
