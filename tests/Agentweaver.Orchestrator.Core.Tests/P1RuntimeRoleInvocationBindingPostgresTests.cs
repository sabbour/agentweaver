using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class P1RuntimeRoleInvocationBindingPostgresTests(CoordinationPostgresFixture fixture)
{
    private const string Schema = "orchestrator";
    private const string RuntimeRole = "agentweaver-id-orchestrator";
    private const string Tenant = "tenant-runtime";
    private const string Project = "project-runtime";

    [Fact]
    public async Task RestrictedRuntimeRoleCreatesStartsRetriesReadsAndCannotMutateP1Ledgers()
    {
        NpgsqlDataSource? runtimeDataSource = null;
        try
        {
            await using (var admin = await fixture.DataSource.OpenConnectionAsync())
            await using (var setup = new NpgsqlCommand($"""
                DROP SCHEMA IF EXISTS "{Schema}" CASCADE;
                DROP ROLE IF EXISTS "{RuntimeRole}";
                CREATE ROLE "{RuntimeRole}" NOLOGIN;
                GRANT "{RuntimeRole}" TO CURRENT_USER;
                """, admin))
                await setup.ExecuteNonQueryAsync();

            await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, Schema);
            var plan = PlannedRuntimeGrants.Load(
                "orchestrator", Schema, RuntimeRole, EmbeddedPlanHash(), CoordinationOwnerMigrator.SupportedVersions);
            await using (var admin = await fixture.DataSource.OpenConnectionAsync())
                await PlannedRuntimeGrants.ApplyAndVerifyAsync(admin, plan);

            var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            runtimeConnection.Options = string.IsNullOrWhiteSpace(runtimeConnection.Options)
                ? $"-c role={RuntimeRole}"
                : $"{runtimeConnection.Options} -c role={RuntimeRole}";
            runtimeDataSource = NpgsqlDataSource.Create(runtimeConnection.ConnectionString);

            var actor = new CoordinationActor("https://identity.example/", Guid.NewGuid().ToString("D"));
            var catalog = ProviderCatalog.Create([], [], []).Value!;
            var contexts = new CoordinatorRunSelectionContextStore(
                runtimeDataSource, Schema, catalog, new ProviderResolver(catalog), []);
            var decisions = new CoordinatorDecisionOwnerStore(
                runtimeDataSource, Schema, contexts, TimeProvider.System);
            var store = new CoordinationOwnerStore(runtimeDataSource, Schema, TimeProvider.System, decisions);
            var receipt = new P1ProjectSetupReceiptSnapshot(
                Guid.NewGuid(), actor.Issuer, actor.Subject, Tenant, Guid.NewGuid(), 1,
                Project, actor.Subject, 1, 1, Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
            var request = new CreateProjectRunRequest(
                "Start from a P1 setup", 1, P1SetupInvocationId: receipt.InvocationId);

            await using (var connection = await runtimeDataSource.OpenConnectionAsync())
            await using (var identity = new NpgsqlCommand("SELECT current_user", connection))
                Assert.Equal(RuntimeRole, await identity.ExecuteScalarAsync());

            var created = await store.CreateProjectRunAsync(
                actor, Tenant, Project, "runtime-role-p1", request, CancellationToken.None, receipt);
            Assert.Equal(created, await store.CreateProjectRunAsync(
                actor, Tenant, Project, "runtime-role-p1", request, CancellationToken.None, receipt));

            var selection = CreateSelection(actor, created.RunId);
            var deliveries = 0;
            Task AcceptRoot(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                CoordinationOwnerStore.ProjectRunRequestRow pending,
                CancellationToken token) =>
                store.AcceptP1InteractiveRootInTransactionAsync(
                    connection, transaction, actor, selection, pending, receipt, token);
            Task Deliver(CoordinationOwnerStore.ProjectRunRequestRow ignoredRow, CancellationToken ignoredToken)
            {
                deliveries++;
                return Task.CompletedTask;
            }
            Task Revalidate(CancellationToken _) => Task.CompletedTask;

            var firstStart = await store.StartProjectRunAsync(
                actor, Tenant, Project, created.RunId, Deliver, Revalidate, CancellationToken.None,
                acceptRootInTransaction: AcceptRoot);
            var retriedStart = await store.StartProjectRunAsync(
                actor, Tenant, Project, created.RunId, Deliver, Revalidate, CancellationToken.None,
                acceptRootInTransaction: AcceptRoot);
            var binding = await store.ReadCurrentP1InvocationBindingAsync(actor, selection, CancellationToken.None);

            Assert.Equal("started", firstStart.State);
            Assert.Equal(firstStart, retriedStart);
            Assert.Equal(1, deliveries);
            Assert.Equal(receipt.InvocationId, binding.SetupInvocationId);
            Assert.Equal(created.RunId, binding.RunId);
            Assert.Equal(created.RootSessionId, binding.RootSessionId);

            await AssertRestrictedP1GrantsAsync(runtimeDataSource);
            await AssertP1MutationDeniedAsync(runtimeDataSource, "p1_run_request_provenance",
                "provenance_id", await ReadProvenanceIdAsync(runtimeDataSource, created.RunId));
            await AssertP1MutationDeniedAsync(runtimeDataSource, "p1_invocation_bindings",
                "core_invocation_id", binding.CoreInvocationId);
        }
        finally
        {
            if (runtimeDataSource is not null)
                await runtimeDataSource.DisposeAsync();
            await using var admin = await fixture.DataSource.OpenConnectionAsync();
            await using (var dropSchema = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", admin))
                await dropSchema.ExecuteNonQueryAsync();
            await using var roleExists = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)", admin);
            roleExists.Parameters.AddWithValue("role", RuntimeRole);
            if ((bool)(await roleExists.ExecuteScalarAsync())!)
            {
                await using var cleanup = new NpgsqlCommand($"""
                    REVOKE "{RuntimeRole}" FROM CURRENT_USER;
                    DROP ROLE "{RuntimeRole}";
                    """, admin);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static AuthorizedRunSelection CreateSelection(CoordinationActor actor, string runId)
    {
        using var snapshot = JsonDocument.Parse(
            """{"source":"projects-config","modelSelection":{"reference":"model:provider/alpha"}}""");
        var selection = new EffectiveRunSelection(
            Project, runId, 1, 1, 1, "context-v1", snapshot.RootElement.Clone());
        return new AuthorizedRunSelection(selection, new ProjectsAuthorizationContext(
            1, actor.Issuer, actor.Subject, Tenant, 1, Project, runId,
            ImmutableArray.Create(new ProjectsAuthority("project", Project,
                ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }

    private static string EmbeddedPlanHash()
    {
        using var plan = JsonDocument.Parse(PlannedRuntimeGrants.ReadEmbeddedPlan());
        var canonical = JsonSerializer.SerializeToUtf8Bytes(plan.RootElement);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static async Task AssertRestrictedP1GrantsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        foreach (var (table, column) in new[]
        {
            ("p1_run_request_provenance", "provenance_id"),
            ("p1_invocation_bindings", "core_invocation_id"),
        })
        {
            await using var command = new NpgsqlCommand("""
                SELECT has_column_privilege(current_user, @relation::regclass, @column, 'UPDATE'),
                       has_table_privilege(current_user, @relation::regclass, 'UPDATE'),
                       has_table_privilege(current_user, @relation::regclass, 'DELETE'),
                       has_table_privilege(current_user, @relation::regclass, 'TRUNCATE')
                """, connection);
            command.Parameters.AddWithValue("relation", $"{Schema}.{table}");
            command.Parameters.AddWithValue("column", column);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
        }
    }

    private static async Task<Guid> ReadProvenanceIdAsync(NpgsqlDataSource dataSource, string runId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT provenance_id
            FROM "{Schema}".p1_run_request_provenance
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("project", Project);
        command.Parameters.AddWithValue("run", runId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertP1MutationDeniedAsync(
        NpgsqlDataSource dataSource,
        string table,
        string lockColumn,
        Guid rowId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var update = new NpgsqlCommand(
            $"""UPDATE "{Schema}"."{table}" SET "{lockColumn}" = "{lockColumn}" WHERE "{lockColumn}" = @id""",
            connection))
        {
            update.Parameters.AddWithValue("id", rowId);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, denied.SqlState);
            Assert.Equal("P1 invocation provenance is append-only", denied.MessageText);
        }

        await using (var delete = new NpgsqlCommand(
            $"""DELETE FROM "{Schema}"."{table}" WHERE "{lockColumn}" = @id""", connection))
        {
            delete.Parameters.AddWithValue("id", rowId);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var truncate = new NpgsqlCommand(
            $"TRUNCATE \"{Schema}\".\"{table}\"", connection))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }
}
