using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Reflection;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class RemoteMcpOAuthConsentPreparationTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 7, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://identity.example.test/";
    private const string ActorId = "actor-1";
    private const string TenantId = "tenant-1";
    private const string ProjectId = "project-1";
    private const string ProjectsOwnerAddress = "https://projects.test/";
    private const string EnvironmentOwnerAddress = "https://environment.test/";
    private static readonly Guid ConnectionId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string InitialConfigurationHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FinalConfigurationHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ChangedConfigurationHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task PreparationLinksThenRereadsAndPersistsOnlyProtectedVerifierReference()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var metadataHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(metadataHandler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets);

            var result = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);

            Assert.Equal(43, result.State.Length);
            Assert.Equal(43, result.PkceChallenge.Length);
            Assert.Equal(2, result.ConfigurationRevision);
            Assert.Equal(FinalConfigurationHash, result.ConfigurationSha256);
            Assert.Equal(2, result.ConnectionRevision);
            Assert.Equal("https://issuer.example.test/authorize", result.AuthorizationUri.GetLeftPart(UriPartial.Path));
            Assert.Equal("registered-client", ParseQuery(result.AuthorizationUri)["client_id"]);
            Assert.Collection(
                metadataHandler.Requests,
                uri => Assert.Equal(
                    "https://mcp.example.test/.well-known/oauth-protected-resource/resource",
                    uri.AbsoluteUri),
                uri => Assert.Equal(
                    "https://issuer.example.test/.well-known/oauth-authorization-server",
                    uri.AbsoluteUri));
            Assert.Equal("remote-mcp-" + ConnectionId.ToString("N") + "-verifier",
                Assert.Single(secrets.SecretIds));
            Assert.Equal(1, environmentHandler.LinkRequests);
            Assert.Equal(4, projectsHandler.AuthorizationRequests);
            Assert.All(projectsHandler.Requests.Concat(environmentHandler.Requests), request =>
            {
                Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
                Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues(
                    ProjectAuthorizationContextContract.TenantSelectorHeader)));
            });
            Assert.DoesNotContain(projectsHandler.Requests.Concat(environmentHandler.Requests), request =>
                request.RequestUri!.AbsolutePath.Contains("/token", StringComparison.Ordinal));

            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal(RemoteMcpOAuthConnectionState.PendingConsent, savedConnection.State);
            Assert.Equal(2, savedConnection.ConfigurationRevision);
            Assert.Equal(FinalConfigurationHash, savedConnection.EnvironmentConfigurationHash);
            Assert.Equal(RemoteMcpOAuthConsentState.Pending, savedConsent.State);
            Assert.Equal(result.CorrelationId, savedConsent.CorrelationId);
            Assert.Equal(result.PkceChallenge, savedConsent.PkceChallenge);
            Assert.Equal(Hash(result.State), savedConsent.StateHash);
            Assert.Equal(Assert.Single(secrets.References).Id, savedConsent.VerifierSecretId);
            Assert.Equal(Assert.Single(secrets.References).Version, savedConsent.VerifierSecretVersion);
            Assert.Equal(result.PkceChallenge, Challenge(Assert.Single(secrets.Values)));

            var persisted = JsonSerializer.Serialize(new { savedConnection, savedConsent });
            Assert.DoesNotContain(result.State, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(Assert.Single(secrets.Values), persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(result.State, result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CallbackExchangesCodeAndPersistsOnlyProtectedTokenReferences()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var preparation = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var status = await service.CompleteCallbackAsync(
                Actor(), Issuer, ActorId,
                new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                CancellationToken.None);

            Assert.Equal(RemoteMcpOAuthConnectionState.Authorized.ToString(), status.State);
            Assert.Collection(
                providerHandler.Requests,
                uri => Assert.Equal(
                    "https://mcp.example.test/.well-known/oauth-protected-resource/resource",
                    uri.AbsoluteUri),
                uri => Assert.Equal(
                    "https://issuer.example.test/.well-known/oauth-authorization-server",
                    uri.AbsoluteUri),
                uri => Assert.Equal(
                    "https://mcp.example.test/.well-known/oauth-protected-resource/resource",
                    uri.AbsoluteUri),
                uri => Assert.Equal(
                    "https://issuer.example.test/.well-known/oauth-authorization-server",
                    uri.AbsoluteUri),
                uri => Assert.Equal("https://issuer.example.test/token", uri.AbsoluteUri));
            Assert.Equal(
                ["remote-mcp-" + ConnectionId.ToString("N") + "-verifier",
                    "remote-mcp-" + ConnectionId.ToString("N") + "-access",
                    "remote-mcp-" + ConnectionId.ToString("N") + "-refresh"],
                secrets.SecretIds);

            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal(RemoteMcpOAuthConnectionState.Authorized, savedConnection.State);
            Assert.Equal(secrets.References[1].Id, savedConnection.AccessTokenSecretId);
            Assert.Equal(secrets.References[1].Version, savedConnection.AccessTokenSecretVersion);
            Assert.Equal(secrets.References[2].Id, savedConnection.RefreshTokenSecretId);
            Assert.Equal(secrets.References[2].Version, savedConnection.RefreshTokenSecretVersion);
            Assert.Equal(RemoteMcpOAuthConsentState.Consumed, savedConsent.State);
            Assert.Null(savedConsent.ClaimAttemptId);

            var persisted = JsonSerializer.Serialize(new { savedConnection, savedConsent });
            Assert.DoesNotContain("access-token-value", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-token-value", persisted, StringComparison.Ordinal);

            var replay = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.CompleteCallbackAsync(
                    Actor(), Issuer, ActorId,
                    new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                    CancellationToken.None));
            Assert.Equal("remote_mcp_connection_revision_conflict", replay.Code);
            Assert.Single(providerHandler.TokenGrantTypes);
        }
    }

    [Fact]
    public async Task CallbackStateCannotBeResolvedByAnotherHuman()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            using var projects = new HttpClient(new ProjectsOwnerHandler());
            using var environment = new HttpClient(new EnvironmentOwnerHandler(row.IdentityBindingReference));
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets, secrets);
            var preparation = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var requestsBeforeCallback = providerHandler.Requests.Count;

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.CompleteCallbackAsync(
                    Actor(), Issuer, "another-human",
                    new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                    CancellationToken.None));

            Assert.Equal("remote_mcp_connection_not_found", error.Code);
            Assert.Equal(requestsBeforeCallback, providerHandler.Requests.Count);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal(RemoteMcpOAuthConsentState.Pending, savedConsent.State);
        }
    }

    [Fact]
    public async Task ProviderCancellationClosesTheConsentWithoutRedeemingSecretsOrExchangingCode()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            var providerHandler = new MetadataProviderHandler();
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var preparation = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.CompleteCallbackAsync(
                    Actor(), Issuer, ActorId,
                    new RemoteMcpOAuthCallbackRequest(preparation.State, null, "access_denied"),
                    CancellationToken.None));

            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal("remote_mcp_oauth_consent_denied", error.Code);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Equal(RemoteMcpOAuthConsentState.Denied, savedConsent.State);
            Assert.Empty(providerHandler.TokenGrantTypes);
            Assert.Empty(secrets.RedeemedReferences);
        }
    }

    [Fact]
    public async Task UncertainTokenExchangeTimeoutConsumesClaimAndClosesPendingConnection()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler(failTokenExchange: true);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var preparation = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.CompleteCallbackAsync(
                    Actor(), Issuer, ActorId,
                    new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                    CancellationToken.None));

            Assert.Equal("remote_mcp_oauth_exchange_uncertain", error.Code);
            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Equal(RemoteMcpOAuthConsentState.Consumed, savedConsent.State);
            Assert.Null(savedConsent.ClaimAttemptId);
        }
    }

    [Fact]
    public async Task TokenSecretWriteFailureConsumesCallbackAfterProviderMayHaveIssuedTokens()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter
            {
                WriteFailure = CreateSecretStoreFailure()
            };
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var preparation = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.CompleteCallbackAsync(
                    Actor(), Issuer, ActorId,
                    new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                    CancellationToken.None));

            Assert.Equal("remote_mcp_secret_store_unavailable", error.Code);
            Assert.Equal("https://issuer.example.test/token", providerHandler.Requests[^1].AbsoluteUri);
            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            var savedConsent = await db.RemoteMcpOAuthConsents.AsNoTracking()
                .SingleAsync(item => item.ConnectionRecordId == row.Id);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Equal(row.ConnectionRevision + 2, savedConnection.ConnectionRevision);
            Assert.Equal(row.CredentialRevision + 2, savedConnection.CredentialRevision);
            Assert.Null(savedConnection.AccessTokenSecretId);
            Assert.Null(savedConnection.AccessTokenSecretVersion);
            Assert.Null(savedConnection.AccessTokenExpiresAt);
            Assert.Null(savedConnection.RefreshTokenSecretId);
            Assert.Null(savedConnection.RefreshTokenSecretVersion);
            Assert.Null(savedConnection.RefreshTokenExpiresAt);
            Assert.Equal(RemoteMcpOAuthConsentState.Consumed, savedConsent.State);
            Assert.Null(savedConsent.ClaimAttemptId);
            Assert.DoesNotContain("access-token-value", secrets.Values);
            Assert.DoesNotContain("refresh-token-value", secrets.Values);
        }
    }

    [Fact]
    public async Task RefreshRotatesOnlyUnderTheCurrentDurableBindingAndCredentialRevision()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var status = await service.RefreshAsync(
                Actor(), Issuer, ActorId, ConnectionId,
                new RemoteMcpOAuthRefreshRequest(
                    row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision),
                CancellationToken.None);

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.Authorized.ToString(), status.State);
            Assert.Equal(row.ConnectionRevision + 2, saved.ConnectionRevision);
            Assert.Equal(row.CredentialRevision + 1, saved.CredentialRevision);
            Assert.Equal("version-2", saved.AccessTokenSecretVersion);
            Assert.Equal("version-2", saved.RefreshTokenSecretVersion);
            Assert.Equal(["refresh_token"], providerHandler.TokenGrantTypes);
            Assert.Contains("stored-refresh-token", secrets.Values);
            Assert.DoesNotContain("access-token-value", JsonSerializer.Serialize(saved));
            Assert.DoesNotContain("refresh-token-value", JsonSerializer.Serialize(saved));
        }
    }

    [Fact]
    public async Task ConcurrentRefreshRequestsShareOneDurableLeaseAndOneProviderExchange()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        await using (var secondDb = CreateDatabaseContext(dataSource))
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            using var projects1 = new HttpClient(new ProjectsOwnerHandler());
            using var projects2 = new HttpClient(new ProjectsOwnerHandler());
            using var environment1 = new HttpClient(new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true));
            using var environment2 = new HttpClient(new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true));
            var providerHandler = new MetadataProviderHandler(synchronizeMetadata: true);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var firstService = CreateService(db, projects1, environment1, provider, secrets, secrets);
            var secondService = CreateService(secondDb, projects2, environment2, provider, secrets, secrets);
            var request = new RemoteMcpOAuthRefreshRequest(
                row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision);

            var first = firstService.RefreshAsync(
                Actor(), Issuer, ActorId, ConnectionId, request, CancellationToken.None);
            var second = secondService.RefreshAsync(
                Actor(), Issuer, ActorId, ConnectionId, request, CancellationToken.None);
            var successful = 0;
            var conflicted = 0;
            foreach (var task in new[] { first, second })
            {
                try
                {
                    await task;
                    successful++;
                }
                catch (RemoteMcpOAuthManagementException error)
                    when (error.Code == "remote_mcp_connection_revision_conflict")
                {
                    conflicted++;
                }
            }

            Assert.Equal(1, successful);
            Assert.Equal(1, conflicted);
            Assert.Equal(["refresh_token"], providerHandler.TokenGrantTypes);
            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.Authorized, saved.State);
            Assert.Equal(row.ConnectionRevision + 2, saved.ConnectionRevision);
        }
    }

    [Fact]
    public async Task RefreshResponseLossBecomesIndeterminateAndCannotBeRetried()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler(failRefreshTokenExchange: true);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var service = CreateService(db, projects, environment, provider, secrets, secrets);
            var request = new RemoteMcpOAuthRefreshRequest(
                row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(Actor(), Issuer, ActorId, ConnectionId, request, CancellationToken.None));

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal("remote_mcp_oauth_refresh_uncertain", error.Code);
            Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate, saved.State);
            Assert.Equal(row.CredentialRevision, saved.CredentialRevision);
            Assert.Equal("version-1", saved.RefreshTokenSecretVersion);
            Assert.Single(providerHandler.TokenGrantTypes);
            await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(Actor(), Issuer, ActorId, ConnectionId, request, CancellationToken.None));
            Assert.Single(providerHandler.TokenGrantTypes);
        }
    }

    [Fact]
    public async Task RefreshSecretWriteFailureAfterProviderSuccessLeavesCredentialIndeterminate()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter
            {
                WriteFailure = CreateSecretStoreFailure()
            };
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var service = CreateService(db, projects, environment, provider, secrets, secrets);
            var request = new RemoteMcpOAuthRefreshRequest(
                row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(Actor(), Issuer, ActorId, ConnectionId, request, CancellationToken.None));

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal("remote_mcp_secret_store_unavailable", error.Code);
            Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate, saved.State);
            Assert.Equal(row.ConnectionRevision + 2, saved.ConnectionRevision);
            Assert.Equal(row.CredentialRevision, saved.CredentialRevision);
            Assert.Equal(row.AccessTokenSecretVersion, saved.AccessTokenSecretVersion);
            Assert.Equal(row.RefreshTokenSecretVersion, saved.RefreshTokenSecretVersion);
            Assert.NotNull(saved.RefreshAttemptId);
            Assert.Single(providerHandler.TokenGrantTypes);
            Assert.DoesNotContain("access-token-value", secrets.Values);
            Assert.DoesNotContain("refresh-token-value", secrets.Values);
        }
    }

    [Fact]
    public async Task RefreshInvalidGrantRevokesAndClearsCurrentCredentialReferences()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler(rejectRefreshTokenExchange: true);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(
                    Actor(), Issuer, ActorId, ConnectionId,
                    new RemoteMcpOAuthRefreshRequest(
                        row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision),
                    CancellationToken.None));

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal("remote_mcp_oauth_refresh_rejected", error.Code);
            Assert.Equal(RemoteMcpOAuthConnectionState.Revoked, saved.State);
            Assert.Null(saved.AccessTokenSecretId);
            Assert.Null(saved.RefreshTokenSecretId);
        }
    }

    [Fact]
    public async Task RefreshAuthorityLossAfterProviderWaitLeavesTheCredentialIndeterminate()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler(denyWriteOnCheck: 4);
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            var providerHandler = new MetadataProviderHandler();
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var service = CreateService(db, projects, environment, provider, secrets, secrets);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(
                    Actor(), Issuer, ActorId, ConnectionId,
                    new RemoteMcpOAuthRefreshRequest(
                        row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision),
                    CancellationToken.None));
            Assert.Equal("remote_mcp_owner_denied", error.Code);
            Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate, saved.State);
            Assert.Equal("version-1", saved.AccessTokenSecretVersion);
            Assert.Equal("version-1", saved.RefreshTokenSecretVersion);
            Assert.Equal(["refresh_token"], providerHandler.TokenGrantTypes);
            Assert.DoesNotContain("access-token-value", secrets.Values);
        }
    }

    [Fact]
    public async Task StatusRecoversAStaleRefreshLeaseAsIndeterminateAfterRestart()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            row.State = RemoteMcpOAuthConnectionState.RefreshInProgress;
            row.ConnectionRevision++;
            row.RefreshAttemptId = Guid.NewGuid();
            row.RefreshStartedAt = Now.AddMinutes(-3);
            await db.SaveChangesAsync();

            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var service = CreateService(db, projects, environment, provider, null);

            var status = await service.ReadStatusAsync(
                Actor(), Issuer, ActorId, ConnectionId, CancellationToken.None);

            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate.ToString(), status.State);
            Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate, saved.State);
            Assert.NotNull(saved.RefreshAttemptId);
            Assert.Equal(row.CredentialRevision, saved.CredentialRevision);
        }
    }

    [Fact]
    public async Task MissingWriterFailsBeforeAnyOwnerRequestOrEnvironmentLink()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            await SeedConnectionOwnerAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(Guid.NewGuid().ToString("N"));
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var service = CreateService(db, projects, environment, provider, secretWriter: null);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.PrepareConsentAsync(
                    Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None));

            Assert.Equal("remote_mcp_secret_store_unavailable", error.Code);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
            Assert.Empty(projectsHandler.Requests);
            Assert.Empty(environmentHandler.Requests);
        }
    }

    [Fact]
    public async Task RegistrationBindsTheAuthenticatedHumanProjectAndCurrentEnvironmentConfiguration()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            await SeedConnectionOwnerAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(Guid.NewGuid().ToString("N"));
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var service = CreateService(db, projects, environment, provider, new ControlledSecretWriter());

            var result = await service.RegisterConnectionAsync(
                Actor(), Issuer, ActorId, RegistrationRequest(), CancellationToken.None);

            Assert.Equal(ConnectionId, result.ConnectionId);
            Assert.Equal(ProjectId, result.ProjectId);
            Assert.Equal(1, result.ConnectionRevision);
            Assert.Equal(0, result.CredentialRevision);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected.ToString(), result.State);
            Assert.False(result.CurrentConfigurationMatches);
            var owner = await db.Users.AsNoTracking().SingleAsync(user => user.Subject == ActorId);
            var saved = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(row => row.ConnectionId == ConnectionId);
            Assert.Equal(owner.Id, saved.OwnerId);
            Assert.Equal(Issuer, saved.OwnerIssuer);
            Assert.Equal(ActorId, saved.OwnerActorId);
            Assert.Equal(TenantId, saved.TenantId);
            Assert.Equal(ProjectId, saved.ProjectId);
            Assert.Equal(1, saved.ConfigurationRevision);
            Assert.Equal(InitialConfigurationHash, saved.EnvironmentConfigurationHash);
            Assert.Equal("https://mcp.example.test/resource", saved.ResourceUri);
            Assert.Equal("https://issuer.example.test/", saved.IssuerUri);
            Assert.Equal("registered-client", saved.ClientId);
            Assert.Equal("https://web.example.test/auth/remote-mcp/oauth/callback", saved.RedirectUri);
            Assert.Equal("[\"tools.read\"]", saved.ScopesJson);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, saved.State);
            Assert.Equal(
                new RemoteMcpOAuthConnectionBinding(
                    owner.Id.ToString("D"), TenantId, ProjectId, ConnectionId,
                    1, InitialConfigurationHash, saved.IdentityBindingReference,
                    new Uri(saved.EndpointUri), new Uri(saved.ResourceUri), new Uri(saved.IssuerUri),
                    saved.ClientId, new Uri(saved.RedirectUri), saved.TransportProfile, ["tools.read"])
                    .BindingHash,
                saved.BindingHash);
        }
    }

    [Fact]
    public async Task RegistrationRejectsUnapprovedScopesAndStaleEnvironmentConfiguration()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            await SeedConnectionOwnerAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(Guid.NewGuid().ToString("N"));
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var service = CreateService(db, projects, environment, provider, new ControlledSecretWriter());

            var scopeError = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RegisterConnectionAsync(
                    Actor(), Issuer, ActorId, RegistrationRequest(["tools.write"]), CancellationToken.None));
            Assert.Equal("remote_mcp_oauth_scope_unapproved", scopeError.Code);
            Assert.Empty(projectsHandler.Requests);
            Assert.Empty(environmentHandler.Requests);

            environmentHandler.DriftCurrentConfiguration();
            var configError = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RegisterConnectionAsync(
                    Actor(), Issuer, ActorId, RegistrationRequest(), CancellationToken.None));
            Assert.Equal("remote_mcp_connection_revision_conflict", configError.Code);
            Assert.Empty(await db.RemoteMcpOAuthConnections.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task RegistrationRejectsAResourceOutsideTheProviderAllowList()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            await SeedConnectionOwnerAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(Guid.NewGuid().ToString("N"));
            var metadataHandler = new MetadataProviderHandler();
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(metadataHandler);
            var service = CreateService(
                db, projects, environment, provider, new ControlledSecretWriter(),
                approvedResources: ["https://other.example.test/resource"]);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RegisterConnectionAsync(
                    Actor(), Issuer, ActorId, RegistrationRequest(), CancellationToken.None));

            Assert.Equal("remote_mcp_oauth_resource_unapproved", error.Code);
            Assert.Empty(metadataHandler.Requests);
            Assert.Empty(await db.RemoteMcpOAuthConnections.AsNoTracking().ToListAsync());
        }
    }

    [Theory]
    [InlineData("client")]
    [InlineData("redirect")]
    [InlineData("scope")]
    public async Task ConsentPreparationRejectsCurrentProviderDriftBeforeMetadataOrSecretWrites(
        string drift)
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            var providerHandler = new MetadataProviderHandler();
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var (clientId, redirectUri, approvedScopes, expectedCode) = ProviderDrift(drift);
            var service = CreateService(
                db, projects, environment, provider, secrets,
                clientId: clientId, redirectUri: redirectUri, approvedScopes: approvedScopes);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.PrepareConsentAsync(
                    Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None));

            Assert.Equal(expectedCode, error.Code);
            Assert.Empty(providerHandler.Requests);
            Assert.Empty(providerHandler.TokenGrantTypes);
            Assert.Empty(secrets.Values);
            Assert.Empty(secrets.RedeemedReferences);
        }
    }

    [Theory]
    [InlineData("client")]
    [InlineData("redirect")]
    [InlineData("scope")]
    public async Task CallbackRejectsCurrentProviderDriftBeforeMetadataOrSecretRedemption(
        string drift)
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            var providerHandler = new MetadataProviderHandler();
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            var preparingService = CreateService(db, projects, environment, provider, secrets);
            var preparation = await preparingService.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);
            var metadataRequestCount = providerHandler.Requests.Count;
            var (clientId, redirectUri, approvedScopes, expectedCode) = ProviderDrift(drift);
            var currentService = CreateService(
                db, projects, environment, provider, secrets, secrets,
                clientId: clientId, redirectUri: redirectUri, approvedScopes: approvedScopes);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                currentService.CompleteCallbackAsync(
                    Actor(), Issuer, ActorId,
                    new RemoteMcpOAuthCallbackRequest(preparation.State, "authorization-code", null),
                    CancellationToken.None));

            Assert.Equal(expectedCode, error.Code);
            Assert.Equal(metadataRequestCount, providerHandler.Requests.Count);
            Assert.Empty(providerHandler.TokenGrantTypes);
            Assert.Empty(secrets.RedeemedReferences);
        }
    }

    [Theory]
    [InlineData("client")]
    [InlineData("redirect")]
    [InlineData("scope")]
    public async Task RefreshRejectsCurrentProviderDriftBeforeMetadataOrSecretRedemption(
        string drift)
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedAuthorizedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, linked: true);
            var providerHandler = new MetadataProviderHandler();
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(providerHandler);
            var secrets = new ControlledSecretWriter();
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-access",
                "version-1", "stored-access-token");
            secrets.AddSecret("remote-mcp-" + ConnectionId.ToString("N") + "-refresh",
                "version-1", "stored-refresh-token");
            var (clientId, redirectUri, approvedScopes, expectedCode) = ProviderDrift(drift);
            var service = CreateService(
                db, projects, environment, provider, secrets, secrets,
                clientId: clientId, redirectUri: redirectUri, approvedScopes: approvedScopes);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.RefreshAsync(
                    Actor(), Issuer, ActorId, ConnectionId,
                    new RemoteMcpOAuthRefreshRequest(
                        row.ConnectionRevision, row.CredentialRevision, row.ConfigurationRevision),
                    CancellationToken.None));

            Assert.Equal(expectedCode, error.Code);
            Assert.Empty(providerHandler.Requests);
            Assert.Empty(providerHandler.TokenGrantTypes);
            Assert.Empty(secrets.RedeemedReferences);
        }
    }

    [Fact]
    public async Task WriteProjectsRevocationAfterProtectedWriteDoesNotPersistPendingConsent()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler(denyWriteOnCheck: 3);
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.PrepareConsentAsync(
                    Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None));

            Assert.Equal("remote_mcp_owner_denied", error.Code);
            Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
            Assert.Single(secrets.Values);
            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Equal(1, savedConnection.ConfigurationRevision);
            Assert.Empty(await db.RemoteMcpOAuthConsents.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task ChangedCurrentConfigurationAfterLinkDoesNotWriteVerifierOrPersistConsent()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, driftAfterLink: true);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.PrepareConsentAsync(
                    Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None));

            Assert.Equal("remote_mcp_connection_revision_conflict", error.Code);
            Assert.Empty(secrets.Values);
            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Empty(await db.RemoteMcpOAuthConsents.AsNoTracking().ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRejectsAuthorityOrConfigurationDriftAfterDatabaseLockWait(
        bool driftConfiguration)
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            var row = await SeedConnectionAsync(db);
            var projectsHandler = new ProjectsOwnerHandler();
            var environmentHandler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var projects = new HttpClient(projectsHandler);
            using var environment = new HttpClient(environmentHandler);
            using var provider = new HttpClient(new MetadataProviderHandler());
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, projects, environment, provider, secrets);

            await using var lockConnection = await dataSource.OpenConnectionAsync();
            await using var lockTransaction = await lockConnection.BeginTransactionAsync();
            await using (var holdConnection = new NpgsqlCommand("""
                SELECT id FROM identity_broker.remote_mcp_oauth_connections
                WHERE connection_id = @connection FOR UPDATE
                """, lockConnection, lockTransaction))
            {
                holdConnection.Parameters.AddWithValue("connection", ConnectionId);
                Assert.NotNull(await holdConnection.ExecuteScalarAsync());
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var preparation = service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), timeout.Token);
            await WaitForConsentPublicationWaitAsync(dataSource, timeout.Token);
            if (driftConfiguration)
                environmentHandler.DriftCurrentConfiguration();
            else
                projectsHandler.DenyWriteOnCheck = 4;
            await lockTransaction.CommitAsync(timeout.Token);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() => preparation);
            Assert.Equal(
                driftConfiguration
                    ? "remote_mcp_connection_revision_conflict"
                    : "remote_mcp_owner_denied",
                error.Code);
            Assert.Single(secrets.Values);
            var savedConnection = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.ConnectionId == ConnectionId);
            Assert.Equal(RemoteMcpOAuthConnectionState.NotConnected, savedConnection.State);
            Assert.Equal(1, savedConnection.ConfigurationRevision);
            Assert.Empty(await db.RemoteMcpOAuthConsents.AsNoTracking().ToListAsync());
        }
    }

    private async Task<(IdentityBrokerDbContext Db, NpgsqlDataSource DataSource)> CreateDatabaseAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(connectionString);
        return (CreateDatabaseContext(dataSource), dataSource);
    }

    private static IdentityBrokerDbContext CreateDatabaseContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        return new IdentityBrokerDbContext(options);
    }

    private static async Task<RemoteMcpOAuthConnectionRecord> SeedConnectionAsync(
        IdentityBrokerDbContext db)
    {
        await SeedConnectionOwnerAsync(db);
        var connection = new RemoteMcpOAuthConnectionRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = (await db.Users.AsNoTracking().SingleAsync(user => user.Subject == ActorId)).Id,
            OwnerIssuer = Issuer,
            OwnerActorId = ActorId,
            TenantId = TenantId,
            ProjectId = ProjectId,
            ConnectionId = ConnectionId,
            ConfigurationRevision = 1,
            EnvironmentConfigurationHash = InitialConfigurationHash,
            IdentityBindingReference = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff").ToString("N"),
            EndpointUri = "https://mcp.example.test/",
            ResourceUri = "https://mcp.example.test/resource",
            IssuerUri = "https://issuer.example.test/",
            ClientId = "registered-client",
            RedirectUri = "https://web.example.test/auth/remote-mcp/oauth/callback",
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ScopesJson = "[\"tools.read\"]",
            BindingHash = new string('c', 64),
            ConnectionRevision = 1,
            CredentialRevision = 0,
            State = RemoteMcpOAuthConnectionState.NotConnected,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.RemoteMcpOAuthConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<RemoteMcpOAuthConnectionRecord> SeedAuthorizedConnectionAsync(
        IdentityBrokerDbContext db)
    {
        var row = await SeedConnectionAsync(db);
        var binding = new RemoteMcpOAuthConnectionBinding(
            row.OwnerId.ToString("D"),
            TenantId,
            ProjectId,
            ConnectionId,
            2,
            FinalConfigurationHash,
            row.IdentityBindingReference,
            new Uri("https://mcp.example.test/"),
            new Uri("https://mcp.example.test/resource"),
            new Uri("https://issuer.example.test/"),
            "registered-client",
            new Uri("https://web.example.test/auth/remote-mcp/oauth/callback"),
            RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ["tools.read"]);
        row.ConfigurationRevision = 2;
        row.EnvironmentConfigurationHash = FinalConfigurationHash;
        row.BindingHash = binding.BindingHash;
        row.ConnectionRevision = 4;
        row.CredentialRevision = 2;
        row.State = RemoteMcpOAuthConnectionState.Authorized;
        row.AccessTokenSecretId = "remote-mcp-" + ConnectionId.ToString("N") + "-access";
        row.AccessTokenSecretVersion = "version-1";
        row.AccessTokenExpiresAt = Now.AddMinutes(10);
        row.RefreshTokenSecretId = "remote-mcp-" + ConnectionId.ToString("N") + "-refresh";
        row.RefreshTokenSecretVersion = "version-1";
        row.RefreshTokenExpiresAt = Now.AddDays(1);
        await db.SaveChangesAsync();
        return row;
    }

    private static async Task SeedConnectionOwnerAsync(IdentityBrokerDbContext db)
    {
        var now = Now;
        db.Users.Add(new BrokerUser
        {
            Id = Guid.NewGuid(),
            Issuer = Issuer,
            Subject = ActorId,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static RemoteMcpOAuthManagementService CreateService(
        IdentityBrokerDbContext db,
        HttpClient projects,
        HttpClient environment,
        HttpClient provider,
        ISecretVersionWriter? secretWriter,
        ISecretRedemption? secretRedemption = null,
        IReadOnlyList<string>? approvedResources = null,
        string? clientId = null,
        string? redirectUri = null,
        IReadOnlyList<string>? approvedScopes = null) =>
        new(db, new RemoteMcpOAuthOptions
        {
            ProjectsOwnerAddress = ProjectsOwnerAddress,
            EnvironmentOwnerAddress = EnvironmentOwnerAddress,
            Providers =
            [
                new RemoteMcpOAuthProviderOptions
                {
                    IssuerUri = "https://issuer.example.test/",
                    ApprovedResources = approvedResources ?? ["https://mcp.example.test/resource"],
                    ClientId = clientId ?? "registered-client",
                    RedirectUri = redirectUri ??
                        "https://web.example.test/auth/remote-mcp/oauth/callback",
                    ApprovedOAuthEndpoints =
                    [
                        "https://issuer.example.test/authorize",
                        "https://issuer.example.test/token"
                    ],
                    ApprovedScopes = approvedScopes ?? ["tools.read"]
                }
            ]
        }, projects, environment, provider, new FrozenTimeProvider(Now), secretWriter, secretRedemption);

    private static (string? ClientId, string? RedirectUri, string[]? ApprovedScopes, string ExpectedCode)
        ProviderDrift(string drift) =>
        drift switch
        {
            "client" => ("replaced-client", null, null, "remote_mcp_oauth_provider_unapproved"),
            "redirect" => (null, "https://web.example.test/new-callback",
                null, "remote_mcp_oauth_provider_unapproved"),
            "scope" => (null, null, ["tools.write"], "remote_mcp_oauth_scope_unapproved"),
            _ => throw new ArgumentOutOfRangeException(nameof(drift))
        };

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static async Task WaitForConsentPublicationWaitAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var observer = await dataSource.OpenConnectionAsync(cancellationToken);
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await using var wait = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND query LIKE 'UPDATE%remote_mcp_oauth_connections%'
                )
                """, observer);
            if ((bool)(await wait.ExecuteScalarAsync(cancellationToken))!)
                return;
            await Task.Delay(25, cancellationToken);
        }
        Assert.Fail("Consent preparation did not wait on its PostgreSQL connection row lock.");
    }

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", Now.AddHours(1), new FrozenTimeProvider(Now)), TenantId);

    private static RemoteMcpOAuthConsentPreparationRequest Request() =>
        new(1, 0, 1, InitialConfigurationHash);

    private static RemoteMcpOAuthConnectionRegistrationRequest RegistrationRequest(
        string[]? scopes = null) =>
        new(ProjectId, ConnectionId, 1, InitialConfigurationHash,
            "https://issuer.example.test/", scopes ?? ["tools.read"]);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value))).ToLowerInvariant();

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlledSecretWriter : ISecretVersionWriter, ISecretRedemption
    {
        private readonly Dictionary<(string Id, string Version), string> _values = [];

        public List<string> SecretIds { get; } = [];
        public List<string> Values { get; } = [];
        public List<SecretRef> References { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<SecretRef> RedeemedReferences { get; } = new();
        public Exception? WriteFailure { get; init; }

        public void AddSecret(string id, string version, string value)
        {
            _values.Add((id, version), value);
            SecretIds.Add(id);
            Values.Add(value);
            References.Add(new SecretRef(id, version));
        }

        public Task<SecretRef> WriteVersionAsync(
            string secretId, SecretCredential credential, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure is not null && secretId.EndsWith("-access", StringComparison.Ordinal))
                throw WriteFailure;
            var version = $"version-{References.Count(reference => reference.Id == secretId) + 1}";
            var value = credential.GetValue();
            SecretIds.Add(secretId);
            Values.Add(value);
            var reference = new SecretRef(secretId, version);
            References.Add(reference);
            _values.Add((reference.Id, reference.Version), value);
            return Task.FromResult(reference);
        }

        public Task<SecretCredential> RedeemAsync(
            SecretRedemptionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedeemedReferences.Enqueue(request.Secret);
            Assert.Contains(request.Purpose,
                new[] { "remote-mcp-oauth-verifier", "remote-mcp-oauth-refresh-token" });
            var value = _values.GetValueOrDefault((request.Secret.Id, request.Secret.Version));
            Assert.NotNull(value);
            return Task.FromResult(new SecretCredential(
                value, Now.AddHours(1), new FrozenTimeProvider(Now)));
        }
    }

    private static AzureKeyVaultSecretException CreateSecretStoreFailure()
    {
        var constructor = typeof(AzureKeyVaultSecretException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(AzureKeyVaultSecretFailure), typeof(int)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (AzureKeyVaultSecretException)constructor.Invoke(
            [AzureKeyVaultSecretFailure.ServiceFailure, 503]);
    }

    private sealed class ProjectsOwnerHandler(
        int denyWriteOnCheck = 0) : HttpMessageHandler
    {
        private int _authorizationChecks;

        public List<HttpRequestMessage> Requests { get; } = [];
        public int DenyWriteOnCheck { get; set; } = denyWriteOnCheck;
        public int AuthorizationRequests => _authorizationChecks;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal("projects.test", request.RequestUri!.Host);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues(
                ProjectAuthorizationContextContract.TenantSelectorHeader)));

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/authorization/context")
            {
                _authorizationChecks++;
                return Task.FromResult(Json(request, AuthorizationContext(
                    _authorizationChecks != DenyWriteOnCheck)));
            }
            if (request.Method == HttpMethod.Get && path == "/api/projects/project-1")
                return Task.FromResult(Json(request,
                    new { projectId = ProjectId, revision = 1, state = "active" }));
            throw new Xunit.Sdk.XunitException($"Unexpected Projects request: {request.Method} {path}");
        }

        private static ProjectAuthorizationContextResponse AuthorizationContext(bool grantWrite) =>
            new(
                ProjectAuthorizationContextContract.CurrentVersion,
                Issuer,
                ActorId,
                TenantId,
                1,
                null,
                null,
                ImmutableArray.Create(new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    ProjectId,
                    grantWrite
                        ? ImmutableArray.Create(
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1),
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.WriteProjects, 1))
                        : ImmutableArray.Create(
                            new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1)))));
    }

    private static HttpResponseMessage Json<T>(HttpRequestMessage request, T body) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = JsonContent.Create(body),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    private sealed class EnvironmentOwnerHandler(
        string identityBindingReference,
        bool driftAfterLink = false,
        bool linked = false) : HttpMessageHandler
    {
        private bool _linked = linked;
        private volatile bool _configurationDrift;

        public List<HttpRequestMessage> Requests { get; } = [];
        public int LinkRequests { get; private set; }

        public void DriftCurrentConfiguration() => _configurationDrift = true;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal("environment.test", request.RequestUri!.Host);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues(
                ProjectAuthorizationContextContract.TenantSelectorHeader)));

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path == $"/api/projects/{ProjectId}/remote-mcp/connections/{ConnectionId:D}")
                return Json(request, Snapshot());
            if (request.Method == HttpMethod.Get &&
                path.StartsWith($"/api/projects/{ProjectId}/remote-mcp/connections/{ConnectionId:D}/configurations/",
                    StringComparison.Ordinal))
                return Json(request, Configuration());
            if (request.Method == HttpMethod.Put &&
                path == $"/api/projects/{ProjectId}/remote-mcp/connections/{ConnectionId:D}/identity-binding")
            {
                LinkRequests++;
                var input = await request.Content!.ReadFromJsonAsync<LinkRemoteMcpIdentityBindingRequest>(
                    cancellationToken: cancellationToken);
                Assert.NotNull(input);
                Assert.Equal(InitialConfigurationHash, input.ExpectedConfigurationSha256);
                Assert.Equal(1, input.ExpectedConfigurationRevision);
                Assert.Equal(identityBindingReference, input.IdentityBindingReference);
                Assert.Equal(identityBindingReference, input.OperationId);
                _linked = true;
                return Json(request, new RemoteMcpIdentityBindingReceipt(
                    ProjectId, ConnectionId, input.OperationId, 2,
                    FinalConfigurationHash, identityBindingReference));
            }
            throw new Xunit.Sdk.XunitException($"Unexpected Environment request: {request.Method} {path}");
        }

        private object Snapshot()
        {
            var revision = _linked ? 2 : 1;
            var connection = new
            {
                projectId = ProjectId,
                connectionId = ConnectionId
            };
            return new
            {
                head = new
                {
                    connection,
                    rowRevision = revision,
                    currentConfigurationRevision = revision,
                    currentDiscoveryRevision = (long?)null,
                    state = "draft"
                },
                configuration = Configuration()
            };
        }

        private object Configuration()
        {
            var revision = _linked ? 2 : 1;
            return new
            {
                connection = new
                {
                    projectId = ProjectId,
                    connectionId = ConnectionId
                },
                configurationRevision = revision,
                configurationSha256 = _configurationDrift
                    ? ChangedConfigurationHash
                    : _linked
                        ? driftAfterLink ? ChangedConfigurationHash : FinalConfigurationHash
                        : InitialConfigurationHash,
                endpointUri = "https://mcp.example.test/",
                resourceUri = "https://mcp.example.test/resource",
                authenticationMode = "delegatedOAuth",
                transportProfile = "streamableHttp20250618",
                identityBindingReference = _linked ? identityBindingReference : null
            };
        }

        private static HttpResponseMessage Json<T>(HttpRequestMessage request, T body) =>
            new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(body),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            };
    }

    private sealed class MetadataProviderHandler(
        bool failTokenExchange = false,
        bool failRefreshTokenExchange = false,
        bool rejectRefreshTokenExchange = false,
        bool synchronizeMetadata = false) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _metadataGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _authorizationServerMetadataResponses;

        public List<Uri> Requests { get; } = [];
        public List<string> TokenGrantTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
                Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var formBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var fields = formBody.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .ToDictionary(
                        parts => Uri.UnescapeDataString(parts[0]),
                        parts => Uri.UnescapeDataString(parts[1]),
                        StringComparer.Ordinal);
                var grantType = fields["grant_type"];
                lock (_sync)
                    TokenGrantTypes.Add(grantType);
                if (grantType == "authorization_code" && failTokenExchange ||
                    grantType == "refresh_token" && failRefreshTokenExchange)
                    throw new TaskCanceledException("Simulated provider timeout.");
                if (grantType == "refresh_token" && rejectRefreshTokenExchange)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        RequestMessage = request,
                        Content = JsonContent.Create(new { error = "invalid_grant" })
                    };
                }
                return Json(request, new
                {
                    access_token = "access-token-value",
                    token_type = "Bearer",
                    expires_in = 3600,
                    refresh_token = "refresh-token-value",
                    refresh_token_expires_in = 86400,
                    scope = "tools.read"
                });
            }

            if (synchronizeMetadata &&
                request.RequestUri.AbsolutePath == "/.well-known/oauth-authorization-server" &&
                Interlocked.Increment(ref _authorizationServerMetadataResponses) == 2)
                _metadataGate.TrySetResult(true);
            if (synchronizeMetadata &&
                request.RequestUri.AbsolutePath == "/.well-known/oauth-authorization-server")
                await _metadataGate.Task.WaitAsync(cancellationToken);

            object body = request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/oauth-protected-resource/resource" => new
                {
                    resource = "https://mcp.example.test/resource",
                    authorization_servers = new[] { "https://issuer.example.test/" },
                    scopes_supported = new[] { "tools.read" }
                },
                "/.well-known/oauth-authorization-server" => new Dictionary<string, object>
                {
                    ["issuer"] = "https://issuer.example.test/",
                    ["authorization_endpoint"] = "https://issuer.example.test/authorize",
                    ["token_endpoint"] = "https://issuer.example.test/token",
                    ["response_types_supported"] = new[] { "code" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                    ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" }
                },
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected OAuth metadata request: {request.RequestUri}")
            };
            return Json(request, body);
        }
    }
}
