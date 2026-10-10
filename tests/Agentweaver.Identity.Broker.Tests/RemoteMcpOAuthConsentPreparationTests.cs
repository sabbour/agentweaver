using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
            var handler = new EnvironmentOwnerHandler(row.IdentityBindingReference);
            using var http = new HttpClient(handler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, http, secrets);

            var result = await service.PrepareConsentAsync(
                Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None);

            Assert.Equal(43, result.State.Length);
            Assert.Equal(43, result.PkceChallenge.Length);
            Assert.Equal(2, result.ConfigurationRevision);
            Assert.Equal(FinalConfigurationHash, result.ConfigurationSha256);
            Assert.Equal(2, result.ConnectionRevision);
            Assert.Equal("remote-mcp-" + ConnectionId.ToString("N") + "-verifier",
                Assert.Single(secrets.SecretIds));
            Assert.Equal(1, handler.LinkRequests);
            Assert.Equal(3, handler.AuthorizationRequests);
            Assert.All(handler.Requests, request =>
            {
                Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
                Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues(
                    ProjectAuthorizationContextContract.TenantSelectorHeader)));
            });
            Assert.DoesNotContain(handler.Requests, request =>
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
    public async Task MissingWriterFailsBeforeAnyOwnerRequestOrEnvironmentLink()
    {
        var (db, dataSource) = await CreateDatabaseAsync();
        await using (dataSource)
        await using (db)
        {
            using var http = new HttpClient(new EnvironmentOwnerHandler(
                Guid.NewGuid().ToString("N")));
            var service = CreateService(db, http, secretWriter: null);

            var error = await Assert.ThrowsAsync<RemoteMcpOAuthManagementException>(() =>
                service.PrepareConsentAsync(
                    Actor(), Issuer, ActorId, ConnectionId, Request(), CancellationToken.None));

            Assert.Equal("remote_mcp_secret_store_unavailable", error.Code);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
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
            var handler = new EnvironmentOwnerHandler(row.IdentityBindingReference, denyWriteOnCheck: 3);
            using var http = new HttpClient(handler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, http, secrets);

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
            var handler = new EnvironmentOwnerHandler(
                row.IdentityBindingReference, driftAfterLink: true);
            using var http = new HttpClient(handler);
            var secrets = new ControlledSecretWriter();
            var service = CreateService(db, http, secrets);

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

    private async Task<(IdentityBrokerDbContext Db, NpgsqlDataSource DataSource)> CreateDatabaseAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .Options;
        return (new IdentityBrokerDbContext(options), dataSource);
    }

    private static async Task<RemoteMcpOAuthConnectionRecord> SeedConnectionAsync(
        IdentityBrokerDbContext db)
    {
        var now = Now;
        var ownerId = Guid.NewGuid();
        var connection = new RemoteMcpOAuthConnectionRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
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
            RedirectUri = "https://identity.example.test/oauth/callback",
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ScopesJson = "[\"tools.read\"]",
            BindingHash = new string('c', 64),
            ConnectionRevision = 1,
            CredentialRevision = 0,
            State = RemoteMcpOAuthConnectionState.NotConnected,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(new BrokerUser
        {
            Id = ownerId,
            Issuer = Issuer,
            Subject = ActorId,
            CreatedAt = now
        });
        db.RemoteMcpOAuthConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static RemoteMcpOAuthManagementService CreateService(
        IdentityBrokerDbContext db,
        HttpClient http,
        ISecretVersionWriter? secretWriter) =>
        new(db, new RemoteMcpOAuthOptions { ProjectsOwnerAddress = "https://environment.test/" },
            http, new FrozenTimeProvider(Now), secretWriter);

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", Now.AddHours(1), new FrozenTimeProvider(Now)), TenantId);

    private static RemoteMcpOAuthConsentPreparationRequest Request() =>
        new(1, 0, 1, InitialConfigurationHash);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value))).ToLowerInvariant();

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlledSecretWriter : ISecretVersionWriter
    {
        public List<string> SecretIds { get; } = [];
        public List<string> Values { get; } = [];
        public List<SecretRef> References { get; } = [];

        public Task<SecretRef> WriteVersionAsync(
            string secretId, SecretCredential credential, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretIds.Add(secretId);
            Values.Add(credential.GetValue());
            var reference = new SecretRef(secretId, "version-1");
            References.Add(reference);
            return Task.FromResult(reference);
        }
    }

    private sealed class EnvironmentOwnerHandler(
        string identityBindingReference,
        bool grantWrite = true,
        int denyWriteOnCheck = 0,
        bool driftAfterLink = false) : HttpMessageHandler
    {
        private bool _linked;
        private int _authorizationChecks;

        public List<HttpRequestMessage> Requests { get; } = [];
        public int LinkRequests { get; private set; }
        public int AuthorizationRequests => _authorizationChecks;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues(
                ProjectAuthorizationContextContract.TenantSelectorHeader)));

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/authorization/context")
            {
                _authorizationChecks++;
                var allow = grantWrite && _authorizationChecks != denyWriteOnCheck;
                return Json(request, AuthorizationContext(allow));
            }
            if (request.Method == HttpMethod.Get && path == "/api/projects/project-1")
                return Json(request, new { projectId = ProjectId, revision = 1, state = "active" });
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
                Assert.Equal(Guid.ParseExact(identityBindingReference, "N"), input.OperationId);
                _linked = true;
                return Json(request, new RemoteMcpIdentityBindingReceipt(
                    ProjectId, ConnectionId, input.OperationId, 2,
                    FinalConfigurationHash, identityBindingReference));
            }
            throw new Xunit.Sdk.XunitException($"Unexpected owner request: {request.Method} {path}");
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
                    state = "active"
                },
                configuration = Configuration()
            };
        }

        private object Configuration()
        {
            var revision = _linked ? 2 : 1;
            return new
            {
                configurationRevision = revision,
                configurationSha256 = _linked
                    ? driftAfterLink ? ChangedConfigurationHash : FinalConfigurationHash
                    : InitialConfigurationHash,
                endpointUri = "https://mcp.example.test/",
                resourceUri = "https://mcp.example.test/resource",
                authenticationMode = "delegatedOAuth",
                transportProfile = "streamableHttp20250618",
                identityBindingReference = _linked ? identityBindingReference : null
            };
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
                    ImmutableArray.Create(new ProjectAuthorizationPermissionGrant(
                        grantWrite ? ProjectAuthorizationPermission.WriteProjects :
                            ProjectAuthorizationPermission.ReadProjects,
                        1)))));

        private static HttpResponseMessage Json<T>(HttpRequestMessage request, T body) =>
            new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(body),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            };
    }
}
