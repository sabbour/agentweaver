#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:package Azure.Identity@1.17.1
#:project ../../../packages/Agentweaver.Persistence.Postgres/Agentweaver.Persistence.Postgres.csproj

// Hands ownership of the Environment, Core (orchestrator), and Events (events_sessions) schemas from the
// operator's Entra role to the dedicated P1 migration role, so later migration Jobs can run
// as that role with no operator connection. The database maintenance runner starts this
// through its owned relay. Every step is idempotent: a second run changes nothing and only
// repeats the readback. It never creates, drops, or alters data.

using Azure.Core;
using Azure.Identity;
using Npgsql;

if (args.Length != 1 || args[0] != "--execute")
    throw new ArgumentException("The P1 ownership handover requires the explicit --execute argument.");

// All inputs are checked before any connection. The host and role are fixed values, so a
// wrong environment can only stop the run, never point it at another server or role.
const string ExpectedHost = "agentweaver-pg.postgres.database.azure.com";
const string ExpectedRole = "agentweaver-id-p1-postgres-migration";
string[] schemas = ["environment", "orchestrator", "events_sessions"];
var host = Environment.GetEnvironmentVariable("P1Handover__PostgresHost");
var admin = Environment.GetEnvironmentVariable("P1Handover__AdminUsername");
var role = Environment.GetEnvironmentVariable("P1Handover__MigrationRole");
var principal = Environment.GetEnvironmentVariable("P1Handover__MigrationPrincipalObjectId");
var localPort = int.TryParse(Environment.GetEnvironmentVariable("P1Handover__LocalPort"), out var port) ? port : 15432;
if (host != ExpectedHost || string.IsNullOrWhiteSpace(admin) || role != ExpectedRole ||
    !Guid.TryParse(principal, out var principalId))
    throw new ArgumentException("The P1 ownership handover requires the exact host, administrator, migration role, and principal.");

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var token = await new AzureCliCredential().GetTokenAsync(
    new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), cancellation.Token);
NpgsqlDataSource CreateSource(string database)
{
    // The relay forwards loopback to the server, so TLS still verifies the real host name.
    var settings = new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = localPort, Database = database, Username = admin,
        Password = token.Token, SslMode = SslMode.VerifyFull, Timeout = 30, CommandTimeout = 60,
        ApplicationName = "agentweaver-p1-ownership-handover",
    };
    var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
    builder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = host);
    return builder.Build();
}

// Step 1: the Entra principal mapping lives in the postgres database. Create it only when the
// role is missing; an existing role must map to exactly this service principal and no other.
await using (var principalSource = CreateSource("postgres"))
await using (var principalConnection = await principalSource.OpenConnectionAsync(cancellation.Token))
{
    await using (var guard = new NpgsqlCommand(
        "SELECT current_user = @admin AND current_database() = 'postgres'", principalConnection))
    {
        guard.Parameters.AddWithValue("admin", admin);
        if (!Equals(await guard.ExecuteScalarAsync(cancellation.Token), true))
            throw new InvalidOperationException("The handover did not connect as the approved administrator.");
    }
    await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)", principalConnection);
    exists.Parameters.AddWithValue("role", role);
    if (!Equals(await exists.ExecuteScalarAsync(cancellation.Token), true))
    {
        await using var create = new NpgsqlCommand(
            "SELECT pg_catalog.pgaadauth_create_principal_with_oid(@role, @principal, 'service', false, false)",
            principalConnection);
        create.Parameters.AddWithValue("role", role);
        create.Parameters.AddWithValue("principal", principalId.ToString());
        await create.ExecuteNonQueryAsync(cancellation.Token);
    }
    await using var mapping = new NpgsqlCommand("""
        SELECT count(*) = 1
        FROM pg_catalog.pgaadauth_list_principals(false)
        WHERE rolname::text = @role AND objectid = @principal AND principaltype = 'service' AND isadmin = 0
        """, principalConnection);
    mapping.Parameters.AddWithValue("role", role);
    mapping.Parameters.AddWithValue("principal", principalId.ToString());
    if (!Equals(await mapping.ExecuteScalarAsync(cancellation.Token), true))
        throw new InvalidOperationException("The migration role does not map to the exact migration identity.");
}

await using var source = CreateSource("agentweaver");
await using var connection = await source.OpenConnectionAsync(cancellation.Token);
// Step 2 runs in one transaction. A failure at any point rolls back every owner change and the
// temporary SET membership, so the database is never left half handed over.
await using var transaction = await connection.BeginTransactionAsync(cancellation.Token);
var quotedRole = Quote(role);
async Task Execute(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync(cancellation.Token);
}
async Task<object?> Scalar(string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    command.Parameters.AddWithValue("role", role);
    command.Parameters.AddWithValue("admin", admin);
    command.Parameters.AddWithValue("schemas", schemas);
    return await command.ExecuteScalarAsync(cancellation.Token);
}
if (!Equals(await Scalar("SELECT current_user = @admin AND current_database() = 'agentweaver'"), true))
    throw new InvalidOperationException("The handover did not connect as the approved administrator to agentweaver.");
if (!Equals(await Scalar("SELECT count(*) = 3 FROM pg_namespace WHERE nspname = ANY(@schemas)"), true))
    throw new InvalidOperationException("All three P1 schemas must already exist. The handover never creates or initializes them.");

// The role gets login only. CONNECT lets it open the database. CREATE on the database is needed
// because the migrators run CREATE SCHEMA IF NOT EXISTS, and PostgreSQL checks that privilege
// before it checks whether the schema exists.
await Execute($"ALTER ROLE {quotedRole} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
await Execute($"GRANT CONNECT, CREATE ON DATABASE agentweaver TO {quotedRole}");

// The bootstrap gives the administrator ADMIN on workload roles but not SET. ALTER ... OWNER TO
// needs SET on the new owner, so add it for this transaction only and remove it again below.
await Execute($"GRANT {quotedRole} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE");

// Collect every object the migration role does not own yet. Sequences owned by a table column
// and composite row types of tables move with their table, so they are not listed separately.
var statements = new List<string>();
await using (var command = new NpgsqlCommand("""
    SELECT format('ALTER SCHEMA %I OWNER TO %I', n.nspname, @role)
    FROM pg_namespace n
    WHERE n.nspname = ANY(@schemas) AND n.nspowner <> (SELECT oid FROM pg_roles WHERE rolname = @role)
    UNION ALL
    SELECT format('ALTER %s %I.%I OWNER TO %I',
        CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW'
          WHEN 'S' THEN 'SEQUENCE' WHEN 'f' THEN 'FOREIGN TABLE' WHEN 'c' THEN 'TYPE' ELSE 'TABLE' END,
        n.nspname, c.relname, @role)
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
      AND c.relowner <> (SELECT oid FROM pg_roles WHERE rolname = @role)
      AND NOT (c.relkind = 'S' AND EXISTS (
        SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
          AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')))
    UNION ALL
    SELECT format('ALTER %s %s OWNER TO %I', CASE p.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END,
        p.oid::regprocedure, @role)
    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = ANY(@schemas) AND p.prokind IN ('f', 'p')
      AND p.proowner <> (SELECT oid FROM pg_roles WHERE rolname = @role)
    UNION ALL
    SELECT format('ALTER %s %I.%I OWNER TO %I', CASE t.typtype WHEN 'd' THEN 'DOMAIN' ELSE 'TYPE' END,
        n.nspname, t.typname, @role)
    FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
    WHERE n.nspname = ANY(@schemas) AND t.typtype IN ('e', 'd', 'r') AND t.typrelid = 0
      AND t.typowner <> (SELECT oid FROM pg_roles WHERE rolname = @role)
    """, connection, transaction))
{
    command.Parameters.AddWithValue("role", role);
    command.Parameters.AddWithValue("schemas", schemas);
    await using var reader = await command.ExecuteReaderAsync(cancellation.Token);
    while (await reader.ReadAsync(cancellation.Token))
        statements.Add(reader.GetString(0));
}
// Schemas come first in the query, so the role owns each schema before its objects move.
foreach (var statement in statements)
    await Execute(statement);

// Remove the temporary SET option. ADMIN stays as the bootstrap left it.
await Execute($"REVOKE SET OPTION FOR {quotedRole} FROM CURRENT_USER");

// Step 3: read back the whole boundary before committing. Any difference rolls everything back.
var verified = Equals(await Scalar("""
    SELECT
      (SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
         AND NOT rolreplication AND NOT rolinherit FROM pg_roles WHERE rolname = @role)
      AND has_database_privilege(@role, 'agentweaver', 'CONNECT')
      AND has_database_privilege(@role, 'agentweaver', 'CREATE')
      -- The administrator keeps no SET or INHERIT path into the migration role.
      AND NOT EXISTS (
        SELECT 1 FROM pg_auth_members m
        WHERE m.roleid = (SELECT oid FROM pg_roles WHERE rolname = @role)
          AND (m.set_option OR m.inherit_option))
      -- The migration role is a member of nothing, so it cannot reach other roles.
      AND NOT EXISTS (
        SELECT 1 FROM pg_auth_members m WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = @role))
      AND NOT EXISTS (
        SELECT 1 FROM pg_namespace WHERE nspname = ANY(@schemas)
          AND nspowner <> (SELECT oid FROM pg_roles WHERE rolname = @role))
      AND NOT EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
          AND c.relowner <> (SELECT oid FROM pg_roles WHERE rolname = @role))
      AND NOT EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = ANY(@schemas) AND p.proowner <> (SELECT oid FROM pg_roles WHERE rolname = @role))
      AND NOT EXISTS (
        SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = ANY(@schemas) AND t.typtype IN ('e', 'd', 'r') AND t.typrelid = 0
          AND t.typowner <> (SELECT oid FROM pg_roles WHERE rolname = @role))
    """), true);
if (!verified)
    throw new InvalidOperationException("The P1 ownership handover readback differs from the expected boundary. Nothing was committed.");
await transaction.CommitAsync(cancellation.Token);
Console.WriteLine(
    $"P1_POSTGRES_OWNERSHIP_HANDOVER_VERIFIED role={role} schemas={string.Join(',', schemas)} changed={statements.Count}");

static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
