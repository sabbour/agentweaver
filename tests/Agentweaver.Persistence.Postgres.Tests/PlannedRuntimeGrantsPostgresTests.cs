using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

[Collection("PostgreSQL")]
public sealed class PlannedRuntimeGrantsPostgresTests
{
    private readonly PostgresFixture _fixture;

    public PlannedRuntimeGrantsPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ApplyAndVerifyBindsPlannedFunctionOidAsPostgresOid()
    {
        var schema = $"runtime_grants_{Guid.NewGuid():N}";
        var role = $"runtime_role_{Guid.NewGuid():N}";
        var quotedSchema = PlannedRuntimeGrants.Quote(schema);
        var quotedRole = PlannedRuntimeGrants.Quote(role);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {quotedRole} NOLOGIN");
            await ExecuteAsync(connection, $"CREATE SCHEMA {quotedSchema}");
            await ExecuteAsync(connection, $"""
                CREATE TABLE {quotedSchema}.runtime_data (id integer PRIMARY KEY);
                CREATE FUNCTION {quotedSchema}.planned_function() RETURNS integer
                LANGUAGE sql IMMUTABLE AS 'SELECT 1'
                """);

            var plan = new PlannedServiceGrants(
                "test",
                "test",
                schema,
                role,
                [],
                [new PlannedTableGrant("runtime_data", ["SELECT"], null)],
                [new PlannedFunctionGrant("planned_function()", true)],
                [],
                []);

            await PlannedRuntimeGrants.ApplyAndVerifyAsync(connection, plan);

            await ExecuteAsync(connection, $"SET ROLE {quotedRole}");
            try
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT {quotedSchema}.planned_function()", connection);
                Assert.Equal(1, await command.ExecuteScalarAsync());
            }
            finally
            {
                await ExecuteAsync(connection, "RESET ROLE");
            }
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedRole}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
