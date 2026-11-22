using Npgsql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderWriteGuardPostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    private const string InsertHead = """
        INSERT INTO projects_config.model_providers
          (provider_id, scope, display_name, type, base_url, deployment, reasoning_efforts,
           state, current_revision, created_by_actor_id, created_at, updated_by_actor_id, updated_at)
        VALUES (@id, 'Platform', 'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'],
                'Draft', 1, 'actor', transaction_timestamp(), 'actor', transaction_timestamp())
        """;
    private const string InsertRevision = """
        INSERT INTO projects_config.model_provider_revisions
          (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
           display_name, type, base_url, deployment, wire_api, azure_api_version, reasoning_efforts,
           prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
           credential_secret_id, created_by_actor_id, created_at)
        SELECT h.provider_id, h.current_revision, h.scope, h.project_id, @operation, repeat('a', 64),
               h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.azure_api_version,
               h.reasoning_efforts, h.prompt_capacity_tokens, repeat('b', 64), NULL, NULL, NULL,
               h.updated_by_actor_id, h.updated_at
        FROM projects_config.model_providers h WHERE h.provider_id = @id
        """;

    [Theory]
    [InlineData("head-only")]
    [InlineData("revision-only")]
    [InlineData("rewind")]
    [InlineData("skip")]
    [InlineData("second-advance")]
    [InlineData("two-revisions")]
    [InlineData("mismatch")]
    [InlineData("credential")]
    [InlineData("available")]
    [InlineData("fake-marker")]
    public async Task RestrictedRoleCannotCommitAnInvalidPair(string attack)
    {
        var id = $"guard-{Guid.NewGuid():N}";
        if (attack != "head-only" && attack != "available")
            await CreateDraftAsync(id);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, id);
        var attemptedCommit = false;
        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            switch (attack)
            {
                case "head-only":
                    await ExecuteAsync(connection, transaction, InsertHead, id);
                    break;
                case "available":
                    await ExecuteAsync(connection, transaction,
                        InsertHead.Replace("'Draft', 1", "'Available', 1", StringComparison.Ordinal), id);
                    await ExecuteAsync(connection, transaction, InsertRevision, id);
                    break;
                case "revision-only":
                    await ExecuteAsync(connection, transaction,
                        InsertRevision.Replace("h.current_revision,", "h.current_revision + 1,", StringComparison.Ordinal), id);
                    break;
                case "rewind":
                    await ExecuteAsync(connection, transaction, """
                        UPDATE projects_config.model_providers SET current_revision = 1 WHERE provider_id = @id
                        """, id);
                    break;
                case "skip":
                    await ExecuteAsync(connection, transaction, """
                        UPDATE projects_config.model_providers SET current_revision = 3 WHERE provider_id = @id
                        """, id);
                    await ExecuteAsync(connection, transaction, InsertRevision, id);
                    break;
                case "second-advance":
                    await AdvanceAsync(connection, transaction, id);
                    await ExecuteAsync(connection, transaction, InsertRevision, id);
                    await ExecuteAsync(connection, transaction, """
                        UPDATE projects_config.model_providers SET current_revision = 3 WHERE provider_id = @id
                        """, id);
                    await ExecuteAsync(connection, transaction, InsertRevision, id);
                    break;
                case "two-revisions":
                    await AdvanceAsync(connection, transaction, id);
                    await ExecuteAsync(connection, transaction, InsertRevision, id);
                    await ExecuteAsync(connection, transaction,
                        InsertRevision.Replace("h.current_revision,", "h.current_revision + 1,", StringComparison.Ordinal), id);
                    break;
                case "mismatch":
                    await AdvanceAsync(connection, transaction, id);
                    await ExecuteAsync(connection, transaction,
                        InsertRevision.Replace("h.display_name,", "'different',", StringComparison.Ordinal), id);
                    break;
                case "credential":
                    await AdvanceAsync(connection, transaction, id);
                    await ExecuteAsync(connection, transaction,
                        InsertRevision.Replace("NULL, NULL, NULL,", "'new-version', NULL, NULL,", StringComparison.Ordinal), id);
                    break;
                case "fake-marker":
                    await ExecuteAsync(connection, transaction, """
                        UPDATE projects_config.model_providers
                        SET current_revision = 2, write_transaction_id = 'legacy'
                        WHERE provider_id = @id
                        """, id);
                    break;
            }
            attemptedCommit = true;
            await transaction.CommitAsync();
        });
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("ck_model_provider_write_guard", failure.ConstraintName);
        if (!attemptedCommit)
            await transaction.RollbackAsync();

        await using var verify = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM projects_config.model_provider_revisions WHERE provider_id = @id
            """, verify);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(attack is "head-only" or "available" ? 0L : 1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RestrictedRoleCanCreateAndEditMatchingDraft()
    {
        var id = $"guard-{Guid.NewGuid():N}";
        await CreateDraftAsync(id);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, id);
        await AdvanceAsync(connection, transaction, id);
        await ExecuteAsync(connection, transaction, InsertRevision, id);
        await transaction.CommitAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT h.current_revision, count(r.*)
            FROM projects_config.model_providers h JOIN projects_config.model_provider_revisions r
              ON r.provider_id = h.provider_id WHERE h.provider_id = @id GROUP BY h.current_revision
            """, connection);
        verify.Parameters.AddWithValue("id", id);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task RestrictedRoleCanCreateTwoDistinctProvidersInOneTransaction()
    {
        var first = $"guard-{Guid.NewGuid():N}";
        var second = $"guard-{Guid.NewGuid():N}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, first);
        foreach (var id in new[] { first, second })
        {
            await ExecuteAsync(connection, transaction, InsertHead, id);
            await ExecuteAsync(connection, transaction, InsertRevision, id);
        }
        await transaction.CommitAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT count(*) FROM projects_config.model_provider_revisions WHERE provider_id IN (@first, @second)
            """, connection);
        verify.Parameters.AddWithValue("first", first);
        verify.Parameters.AddWithValue("second", second);
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RestrictedRoleCannotReuseOperationAcrossProviders()
    {
        var first = $"guard-{Guid.NewGuid():N}";
        var second = $"guard-{Guid.NewGuid():N}";
        var operation = Guid.NewGuid();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, first);
        await ExecuteAsync(connection, transaction, InsertHead, first);
        await ExecuteAsync(connection, transaction, InsertRevision, first, operation);
        await ExecuteAsync(connection, transaction, InsertHead, second);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, transaction, InsertRevision, second, operation));
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_model_provider_revisions_operation_id", error.ConstraintName);
        await transaction.RollbackAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT count(*) FROM projects_config.model_providers WHERE provider_id IN (@first, @second)
            """, connection);
        verify.Parameters.AddWithValue("first", first);
        verify.Parameters.AddWithValue("second", second);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("available-without-op")]
    [InlineData("available-with-draft-op")]
    [InlineData("available-without-pin")]
    [InlineData("available-pin-mismatch")]
    [InlineData("available-op-hash-mismatch")]
    [InlineData("disable-leaving-available")]
    public async Task RestrictedRoleCannotBypassAvailableProviderGuards(string attack)
    {
        var id = $"guard-{Guid.NewGuid():N}";
        await CreateDraftAsync(id);
        var slotId = Guid.NewGuid();
        var opId = Guid.NewGuid();
        var credVersion = new string('1', 32);
        var configHash = new string('b', 64);
        var pinHash = new string('c', 64);
        var otherPinHash = new string('d', 64);
        var validPinJson = $$$"""{"rateCardId":"card-1","rateCardVersion":"v1","contentHash":"{{{pinHash}}}","provenance":{"source":"manual","sourceUrl":"https://pricing.example/card","retrievedOn":"2026-11-22"}}""";
        var mismatchedPinJson = $$$"""{"rateCardId":"other-card","rateCardVersion":"v1","contentHash":"{{{pinHash}}}","provenance":{"source":"manual","sourceUrl":"https://pricing.example/card","retrievedOn":"2026-11-22"}}""";

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, id);
        var attemptedCommit = false;
        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            var headState = "Available";
            var revisionPinJson = validPinJson;
            if (attack == "available-without-pin")
                revisionPinJson = null!;
            else if (attack == "available-pin-mismatch")
                revisionPinJson = mismatchedPinJson;

            await ExecuteAsync(connection, transaction, $"""
                UPDATE projects_config.model_providers
                SET current_revision = current_revision + 1,
                    state = '{headState}',
                    updated_by_actor_id = 'actor',
                    updated_at = transaction_timestamp()
                WHERE provider_id = @id
                """, id);

            if (attack != "available-without-op")
            {
                var opKind = attack == "disable-leaving-available" ? "Disable" : "Publish";
                var opState = attack is "available-with-draft-op" or "disable-leaving-available" ? "Draft" : "Available";
                var opCredVersion = opKind == "Disable" ? (string?)null : credVersion;
                var opSecretId = opKind == "Disable" ? (string?)null : $"byok-platform-{id}";
                var opCardId = opState == "Available" ? "card-1" : null;
                var opCardVersion = opState == "Available" ? "v1" : null;
                var opPinHash = opState == "Available" ? (attack == "available-op-hash-mismatch" ? otherPinHash : pinHash) : null;

                await using var opCmd = new NpgsqlCommand("""
                    INSERT INTO projects_config.model_provider_credential_operations
                      (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                       expected_provider_revision, original_provider_revision, original_credential_revision,
                       credential_slot_id, credential_secret_id, credential_version, configuration_hash,
                       provider_state, rate_card_id, rate_card_version, rate_card_pin_hash,
                       original_tenant, original_membership_id, original_membership_revision,
                       current_membership_id, current_membership_revision, current_assignment_id,
                       current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                    VALUES
                      (@op, @kind, @id, 'Platform', NULL, 2,
                       CASE WHEN @kind = 'Publish' THEN 1 ELSE NULL END,
                       CASE WHEN @kind = 'Disable' THEN 1 ELSE NULL END,
                       CASE WHEN @kind = 'Disable' THEN 1 ELSE NULL END,
                       @slot, @secret, @cred, @cfg,
                       @pstate, @rcid, @rcver, @rchash,
                       'tenant-1', @m, 1, @m, 1, @a, 1, 'https://issuer.example', 'actor', repeat('e', 64), transaction_timestamp())
                    """, connection, transaction);
                opCmd.Parameters.AddWithValue("op", opId);
                opCmd.Parameters.AddWithValue("kind", opKind);
                opCmd.Parameters.AddWithValue("id", id);
                opCmd.Parameters.AddWithValue("slot", slotId);
                opCmd.Parameters.AddWithValue("secret", (object?)opSecretId ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("cred", (object?)opCredVersion ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("cfg", configHash);
                opCmd.Parameters.AddWithValue("pstate", opState);
                opCmd.Parameters.AddWithValue("rcid", (object?)opCardId ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("rcver", (object?)opCardVersion ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("rchash", (object?)opPinHash ?? DBNull.Value);
                opCmd.Parameters.AddWithValue("m", Guid.NewGuid());
                opCmd.Parameters.AddWithValue("a", Guid.NewGuid());
                await opCmd.ExecuteNonQueryAsync();
            }

            await using var revCmd = new NpgsqlCommand("""
                INSERT INTO projects_config.model_provider_revisions
                  (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                   display_name, type, base_url, deployment, wire_api, azure_api_version, reasoning_efforts,
                   prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
                   credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                   created_by_actor_id, created_at)
                SELECT h.provider_id, h.current_revision, h.scope, h.project_id, @op, repeat('a', 64),
                       h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.azure_api_version,
                       h.reasoning_efforts, h.prompt_capacity_tokens, @cfg, @cred, @slot, @secret,
                       'card-1', 'v1', @pin::jsonb, h.updated_by_actor_id, h.updated_at
                FROM projects_config.model_providers h WHERE h.provider_id = @id
                """, connection, transaction);
            revCmd.Parameters.AddWithValue("id", id);
            revCmd.Parameters.AddWithValue("op", opId);
            revCmd.Parameters.AddWithValue("cfg", configHash);
            revCmd.Parameters.AddWithValue("cred", attack == "disable-leaving-available" ? (object)DBNull.Value : credVersion);
            revCmd.Parameters.AddWithValue("slot", attack == "disable-leaving-available" ? (object)DBNull.Value : slotId);
            revCmd.Parameters.AddWithValue("secret", attack == "disable-leaving-available" ? (object)DBNull.Value : $"byok-platform-{id}");
            revCmd.Parameters.AddWithValue("pin", (object?)revisionPinJson ?? DBNull.Value);
            await revCmd.ExecuteNonQueryAsync();

            attemptedCommit = true;
            await transaction.CommitAsync();
        });
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("ck_model_provider_write_guard", failure.ConstraintName);
        if (!attemptedCommit)
            await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ImmediateConstraintsCannotCommitAnOrphanHead()
    {
        var id = $"guard-{Guid.NewGuid():N}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, id);
        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE", id);
        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, transaction, InsertHead, id));
        Assert.Equal("ck_model_provider_write_guard", failure.ConstraintName);
        await transaction.RollbackAsync();
        await using var verify = new NpgsqlCommand(
            "SELECT count(*) FROM projects_config.model_providers WHERE provider_id = @id", connection);
        verify.Parameters.AddWithValue("id", id);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("model_provider_revisions_write_pair")]
    [InlineData("model_provider_revisions_immutable")]
    public async Task RuntimeStartupRejectsDisabledGuard(string trigger)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand($"""
            ALTER TABLE projects_config.model_provider_revisions
            DISABLE TRIGGER "{trigger}"
            """, admin))
            await disable.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
        }
        finally
        {
            await using var enable = new NpgsqlCommand($"""
                ALTER TABLE projects_config.model_provider_revisions
                ENABLE TRIGGER "{trigger}"
                """, admin);
            await enable.ExecuteNonQueryAsync();
        }
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
    }

    [Fact]
    public async Task RuntimeStartupRejectsChangedGuardLiteralWhitespace()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand("""
            SELECT pg_get_functiondef('projects_config.guard_model_provider_reference_cross_table()'::regprocedure)
            """, admin);
        var original = (string)(await read.ExecuteScalarAsync())!;
        var changed = original.Replace("provider reference collision", "provider  reference collision",
            StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        await using (var alter = new NpgsqlCommand(changed, admin))
            await alter.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
        }
        finally
        {
            await using var restore = new NpgsqlCommand(original, admin);
            await restore.ExecuteNonQueryAsync();
        }
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
    }

    [Theory]
    [InlineData("direct", "tenant_memberships", "state")]
    [InlineData("inherited", "tenant_memberships", "state")]
    [InlineData("public", "tenant_memberships", "state")]
    [InlineData("public", "project_role_assignments", "state")]
    [InlineData("direct", "project_role_assignments", "role")]
    public async Task RuntimeAndMigrationRejectUnplannedColumnWrites(string source, string table, string column)
    {
        var role = $"guard_column_{Guid.NewGuid():N}";
        var runtimeName = "\"agentweaver-id-projects-config\"";
        var grantee = source switch
        {
            "inherited" => $"\"{role}\"",
            "public" => "PUBLIC",
            _ => runtimeName,
        };
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
        if (source == "inherited")
        {
            await using var create = new NpgsqlCommand($"""
                CREATE ROLE "{role}" NOLOGIN;
                GRANT "{role}" TO {runtimeName} WITH INHERIT TRUE, SET TRUE;
                """, admin);
            await create.ExecuteNonQueryAsync();
        }
        await using (var grant = new NpgsqlCommand(
            $"GRANT UPDATE (\"{column}\") ON projects_config.\"{table}\" TO {grantee}", admin))
            await grant.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, options));
        }
        finally
        {
            await using var revoke = new NpgsqlCommand($"""
                REVOKE UPDATE ("{column}") ON projects_config."{table}" FROM {grantee};
                {(source == "inherited" ? $"REVOKE \"{role}\" FROM {runtimeName}; DROP ROLE \"{role}\";" : "")}
                """, admin);
            await revoke.ExecuteNonQueryAsync();
        }
        await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
    }

    [Fact]
    public async Task RuntimeStartupAllowsPlannedTableAndReachableColumnReads()
    {
        var role = $"guard_reader_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var grant = new NpgsqlCommand($"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT SELECT (state) ON projects_config.tenant_memberships TO "{role}";
            GRANT "{role}" TO "agentweaver-id-projects-config" WITH SET TRUE, INHERIT FALSE;
            """, admin))
            await grant.ExecuteNonQueryAsync();
        try
        {
            await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
        }
        finally
        {
            await using var revoke = new NpgsqlCommand($"""
                REVOKE "{role}" FROM "agentweaver-id-projects-config";
                REVOKE SELECT (state) ON projects_config.tenant_memberships FROM "{role}";
                DROP ROLE "{role}";
                """, admin);
            await revoke.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("parameter")]
    [InlineData("membership")]
    [InlineData("revision-column")]
    public async Task RuntimeStartupRejectsPrivilegesReachableBySetRole(string privilege)
    {
        var role = $"guard_extra_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        var (grantPrivilege, revokePrivilege, directCheck) = privilege switch
        {
            "parameter" => (
                $"GRANT SET ON PARAMETER session_replication_role TO \"{role}\"",
                $"REVOKE SET ON PARAMETER session_replication_role FROM \"{role}\"",
                "SELECT NOT pg_catalog.has_parameter_privilege('agentweaver-id-projects-config', 'session_replication_role', 'SET')"),
            "membership" => (
                $"GRANT INSERT ON projects_config.tenant_memberships TO \"{role}\"",
                $"REVOKE INSERT ON projects_config.tenant_memberships FROM \"{role}\"",
                "SELECT NOT pg_catalog.has_table_privilege('agentweaver-id-projects-config', 'projects_config.tenant_memberships', 'INSERT')"),
            _ => (
                $"GRANT UPDATE (credential_version) ON projects_config.model_provider_revisions TO \"{role}\"",
                $"REVOKE UPDATE (credential_version) ON projects_config.model_provider_revisions FROM \"{role}\"",
                "SELECT NOT pg_catalog.has_column_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'credential_version', 'UPDATE')"),
        };
        await using (var grant = new NpgsqlCommand($"""
            CREATE ROLE "{role}" NOLOGIN;
            {grantPrivilege};
            GRANT "{role}" TO "agentweaver-id-projects-config" WITH SET TRUE, INHERIT FALSE;
            """, admin))
            await grant.ExecuteNonQueryAsync();
        try
        {
            await using var direct = new NpgsqlCommand(directCheck, admin);
            Assert.True((bool)(await direct.ExecuteScalarAsync())!);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
        }
        finally
        {
            await using var revoke = new NpgsqlCommand($"""
                REVOKE "{role}" FROM "agentweaver-id-projects-config";
                {revokePrivilege};
                DROP ROLE "{role}";
                """, admin);
            await revoke.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeAndMigrationRejectPublicProviderGrants(bool columnGrant)
    {
        var permission = columnGrant ? "SELECT (display_name)" : "SELECT";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(connectionString);
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var grant = new NpgsqlCommand(
            $"GRANT {permission} ON projects_config.model_providers TO PUBLIC", admin))
            await grant.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, options));
        }
        finally
        {
            await using var revoke = new NpgsqlCommand(
                $"REVOKE {permission} ON projects_config.model_providers FROM PUBLIC", admin);
            await revoke.ExecuteNonQueryAsync();
        }
    }

    private async Task CreateDraftAsync(string id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """SET LOCAL ROLE "agentweaver-id-projects-config" """, id);
        await ExecuteAsync(connection, transaction, InsertHead, id);
        await ExecuteAsync(connection, transaction, InsertRevision, id);
        await transaction.CommitAsync();
    }

    private static Task AdvanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id) =>
        ExecuteAsync(connection, transaction, """
            UPDATE projects_config.model_providers
            SET current_revision = 2, updated_at = transaction_timestamp()
            WHERE provider_id = @id
            """, id);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string id, Guid? operation = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("operation", operation ?? Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }
}
