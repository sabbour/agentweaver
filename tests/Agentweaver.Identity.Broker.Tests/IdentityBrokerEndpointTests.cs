using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityBrokerEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _postgres;
    private FakeIdentityProvider _fakeIdp = null!;
    private IdentityBrokerWebApplicationFactory _factory = null!;
    private HttpClient _broker = null!;
    private HttpClient _fakeIdpClient = null!;
    private (string PfxPath, string Password) _signingCertificate;

    public IdentityBrokerEndpointTests(PostgresContainerFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _signingCertificate = TestSigningCertificate.Create();
        _factory = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp,
            signingCertificate: _signingCertificate);
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            // OpenIddict's authorization/token endpoints reject non-HTTPS requests by
            // design; TestServer treats this as a plain-HTTP in-memory transport unless the
            // client's base address is explicitly HTTPS, which flips HttpContext.Request.IsHttps
            // to true without requiring any real TLS handshake.
            BaseAddress = new Uri("https://broker.test.local"),
        });
        _fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler()) { BaseAddress = new Uri(FakeIdentityProvider.Authority) };
    }

    public async Task DisposeAsync()
    {
        _broker.Dispose();
        _fakeIdpClient.Dispose();
        await _factory.DisposeAsync();
        await _fakeIdp.DisposeAsync();
        File.Delete(_signingCertificate.PfxPath);
        if (Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
    }

    private async Task<(string Code, string CodeVerifier)> RunHappyPathAuthorizeAsync(string scope = "openid profile email api.read offline_access")
    {
        var (verifier, challenge) = Pkce.Create();
        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            _broker, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, scope, challenge);
        return (code, verifier);
    }

    [Fact]
    public async Task HappyPath_IssuesAccessTokenValidatedByRealMiddleware()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();

        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);

        var accessToken = tokens.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.True(tokens.TryGetProperty("refresh_token", out _));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/diagnostics/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var whoami = await _broker.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var body = await whoami.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("subject").GetString()));
    }

    [Fact]
    public async Task ExternalLoginSupportsPublicPkceAndConfiguredConfidentialClient()
    {
        var publicConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        await using var publicFactory = new IdentityBrokerWebApplicationFactory(
            publicConnectionString, _fakeIdp, signingCertificate: _signingCertificate,
            externalProviderClientSecret: null);
        using var publicBroker = publicFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });

        var (_, publicChallenge) = Pkce.Create();
        var publicCallback = await BrokerFlowDriver.AttemptExternalCallbackAsync(
            publicBroker, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid profile email",
            publicChallenge);
        Assert.Equal(HttpStatusCode.Redirect, publicCallback.StatusCode);
        var publicTokenRequest = Assert.Single(_fakeIdp.TokenRequests);
        Assert.False(publicTokenRequest.HasClientSecret);
        Assert.True(publicTokenRequest.HasCodeVerifier);

        var (_, confidentialChallenge) = Pkce.Create();
        var confidentialCallback = await BrokerFlowDriver.AttemptExternalCallbackAsync(
            _broker, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid profile email",
            confidentialChallenge);
        Assert.Equal(HttpStatusCode.Redirect, confidentialCallback.StatusCode);
        var confidentialTokenRequest = _fakeIdp.TokenRequests[1];
        Assert.True(confidentialTokenRequest.HasClientSecret);
        Assert.Equal("fake-idp-client-secret", confidentialTokenRequest.ClientSecret);
        Assert.True(confidentialTokenRequest.HasCodeVerifier);
    }

    [Fact]
    public async Task Authorize_UnregisteredClient_IsRejected()
    {
        var (_, challenge) = Pkce.Create();
        var response = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, "no-such-client", IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid", challenge, "S256");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_RedirectUriMismatch_IsRejected()
    {
        var (_, challenge) = Pkce.Create();
        var response = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId, "https://attacker.test/callback",
            "openid", challenge, "S256");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_MissingPkce_IsRejected()
    {
        var response = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid", codeChallenge: null, codeChallengeMethod: null);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            Assert.Contains("error=", response.Headers.Location!.Query);
        }
    }

    [Fact]
    public async Task Authorize_PlainCodeChallengeMethod_IsRejected()
    {
        var (verifier, _) = Pkce.Create();
        var response = await BrokerFlowDriver.StartAuthorizeAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid", verifier, "plain");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            Assert.Contains("error=", response.Headers.Location!.Query);
        }
    }

    [Fact]
    public async Task Refresh_Success_IssuesNewAccessToken()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var refreshed = await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, refreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedBody = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(refreshedBody.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Refresh_Replay_RevokesWholeFamily()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        var firstRefreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var firstUse = await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, firstRefreshToken);
        Assert.Equal(HttpStatusCode.OK, firstUse.StatusCode);
        var firstUseBody = await firstUse.Content.ReadFromJsonAsync<JsonElement>();
        var secondRefreshToken = firstUseBody.GetProperty("refresh_token").GetString()!;

        // Replaying the already-redeemed first refresh token must fail AND burn the
        // whole family, so the still-unused second refresh token must also now fail.
        var replay = await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, firstRefreshToken);
        Assert.NotEqual(HttpStatusCode.OK, replay.StatusCode);

        var secondUseAfterReplay = await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, secondRefreshToken);
        Assert.NotEqual(HttpStatusCode.OK, secondUseAfterReplay.StatusCode);
    }

    [Fact]
    public async Task TokenExchange_DisabledUser_IsRejected()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            var user = await db.Users.SingleAsync();
            user.Disabled = true;
            await db.SaveChangesAsync();
        }

        var response = await _broker.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = IdentityBrokerWebApplicationFactory.TestClientId,
            ["redirect_uri"] = IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier,
        }));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(IdTokenTampering.WrongIssuer)]
    [InlineData(IdTokenTampering.WrongSignature)]
    [InlineData(IdTokenTampering.Expired)]
    [InlineData(IdTokenTampering.WrongAudience)]
    [InlineData(IdTokenTampering.WrongNonce)]
    [InlineData(IdTokenTampering.MissingNonce)]
    public async Task Authorize_TamperedUpstreamIdToken_IsRejectedByRealMiddleware(IdTokenTampering tampering)
    {
        _fakeIdp.Tampering = tampering;
        var (_, challenge) = Pkce.Create();

        var callbackResponse = await BrokerFlowDriver.AttemptExternalCallbackAsync(
            _broker, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid", challenge);

        // The broker's real OpenIdConnect middleware must reject the forged/wrong-issuer/
        // wrong-signature/expired upstream id_token before ever completing a local sign-in —
        // it must not be treated as a successful callback redirect into /connect/authorize/resume.
        Assert.NotEqual(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, callbackResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, callbackResponse.StatusCode);
        Assert.DoesNotContain(callbackResponse.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies : [], cookie => cookie.StartsWith(".AspNetCore.Identity.Local="));
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task Logs_NeverContainRawTokenOrSecretValues()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var idToken = tokens.GetProperty("id_token").GetString()!;

        await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, refreshToken);
        await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, refreshToken);
        await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId, "invalid-sensitive-refresh");
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = _broker.BaseAddress!,
        });
        _fakeIdp.Subject = "redaction-denial-subject";
        var (_, challenge) = Pkce.Create();
        var consent = await BrokerFlowDriver.BeginConsentAsync(anonymous, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read offline_access", challenge);
        await BrokerFlowDriver.SubmitConsentAsync(anonymous, consent, approve: false);
        using var invalidLogin = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = _broker.BaseAddress!,
        });
        _fakeIdp.Tampering = IdTokenTampering.WrongNonce;
        var rejected = await BrokerFlowDriver.AttemptExternalCallbackAsync(invalidLogin, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid", challenge);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        var sensitiveValues = new[] { code, accessToken, refreshToken, idToken, verifier,
            "fake-idp-client-secret", "invalid-sensitive-refresh" }.Concat(_fakeIdp.SensitiveValues);
        foreach (var message in _factory.LogMessages)
        {
            foreach (var sensitive in sensitiveValues)
            {
                Assert.DoesNotContain(sensitive, message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task RepeatedBrokerHostsDisposeTheirOwnedConnectionPools()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var before = await PostgresContainerFixture.CountConnectionsAsync(connectionString);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var factory = new IdentityBrokerWebApplicationFactory(
                connectionString, _fakeIdp, signingCertificate: _signingCertificate);
            long during;
            await using (factory)
            {
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false,
                    BaseAddress = new Uri("https://broker.test.local"),
                });
                using var response = await client.GetAsync("/health/ready");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                during = await PostgresContainerFixture.CountConnectionsAsync(connectionString);
                Assert.True(during > before);
            }
            var after = await PostgresContainerFixture.CountConnectionsAsync(connectionString);
            Console.WriteLine($"Broker pool iteration {iteration + 1}: before={before}, during={during}, after={after}.");
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task Persistence_UserAndAuthorizationSurviveHostRestart()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(_broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code, verifier);
        string authorizationId;
        using (var originalScope = _factory.Services.CreateScope())
        {
            var authorizations = originalScope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = Assert.Single(await authorizations.FindAsync(
                subject: null, client: null, status: Statuses.Valid, type: AuthorizationTypes.Permanent,
                scopes: null, cancellationToken: default).ToListAsync());
            authorizationId = (await authorizations.GetIdAsync(authorization))!;
            var storedTokens = await originalScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
                .FindByAuthorizationIdAsync(authorizationId).ToListAsync();
            Assert.True(storedTokens.Count >= 3);
        }

        var connectionString = _factory.ConnectionStringForRestartTest;
        await _factory.DisposeAsync();

        _factory = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp,
            signingCertificate: _signingCertificate);
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var userCount = await db.Users.CountAsync();
        Assert.Equal(1, userCount);
        var grants = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        Assert.NotNull(await grants.FindByIdAsync(authorizationId));
        var refreshed = await BrokerFlowDriver.RefreshAsync(_broker, IdentityBrokerWebApplicationFactory.TestClientId,
            tokens.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var (_, challenge) = Pkce.Create();
        var callback = await BrokerFlowDriver.AttemptExternalCallbackAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid profile email api.read offline_access", challenge);
        var resume = await _broker.GetAsync(callback.Headers.Location);
        var final = await _broker.GetAsync(resume.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, final.StatusCode);
        Assert.True(QueryHelpers.ParseQuery(final.Headers.Location!.Query).ContainsKey("code"));
        Assert.Single(await grants.FindAsync(subject: null, client: null, status: Statuses.Valid,
            type: AuthorizationTypes.Permanent, scopes: null, cancellationToken: default).ToListAsync());
    }

    private SigningCredentials Signing => _factory.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>()
        .Value.SigningCredentials.Single();

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("certificate")]
    public async Task Startup_InvalidOrMissingProductionConfigurationFailsClosed(string mode)
    {
        await _factory.DisposeAsync();
        using var invalid = new IdentityBrokerWebApplicationFactory(await _postgres.CreateMigratedDatabaseAsync(), _fakeIdp,
            signingCertificate: _signingCertificate, configure: settings =>
            {
                if (mode == "issuer") settings["IdentityBroker__Issuer"] = "http://untrusted.test";
                if (mode == "client") settings["IdentityBroker__ExternalProvider__ClientId"] = string.Empty;
                if (mode == "certificate") settings["IdentityBroker__Signing__PfxPath"] =
                    Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "identity-tests", "absent-generated-test.pfx");
            });
        Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
        Assert.Empty(typeof(Program).Assembly.GetCustomAttributes(
            typeof(Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute), inherit: false));
    }

    [Fact]
    public async Task Resources_AudiencePolicyAndRefreshUseConfiguredAudience()
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(_broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        await using var resource = await ResourceServer.StartAsync(Signing.Key);
        await using var wrongResource = await ResourceServer.StartAsync(Signing.Key, "https://other-api.test");
        foreach (var body in new[] { tokens, await (await BrokerFlowDriver.RefreshAsync(_broker,
            IdentityBrokerWebApplicationFactory.TestClientId, tokens.GetProperty("refresh_token").GetString()!))
            .Content.ReadFromJsonAsync<JsonElement>() })
        {
            var token = body.GetProperty("access_token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await resource.CallAsync(token)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrongResource.CallAsync(token)).StatusCode);
            Assert.Equal(["https://api.test"], new JwtSecurityTokenHandler().ReadJwtToken(token).Audiences);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await resource.CallAsync(tokens.GetProperty("id_token").GetString()!)).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("purpose")]
    [InlineData("expiry")]
    public async Task ResourceMiddleware_RejectsInvalidTokens(string tampering)
    {
        await using var resource = await ResourceServer.StartAsync(Signing.Key);
        using var rsa = RSA.Create(2048);
        var credentials = tampering == "signature"
            ? new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256) : Signing;
        var token = new JwtSecurityToken(
            issuer: tampering == "issuer" ? "https://wrong-issuer.test" : IdentityBrokerWebApplicationFactory.Issuer,
            audience: tampering == "audience" ? "https://wrong-api.test" : "https://api.test",
            claims: [new Claim("sub", Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: tampering == "expiry" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        token.Header["typ"] = tampering == "purpose" ? "JWT" : "at+jwt";
        Assert.Equal(HttpStatusCode.Unauthorized, (await resource.CallAsync(new JwtSecurityTokenHandler().WriteToken(token))).StatusCode);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("different-user")]
    [InlineData("csrf")]
    [InlineData("scope")]
    public async Task Consent_RejectsUnboundApprovalWithoutConsumingTransaction(string mode)
    {
        var (_, challenge) = Pkce.Create();
        var prompt = await BrokerFlowDriver.BeginConsentAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read", challenge);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = _broker.BaseAddress!,
        });
        var actor = _broker;
        var actorPrompt = prompt;
        if (mode is "anonymous" or "different-user") actor = other;
        if (mode == "different-user")
        {
            _fakeIdp.Subject = "another-subject";
            var otherPrompt = await BrokerFlowDriver.BeginConsentAsync(other, _fakeIdpClient,
                IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
                "openid api.read", challenge);
            actorPrompt = JsonSerializer.SerializeToElement(new
            {
                consent_handle = prompt.GetProperty("consent_handle").GetString(),
                csrf_token = otherPrompt.GetProperty("csrf_token").GetString(),
            });
        }
        var denied = await BrokerFlowDriver.SubmitConsentAsync(actor, actorPrompt,
            scopes: mode == "scope" ? ["unregistered-scope"] : null, csrf: mode != "csrf");
        Assert.True(denied.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var pending = await db.PendingAuthorizations.SingleAsync(p => p.HandleHash ==
            OpaqueHandle.Hash(prompt.GetProperty("consent_handle").GetString()!));
        Assert.Null(pending.ConsumedAt);
        Assert.Equal(HttpStatusCode.Redirect, (await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt)).StatusCode);
    }

    [Fact]
    public async Task Consent_DenialConsumesOnlyOwnedTransactionWithoutCreatingGrant()
    {
        var (_, challenge) = Pkce.Create();
        var prompt = await BrokerFlowDriver.BeginConsentAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid", challenge);
        var denied = await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt, approve: false);
        Assert.Contains("error=access_denied", denied.Headers.Location!.Query);
        Assert.Equal(HttpStatusCode.BadRequest, (await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
            .FindAsync(subject: null, client: null, status: null, type: null,
                scopes: null, cancellationToken: default).ToListAsync());
    }

    [Fact]
    public async Task Consent_ExpiredTransactionCannotCreateGrantOrBeConsumed()
    {
        var (_, challenge) = Pkce.Create();
        var prompt = await BrokerFlowDriver.BeginConsentAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid", challenge);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var pending = await db.PendingAuthorizations.SingleAsync(p => p.HandleHash ==
            OpaqueHandle.Hash(prompt.GetProperty("consent_handle").GetString()!));
        pending.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt)).StatusCode);
        await db.Entry(pending).ReloadAsync();
        Assert.Null(pending.ConsumedAt);
    }

    [Theory]
    [InlineData("pkce")]
    [InlineData("client")]
    [InlineData("redirect")]
    [InlineData("refresh")]
    [InlineData("grant")]
    public async Task Token_InvalidBindingFailsClosed(string mode)
    {
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = mode == "grant" ? "client_credentials" : mode == "refresh" ? "refresh_token" : "authorization_code",
            ["client_id"] = mode == "client" ? "unregistered" : IdentityBrokerWebApplicationFactory.TestClientId,
            ["redirect_uri"] = mode == "redirect" ? "https://attacker.test/callback" : IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            ["code"] = code, ["code_verifier"] = mode == "pkce" ? "wrong-verifier" : verifier,
            ["refresh_token"] = "invalid-refresh",
        };
        var response = await _broker.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("access_token", out _));
    }

    [Fact]
    public async Task ConfidentialClient_UnchangedRestartUsesNativeSecretValidationAndChangesFailClosed()
    {
        await _factory.DisposeAsync();
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        _factory = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp, confidential: true,
            signingCertificate: _signingCertificate);
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://broker.test.local"),
        });
        using (var scope = _factory.Services.CreateScope())
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var app = (await apps.FindByClientIdAsync(IdentityBrokerWebApplicationFactory.TestClientId))!;
            Assert.True(await apps.ValidateClientSecretAsync(app, "generated-test-client-secret"));
            var descriptor = new OpenIddictApplicationDescriptor();
            await apps.PopulateAsync(descriptor, app);
            Assert.NotEqual("generated-test-client-secret", descriptor.ClientSecret);
            var options = scope.ServiceProvider.GetRequiredService<IdentityBrokerOptions>();
            var client = options.Clients[0];
            var seeder = scope.ServiceProvider.GetRequiredService<BrokerClientSeeder>();
            await seeder.SeedAsync(default);
            client.ClientSecret = "changed-sensitive-client-secret";
            await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(default));
            client.ClientSecret = "generated-test-client-secret";
            client.RedirectUris = ["https://changed.test/callback"];
            await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(default));
        }
        Assert.All(_factory.LogMessages, message =>
        {
            Assert.DoesNotContain("generated-test-client-secret", message);
            Assert.DoesNotContain("changed-sensitive-client-secret", message);
        });
        await _factory.DisposeAsync();
        _factory = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp, confidential: true,
            signingCertificate: _signingCertificate);
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://broker.test.local"),
        });
        Assert.Equal(HttpStatusCode.OK, (await _broker.GetAsync("/health/ready")).StatusCode);
        await _factory.DisposeAsync();
        using (var changedSecret = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp,
            confidential: true, clientSecret: "changed-sensitive-client-secret", signingCertificate: _signingCertificate))
            Assert.Throws<InvalidOperationException>(() => changedSecret.CreateClient());
        using (var changedRegistration = new IdentityBrokerWebApplicationFactory(connectionString, _fakeIdp,
            confidential: true, redirectUri: "https://changed.test/callback", signingCertificate: _signingCertificate))
            Assert.Throws<InvalidOperationException>(() => changedRegistration.CreateClient());
    }

    [Fact]
    public async Task ClientWithoutOfflineAccess_CannotRequestOrReceiveRefreshTokens()
    {
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await _postgres.CreateMigratedDatabaseAsync(), _fakeIdp, signingCertificate: _signingCertificate,
            configure: settings => settings["IdentityBroker__Clients__0__Scopes__4"] = null);
        using var broker = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://broker.test.local"),
        });
        var (verifier, challenge) = Pkce.Create();
        var rejected = await BrokerFlowDriver.StartAuthorizeAsync(broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read offline_access", challenge, "S256");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("error:invalid_request", await rejected.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            Assert.Empty(await db.Users.ToListAsync());
            Assert.Empty(await db.PendingAuthorizations.ToListAsync());
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = (await applications.FindByClientIdAsync(IdentityBrokerWebApplicationFactory.TestClientId))!;
            Assert.DoesNotContain(Permissions.GrantTypes.RefreshToken,
                await applications.GetPermissionsAsync(application));
        }

        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read", challenge);
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code, verifier);
        Assert.True(tokens.TryGetProperty("access_token", out _));
        Assert.False(tokens.TryGetProperty("refresh_token", out _));
        var refresh = await BrokerFlowDriver.RefreshAsync(broker,
            IdentityBrokerWebApplicationFactory.TestClientId, "unissued-refresh-token");
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Contains("invalid_grant", await refresh.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_UnregisteredScopeIsRejectedBeforeLoginOrConsent()
    {
        var (_, challenge) = Pkce.Create();
        var response = await BrokerFlowDriver.StartAuthorizeAsync(_broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid admin.unregistered", challenge, "S256");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error:invalid_scope", await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        Assert.Empty(await db.PendingAuthorizations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_GrantBoundToAnotherRegisteredClientIsRejected(bool refresh)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var original = (await applications.FindByClientIdAsync(IdentityBrokerWebApplicationFactory.TestClientId))!;
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, original);
            descriptor.ClientId = "other-registered-client";
            await applications.CreateAsync(descriptor);
        }
        var (code, verifier) = await RunHappyPathAuthorizeAsync();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = refresh ? "refresh_token" : "authorization_code",
            ["client_id"] = "other-registered-client",
            ["redirect_uri"] = IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            ["code"] = code, ["code_verifier"] = verifier,
        };
        if (refresh)
        {
            var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(_broker,
                IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
                code, verifier);
            form["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!;
        }
        var response = await _broker.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Consent_NarrowsScopesAndCodeCannotBeReused()
    {
        var (verifier, challenge) = Pkce.Create();
        var prompt = await BrokerFlowDriver.BeginConsentAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read offline_access", challenge);
        var consent = await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt, scopes: ["openid", "api.read"]);
        var codeResponse = await _broker.GetAsync(consent.Headers.Location);
        var code = QueryHelpers.ParseQuery(codeResponse.Headers.Location!.Query)["code"].ToString();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(_broker,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        Assert.False(tokens.TryGetProperty("refresh_token", out _));
        Assert.DoesNotContain("offline_access", tokens.GetProperty("scope").GetString()!);
        var replay = await _broker.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = IdentityBrokerWebApplicationFactory.TestClientId,
            ["redirect_uri"] = IdentityBrokerWebApplicationFactory.TestClientRedirectUri, ["code"] = code,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Consent_CancellationDuringPostgresLockWaitDoesNotConsumeTransaction()
    {
        var (_, challenge) = Pkce.Create();
        var prompt = await BrokerFlowDriver.BeginConsentAsync(_broker, _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId, IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid", challenge);
        var hash = OpaqueHandle.Hash(prompt.GetProperty("consent_handle").GetString()!);
        await using var connection = new NpgsqlConnection(_factory.ConnectionStringForRestartTest);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM identity_broker.pending_authorizations WHERE \"HandleHash\" = @hash FOR UPDATE", connection, tx);
        command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteScalarAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = BrokerFlowDriver.SubmitConsentAsync(_broker, prompt, ct: cts.Token);
        await using var observer = new NpgsqlConnection(_factory.ConnectionStringForRestartTest);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        bool waiting;
        do
        {
            await using var query = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE 'UPDATE%pending_authorizations%')", observer);
            waiting = (bool)(await query.ExecuteScalarAsync())!;
            if (waiting) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);
        Assert.True(waiting);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await tx.RollbackAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        Assert.Null((await db.PendingAuthorizations.SingleAsync(p => p.HandleHash == hash)).ConsumedAt);
        Assert.Equal(HttpStatusCode.Redirect, (await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt)).StatusCode);
    }
}
