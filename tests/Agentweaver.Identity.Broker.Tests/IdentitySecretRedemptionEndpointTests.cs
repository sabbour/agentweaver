extern alias OrchestratorHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http;
using OrchestratorHost::Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentitySecretRedemptionEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private FakeIdentityProvider _fakeIdp = null!;
    private IdentityBrokerWebApplicationFactory _factory = null!;
    private HttpClient _broker = null!;
    private RecordingSecretRedemption _backend = null!;

    public async Task InitializeAsync()
    {
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _backend = new RecordingSecretRedemption();
        _factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateMigratedDatabaseAsync(),
            _fakeIdp,
            configureServices: services =>
            {
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption>(_backend);
            });
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
    }

    public async Task DisposeAsync()
    {
        _broker.Dispose();
        await _factory.DisposeAsync();
        await _fakeIdp.DisposeAsync();
    }

    [Fact]
    public async Task Redeem_RequiresValidatedBearerAndReturnsOnlyAuthorizedExactVersion()
    {
        await AddGrantAsync(Grant("grant:success", "secret-a", "version-a"));

        var response = await SendAsync(
            Input("secret-a", "version-a"),
            CreateToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SecretRedemptionResult>();
        Assert.NotNull(body);
        Assert.Equal("secret-a", body.SecretId);
        Assert.Equal("version-a", body.SecretVersion);
        Assert.Equal(_backend.Value, body.Value);
        var request = Assert.Single(_backend.Requests);
        Assert.Equal("version-a", request.Secret.Version);
        Assert.DoesNotContain(_backend.Value, string.Join('\n', _factory.LogMessages));
    }

    [Fact]
    public async Task BrokerIssuedRunToken_RedeemsOnlyItsDurablyBoundProjectAndRun()
    {
        const string externalSubject = "oauth-run-redemption-subject";
        const string projectId = "oauth-project";
        const string runId = "oauth-run";
        _fakeIdp.Subject = externalSubject;
        using var fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority),
        };
        var (verifier, challenge) = Pkce.Create();
        Guid actorId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            var user = await scope.ServiceProvider.GetRequiredService<BrokerUserProvisioner>()
                .ProvisionAsync(FakeIdentityProvider.Authority, externalSubject, "OAuth test user", null, default);
            actorId = user.Id;
            var authority = scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>();
            await authority.ReplaceAsync(
                CreateRunGrant("grant:oauth-run", actorId, projectId, runId, "secret-oauth", "version-a"),
                0,
                "create-oauth-run");
            await authority.ReplaceAsync(
                CreateRunGrant("grant:other-run", actorId, "other-project", "other-run", "secret-other", "version-a"),
                0,
                "create-other-run");
        }

        var prompt = await BrokerFlowDriver.BeginConsentAsync(
            _broker,
            fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read",
            challenge,
            projectId: projectId,
            runId: runId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
            var pending = await db.PendingAuthorizations.SingleAsync(candidate =>
                candidate.HandleHash == OpaqueHandle.Hash(prompt.GetProperty("consent_handle").GetString()!));
            Assert.Equal(actorId, pending.SubjectUserId);
            Assert.Equal(projectId, pending.ProjectId);
            Assert.Equal(runId, pending.RunId);
        }

        var consent = await BrokerFlowDriver.SubmitConsentAsync(_broker, prompt, scopes: ["openid", "api.read"]);
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var codeResponse = await _broker.GetAsync(consent.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, codeResponse.StatusCode);
        var code = QueryHelpers.ParseQuery(codeResponse.Headers.Location!.Query)["code"].ToString();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var issuedPrincipal = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.Equal(actorId.ToString(), issuedPrincipal.Subject);
        Assert.Equal(projectId, issuedPrincipal.Claims.Single(claim =>
            claim.Type == SecretRedemptionEndpoints.ProjectIdClaim).Value);
        Assert.Equal(runId, issuedPrincipal.Claims.Single(claim =>
            claim.Type == SecretRedemptionEndpoints.RunIdClaim).Value);

        using var success = await SendAsync(
            Input("secret-oauth", "version-a", runId: runId),
            accessToken);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Single(_backend.Requests);

        using var crossBinding = await SendAsync(
            Input("secret-other", "version-a", runId: runId),
            accessToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossBinding.StatusCode);
        Assert.Single(_backend.Requests);
    }

    [Fact]
    public async Task BrokerIssuedDistinctAudienceTokenRedeemsPurposeBoundSecretAndInvalidatesConsumerCredential()
    {
        const string orchestratorAudience = "https://orchestrator.test";
        const string brokerAudience = "https://broker-redemption.test";
        const string projectId = "source-control-project";
        const string runId = "source-control-run";
        const string secretId = "github-api";
        const string secretVersion = "version-7";
        const string grantId = "grant:source-control-api";

        await RestartWithDistinctAudiencesAsync(orchestratorAudience, brokerAudience);
        _fakeIdp.Subject = "source-control-oauth-subject";
        using var fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority),
        };
        var (verifier, challenge) = Pkce.Create();
        Guid actorId;
        using (var scope = _factory.Services.CreateScope())
        {
            actorId = (await scope.ServiceProvider.GetRequiredService<BrokerUserProvisioner>()
                .ProvisionAsync(
                    FakeIdentityProvider.Authority,
                    _fakeIdp.Subject,
                    "SourceControl OAuth user",
                    null,
                    default)).Id;
            await scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>()
                .ReplaceAsync(
                    new SecretRedemptionGrant(
                        grantId,
                        actorId.ToString(),
                        projectId,
                        runId,
                        SourceControlSecretPurposes.Api,
                        new SecretRef(secretId, secretVersion),
                        GrantState.Active,
                        DateTimeOffset.UtcNow.AddMinutes(30),
                        revision: "draft"),
                    0,
                    "create-source-control-api-grant");
        }

        var prompt = await BrokerFlowDriver.BeginConsentAsync(
            _broker,
            fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            "openid api.read projects.orchestrator",
            challenge,
            projectId: projectId,
            runId: runId);
        var consent = await BrokerFlowDriver.SubmitConsentAsync(
            _broker, prompt, scopes: ["openid", "api.read", "projects.orchestrator"]);
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var codeResponse = await _broker.GetAsync(consent.Headers.Location!);
        Assert.Equal(HttpStatusCode.Redirect, codeResponse.StatusCode);
        var code = QueryHelpers.ParseQuery(codeResponse.Headers.Location!.Query)["code"].ToString();
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            _broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var principal = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var audiences = principal.Claims
            .Where(claim => claim.Type == "aud")
            .Select(claim => claim.Value)
            .ToArray();
        Assert.Contains(orchestratorAudience, audiences);
        Assert.Contains(brokerAudience, audiences);
        Assert.Equal(actorId.ToString(), principal.Subject);
        Assert.Equal(projectId, principal.Claims.Single(claim =>
            claim.Type == SecretRedemptionEndpoints.ProjectIdClaim).Value);
        Assert.Equal(runId, principal.Claims.Single(claim =>
            claim.Type == SecretRedemptionEndpoints.RunIdClaim).Value);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken).ToString();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(principal.Claims, "Bearer"));
        var acceptedRun = new SourceControlAcceptedRunBinding(
            IdentityBrokerWebApplicationFactory.Issuer + "/",
            actorId.ToString(),
            "source-control-tenant",
            projectId,
            runId,
            "source-control-root",
            new string('A', 64),
            1,
            1,
            1,
            "source-control-context",
            1);
        var client = new SourceControlSecretRedemptionClient(
            _broker,
            new SourceControlSecretRedemptionOptions(
                IdentityBrokerWebApplicationFactory.Issuer,
                orchestratorAudience,
                brokerAudience,
                _broker.BaseAddress!.AbsoluteUri),
            TimeProvider.System);
        SecretCredential? usedCredential = null;
        var redeemedValue = await client.WithCredentialAsync(
            context,
            acceptedRun,
            new SourceControlCredentialReference(
                new SecretRef(secretId, secretVersion),
                SourceControlSecretPurposes.Api),
            (credential, _) =>
            {
                usedCredential = credential;
                return Task.FromResult(credential.GetValue());
            },
            CancellationToken.None);

        Assert.Equal(_backend.Value, redeemedValue);
        Assert.NotNull(usedCredential);
        Assert.Throws<InvalidOperationException>(() => usedCredential.GetValue());
        var brokerRequest = Assert.Single(_backend.Requests);
        Assert.Equal(SourceControlSecretPurposes.Api, brokerRequest.Purpose);
        Assert.Equal(secretVersion, brokerRequest.Secret.Version);

        using var orchestratorOnly = await SendAsync(
            Input(secretId, secretVersion, SourceControlSecretPurposes.Api, runId),
            CreateToken(
                actorId.ToString(),
                projectId,
                runId,
                audience: orchestratorAudience));
        Assert.Equal(HttpStatusCode.Unauthorized, orchestratorOnly.StatusCode);
        using var wrongVersion = await SendAsync(
            Input(secretId, "wrong-version", SourceControlSecretPurposes.Api, runId),
            accessToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongVersion.StatusCode);
        Assert.Single(_backend.Requests);
        Assert.DoesNotContain(_backend.Value, string.Join('\n', _factory.LogMessages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeem_GrantSurvivesIdentityHostRestart()
    {
        await AddGrantAsync(Grant("grant:host-restart", "secret-a", "version-a"));
        var connectionString = _factory.ConnectionStringForRestartTest;

        _broker.Dispose();
        await _factory.DisposeAsync();
        _backend = new RecordingSecretRedemption();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString,
            _fakeIdp,
            configureServices: services =>
            {
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption>(_backend);
            });
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });

        using var response = await SendAsync(Input("secret-a", "version-a"), CreateToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_backend.Requests);
    }

    [Fact]
    public async Task Redeem_RejectsUntrustedClaimsAndBodyIdentityBeforeBackendContact()
    {
        await AddGrantAsync(Grant("grant:bindings", "secret-a", "version-a"));

        var mismatches = new[]
        {
            (Input("secret-a", "version-a"), CreateToken(actorId: "other-actor")),
            (Input("secret-a", "version-a"), CreateToken(projectId: "other-project")),
            (Input("secret-a", "version-a"), CreateToken(runId: "other-run")),
            (Input("secret-a", "version-a", runId: "other-run"), CreateToken()),
            (Input("secret-a", "version-a", purpose: "publish"), CreateToken()),
            (Input("secret-b", "version-a"), CreateToken()),
            (Input("secret-a", "version-b"), CreateToken()),
        };

        foreach (var (input, token) in mismatches)
        {
            using var response = await SendAsync(input, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var spoofedHeaders = new HttpRequestMessage(HttpMethod.Post, "/secrets/redeem")
        {
            Content = JsonContent.Create(Input("secret-a", "version-a")),
        })
        {
            spoofedHeaders.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(actorId: "attacker"));
            spoofedHeaders.Headers.Add("X-Actor-Id", "actor-1");
            spoofedHeaders.Headers.Add("X-Project-Id", "project-1");
            spoofedHeaders.Headers.Add("X-Run-Id", "run-1");
            using var response = await _broker.SendAsync(spoofedHeaders);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var bodyIdentityAttempt = await SendRawAsync(
            """{"secretId":"secret-a","secretVersion":"version-a","purpose":"configure","runId":"run-1","actorId":"attacker","projectId":"project-1"}""",
            CreateToken(actorId: "attacker"));
        Assert.Equal(HttpStatusCode.BadRequest, bodyIdentityAttempt.StatusCode);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task Redeem_RejectsInvalidIssuerAudienceSignatureAndLifetimeInAuthenticationMiddleware()
    {
        await AddGrantAsync(Grant("grant:token-validation", "secret-a", "version-a"));

        var invalidTokens = new[]
        {
            CreateToken(issuer: "https://other-issuer.test"),
            CreateToken(audience: "https://other-api.test"),
            CreateToken(expiresAt: DateTime.UtcNow.AddMinutes(-10)),
            CreateToken(useUntrustedSigningKey: true),
        };

        foreach (var token in invalidTokens)
        {
            using var response = await SendAsync(Input("secret-a", "version-a"), token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task Redeem_DeniesExpiredRevokedReplacedAndAmbiguousGrantsBeforeBackendContact()
    {
        await AddExpiredGrantAsync("grant:expired", "secret-expired", "version-a");

        await AddGrantAsync(Grant("grant:revoked", "secret-revoked", "version-a"));
        await RevokeAsync("grant:revoked", 1, "revoke-http");

        await AddGrantAsync(Grant("grant:replaced", "secret-replaced", "version-a"));
        await ReplaceGrantAsync(Grant("grant:replaced", "secret-replaced", "version-b"), 1, "replace-http");

        await AddGrantAsync(Grant("grant:ambiguous-a", "secret-ambiguous", "version-a"));
        await AddGrantAsync(Grant("grant:ambiguous-b", "secret-ambiguous", "version-a"));

        var deniedRequests = new[]
        {
            Input("secret-expired", "version-a"),
            Input("secret-revoked", "version-a"),
            Input("secret-replaced", "version-a"),
            Input("secret-ambiguous", "version-a"),
        };
        foreach (var input in deniedRequests)
        {
            using var response = await SendAsync(input, CreateToken());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task Redeem_InvalidatesAcquiredCredentialAfterRevocationAndRedactsDenial()
    {
        const string credentialBytes = "generated-secret-value-do-not-log-or-return-in-errors";
        await AddGrantAsync(Grant("grant:revoked-during-acquisition", "secret-a", "version-a"));

        _backend.Handler = async (_, _) =>
        {
            await RevokeAsync("grant:revoked-during-acquisition", 1, "revoke-during-acquisition");
            return _backend.NewCredential(credentialBytes);
        };

        using var response = await SendAsync(Input("secret-a", "version-a"), CreateToken());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(credentialBytes, body, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialBytes, string.Join('\n', _factory.LogMessages), StringComparison.Ordinal);
        Assert.NotNull(_backend.LastCredential);
        Assert.Throws<InvalidOperationException>(() => _backend.LastCredential.GetValue());
    }

    [Fact]
    public async Task Redeem_RedactsBackendErrorsAndHonorsCancellationAfterAcquisition()
    {
        const string credentialBytes = "generated-backend-error-secret";
        await AddGrantAsync(Grant("grant:redacted-error", "secret-a", "version-a"));
        _backend.Handler = (_, _) => Task.FromException<SecretCredential>(new InvalidOperationException(credentialBytes));

        using (var response = await SendAsync(Input("secret-a", "version-a"), CreateToken()))
        {
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(credentialBytes, body, StringComparison.Ordinal);
            Assert.DoesNotContain(credentialBytes, string.Join('\n', _factory.LogMessages), StringComparison.Ordinal);
        }

        await AddGrantAsync(Grant("grant:cancellation", "secret-cancel", "version-a"));
        _backend.Reset();
        _backend.Handler = async (_, _) =>
        {
            _backend.Started.TrySetResult();
            await _backend.Release.Task.ConfigureAwait(false);
            _backend.ReturnedCredential = _backend.NewCredential("generated-cancelled-credential");
            _backend.Returned.TrySetResult();
            return _backend.ReturnedCredential;
        };

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/secrets/redeem")
        {
            Content = JsonContent.Create(Input("secret-cancel", "version-a")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken());
        var pending = _broker.SendAsync(request, cancellation.Token);
        await _backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        _backend.Release.TrySetResult();
        await _backend.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(10)));

        var invalidationDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < invalidationDeadline)
        {
            try
            {
                _backend.ReturnedCredential!.GetValue();
                await Task.Delay(10);
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
        Assert.Throws<InvalidOperationException>(() => _backend.ReturnedCredential!.GetValue());
    }

    [Fact]
    public async Task ProductionHostComposesWorkloadIdentityKeyVaultAdapterWithExplicitRootUri()
    {
        await using var production = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateMigratedDatabaseAsync(),
            _fakeIdp);
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        using var scope = production.Services.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<ISecretRedemption>();
        var adapter = scope.ServiceProvider.GetRequiredService<AzureKeyVaultSecretRedemption>();

        Assert.Same(adapter, backend);
        Assert.Equal(new Uri("https://identity-test-vault.vault.azure.net/"),
            scope.ServiceProvider.GetRequiredService<AzureKeyVaultConfiguration>().VaultUri);
        Assert.IsType<AzureKeyVaultSecretRedemption>(backend);
    }

    private SecretRedemptionInput Input(
        string secretId,
        string version,
        string purpose = "configure",
        string runId = "run-1") =>
        new(secretId, version, purpose, runId);

    private async Task<HttpResponseMessage> SendAsync(SecretRedemptionInput input, string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/secrets/redeem")
        {
            Content = JsonContent.Create(input),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _broker.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(string body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/secrets/redeem")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _broker.SendAsync(request);
    }

    private async Task AddGrantAsync(SecretRedemptionGrant grant)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>()
            .ReplaceAsync(grant, 0, "create-" + Guid.NewGuid().ToString("N"));
    }

    private async Task ReplaceGrantAsync(SecretRedemptionGrant grant, long expectedRevision, string key)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>()
            .ReplaceAsync(grant, expectedRevision, key);
    }

    private async Task RevokeAsync(string grantId, long expectedRevision, string key)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>()
            .RevokeAsync(grantId, expectedRevision, key);
    }

    private async Task AddExpiredGrantAsync(string grantId, string secretId, string version)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_broker.secret_grant_heads (grant_id, current_revision)
            VALUES ({grantId}, 1)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_broker.secret_grant_revisions
                (grant_id, revision, actor_id, project_id, run_id, purpose, secret_id, secret_version, state, expires_at)
            VALUES ({grantId}, 1, 'actor-1', 'project-1', 'run-1', 'configure', {secretId}, {version}, 0, {DateTimeOffset.UtcNow.AddMinutes(-1)})
            """);
    }

    private SecretRedemptionGrant Grant(string grantId, string secretId, string version) =>
        IdentityGrantAuthorityTests.CreateGrant(grantId, secretId, version);

    private static SecretRedemptionGrant CreateRunGrant(
        string grantId,
        Guid actorId,
        string projectId,
        string runId,
        string secretId,
        string version) =>
        new(
            grantId,
            actorId.ToString(),
            projectId,
            runId,
            "configure",
            new SecretRef(secretId, version),
            GrantState.Active,
            DateTimeOffset.UtcNow.AddMinutes(30),
            revision: "draft");

    private async Task RestartWithDistinctAudiencesAsync(
        string orchestratorAudience,
        string brokerAudience)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        _broker.Dispose();
        await _factory.DisposeAsync();
        _backend = new RecordingSecretRedemption();
        _factory = new IdentityBrokerWebApplicationFactory(
            connectionString,
            _fakeIdp,
            configure: settings =>
            {
                settings["IdentityBroker__SecretRedemption__Audience"] = brokerAudience;
                settings["IdentityBroker__Clients__0__Resources__0"] = orchestratorAudience;
                settings["IdentityBroker__Clients__0__Resources__1"] = brokerAudience;
                settings["IdentityBroker__Clients__0__Scopes__5"] = "projects.orchestrator";
            },
            configureServices: services =>
            {
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption>(_backend);
            });
        _broker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
    }

    private string CreateToken(
        string actorId = "actor-1",
        string projectId = "project-1",
        string runId = "run-1",
        string issuer = IdentityBrokerWebApplicationFactory.Issuer,
        string audience = "https://api.test",
        DateTime? expiresAt = null,
        bool useUntrustedSigningKey = false)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _factory.SigningCertificateForTest.PfxPath,
            _factory.SigningCertificateForTest.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        using var otherKey = useUntrustedSigningKey ? RSA.Create(2048) : null;
        SecurityKey signingKey = useUntrustedSigningKey
            ? new RsaSecurityKey(otherKey!)
            : new X509SecurityKey(certificate);
        var tokenExpiry = expiresAt ?? DateTime.UtcNow.AddMinutes(5);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim("sub", actorId),
                new Claim(SecretRedemptionEndpoints.ProjectIdClaim, projectId),
                new Claim(SecretRedemptionEndpoints.RunIdClaim, runId),
            ],
            tokenExpiry < DateTime.UtcNow ? tokenExpiry.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(-1),
            tokenExpiry,
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class RecordingSecretRedemption : ISecretRedemption
{
    private readonly List<SecretRedemptionRequest> _requests = [];

    public string Value { get; } = "generated-credential-" + Guid.NewGuid().ToString("N");

    public IReadOnlyList<SecretRedemptionRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public Func<SecretRedemptionRequest, CancellationToken, Task<SecretCredential>>? Handler { get; set; }

    public SecretCredential? LastCredential { get; private set; }

    public SecretCredential? ReturnedCredential { get; set; }

    public TaskCompletionSource Started { get; private set; } = NewSignal();

    public TaskCompletionSource Release { get; private set; } = NewSignal();

    public TaskCompletionSource Returned { get; private set; } = NewSignal();

    public async Task<SecretCredential> RedeemAsync(
        SecretRedemptionRequest request,
        CancellationToken cancellationToken)
    {
        lock (_requests) _requests.Add(request);
        if (Handler is { } handler)
        {
            var result = await handler(request, cancellationToken).ConfigureAwait(false);
            LastCredential = result;
            return result;
        }

        return NewCredential(Value);
    }

    public SecretCredential NewCredential(string value)
    {
        var credential = new SecretCredential(value, DateTimeOffset.UtcNow.AddMinutes(3));
        LastCredential = credential;
        return credential;
    }

    public void Reset()
    {
        lock (_requests) _requests.Clear();
        Handler = null;
        LastCredential = null;
        ReturnedCredential = null;
        Started = NewSignal();
        Release = NewSignal();
        Returned = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
