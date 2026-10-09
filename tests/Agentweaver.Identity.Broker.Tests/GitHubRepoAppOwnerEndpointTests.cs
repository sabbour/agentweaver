using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
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
    private bool _allowRepoAppProviderEffects;
    private int _secretWrites;

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
                services.AddSingleton<ISecretVersionWriter>(_ =>
                    new TestSecretVersionWriter(
                        () => _allowRepoAppProviderEffects,
                        () => _secretWrites++));
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption>(_ =>
                    new TestSecretRedemption(() => _allowRepoAppProviderEffects));
                services.AddHttpClient("github-repo-app-oauth")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new RepoAppOAuthHandler(() => _allowRepoAppProviderEffects));
                services.AddHttpClient("github-repo-app-api")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new RepoAppApiHandler(() => _allowRepoAppProviderEffects));
                services.AddHttpClient("github-repo-app-installation-api")
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
            Assert.Equal("connected-owner", connected.GetProperty("githubLogin").GetString());
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
        Assert.Equal(0, _secretWrites);
    }

    [Fact]
    public async Task ConnectFormUsesBrokerAntiforgeryCookieAndReturnsUnfollowedOAuthRedirect()
    {
        var (_, challenge) = Pkce.Create();
        _ = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            _broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            string.Join(' ', IdentityBrokerWebApplicationFactory.TestClientScopes),
            challenge);

        using var csrfResponse = await _broker.GetAsync("/auth/github/repo-app/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        Assert.True(csrfResponse.Headers.CacheControl?.NoStore);
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<JsonElement>();
        var csrfToken = csrf.GetProperty("csrf_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        using (var invalid = await _broker.PostAsync(
                   "/auth/github/repo-app/connect",
                   new FormUrlEncodedContent(new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = "invalid-token"
                   })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var connect = await _broker.PostAsync(
            "/auth/github/repo-app/connect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrfToken!
            }));
        Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        Assert.True(connect.Headers.CacheControl?.NoStore);
        var redirect = connect.Headers.Location
            ?? throw new InvalidOperationException("The Broker omitted the GitHub authorization redirect.");
        Assert.Equal("github.com", redirect.Host);
        Assert.Equal("/login/oauth/authorize", redirect.AbsolutePath);
        var query = QueryHelpers.ParseQuery(redirect.Query);
        Assert.Equal("repo-app-client", query["client_id"].ToString());
        Assert.Equal(
            "https://broker.test.local/auth/github/repo-app/callback",
            query["redirect_uri"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["state"].ToString()));
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"].ToString()));
        Assert.Contains(
            connect.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(
                GitHubRepoAppConnectionService.CallbackCookie + "=",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepoAppCallbacksRefreshTheCurrentOwnerInstallationList()
    {
        var (_, challenge) = Pkce.Create();
        _ = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            _broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid profile email",
            challenge);

        var csrf = await _broker.GetFromJsonAsync<JsonElement>("/auth/github/repo-app/csrf");
        using var connect = new HttpRequestMessage(HttpMethod.Post, "/auth/github/repo-app/connect")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>(
                    "__RequestVerificationToken",
                    csrf.GetProperty("csrf_token").GetString()!)
            ])
        };
        var start = await _broker.SendAsync(connect);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].Single()
            ?? throw new Xunit.Sdk.XunitException("The GitHub authorization redirect had no state.");
        _allowRepoAppProviderEffects = true;

        using var callback = await _broker.GetAsync(
            $"/auth/github/repo-app/callback?code=authorization-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(
            new Uri("https://web.test/settings/source-control?repoApp=connected"),
            callback.Headers.Location);
        using var scope = _factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>()
            .RepoAppConnections.AsNoTracking().SingleAsync();
        Assert.Equal("connected-user", connection.GitHubLogin);
        Assert.Equal(2, _secretWrites);

        var installationCsrf = await _broker.GetFromJsonAsync<JsonElement>("/auth/github/repo-app/csrf");
        using var installationStart = await _broker.PostAsync(
            "/auth/github/repo-app/install",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = installationCsrf.GetProperty("csrf_token").GetString()!
            }));
        Assert.Equal(HttpStatusCode.Redirect, installationStart.StatusCode);
        Assert.True(installationStart.Headers.CacheControl?.NoStore);
        var installationRedirect = installationStart.Headers.Location
            ?? throw new InvalidOperationException("The Broker omitted the GitHub installation redirect.");
        Assert.Equal("github.com", installationRedirect.Host);
        Assert.Equal("/apps/agentweaver/installations/new", installationRedirect.AbsolutePath);
        var installationState = QueryHelpers.ParseQuery(installationRedirect.Query)["state"].Single()
            ?? throw new Xunit.Sdk.XunitException("The GitHub installation redirect had no state.");

        using var installationCallback = await _broker.GetAsync(
            "/auth/github/repo-app/callback?installation_id=456&setup_action=install" +
            $"&state={Uri.EscapeDataString(installationState)}");
        Assert.True(
            installationCallback.StatusCode == HttpStatusCode.Redirect,
            $"Expected installation callback redirect, got {installationCallback.StatusCode}: " +
            await installationCallback.Content.ReadAsStringAsync());
        Assert.Equal(
            new Uri("https://web.test/settings/source-control?repoApp=connected"),
            installationCallback.Headers.Location);

        using var statusResponse = await _broker.GetAsync("/auth/github/repo-app/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.True(statusResponse.Headers.CacheControl?.NoStore);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connected", status.GetProperty("state").GetString());
        Assert.Equal(connection.ConnectionId, status.GetProperty("connectionId").GetString());

        using var repositoriesResponse = await _broker.GetAsync("/auth/github/repo-app/repositories");
        Assert.Equal(HttpStatusCode.OK, repositoriesResponse.StatusCode);
        Assert.True(repositoriesResponse.Headers.CacheControl?.NoStore);
        var repositories = await repositoriesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(connection.ConnectionId, repositories.GetProperty("connectionId").GetString());
        Assert.Equal(
            "connected-user",
            repositories.GetProperty("githubLogin").GetString());
        var repository = Assert.Single(repositories.GetProperty("repositories").EnumerateArray());
        Assert.Equal(456, repository.GetProperty("installationId").GetInt64());
        Assert.Equal(789, repository.GetProperty("repositoryId").GetInt64());
        Assert.Equal("octo/widget", repository.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task BrowserCorsIsExactCredentialedAndLimitedToBrokerFetchRoutes()
    {
        var routes = new[]
        {
            ("/connect/authorize", "GET"),
            ("/connect/authorize/resume", "GET"),
            ("/connect/consent", "POST"),
            ("/connect/token", "POST"),
            ("/auth/github/repo-app/csrf", "GET"),
            ("/auth/github/repo-app/status", "GET"),
            ("/auth/github/repo-app/repositories", "GET"),
            ("/auth/github/repo-app/disconnect", "POST"),
            ("/auth/github/repo-app/selection", "POST"),
        };

        foreach (var (path, method) in routes)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
            preflight.Headers.Add("Origin", "https://web.test");
            preflight.Headers.Add("Access-Control-Request-Method", method);
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
            using var response = await _broker.SendAsync(preflight);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("https://web.test",
                Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true",
                Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.Contains(method,
                string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")),
                StringComparison.OrdinalIgnoreCase);
            var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x-csrf-token", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        }

        using var wrongOrigin = new HttpRequestMessage(HttpMethod.Options, "/auth/github/repo-app/status");
        wrongOrigin.Headers.Add("Origin", "https://attacker.test");
        wrongOrigin.Headers.Add("Access-Control-Request-Method", "GET");
        using var denied = await _broker.SendAsync(wrongOrigin);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/github/repo-app/status");
        statusRequest.Headers.Add("Origin", "https://web.test");
        using var status = await _broker.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal("https://web.test",
            Assert.Single(status.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true",
            Assert.Single(status.Headers.GetValues("Access-Control-Allow-Credentials")));

        using var internalRequest = new HttpRequestMessage(
            HttpMethod.Post, "/internal/source-control/github-app/installations/token")
        {
            Content = JsonContent.Create(new { })
        };
        internalRequest.Headers.Add("Origin", "https://web.test");
        using var internalResponse = await _broker.SendAsync(internalRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, internalResponse.StatusCode);
        Assert.False(internalResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        healthRequest.Headers.Add("Origin", "https://web.test");
        using var health = await _broker.SendAsync(healthRequest);
        Assert.False(health.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private sealed class TestSecretVersionWriter(
        Func<bool> allowWrite,
        Action recordWrite) : ISecretVersionWriter
    {
        public Task<SecretRef> WriteVersionAsync(
            string secretId,
            SecretCredential credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowWrite())
                throw new Xunit.Sdk.XunitException("Status and disconnect must not write secrets.");
            recordWrite();
            return Task.FromResult(new SecretRef(secretId, "version-1"));
        }
    }

    private sealed class TestSecretRedemption(Func<bool> allowRedemption) : ISecretRedemption
    {
        public Task<SecretCredential> RedeemAsync(
            SecretRedemptionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowRedemption() || request.Purpose != "github-repo-app-user-access")
                throw new Xunit.Sdk.XunitException(
                    "Only enabled Repo App user-access redemption is allowed.");
            return Task.FromResult(new SecretCredential(
                "user-access-secret",
                DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class NoExternalHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Status and disconnect must not call GitHub.");
    }

    private sealed class RepoAppOAuthHandler(Func<bool> allowProviderEffect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!allowProviderEffect())
                throw new Xunit.Sdk.XunitException("Status and disconnect must not call GitHub.");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/login/oauth/access_token", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"user-access-secret\",\"expires_in\":28800," +
                    "\"refresh_token\":\"user-refresh-secret\",\"refresh_token_expires_in\":15897600}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class RepoAppApiHandler(Func<bool> allowProviderEffect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!allowProviderEffect())
                throw new Xunit.Sdk.XunitException("Status and disconnect must not call GitHub.");
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("user-access-secret", request.Headers.Authorization?.Parameter);
            var body = request.RequestUri!.PathAndQuery switch
            {
                "/user" => "{\"login\":\"connected-user\"}",
                "/user/installations?per_page=100&page=1" =>
                    "{\"installations\":[{\"id\":456,\"account\":{\"login\":\"octo\"," +
                    "\"type\":\"Organization\"},\"repository_selection\":\"selected\"}]}",
                "/user/installations/456/repositories?per_page=100&page=1" =>
                    "{\"repositories\":[{\"id\":789,\"full_name\":\"octo/widget\"," +
                    "\"owner\":{\"login\":\"octo\"},\"private\":true,\"default_branch\":\"main\"}]}",
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected Repo App API path: {request.RequestUri.PathAndQuery}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
