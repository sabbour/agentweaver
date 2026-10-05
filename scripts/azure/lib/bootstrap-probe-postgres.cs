#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:package Azure.Identity@1.17.1
#:project ../../../packages/Agentweaver.Persistence.Postgres/Agentweaver.Persistence.Postgres.csproj

using Azure.Core;
using Azure.Identity;
using Npgsql;
using Agentweaver.Persistence.Postgres;
using System.Text.RegularExpressions;

if (args.Length != 1 || args[0] != "--execute")
    throw new ArgumentException("Probe PostgreSQL bootstrap requires the explicit --execute argument.");
var host = Environment.GetEnvironmentVariable("IdentityBroker__Bootstrap__PostgresHost");
var admin = Environment.GetEnvironmentVariable("IdentityBroker__Bootstrap__AdminUsername");
var principal = Environment.GetEnvironmentVariable("FoundationProbe__Bootstrap__PrincipalObjectId");
if (host != "aw-v1-p0-pg.postgres.database.azure.com" || string.IsNullOrWhiteSpace(admin) ||
    !Guid.TryParse(principal, out var principalId))
    throw new ArgumentException("Probe PostgreSQL bootstrap requires the exact approved host, administrator, and Probe principal.");

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = await new AzureCliCredential().GetTokenAsync(
    new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), cancellation.Token);
var settings = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = 15432, Database = "agentweaver", Username = admin,
    Password = token.Token, SslMode = SslMode.VerifyFull, Timeout = 30, CommandTimeout = 30,
    ApplicationName = "agentweaver-foundation-probe-bootstrap",
};
var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
builder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = host);
var principalSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" };
var principalBuilder = new NpgsqlDataSourceBuilder(principalSettings.ConnectionString);
principalBuilder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = host);
await using var principalSource = principalBuilder.Build();
await using var principalConnection = await principalSource.OpenConnectionAsync(cancellation.Token);
await using (var guard = new NpgsqlCommand("SELECT current_user = @admin AND current_database() = 'postgres'", principalConnection))
{
    guard.Parameters.AddWithValue("admin", admin);
    if (!Equals(await guard.ExecuteScalarAsync(cancellation.Token), true))
        throw new InvalidOperationException("Probe principal setup did not connect as the approved administrator to postgres.");
}
await using var source = builder.Build();
await using var connection = await source.OpenConnectionAsync(cancellation.Token);
await using (var command = new NpgsqlCommand("SELECT current_user = @admin AND current_database() = 'agentweaver'", connection))
{
    command.Parameters.AddWithValue("admin", admin);
    if (!Equals(await command.ExecuteScalarAsync(cancellation.Token), true))
        throw new InvalidOperationException("Probe bootstrap did not connect as the approved administrator to agentweaver.");
}
await using var transaction = await connection.BeginTransactionAsync(cancellation.Token);
async Task<object?> Scalar(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    return await command.ExecuteScalarAsync(cancellation.Token);
}
async Task Execute(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync(cancellation.Token);
}
var roleExists = Equals(await Scalar("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'foundation_probe_runtime')"), true);
var schemaExists = Equals(await Scalar("SELECT to_regnamespace('foundation_probe') IS NOT NULL"), true);
if (schemaExists && !roleExists)
    throw new InvalidOperationException("Partial Probe PostgreSQL state requires explicit reconciliation before retry.");
if (roleExists)
{
    await using var mapping = new NpgsqlCommand("""
        SELECT count(*) = 1
        FROM pg_catalog.pgaadauth_list_principals(false)
        WHERE rolname::text = 'foundation_probe_runtime' AND objectid = @principal
          AND principaltype = 'service' AND isadmin = 0
        """, principalConnection);
    mapping.Parameters.AddWithValue("principal", principalId.ToString());
    if (!Equals(await mapping.ExecuteScalarAsync(cancellation.Token), true))
        throw new InvalidOperationException("Existing Probe role does not map to the exact approved principal.");
}
else
{
    await using var principalTransaction = await principalConnection.BeginTransactionAsync(cancellation.Token);
    await using (var create = new NpgsqlCommand(
        "SELECT pg_catalog.pgaadauth_create_principal_with_oid('foundation_probe_runtime', @principal, 'service', false, false)",
        principalConnection, principalTransaction))
    {
        create.Parameters.AddWithValue("principal", principalId.ToString());
        await create.ExecuteNonQueryAsync(cancellation.Token);
    }
    await using (var attributes = new NpgsqlCommand(
        "ALTER ROLE foundation_probe_runtime NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT",
        principalConnection, principalTransaction))
        await attributes.ExecuteNonQueryAsync(cancellation.Token);
    await principalTransaction.CommitAsync(cancellation.Token);
}
if (!Equals(await Scalar("""
    SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
      AND NOT rolreplication AND NOT rolinherit
    FROM pg_roles WHERE rolname = 'foundation_probe_runtime'
    """), true))
    throw new InvalidOperationException("Existing Probe role attributes differ from the approved runtime role.");
if (!schemaExists)
{
    await Execute("CREATE SCHEMA foundation_probe");
    await Execute("REVOKE ALL ON SCHEMA foundation_probe FROM PUBLIC");
    await Execute("GRANT CONNECT ON DATABASE agentweaver TO foundation_probe_runtime");
    await Execute("GRANT USAGE ON SCHEMA foundation_probe TO foundation_probe_runtime");
    foreach (var file in new[] { "001_outbox.sql", "002_consumer_inbox.sql" })
    {
        await using var migration = typeof(PostgresOutbox).Assembly.GetManifestResourceStream(
            $"Agentweaver.Persistence.Postgres.Migrations.{file}")
            ?? throw new InvalidOperationException("The admitted persistence migration is missing.");
        using var text = new StreamReader(migration);
        var sql = await text.ReadToEndAsync(cancellation.Token);
        if (Regex.IsMatch(sql, @"(?im)^\s*(BEGIN|COMMIT|ROLLBACK)\s*;"))
            throw new InvalidOperationException("An embedded persistence migration has an unexpected transaction envelope.");
        await Execute(sql.Replace("{schema}", "foundation_probe", StringComparison.Ordinal));
    }
    await Execute("""
        CREATE TABLE foundation_probe.outbox_schema_migrations (
            version integer PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        INSERT INTO foundation_probe.outbox_schema_migrations (version) VALUES (1), (2)
        """);
    var effectsSql = await File.ReadAllTextAsync(
        Path.Combine("tools", "Agentweaver.FoundationProbe", "schema", "001_probe_effects.sql"),
        cancellation.Token);
    var transactionStatements = Regex.Matches(effectsSql, @"(?m)^(BEGIN|COMMIT);\r?$");
    if (transactionStatements.Count != 2 ||
        transactionStatements[0].Groups[1].Value != "BEGIN" ||
        transactionStatements[1].Groups[1].Value != "COMMIT")
        throw new InvalidOperationException("The canonical Probe effects migration has an unexpected transaction envelope.");
    await Execute(Regex.Replace(effectsSql, @"(?m)^(BEGIN|COMMIT);\r?$", ""));
    await Execute("""
        GRANT SELECT, INSERT, UPDATE, DELETE
        ON foundation_probe.outbox_streams, foundation_probe.outbox_events,
           foundation_probe.consumer_inbox_receipts
        TO foundation_probe_runtime
        """);
}
var privileges = Equals(await Scalar("""
    SELECT
      has_database_privilege('foundation_probe_runtime', 'agentweaver', 'CONNECT')
      AND NOT has_database_privilege('foundation_probe_runtime', 'agentweaver', 'CREATE')
      AND NOT has_schema_privilege('foundation_probe_runtime', 'foundation_probe', 'CREATE')
      AND has_schema_privilege('foundation_probe_runtime', 'foundation_probe', 'USAGE')
      AND NOT has_schema_privilege('foundation_probe_runtime', 'identity_broker', 'USAGE')
      AND (SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolinherit
           FROM pg_roles WHERE rolname = 'foundation_probe_runtime')
      AND NOT EXISTS (
        SELECT 1 FROM pg_auth_members
        WHERE member = (SELECT oid FROM pg_roles WHERE rolname = 'foundation_probe_runtime'))
      AND EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'foundation_probe'
        AND nspowner = (SELECT oid FROM pg_roles WHERE rolname = current_user))
      AND (SELECT array_agg(version ORDER BY version) = ARRAY[1, 2]
           FROM foundation_probe.outbox_schema_migrations)
      AND has_table_privilege('foundation_probe_runtime', 'foundation_probe.probe_effects', 'SELECT')
      AND has_table_privilege('foundation_probe_runtime', 'foundation_probe.probe_effects', 'INSERT')
      AND NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.probe_effects', 'UPDATE')
      AND NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.probe_effects', 'DELETE')
      AND NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.outbox_schema_migrations', 'INSERT')
      AND NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.outbox_schema_migrations', 'UPDATE')
      AND NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.outbox_schema_migrations', 'DELETE')
      AND NOT EXISTS (
        SELECT 1 FROM pg_tables
        WHERE schemaname = 'foundation_probe' AND tableowner <> current_user)
      AND NOT EXISTS (
        SELECT 1 FROM unnest(ARRAY['outbox_streams', 'outbox_events', 'consumer_inbox_receipts']) AS required(table_name)
        WHERE NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.' || required.table_name, 'SELECT')
           OR NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.' || required.table_name, 'INSERT')
           OR NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.' || required.table_name, 'UPDATE')
           OR NOT has_table_privilege('foundation_probe_runtime', 'foundation_probe.' || required.table_name, 'DELETE'))
    """), true);
if (!privileges)
    throw new InvalidOperationException("Probe PostgreSQL privilege boundary does not match the approved owned schema.");
await transaction.CommitAsync(cancellation.Token);
Console.WriteLine("FOUNDATION_PROBE_POSTGRES_BOOTSTRAP_OK database=agentweaver schema=foundation_probe");
Console.WriteLine("FOUNDATION_PROBE_POSTGRES_PRIVILEGES role=foundation_probe_runtime schema=foundation_probe runtimeDdl=false brokerSchemaAccess=false");
