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

    [Fact]
    public async Task EnvironmentPolicyLedgerRevokesTableAndColumnInsertsIdempotentlyWhilePreservingReadsAndOtherGrants()
    {
        var role = $"env_rt_{Guid.NewGuid():N}";
        var quotedRole = PlannedRuntimeGrants.Quote(role);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        try
        {
            await ExecuteAsync(connection, $"""
                CREATE ROLE {quotedRole} NOLOGIN;
                CREATE SCHEMA IF NOT EXISTS environment;
                CREATE TABLE environment.owners (id integer PRIMARY KEY, name text NOT NULL);
                CREATE TABLE environment.platform_policy_apply_reservations (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_holds (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_receipts (id integer PRIMARY KEY, payload text NOT NULL);
                INSERT INTO environment.platform_policy_apply_reservations VALUES (1, 'existing-res');
                INSERT INTO environment.platform_policy_proof_holds VALUES (1, 'existing-hold');
                INSERT INTO environment.platform_policy_proof_receipts VALUES (1, 'existing-receipt');
                GRANT USAGE ON SCHEMA environment TO {quotedRole};
                GRANT SELECT, INSERT, UPDATE ON environment.owners TO {quotedRole};
                GRANT SELECT, INSERT ON environment.platform_policy_apply_reservations TO {quotedRole};
                GRANT INSERT (payload) ON environment.platform_policy_apply_reservations TO {quotedRole};
                GRANT SELECT, INSERT ON environment.platform_policy_proof_holds TO {quotedRole};
                GRANT INSERT (payload) ON environment.platform_policy_proof_holds TO {quotedRole};
                GRANT SELECT, INSERT ON environment.platform_policy_proof_receipts TO {quotedRole};
                GRANT INSERT (payload) ON environment.platform_policy_proof_receipts TO {quotedRole};
                """);

            var plan = new PlannedServiceGrants(
                "test-env",
                "environment",
                "environment",
                role,
                [],
                [
                    new PlannedTableGrant("owners", ["SELECT", "INSERT", "UPDATE"], null),
                    new PlannedTableGrant("platform_policy_apply_reservations", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_holds", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_receipts", ["SELECT"], null),
                ],
                [],
                [],
                []);

            // Apply twice to prove idempotency.
            await PlannedRuntimeGrants.ApplyAndVerifyAsync(connection, plan);
            await PlannedRuntimeGrants.ApplyAndVerifyAsync(connection, plan);

            await PlannedRuntimeGrants.VerifyReadOnlyPolicyLedgerAsync(connection, null, role);

            await ExecuteAsync(connection, $"SET ROLE {quotedRole}");
            try
            {
                foreach (var (table, expected) in new[]
                {
                    ("platform_policy_apply_reservations", "existing-res"),
                    ("platform_policy_proof_holds", "existing-hold"),
                    ("platform_policy_proof_receipts", "existing-receipt"),
                })
                {
                    await using var select = new NpgsqlCommand($"SELECT payload FROM environment.{table} WHERE id = 1", connection);
                    Assert.Equal(expected, (string)(await select.ExecuteScalarAsync())!);

                    var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
                    {
                        await using var insert = new NpgsqlCommand(
                            $"INSERT INTO environment.{table} (id, payload) VALUES (2, 'bad')", connection);
                        await insert.ExecuteNonQueryAsync();
                    });
                    Assert.Equal("42501", denied.SqlState);
                }

                await using var ownerInsert = new NpgsqlCommand(
                    "INSERT INTO environment.owners (id, name) VALUES (10, 'ok')", connection);
                await ownerInsert.ExecuteNonQueryAsync();
            }
            finally
            {
                await ExecuteAsync(connection, "RESET ROLE");
            }
        }
        finally
        {
            await ExecuteAsync(connection, "DROP SCHEMA IF EXISTS environment CASCADE");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedRole}");
        }
    }

    [Theory]
    [InlineData("inherited-usage")]
    [InlineData("set-reachable")]
    [InlineData("public-table")]
    [InlineData("public-column")]
    [InlineData("alternate-grantor")]
    [InlineData("dependent-grant")]
    [InlineData("admin-option-column-insert")]
    [InlineData("admin-option-set-only-column-insert")]
    [InlineData("set-to-usage-ownership")]
    [InlineData("multihop-admin-option-chain")]
    public async Task EnvironmentPolicyLedgerRefusesAndRollsBackHiddenOrDependentInsertPaths(string scenario)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var role = $"env_rt_{suffix}";
        var helperRole = $"env_helper_{suffix}";
        var secondHelperRole = $"env_helper2_{suffix}";
        var quotedRole = PlannedRuntimeGrants.Quote(role);
        var quotedHelper = PlannedRuntimeGrants.Quote(helperRole);
        var quotedSecondHelper = PlannedRuntimeGrants.Quote(secondHelperRole);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        try
        {
            await ExecuteAsync(connection, $"""
                CREATE ROLE {quotedRole} NOLOGIN;
                CREATE ROLE {quotedHelper} NOLOGIN;
                CREATE ROLE {quotedSecondHelper} NOLOGIN;
                CREATE SCHEMA IF NOT EXISTS environment;
                CREATE TABLE environment.owners (id integer PRIMARY KEY, name text NOT NULL);
                CREATE TABLE environment.platform_policy_apply_reservations (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_holds (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_receipts (id integer PRIMARY KEY, payload text NOT NULL);
                """);

            switch (scenario)
            {
                case "inherited-usage":
                    await ExecuteAsync(connection, $"""
                        GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedHelper};
                        GRANT {quotedHelper} TO {quotedRole} WITH INHERIT TRUE, SET FALSE;
                        """);
                    break;
                case "set-reachable":
                    await ExecuteAsync(connection, $"""
                        GRANT INSERT ON environment.platform_policy_proof_holds TO {quotedHelper};
                        GRANT {quotedHelper} TO {quotedRole} WITH INHERIT FALSE, SET TRUE;
                        """);
                    break;
                case "public-table":
                    await ExecuteAsync(connection,
                        "GRANT INSERT ON environment.platform_policy_proof_receipts TO PUBLIC;");
                    break;
                case "public-column":
                    await ExecuteAsync(connection,
                        "GRANT INSERT (payload) ON environment.platform_policy_proof_receipts TO PUBLIC;");
                    break;
                case "alternate-grantor":
                    await ExecuteAsync(connection, $"""
                        GRANT USAGE ON SCHEMA environment TO {quotedHelper};
                        GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedHelper} WITH GRANT OPTION;
                        SET ROLE {quotedHelper};
                        GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedRole};
                        RESET ROLE;
                        """);
                    break;
                case "dependent-grant":
                    await ExecuteAsync(connection, $"""
                        GRANT USAGE ON SCHEMA environment TO {quotedRole};
                        GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedRole} WITH GRANT OPTION;
                        SET ROLE {quotedRole};
                        GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedHelper};
                        RESET ROLE;
                        """);
                    break;
                case "admin-option-column-insert":
                    await ExecuteAsync(connection, $"""
                        GRANT INSERT (payload) ON environment.platform_policy_proof_receipts TO {quotedHelper};
                        GRANT {quotedHelper} TO {quotedRole} WITH ADMIN OPTION, INHERIT FALSE, SET FALSE;
                        """);
                    break;
                case "admin-option-set-only-column-insert":
                    await ExecuteAsync(connection, $"""
                        GRANT INSERT (payload) ON environment.platform_policy_proof_receipts TO {quotedSecondHelper};
                        GRANT {quotedSecondHelper} TO {quotedHelper} WITH INHERIT FALSE, SET TRUE;
                        GRANT {quotedHelper} TO {quotedRole} WITH ADMIN OPTION, INHERIT FALSE, SET FALSE;
                        """);
                    break;
                case "set-to-usage-ownership":
                    await ExecuteAsync(connection, $"""
                        GRANT SELECT ON environment.platform_policy_apply_reservations,
                            environment.platform_policy_proof_holds,
                            environment.platform_policy_proof_receipts TO {quotedRole};
                        ALTER TABLE environment.platform_policy_proof_holds OWNER TO {quotedSecondHelper};
                        GRANT {quotedSecondHelper} TO {quotedHelper} WITH INHERIT TRUE, SET FALSE;
                        GRANT {quotedHelper} TO {quotedRole} WITH INHERIT FALSE, SET TRUE;
                        """);
                    break;
                case "multihop-admin-option-chain":
                    await ExecuteAsync(connection, $"""
                        GRANT INSERT (payload) ON environment.platform_policy_apply_reservations TO {quotedSecondHelper};
                        GRANT {quotedSecondHelper} TO {quotedHelper} WITH ADMIN OPTION, INHERIT FALSE, SET FALSE;
                        GRANT {quotedHelper} TO {quotedRole} WITH ADMIN OPTION, INHERIT FALSE, SET FALSE;
                        """);
                    break;
            }

            var plan = new PlannedServiceGrants(
                "test-env",
                "environment",
                "environment",
                role,
                [],
                [
                    new PlannedTableGrant("owners", ["SELECT", "INSERT", "UPDATE"], null),
                    new PlannedTableGrant("platform_policy_apply_reservations", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_holds", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_receipts", ["SELECT"], null),
                ],
                [],
                [],
                []);

            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                PlannedRuntimeGrants.ApplyAndVerifyAsync(connection, plan));
            if (scenario.StartsWith("admin-option-", StringComparison.Ordinal) ||
                scenario == "multihop-admin-option-chain")
                Assert.Contains("ADMIN OPTION", failure.Message, StringComparison.Ordinal);
            if (scenario == "set-to-usage-ownership")
            {
                var verifyFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PlannedRuntimeGrants.VerifyReadOnlyPolicyLedgerAsync(connection, null, role));
                Assert.Contains("owned by reachable role", verifyFailure.Message, StringComparison.Ordinal);
            }

            // Transaction rolled back: unrelated owners table was not granted to the runtime role.
            await using var ownersCheck = new NpgsqlCommand(
                "SELECT pg_catalog.has_table_privilege(@role, 'environment.owners', 'INSERT')", connection);
            ownersCheck.Parameters.AddWithValue("role", role);
            Assert.Equal(false, await ownersCheck.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE");
            await ExecuteAsync(connection, "DROP SCHEMA IF EXISTS environment CASCADE");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedRole}");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedHelper}");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedSecondHelper}");
        }
    }

    [Fact]
    public async Task EnvironmentPolicyLedgerRejectsInheritThenSetChainAndAllowsUnreachableInsertRole()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var role = $"env_rt_{suffix}";
        var inheritedRole = $"env_inh_{suffix}";
        var setTargetRole = $"env_set_{suffix}";
        var unreachableRole = $"env_unreach_{suffix}";
        var quotedRole = PlannedRuntimeGrants.Quote(role);
        var quotedInherited = PlannedRuntimeGrants.Quote(inheritedRole);
        var quotedSetTarget = PlannedRuntimeGrants.Quote(setTargetRole);
        var quotedUnreachable = PlannedRuntimeGrants.Quote(unreachableRole);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        try
        {
            await ExecuteAsync(connection, $"""
                CREATE ROLE {quotedRole} NOLOGIN;
                CREATE ROLE {quotedInherited} NOLOGIN;
                CREATE ROLE {quotedSetTarget} NOLOGIN;
                CREATE ROLE {quotedUnreachable} NOLOGIN;
                CREATE SCHEMA IF NOT EXISTS environment;
                CREATE TABLE environment.platform_policy_apply_reservations (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_holds (id integer PRIMARY KEY, payload text NOT NULL);
                CREATE TABLE environment.platform_policy_proof_receipts (id integer PRIMARY KEY, payload text NOT NULL);
                GRANT USAGE ON SCHEMA environment TO {quotedRole};
                GRANT SELECT ON environment.platform_policy_apply_reservations,
                    environment.platform_policy_proof_holds,
                    environment.platform_policy_proof_receipts TO {quotedRole};
                -- Non-reachable membership (INHERIT FALSE, SET FALSE, ADMIN FALSE) does not grant SET ROLE or INSERT.
                GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedUnreachable};
                GRANT {quotedUnreachable} TO {quotedInherited} WITH INHERIT FALSE, SET FALSE;
                GRANT {quotedInherited} TO {quotedRole} WITH INHERIT TRUE, SET FALSE;
                """);

            await ExecuteAsync(connection, $"SET SESSION AUTHORIZATION {quotedRole}");
            try
            {
                var setDenied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(connection, $"SET ROLE {quotedUnreachable}"));
                Assert.Equal("42501", setDenied.SqlState);
            }
            finally
            {
                await ExecuteAsync(connection, "RESET SESSION AUTHORIZATION");
            }

            var plan = new PlannedServiceGrants(
                "test-env",
                "environment",
                "environment",
                role,
                [],
                [
                    new PlannedTableGrant("platform_policy_apply_reservations", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_holds", ["SELECT"], null),
                    new PlannedTableGrant("platform_policy_proof_receipts", ["SELECT"], null),
                ],
                [],
                [],
                []);

            // Unreachable role with INSERT is allowed.
            await PlannedRuntimeGrants.ApplyAndVerifyAsync(connection, plan);
            await PlannedRuntimeGrants.VerifyReadOnlyPolicyLedgerAsync(connection, null, role);

            // Check whether PostgreSQL 16 allows session_user=role to SET ROLE setTargetRole via inheritedRole (INHERIT TRUE -> SET TRUE).
            await ExecuteAsync(connection, $"""
                GRANT INSERT ON environment.platform_policy_apply_reservations TO {quotedSetTarget};
                GRANT {quotedSetTarget} TO {quotedInherited} WITH INHERIT FALSE, SET TRUE;
                """);
            var canSetViaInherited = false;
            await ExecuteAsync(connection, $"SET SESSION AUTHORIZATION {quotedRole}");
            try
            {
                await ExecuteAsync(connection, $"SET ROLE {quotedSetTarget}");
                canSetViaInherited = true;
                await ExecuteAsync(connection, "RESET ROLE");
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                canSetViaInherited = false;
            }
            finally
            {
                await ExecuteAsync(connection, "RESET SESSION AUTHORIZATION");
            }

            Assert.False(canSetViaInherited);
            await PlannedRuntimeGrants.VerifyReadOnlyPolicyLedgerAsync(connection, null, role);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE");
            await ExecuteAsync(connection, "DROP SCHEMA IF EXISTS environment CASCADE");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedRole}");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedInherited}");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedSetTarget}");
            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {quotedUnreachable}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
