using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentRemoteMcpConnectionPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    private const string RuntimeRole = "aw_remote_mcp_runtime";
    private const string RuntimePassword = "remote-mcp-test-only";

    [Fact]
    public async Task ConnectionMigrationRollsBackAndReappliesOnPostgres()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema))
            .Options;

        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>()
                .MigrateAsync("20261006170000_SandboxLeases");
        Assert.False(await RemoteMcpTablesExistAsync(fixture.DataSource));

        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>()
                .MigrateAsync("20261010135714_AddRemoteMcpConnections");
        Assert.True(await RemoteMcpTablesExistAsync(fixture.DataSource));
        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>().MigrateAsync();
        await EnvironmentMigrator.VerifyMigrationsAppliedAsync(fixture.DataSource, options);
    }

    [Fact]
    public void CurrentConnectionModelMatchesItsMigrationSnapshot()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", EnvironmentDbContext.Schema))
            .Options;
        using var context = new EnvironmentDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task RuntimeRoleCanWriteOnlyTheHeadAndAppendOnlyConnectionRows()
    {
        await ExecuteSqlAsync(fixture.DataSource, $"""
            CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}';
            GRANT USAGE ON SCHEMA environment TO {RuntimeRole};
            GRANT SELECT, INSERT, UPDATE ON environment.owners TO {RuntimeRole};
            GRANT SELECT, INSERT ON environment.lifecycle_operations TO {RuntimeRole};
            GRANT SELECT, INSERT, UPDATE ON environment.owner_effects TO {RuntimeRole};
            GRANT SELECT, INSERT, UPDATE ON environment.sandbox_leases TO {RuntimeRole};
            GRANT SELECT, INSERT, UPDATE ON environment.remote_mcp_connections TO {RuntimeRole};
            GRANT SELECT, INSERT ON environment.remote_mcp_connection_configurations TO {RuntimeRole};
            GRANT SELECT, INSERT ON environment.remote_mcp_catalog_snapshots TO {RuntimeRole};
            GRANT SELECT, INSERT ON environment.remote_mcp_connection_idempotency TO {RuntimeRole};
            """);

        try
        {
            await using var runtimeDataSource = fixture.CreateDataSource(
                "remote-mcp-runtime-role",
                RuntimeRole,
                RuntimePassword);
            await EnvironmentMigrator.VerifyRuntimeAuthorityAsync(runtimeDataSource);

            var forbiddenUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteSqlAsync(
                    runtimeDataSource,
                    "UPDATE environment.remote_mcp_connection_configurations SET display_name = 'changed'"));
            Assert.Equal("42501", forbiddenUpdate.SqlState);
            var forbiddenDelete = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteSqlAsync(runtimeDataSource, "DELETE FROM environment.remote_mcp_connections"));
            Assert.Equal("42501", forbiddenDelete.SqlState);
            var forbiddenTruncate = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteSqlAsync(runtimeDataSource, "TRUNCATE environment.remote_mcp_connection_idempotency"));
            Assert.Equal("42501", forbiddenTruncate.SqlState);

            await ExecuteSqlAsync(
                fixture.DataSource,
                $"GRANT UPDATE ON environment.remote_mcp_connection_configurations TO {RuntimeRole}");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnvironmentMigrator.VerifyRuntimeAuthorityAsync(runtimeDataSource));
        }
        finally
        {
            await ExecuteSqlAsync(fixture.DataSource, $"DROP OWNED BY {RuntimeRole}");
            await ExecuteSqlAsync(fixture.DataSource, $"DROP ROLE {RuntimeRole}");
        }
    }

    [Fact]
    public async Task ChangedCurrentAuthorityRollsBackConnectionAndIdempotencyRows()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var projects = new SequencedProjectsConfigClient(call =>
            AuthorizationContext(projectId, membershipRevision: call == 3 ? 2 : 1));
        var store = new RemoteMcpConnectionStore(fixture.DataSource, projects, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.CreateAsync(
                new CurrentCallerRequest("test-bearer"),
                projectId,
                Draft(),
                "create-rollback",
                CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal((0L, 0L, 0L), await CountOwnerRowsAsync(fixture.DataSource, projectId));
    }

    [Fact]
    public async Task IdempotencyReplaySurvivesStoreAndDataSourceRestart()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        const string idempotencyKey = "create-before-restart";
        RemoteMcpConnectionMutationResult first;
        await using (var dataSource = fixture.CreateDataSource("remote-mcp-before-restart"))
        {
            var store = new RemoteMcpConnectionStore(
                dataSource,
                new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
                TimeProvider.System);
            first = await store.CreateAsync(
                caller, projectId, Draft(), idempotencyKey, CancellationToken.None);
        }

        await using (var dataSource = fixture.CreateDataSource("remote-mcp-after-restart"))
        {
            var store = new RemoteMcpConnectionStore(
                dataSource,
                new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
                TimeProvider.System);
            var replay = await store.CreateAsync(
                caller, projectId, Draft(), idempotencyKey, CancellationToken.None);

            Assert.True(replay.Replayed);
            Assert.Equal(first.Snapshot.Head.Connection.ConnectionId, replay.Snapshot.Head.Connection.ConnectionId);
            Assert.Equal(first.Snapshot.Head.RowRevision, replay.Snapshot.Head.RowRevision);
            Assert.Equal(first.Snapshot.Configuration.ConfigurationSha256, replay.Snapshot.Configuration.ConfigurationSha256);
            Assert.Equal((1L, 1L, 1L), await CountOwnerRowsAsync(dataSource, projectId));

            var conflict = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
                store.CreateAsync(
                    caller,
                    projectId,
                    Draft() with { DisplayName = "Different" },
                    idempotencyKey,
                    CancellationToken.None));
            Assert.Equal("idempotency_conflict", conflict.Code);
        }
    }

    [Fact]
    public async Task IdentityBindingCreatesImmutableRevisionAndReplaysFinalPins()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var store = new RemoteMcpConnectionStore(
            fixture.DataSource,
            new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
            TimeProvider.System);
        var created = await store.CreateAsync(
            caller, projectId, OAuthDraft(), "create-before-binding", CancellationToken.None);
        var enabled = await store.SetStateAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            created.Snapshot.Head.RowRevision,
            RemoteMcpConnectionState.Enabled,
            "enable-before-binding",
            CancellationToken.None);
        var parsedCatalog = RemoteMcpMetadataParser.ParseToolsList(Encoding.UTF8.GetBytes(
            """{"result":{"tools":[{"name":"forecast","inputSchema":{"type":"object"}}]}}"""));
        var appliedPolicy = new RemoteMcpAppliedNetworkPolicyReference(
            "policy-reference",
            new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity("tenant-test", projectId, "run-test", "environment-test"),
                lifecycleGeneration: 1),
            appliedGeneration: 1,
            egressIntentSha256: new string('a', 64));
        var pinned = await store.SaveDiscoveryAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            enabled.Snapshot.Head.RowRevision,
            parsedCatalog,
            registryMetadataSha256: null,
            appliedNetworkPolicy: appliedPolicy,
            idempotencyKey: "save-catalog-before-binding",
            cancellationToken: CancellationToken.None);
        Assert.NotNull(pinned.Snapshot.Catalog);
        Assert.Equal(1, await CountCatalogRowsAsync(fixture.DataSource, projectId));
        var identityReference = Guid.NewGuid().ToString("N");

        var bound = await store.BindIdentityAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            created.Snapshot.Configuration.ConfigurationRevision,
            created.Snapshot.Configuration.ConfigurationSha256,
            identityReference,
            "bind-identity",
            CancellationToken.None);

        Assert.Equal(projectId, bound.ProjectId);
        Assert.Equal(created.Snapshot.Head.Connection.ConnectionId, bound.ConnectionId);
        Assert.Equal("bind-identity", bound.OperationId);
        Assert.Equal(2, bound.FinalConfigurationRevision);
        Assert.Equal(identityReference, bound.IdentityBindingReference);
        Assert.NotEqual(
            created.Snapshot.Configuration.ConfigurationSha256,
            bound.FinalConfigurationSha256);
        var current = await store.GetAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(bound.FinalConfigurationRevision, current.Configuration.ConfigurationRevision);
        Assert.Equal(bound.FinalConfigurationSha256, current.Configuration.ConfigurationSha256);
        Assert.Equal(RemoteMcpConnectionState.Draft, current.Head.State);
        Assert.Null(current.Head.CurrentDiscoveryRevision);
        Assert.Null(current.Catalog);

        var original = await store.GetConfigurationRevisionAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            created.Snapshot.Configuration.ConfigurationRevision,
            CancellationToken.None);
        Assert.NotNull(original);
        Assert.Null(original.IdentityBindingReference);

        var replay = await store.BindIdentityAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            created.Snapshot.Configuration.ConfigurationRevision,
            created.Snapshot.Configuration.ConfigurationSha256,
            identityReference,
            "bind-identity",
            CancellationToken.None);
        Assert.Equal(bound, replay);
        Assert.Equal((1L, 2L, 4L), await CountOwnerRowsAsync(fixture.DataSource, projectId));
        Assert.Equal(1, await CountCatalogRowsAsync(fixture.DataSource, projectId));
    }

    [Fact]
    public async Task IdentityBindingRejectsStalePinsInvalidReferencesAndNonOAuthConnections()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var store = new RemoteMcpConnectionStore(
            fixture.DataSource,
            new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
            TimeProvider.System);
        var created = await store.CreateAsync(
            caller, projectId, OAuthDraft(), "create-oauth", CancellationToken.None);
        var connectionId = created.Snapshot.Head.Connection.ConnectionId;
        var identityReference = Guid.NewGuid().ToString("N");

        var staleHash = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                created.Snapshot.Configuration.ConfigurationRevision,
                new string('0', 64),
                identityReference,
                "bind-stale-hash",
                CancellationToken.None));
        Assert.Equal("configuration_changed", staleHash.Code);

        var staleRevision = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                created.Snapshot.Configuration.ConfigurationRevision + 1,
                created.Snapshot.Configuration.ConfigurationSha256,
                identityReference,
                "bind-stale-revision",
                CancellationToken.None));
        Assert.Equal("configuration_changed", staleRevision.Code);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                created.Snapshot.Configuration.ConfigurationRevision,
                created.Snapshot.Configuration.ConfigurationSha256,
                "not-a-uuid",
                "bind-invalid-reference",
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                created.Snapshot.Configuration.ConfigurationRevision,
                created.Snapshot.Configuration.ConfigurationSha256,
                Guid.Empty.ToString("N"),
                "bind-empty-reference",
                CancellationToken.None));

        var unauthenticatedConnection = await store.CreateAsync(
            caller, projectId, Draft(), "create-no-auth", CancellationToken.None);
        var wrongMode = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                unauthenticatedConnection.Snapshot.Head.Connection.ConnectionId,
                unauthenticatedConnection.Snapshot.Configuration.ConfigurationRevision,
                unauthenticatedConnection.Snapshot.Configuration.ConfigurationSha256,
                identityReference,
                "bind-no-auth",
                CancellationToken.None));
        Assert.Equal("identity_binding_requires_oauth", wrongMode.Code);
    }

    [Fact]
    public async Task IdentityBindingReplaysOnlyForCurrentAuthorizedActor()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var projects = new MutableProjectsConfigClient(projectId);
        var store = new RemoteMcpConnectionStore(fixture.DataSource, projects, TimeProvider.System);
        var caller = new CurrentCallerRequest("test-bearer");
        var created = await store.CreateAsync(
            caller, projectId, OAuthDraft(), "create-actor-bound", CancellationToken.None);
        var connectionId = created.Snapshot.Head.Connection.ConnectionId;
        var expectedRevision = created.Snapshot.Configuration.ConfigurationRevision;
        var expectedHash = created.Snapshot.Configuration.ConfigurationSha256;
        var identityReference = Guid.NewGuid().ToString("N");

        var bound = await store.BindIdentityAsync(
            caller,
            projectId,
            connectionId,
            expectedRevision,
            expectedHash,
            identityReference,
            "actor-bound-key",
            CancellationToken.None);
        Assert.Equal("actor-bound-key", bound.OperationId);

        projects.ActorId = "other-actor";
        var otherActor = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                expectedRevision,
                expectedHash,
                identityReference,
                "actor-bound-key",
                CancellationToken.None));
        Assert.Equal("idempotency_conflict", otherActor.Code);

        projects.HasWriteAuthority = false;
        var revokedActor = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                connectionId,
                expectedRevision,
                expectedHash,
                identityReference,
                "actor-bound-key",
                CancellationToken.None));
        Assert.Equal("project_write_not_authorized", revokedActor.Code);
    }

    [Fact]
    public async Task ChangedCurrentAuthorityRollsBackIdentityBindingAndIdempotencyRows()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var initialStore = new RemoteMcpConnectionStore(
            fixture.DataSource,
            new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
            TimeProvider.System);
        var created = await initialStore.CreateAsync(
            caller, projectId, OAuthDraft(), "create-before-authority-change", CancellationToken.None);
        var changedProjects = new SequencedProjectsConfigClient(call =>
            AuthorizationContext(projectId, membershipRevision: call == 4 ? 2 : 1));
        var store = new RemoteMcpConnectionStore(fixture.DataSource, changedProjects, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                created.Snapshot.Head.Connection.ConnectionId,
                created.Snapshot.Configuration.ConfigurationRevision,
                created.Snapshot.Configuration.ConfigurationSha256,
                Guid.NewGuid().ToString("N"),
                "bind-authority-change",
                CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal((1L, 1L, 1L), await CountOwnerRowsAsync(fixture.DataSource, projectId));
        var current = await initialStore.GetAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(1, current.Configuration.ConfigurationRevision);
        Assert.Null(current.Configuration.IdentityBindingReference);
    }

    [Fact]
    public async Task IdentityBindingRejectsAuthorityChangedBeforeMutationStarts()
    {
        var projectId = "project-" + Guid.NewGuid().ToString("N");
        var caller = new CurrentCallerRequest("test-bearer");
        var initialStore = new RemoteMcpConnectionStore(
            fixture.DataSource,
            new SequencedProjectsConfigClient(_ => AuthorizationContext(projectId)),
            TimeProvider.System);
        var created = await initialStore.CreateAsync(
            caller, projectId, OAuthDraft(), "create-before-initial-authority-change", CancellationToken.None);
        var changedProjects = new SequencedProjectsConfigClient(call =>
            AuthorizationContext(projectId, actorId: call == 1 ? "actor-test" : "other-actor"));
        var store = new RemoteMcpConnectionStore(fixture.DataSource, changedProjects, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<RemoteMcpConnectionException>(() =>
            store.BindIdentityAsync(
                caller,
                projectId,
                created.Snapshot.Head.Connection.ConnectionId,
                created.Snapshot.Configuration.ConfigurationRevision,
                created.Snapshot.Configuration.ConfigurationSha256,
                Guid.NewGuid().ToString("N"),
                "bind-initial-authority-change",
                CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal((1L, 1L, 1L), await CountOwnerRowsAsync(fixture.DataSource, projectId));
        var current = await initialStore.GetAsync(
            caller,
            projectId,
            created.Snapshot.Head.Connection.ConnectionId,
            CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(created.Snapshot.Head.RowRevision, current.Head.RowRevision);
        Assert.Equal(created.Snapshot.Configuration.ConfigurationRevision, current.Configuration.ConfigurationRevision);
        Assert.Equal(created.Snapshot.Configuration.ConfigurationSha256, current.Configuration.ConfigurationSha256);
        Assert.Null(current.Configuration.IdentityBindingReference);
    }

    private static RemoteMcpConnectionDraft Draft() =>
        new("Weather", "https://mcp.example.com", null, RemoteMcpAuthenticationMode.None);

    private static RemoteMcpConnectionDraft OAuthDraft() =>
        new(
            "Weather",
            "https://mcp.example.com",
            "https://mcp.example.com/resource",
            RemoteMcpAuthenticationMode.DelegatedOAuth);

    private static ProjectAuthorizationContextResponse AuthorizationContext(
        string projectId,
        long membershipRevision = 1,
        string actorId = "actor-test",
        ProjectAuthorizationPermission permission = ProjectAuthorizationPermission.WriteProjects) =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://identity.example",
            actorId,
            "tenant-test",
            membershipRevision,
            null,
            null,
            ImmutableArray.Create(new EffectiveProjectAuthorization(
                ProjectAuthorityResourceType.Project,
                projectId,
                permission == ProjectAuthorizationPermission.ReadProjects
                    ? ImmutableArray.Create(new ProjectAuthorizationPermissionGrant(
                        ProjectAuthorizationPermission.ReadProjects, RoleRevision: 1))
                    : ImmutableArray.Create(
                        new ProjectAuthorizationPermissionGrant(
                            ProjectAuthorizationPermission.ReadProjects, RoleRevision: 1),
                        new ProjectAuthorizationPermissionGrant(permission, RoleRevision: 1)))));

    private static async Task<bool> RemoteMcpTablesExistAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('environment.remote_mcp_connections') IS NOT NULL
               AND to_regclass('environment.remote_mcp_connection_configurations') IS NOT NULL
               AND to_regclass('environment.remote_mcp_catalog_snapshots') IS NOT NULL
               AND to_regclass('environment.remote_mcp_connection_idempotency') IS NOT NULL
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(long Connections, long Configurations, long Idempotency)> CountOwnerRowsAsync(
        NpgsqlDataSource dataSource,
        string projectId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM environment.remote_mcp_connections
                 WHERE tenant_id = 'tenant-test' AND project_id = @project_id),
                (SELECT count(*) FROM environment.remote_mcp_connection_configurations
                 WHERE tenant_id = 'tenant-test' AND project_id = @project_id),
                (SELECT count(*) FROM environment.remote_mcp_connection_idempotency
                 WHERE tenant_id = 'tenant-test' AND project_id = @project_id)
            """,
            connection);
        command.Parameters.AddWithValue("project_id", projectId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> CountCatalogRowsAsync(NpgsqlDataSource dataSource, string projectId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM environment.remote_mcp_catalog_snapshots
            WHERE tenant_id = 'tenant-test' AND project_id = @project_id
            """,
            connection);
        command.Parameters.AddWithValue("project_id", projectId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SequencedProjectsConfigClient(
        Func<int, ProjectAuthorizationContextResponse> contextFactory) : IProjectsConfigClient
    {
        private int _authorizationReads;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(contextFactory(Interlocked.Increment(ref _authorizationReads)));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Remote MCP connection tests do not read run selection.");
    }

    private sealed class MutableProjectsConfigClient(string projectId) : IProjectsConfigClient
    {
        public string ActorId { get; set; } = "actor-a";
        public bool HasWriteAuthority { get; set; } = true;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = AuthorizationContext(
                projectId,
                actorId: ActorId,
                permission: HasWriteAuthority
                    ? ProjectAuthorizationPermission.WriteProjects
                    : ProjectAuthorizationPermission.ReadProjects);
            return Task.FromResult(context);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string requestedProjectId,
            string runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Remote MCP connection tests do not read run selection.");
    }
}
