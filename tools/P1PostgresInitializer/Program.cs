extern alias AzureIdentity;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Environment;
using Agentweaver.EventsAndSessions;
using Agentweaver.Knowledge;
using Agentweaver.Orchestrator;
using Agentweaver.Projects.Config;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AzureCliCredential = AzureIdentity::Azure.Identity.AzureCliCredential;
using AzureCliCredentialOptions = AzureIdentity::Azure.Identity.AzureCliCredentialOptions;

if (args.Length == 1 && args[0] == "--plan")
{
    Console.WriteLine(await RunLocalAsync("node",
        ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]));
    return 0;
}
if (args.Length != 9 || args[0] != "--initialize" ||
    args[1] != "--source-sha" || args[3] != "--core-merge-sha" ||
    args[5] != "--plan-sha256" || args[7] != "--receipt")
    throw new ArgumentException(
        "Use --plan, or --initialize --source-sha SHA --core-merge-sha SHA --plan-sha256 SHA256 --receipt PATH. Initialization requires separate approval.");

var sourceSha = args[2];
var coreMergeSha = args[4];
var expectedPlanHash = args[6];
if (!Regex.IsMatch(sourceSha, "^[0-9a-f]{40}$") ||
    !Regex.IsMatch(coreMergeSha, "^[0-9a-f]{40}$") ||
    !Regex.IsMatch(expectedPlanHash, "^[0-9a-f]{64}$"))
    throw new ArgumentException("Full lowercase source, Core merge, and plan hashes are required.");
if (!string.IsNullOrWhiteSpace(await RunLocalAsync("git", ["status", "--porcelain"])) ||
    await RunLocalAsync("git", ["rev-parse", "HEAD"]) != sourceSha ||
    await RunLocalAsync("git", ["rev-parse", "origin/v1"]) != sourceSha)
    throw new InvalidOperationException("Initialization requires the clean, locally fetched, reviewed current v1 tip.");
await RunLocalAsync("git", ["merge-base", "--is-ancestor", coreMergeSha, sourceSha]);
var coreParents = await RunLocalAsync("git", ["show", "--format=%P", "--no-patch", coreMergeSha]);
if (coreParents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
    throw new InvalidOperationException("The supplied Core commit must be the actual separately reviewed merge.");
var migratorSource = await File.ReadAllTextAsync(
    Path.Combine("services", "orchestrator", "Agentweaver.Orchestrator", "CoordinationOwnerMigrator.cs"));
if (!Regex.IsMatch(migratorSource, @"Enumerable\.Range\(1,\s*16\)\.Concat\(\[18,\s*19,\s*20\]\)") ||
    !migratorSource.Contains("16 => \"project_backlog\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("18 => \"reviewed_remote_tool_snapshots\"", StringComparison.Ordinal))
    throw new InvalidOperationException("Reviewed Core 016/018 migration wiring is required before initialization; 017 is not permitted.");

var planJson = await RunLocalAsync("node", ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]);
var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))).ToLowerInvariant();
if (planHash != expectedPlanHash)
    throw new InvalidOperationException("The reviewed plan hash differs from the exact source plan.");
var plan = JsonSerializer.Deserialize<PostgresPlan>(planJson, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
}) ?? throw new InvalidOperationException("The exact source plan is missing.");
if (plan.Services.Any(service => service.Tables.Any(table => table.MutationDenialRequired)))
    throw new InvalidOperationException(
        "Initialization remains blocked until the separate row-lock mutation guards and restricted-role tests are reviewed. No credential or database connection was used.");

var receiptPath = Path.GetFullPath(args[8]);
var receiptRoot = Path.GetFullPath("Temp") + Path.DirectorySeparatorChar;
if (!receiptPath.StartsWith(receiptRoot, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Use a new receipt file beneath this source copy's Temp directory.");
Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
await using var receipt = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
async Task RecordAsync(string stage)
{
    var line = JsonSerializer.Serialize(new { stage, sourceSha, coreMergeSha, planHash, at = DateTimeOffset.UtcNow });
    await receipt.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
    receipt.Flush(flushToDisk: true);
}

await RecordAsync("source_checked");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var cancellationToken = cancellation.Token;
try
{
    var token = await new AzureCliCredential(new AzureCliCredentialOptions { TenantId = plan.Target.TenantId })
        .GetTokenAsync(new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), cancellationToken);
    NpgsqlDataSource CreateSource(string database)
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = plan.Target.ConnectionHost, Port = plan.Target.ConnectionPort,
            Database = database, Username = plan.Target.OperatorName, Password = token.Token,
            SslMode = SslMode.VerifyFull, Timeout = 30, CommandTimeout = 60,
            ApplicationName = "agentweaver-p1-postgres-operator-initializer",
        };
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        builder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = plan.Target.PostgresHost);
        return builder.Build();
    }

    await using var principalSource = CreateSource("postgres");
    await using var principalConnection = await principalSource.OpenConnectionAsync(cancellationToken);
    await using (var identity = new NpgsqlCommand("""
        SELECT current_database() = 'postgres' AND current_user = @operator
          AND EXISTS (
            SELECT 1 FROM pg_catalog.pgaadauth_list_principals(false)
            WHERE rolname::text = current_user AND objectid = @object AND principaltype = 'user')
        """, principalConnection))
    {
        identity.Parameters.AddWithValue("operator", plan.Target.OperatorName);
        identity.Parameters.AddWithValue("object", plan.Target.OperatorObjectId);
        if (await identity.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("The connection is not the exact existing operator on postgres.");
    }
    await using var source = CreateSource(plan.Target.Database);
    await using var connection = await source.OpenConnectionAsync(cancellationToken);
    await using (var fresh = new NpgsqlCommand("""
        SELECT current_user = @operator AND current_database() = 'agentweaver'
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles))
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = ANY(@schemas))
        """, connection))
    {
        fresh.Parameters.AddWithValue("operator", plan.Target.OperatorName);
        fresh.Parameters.AddWithValue("roles", plan.Services.Select(item => item.Role).ToArray());
        fresh.Parameters.AddWithValue("schemas", plan.Services.Select(item => item.Schema).ToArray());
        if (await fresh.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("The target contains existing, partial, or unapproved state. Refusing to adopt, reset, or retry it.");
    }
    var existingBoundary = await ReadExistingBoundaryAsync(connection, plan, cancellationToken);
    await RecordAsync("fresh_target_checked");

    await using (var transaction = await principalConnection.BeginTransactionAsync(cancellationToken))
    {
        foreach (var service in plan.Services)
        {
            await using var create = new NpgsqlCommand(
                "SELECT pg_catalog.pgaadauth_create_principal_with_oid(@role, @object, 'service', false, false)",
                principalConnection, transaction);
            create.Parameters.AddWithValue("role", service.Role);
            create.Parameters.AddWithValue("object", service.PrincipalObjectId);
            await create.ExecuteNonQueryAsync(cancellationToken);
            await ExecuteAsync(principalConnection, transaction,
                $"ALTER ROLE {Quote(service.Role)} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS", cancellationToken);
            await ExecuteAsync(principalConnection, transaction,
                $"GRANT {Quote(service.Role)} TO {Quote(plan.Target.OperatorName)} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
    await RecordAsync("five_runtime_roles_created");

    foreach (var service in plan.Services)
    {
        await ExecuteAsync(connection, null, $"CREATE SCHEMA {Quote(service.Schema)} AUTHORIZATION {Quote(plan.Target.OperatorName)}", cancellationToken);
        await ExecuteAsync(connection, null, $"REVOKE ALL ON SCHEMA {Quote(service.Schema)} FROM PUBLIC", cancellationToken);
        await RecordAsync($"{service.Service}:schema_created");
        switch (service.Service)
        {
            case "projects-config":
                await ProjectsConfigMigrator.MigrateAsync(source,
                    new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                        .UseNpgsql(source, settings => settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options,
                    cancellationToken);
                break;
            case "environment":
                await EnvironmentMigrator.MigrateAsync(source,
                    new DbContextOptionsBuilder<EnvironmentDbContext>()
                        .UseNpgsql(source, settings => settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options,
                    cancellationToken);
                break;
            case "knowledge":
                await KnowledgeMigrator.MigrateAsync(source, service.Schema, cancellationToken);
                break;
            case "events-and-sessions":
                await EventsAndSessionsMigrator.MigrateAsync(source, service.Schema, cancellationToken);
                break;
            case "orchestrator":
                await CoordinationOwnerMigrator.MigrateAsync(source, service.Schema, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The source plan contains an unsupported service.");
        }
        await RecordAsync($"{service.Service}:migrated");
        await AssertOwnedObjectsAsync(connection, service, plan.Target.OperatorName, cancellationToken);
        await using var grants = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, grants, $"GRANT CONNECT ON DATABASE agentweaver TO {Quote(service.Role)}", cancellationToken);
        await ExecuteAsync(connection, grants, $"GRANT USAGE ON SCHEMA {Quote(service.Schema)} TO {Quote(service.Role)}", cancellationToken);
        foreach (var table in service.Tables)
        {
            var relation = $"{Quote(service.Schema)}.{Quote(table.Table)}";
            await ExecuteAsync(connection, grants,
                $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {Quote(service.Role)}", cancellationToken);
            if (table.LockColumn is not null)
                await ExecuteAsync(connection, grants,
                    $"GRANT UPDATE ({Quote(table.LockColumn)}) ON TABLE {relation} TO {Quote(service.Role)}", cancellationToken);
        }
        foreach (var function in service.Functions)
        {
            var signature = $"{Quote(service.Schema)}.{function.Signature}";
            await ExecuteAsync(connection, grants, $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken);
            if (function.Execute)
                await ExecuteAsync(connection, grants, $"GRANT EXECUTE ON FUNCTION {signature} TO {Quote(service.Role)}", cancellationToken);
        }
        if (await ReadExistingBoundaryAsync(connection, plan, cancellationToken) != existingBoundary)
            throw new InvalidOperationException("Existing database, schema, owner, or default privileges changed. The current grant stage is not committed.");
        await transactionCheckAsync(connection, grants, service, cancellationToken);
        await grants.CommitAsync(cancellationToken);
        await RecordAsync($"{service.Service}:exact_grants_checked_and_committed");
    }
    await RecordAsync("operator_initialization_finished_runtime_identity_checks_still_required");
    Console.WriteLine("P1_POSTGRES_OPERATOR_INITIALIZATION_FINISHED runtimeIdentityProof=false deploymentProof=false");
    return 0;
}
catch (Exception error)
{
    await RecordAsync("failed_requires_explicit_reconciliation");
    Console.Error.WriteLine($"P1 PostgreSQL initialization stopped: {error.GetType().Name}. Committed stages remain in {receiptPath}; do not retry or reset partial state.");
    if (error is PostgresException postgres)
        Console.Error.WriteLine($"PostgreSQL SQLSTATE: {postgres.SqlState}");
    return 1;
}

static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<string> RunLocalAsync(string name, string[] arguments)
{
    var settings = new ProcessStartInfo(name) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) settings.ArgumentList.Add(argument);
    using var child = Process.Start(settings) ?? throw new InvalidOperationException($"Could not start {name}.");
    var output = child.StandardOutput.ReadToEndAsync();
    var error = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0) throw new InvalidOperationException($"{name} failed: {(await error).Trim()}");
    return (await output).Trim();
}

static async Task<string> ReadExistingBoundaryAsync(NpgsqlConnection connection, PostgresPlan plan, CancellationToken cancellationToken)
{
    await using var query = new NpgsqlCommand("""
        SELECT jsonb_build_object(
          'database', (SELECT jsonb_build_array(datdba, datacl) FROM pg_catalog.pg_database WHERE datname = 'agentweaver'),
          'schemas', (SELECT jsonb_agg(jsonb_build_array(nspname, nspowner, nspacl) ORDER BY nspname)
                     FROM pg_catalog.pg_namespace WHERE nspname <> ALL(@schemas)),
          'defaults', (SELECT jsonb_agg(to_jsonb(d) ORDER BY d.oid) FROM pg_catalog.pg_default_acl d),
          'objects', (SELECT jsonb_agg(jsonb_build_array(n.nspname, c.relname, c.relowner, c.relacl) ORDER BY n.nspname, c.relname)
                      FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname IN ('public', 'identity_broker', 'foundation_probe')))::text
        """, connection);
    query.Parameters.AddWithValue("schemas", plan.Services.Select(item => item.Schema).ToArray());
    return (string?)await query.ExecuteScalarAsync(cancellationToken)
        ?? throw new InvalidOperationException("The existing ownership and privilege boundary could not be read.");
}

static async Task AssertOwnedObjectsAsync(NpgsqlConnection connection, ServicePlan service, string owner, CancellationToken cancellationToken)
{
    await using var query = new NpgsqlCommand("""
        SELECT n.nspowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
          AND (SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c
               WHERE c.relnamespace = n.oid AND c.relkind IN ('r', 'p', 'f', 'S')) = @tables
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
               WHERE c.relnamespace = n.oid AND c.relowner <> n.nspowner)
          AND (SELECT count(*) FROM pg_catalog.pg_proc p WHERE p.pronamespace = n.oid) = cardinality(@functions)
          AND NOT EXISTS (SELECT 1 FROM unnest(@functions) AS required(signature)
                          WHERE pg_catalog.to_regprocedure(required.signature) IS NULL)
        FROM pg_catalog.pg_namespace n WHERE n.nspname = @schema
        """, connection);
    query.Parameters.AddWithValue("owner", owner);
    query.Parameters.AddWithValue("schema", service.Schema);
    query.Parameters.AddWithValue("tables", service.Tables.Select(item => item.Table).Order(StringComparer.Ordinal).ToArray());
    query.Parameters.AddWithValue("functions", service.Functions.Select(item => $"{service.Schema}.{item.Signature}").ToArray());
    if (await query.ExecuteScalarAsync(cancellationToken) is not true)
        throw new InvalidOperationException($"The {service.Service} schema contains unexpected or foreign-owned objects. No grants were applied.");
}

static async Task transactionCheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ServicePlan service, CancellationToken cancellationToken)
{
    await using var query = new NpgsqlCommand("""
        SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
          AND NOT rolreplication AND NOT rolinherit AND NOT rolbypassrls
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member = r.oid)
          AND has_database_privilege(r.oid, 'agentweaver', 'CONNECT')
          AND NOT has_database_privilege(r.oid, 'agentweaver', 'CREATE')
          AND NOT has_database_privilege(r.oid, 'agentweaver', 'TEMP')
          AND has_schema_privilege(r.oid, @schema, 'USAGE')
          AND NOT has_schema_privilege(r.oid, @schema, 'CREATE')
        FROM pg_catalog.pg_roles r WHERE rolname = @role
        """, connection, transaction);
    query.Parameters.AddWithValue("role", service.Role);
    query.Parameters.AddWithValue("schema", service.Schema);
    if (await query.ExecuteScalarAsync(cancellationToken) is not true)
        throw new InvalidOperationException($"The {service.Service} runtime role has unexpected database or role authority.");
    foreach (var table in service.Tables)
    {
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
        {
            await using var check = new NpgsqlCommand(
                "SELECT has_table_privilege(@role, @table, @privilege)", connection, transaction);
            check.Parameters.AddWithValue("role", service.Role);
            check.Parameters.AddWithValue("table", $"{service.Schema}.{table.Table}");
            check.Parameters.AddWithValue("privilege", privilege);
            if (!Equals(await check.ExecuteScalarAsync(cancellationToken), table.Privileges.Contains(privilege)))
                throw new InvalidOperationException($"Unexpected {privilege} authority on {service.Schema}.{table.Table}.");
        }
        if (table.LockColumn is not null)
            throw new InvalidOperationException("One-column row-lock grants still require reviewed mutation-denial source and restricted-role proof. Initialization is not yet permitted.");
    }
}

internal sealed record PostgresPlan(int FormatVersion, TargetPlan Target, ServicePlan[] Services, int[] CoreVersions);
internal sealed record TargetPlan(string TenantId, string PostgresHost, string Database, string OperatorName,
    string OperatorObjectId, string ConnectionHost, int ConnectionPort);
internal sealed record ServicePlan(string Service, string Schema, string Role, string PrincipalObjectId, TablePlan[] Tables, FunctionPlan[] Functions);
internal sealed record TablePlan(string Table, string[] Privileges, string? LockColumn, bool MutationDenialRequired);
internal sealed record FunctionPlan(string Signature, bool Execute);
