using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderGuardMigrationPostgresTests
{
    [Fact]
    public async Task PopulatedLegacyRowsSurviveAndDraftEditKeepsCredential()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        var before = await fixture.RevisionJsonAsync();
        var headsBefore = await fixture.HeadJsonAsync();
        await ProjectsConfigMigrator.MigrateAsync(fixture.Migration, fixture.Options);
        Assert.Equal(before, await fixture.RevisionJsonAsync());
        Assert.Equal(headsBefore, await fixture.HeadJsonAsync());
        Assert.True(await fixture.ScalarAsync("""
            SELECT bool_and(write_transaction_id = 'legacy')
            FROM projects_config.model_provider_revisions
            """));
        Assert.True(await fixture.ScalarAsync("""
            SELECT bool_and(write_transaction_id = 'legacy')
            FROM projects_config.model_providers
            """));

        await using var connection = await fixture.Admin.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SET LOCAL ROLE "agentweaver-id-projects-config";
            UPDATE projects_config.model_providers
            SET current_revision = 3, display_name = 'edited',
                updated_at = '2026-01-03T00:00:00Z'::timestamptz
            WHERE provider_id = 'legacy-draft';
            INSERT INTO projects_config.model_provider_revisions
              (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
               display_name, type, base_url, deployment, wire_api, azure_api_version, reasoning_efforts,
               prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
               credential_secret_id, created_by_actor_id, created_at)
            SELECT h.provider_id, 3, h.scope, h.project_id, gen_random_uuid(), repeat('a',64),
                   h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.azure_api_version,
                   h.reasoning_efforts, h.prompt_capacity_tokens, repeat('b',64), previous.credential_version,
                   previous.credential_slot_id, previous.credential_secret_id, h.updated_by_actor_id, h.updated_at
            FROM projects_config.model_providers h
            JOIN projects_config.model_provider_revisions previous
              ON previous.provider_id = h.provider_id AND previous.provider_revision = 2
            WHERE h.provider_id = 'legacy-draft';
            """, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        Assert.True(await fixture.ScalarAsync("""
            SELECT ROW(a.credential_version, a.credential_slot_id, a.credential_secret_id)
                   IS NOT DISTINCT FROM
                   ROW(b.credential_version, b.credential_slot_id, b.credential_secret_id)
            FROM projects_config.model_provider_revisions a
            JOIN projects_config.model_provider_revisions b ON b.provider_id = a.provider_id
            WHERE a.provider_id = 'legacy-draft' AND a.provider_revision = 2 AND b.provider_revision = 3
            """));

        foreach (var credential in new string?[] { null, "rotated" })
        {
            await using var attackConnection = await fixture.Admin.OpenConnectionAsync();
            await using var attackTransaction = await attackConnection.BeginTransactionAsync();
            await using var attack = new NpgsqlCommand("""
                SET LOCAL ROLE "agentweaver-id-projects-config";
                UPDATE projects_config.model_providers
                SET current_revision = 4, updated_at = '2026-01-04T00:00:00Z'::timestamptz
                WHERE provider_id = 'legacy-draft';
                INSERT INTO projects_config.model_provider_revisions
                  (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                   display_name, type, base_url, deployment, wire_api, azure_api_version, reasoning_efforts,
                   prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
                   credential_secret_id, created_by_actor_id, created_at)
                SELECT h.provider_id, 4, h.scope, h.project_id, gen_random_uuid(), repeat('a',64),
                       h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.azure_api_version,
                       h.reasoning_efforts, h.prompt_capacity_tokens, repeat('b',64), @credential,
                       previous.credential_slot_id, previous.credential_secret_id, h.updated_by_actor_id, h.updated_at
                FROM projects_config.model_providers h
                JOIN projects_config.model_provider_revisions previous
                  ON previous.provider_id = h.provider_id AND previous.provider_revision = 3
                WHERE h.provider_id = 'legacy-draft';
                """, attackConnection, attackTransaction);
            attack.Parameters.Add("credential", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)credential ?? DBNull.Value;
            await attack.ExecuteNonQueryAsync();
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => attackTransaction.CommitAsync());
            Assert.Equal("ck_model_provider_write_guard", rejected.ConstraintName);
        }
        await using (var availableConnection = await fixture.Admin.OpenConnectionAsync())
        await using (var availableTransaction = await availableConnection.BeginTransactionAsync())
        await using (var change = new NpgsqlCommand("""
            SET LOCAL ROLE "agentweaver-id-projects-config";
            UPDATE projects_config.model_providers SET display_name = 'unapproved'
            WHERE provider_id = 'legacy-available';
            """, availableConnection, availableTransaction))
        {
            await change.ExecuteNonQueryAsync();
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => availableTransaction.CommitAsync());
            Assert.Equal("ck_model_provider_write_guard", rejected.ConstraintName);
        }
        Assert.True(await fixture.ScalarAsync("""
            SELECT (SELECT current_revision FROM projects_config.model_providers WHERE provider_id = 'legacy-draft') = 3
               AND (SELECT state FROM projects_config.model_providers WHERE provider_id = 'legacy-available') = 'Available'
            """));

        await using var db = new ProjectsConfigDbContext(fixture.Options);
        var failure = await Record.ExceptionAsync(
            () => db.GetService<IMigrator>().MigrateAsync("20261119132116_ReserveModelProviderReferences"));
        Assert.Equal("55000", PostgresFailure(failure).SqlState);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("index")]
    [InlineData("trigger")]
    [InlineData("foreign-key")]
    public async Task CorruptLegacyStateRollsBackSchemaAndHistory(string corruption)
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        await fixture.ExecuteAsync(corruption switch
        {
            "missing" => "UPDATE projects_config.model_providers SET current_revision = 3 WHERE provider_id = 'legacy-draft'",
            "mismatch" => "UPDATE projects_config.model_providers SET display_name = 'mismatch' WHERE provider_id = 'legacy-draft'",
            "trigger" => "ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER model_provider_revisions_immutable",
            "foreign-key" => """ALTER TABLE projects_config.model_providers DROP CONSTRAINT "FK_model_providers_projects_project_id" """,
            _ => """
                DROP INDEX projects_config.ux_model_provider_revisions_operation_id;
                INSERT INTO projects_config.model_provider_revisions
                  (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                   display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                   created_by_actor_id, created_at)
                SELECT provider_id, 3, scope, project_id, operation_id, request_fingerprint,
                       display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                       created_by_actor_id, created_at
                FROM projects_config.model_provider_revisions
                WHERE provider_id = 'legacy-draft' AND provider_revision = 2
                """,
        });
        var before = await fixture.RevisionJsonAsync();
        var failure = await Record.ExceptionAsync(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.Migration, fixture.Options));
        Assert.Equal("55000", PostgresFailure(failure).SqlState);
        Assert.Equal(before, await fixture.RevisionJsonAsync());
        Assert.True(await fixture.ScalarAsync("""
            SELECT NOT EXISTS (
              SELECT 1 FROM information_schema.columns WHERE table_schema = 'projects_config'
                AND table_name IN ('model_providers','model_provider_revisions')
                AND column_name = 'write_transaction_id')
            AND NOT EXISTS (
              SELECT 1 FROM projects_config.__ef_migrations_history
              WHERE "MigrationId" LIKE '%GuardModelProviderPublication')
            AND to_regprocedure('projects_config.check_model_provider_pair()') IS NULL
            AND NOT has_schema_privilege(
                'agentweaver-id-projects-config', 'projects_config', 'USAGE')
            """));
    }

    [Fact]
    public async Task WrongPrincipalCannotApplyPendingMigration()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.Admin, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        var before = await fixture.RevisionJsonAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.Admin, options));
        Assert.Equal(before, await fixture.RevisionJsonAsync());
        Assert.True(await fixture.ScalarAsync("""
            SELECT NOT EXISTS (
              SELECT 1 FROM projects_config.__ef_migrations_history
              WHERE "MigrationId" LIKE '%GuardModelProviderPublication')
              AND to_regprocedure('projects_config.check_model_provider_pair()') IS NULL
            """));
    }

    [Fact]
    public async Task GrantFailureRollsBackGuardMigrationAndHistory()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        var before = await fixture.RevisionJsonAsync();
        await fixture.ExecuteAsync("GRANT SELECT (result) ON projects_config.project_setup_operations TO PUBLIC");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.Migration, fixture.Options));
        Assert.Equal(before, await fixture.RevisionJsonAsync());
        Assert.True(await fixture.ScalarAsync("""
            SELECT NOT EXISTS (
              SELECT 1 FROM projects_config.__ef_migrations_history
              WHERE "MigrationId" LIKE '%GuardModelProviderPublication')
              AND to_regprocedure('projects_config.check_model_provider_pair()') IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM information_schema.columns WHERE table_schema = 'projects_config'
                    AND table_name IN ('model_providers','model_provider_revisions')
                    AND column_name = 'write_transaction_id')
              AND NOT has_schema_privilege(
                  'agentweaver-id-projects-config', 'projects_config', 'USAGE')
            """));
    }

    [Fact]
    public async Task ConcurrentProductionMigrationsApplyPendingMigrationOnce()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ProjectsConfigMigrator.MigrateAsync(fixture.Migration, fixture.Options)));
        await using var db = new ProjectsConfigDbContext(fixture.Options);
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(migrations, name => name.EndsWith("_GuardModelProviderPublication", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task ProductionMigrateCommandUpgradesPopulatedDatabase()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        var before = await fixture.RevisionJsonAsync();
        var root = FindRepositoryRoot();
        var caPath = Path.Combine(AppContext.BaseDirectory, $"guard-test-ca-{Guid.NewGuid():N}.pem");
        try
        {
            var connectionString = await fixture.EnableTlsAsync(caPath);
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var token = builder.Password;
            builder.Password = null;
            builder.Options = "-c role=agentweaver-id-projects-config-migration";
            builder.RootCertificate = caPath;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var assembly = Path.Combine(root, "services", "projects-config", "Agentweaver.Projects.Config",
                "bin", configuration, "net10.0", "Agentweaver.Projects.Config.dll");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("--migrate");
            start.Environment["ConnectionStrings__ProjectsConfigMigration"] = builder.ConnectionString;
            start.Environment["ProjectsConfig__Migration__AccessToken"] = token;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(before, await fixture.RevisionJsonAsync());
            await using var db = new ProjectsConfigDbContext(fixture.Options);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            if (File.Exists(caPath))
                File.Delete(caPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentweaver.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task LockTimeoutRollsBackGuardMigrationAndHistory()
    {
        await using var fixture = await PreGuardDatabase.StartAsync();
        await fixture.ExecuteAsync(LegacyRows);
        await using var connection = await fixture.Admin.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var held = new NpgsqlCommand(
            "LOCK TABLE projects_config.model_providers IN ACCESS SHARE MODE", connection, transaction);
        await held.ExecuteNonQueryAsync();
        var failure = await Record.ExceptionAsync(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.Migration, fixture.Options));
        Assert.Equal("55P03", PostgresFailure(failure).SqlState);
        await transaction.RollbackAsync();
        Assert.True(await fixture.ScalarAsync("""
            SELECT NOT EXISTS (
              SELECT 1 FROM projects_config.__ef_migrations_history
              WHERE "MigrationId" LIKE '%GuardModelProviderPublication')
              AND to_regprocedure('projects_config.check_model_provider_pair()') IS NULL
            """));
    }

    private static PostgresException PostgresFailure(Exception? failure) =>
        Assert.IsType<PostgresException>(failure is InvalidOperationException ? failure.InnerException : failure);

    internal const string LegacyRows = """
        INSERT INTO projects_config.model_providers
          (provider_id, scope, display_name, type, base_url, deployment, reasoning_efforts,
           state, current_revision, created_by_actor_id, created_at, updated_by_actor_id, updated_at)
        VALUES
          ('legacy-draft', 'Platform', 'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'],
           'Draft', 2, 'actor', '2026-01-01Z'::timestamptz, 'actor', '2026-01-02Z'::timestamptz),
          ('legacy-available', 'Platform', 'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'],
           'Available', 1, 'actor', '2026-01-01Z'::timestamptz, 'actor', '2026-01-02Z'::timestamptz);
        INSERT INTO projects_config.model_provider_revisions
          (provider_id, provider_revision, scope, operation_id, request_fingerprint,
           display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
           credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
        VALUES
          ('legacy-draft', 1, 'Platform', gen_random_uuid(), repeat('a',64),
           'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'], repeat('b',64),
           NULL, NULL, NULL, 'actor', '2026-01-01Z'::timestamptz),
          ('legacy-draft', 2, 'Platform', gen_random_uuid(), repeat('a',64),
           'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'], repeat('b',64),
           '0123456789abcdef0123456789abcdef', NULL, 'retained-secret', 'actor', '2026-01-02Z'::timestamptz),
          ('legacy-available', 1, 'Platform', gen_random_uuid(), repeat('a',64),
           'name', 'openai', 'https://example.test', 'deployment', ARRAY['low'], repeat('b',64),
           '0123456789abcdef0123456789abcdef', NULL, 'retained-secret', 'actor', '2026-01-02Z'::timestamptz);
        """;

    internal sealed class PreGuardDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        public NpgsqlDataSource Admin { get; private set; } = null!;
        public NpgsqlDataSource Migration { get; private set; } = null!;
        public DbContextOptions<ProjectsConfigDbContext> Options { get; private set; } = null!;

        public NpgsqlDataSource CreateRuntimeDataSource()
        {
            var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Options = "-c role=agentweaver-id-projects-config",
            };
            return NpgsqlDataSource.Create(builder.ConnectionString);
        }

        public async Task<string> EnableTlsAsync(string caPath)
        {
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=guard-test-root", rootKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1));
            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest("CN=localhost", serverKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(names.Build());
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var server = serverRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
            await File.WriteAllTextAsync(caPath, root.ExportCertificatePem());
            await WriteContainerFileAsync("server.crt", server.ExportCertificatePem());
            await WriteContainerFileAsync("server.key", serverKey.ExportPkcs8PrivateKeyPem());
            await ExecuteAsync("ALTER SYSTEM SET ssl_cert_file = 'server.crt'");
            await ExecuteAsync("ALTER SYSTEM SET ssl_key_file = 'server.key'");
            await ExecuteAsync("ALTER SYSTEM SET ssl = 'on'");
            Assert.True(await ScalarAsync("SELECT pg_reload_conf()"));
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await ScalarAsync("SELECT current_setting('ssl') = 'on'"))
                    return container.GetConnectionString();
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The disposable PostgreSQL TLS listener did not start.");
        }

        private async Task WriteContainerFileAsync(string name, string content)
        {
            var start = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add("--user");
            start.ArgumentList.Add("root");
            start.ArgumentList.Add(container.Id);
            start.ArgumentList.Add("sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"umask 077; cat > \"$PGDATA/{name}\"; chown postgres:postgres \"$PGDATA/{name}\"");
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(content);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            Assert.Equal(0, process.ExitCode);
        }

        public static async Task<PreGuardDatabase> StartAsync()
        {
            var fixture = new PreGuardDatabase();
            await fixture.container.StartAsync();
            fixture.Admin = NpgsqlDataSource.Create(fixture.container.GetConnectionString());
            await fixture.ExecuteAsync("""
                CREATE ROLE "agentweaver-id-projects-config-migration"
                  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
                CREATE ROLE "agentweaver-id-projects-config"
                  NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
                CREATE SCHEMA projects_config AUTHORIZATION "agentweaver-id-projects-config-migration";
                """);
            var builder = new NpgsqlConnectionStringBuilder(fixture.container.GetConnectionString())
            {
                Options = "-c role=agentweaver-id-projects-config-migration",
            };
            fixture.Migration = NpgsqlDataSource.Create(builder.ConnectionString);
            fixture.Options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(fixture.Migration, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(fixture.Options);
            await db.GetService<IMigrator>().MigrateAsync("20261119132116_ReserveModelProviderReferences");
            return fixture;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<bool> ScalarAsync(string sql)
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async Task<string> RevisionJsonAsync()
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT coalesce(string_agg((to_jsonb(r) - 'write_transaction_id')::text, ',' ORDER BY provider_id, provider_revision), '')
                FROM projects_config.model_provider_revisions r
                """, connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<string> HeadJsonAsync()
        {
            await using var connection = await Admin.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT coalesce(string_agg((to_jsonb(h) - 'write_transaction_id')::text, ',' ORDER BY provider_id), '')
                FROM projects_config.model_providers h
                """, connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await Migration.DisposeAsync();
            await Admin.DisposeAsync();
            await container.DisposeAsync();
        }
    }
}
