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
using Agentweaver.P1PostgresInitializer;
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
if (!Regex.IsMatch(migratorSource, @"Enumerable\.Range\(1,\s*16\)\.Concat\(\[18,\s*19,\s*20,\s*21\]\)") ||
    !migratorSource.Contains("16 => \"project_backlog\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("18 => \"reviewed_remote_tool_snapshots\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("21 => \"row_lock_record_updates\"", StringComparison.Ordinal))
    throw new InvalidOperationException("Reviewed Core 016/018/021 migration wiring is required before initialization; 017 is not permitted.");

var planJson = await RunLocalAsync("node", ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]);
var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))).ToLowerInvariant();
if (planHash != expectedPlanHash)
    throw new InvalidOperationException("The reviewed plan hash differs from the exact source plan.");
var plan = JsonSerializer.Deserialize<PostgresPlan>(planJson, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
}) ?? throw new InvalidOperationException("The exact source plan is missing.");
InitializerChecks.AssertMutationDenialPlan(plan.Services);

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
              AS p(role_name, principal_type, object_id, tenant_id, is_mfa, is_admin)
            WHERE p.role_name::text = current_user AND p.object_id = @object
              AND p.principal_type = 'user' AND p.tenant_id = @tenant)
        """, principalConnection))
    {
        identity.Parameters.AddWithValue("operator", plan.Target.OperatorName);
        identity.Parameters.AddWithValue("object", plan.Target.OperatorObjectId);
        identity.Parameters.AddWithValue("tenant", plan.Target.TenantId);
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
    var existingBoundary = await ReadExistingBoundaryAsync(connection, null, plan, cancellationToken);
    var operatorOid = await ReadJsonAsync<uint>(connection, null,
        "SELECT to_jsonb(oid)::text FROM pg_catalog.pg_roles WHERE rolname = current_user", cancellationToken);
    RuntimePrincipal[] principals = [];
    await RecordAsync("fresh_target_checked");

    await using (var transaction = await principalConnection.BeginTransactionAsync(cancellationToken))
    {
        await InitializerChecks.RunStageAsync(
            () => AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken),
            async () =>
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
            },
            async () =>
            {
                principals = await ReadPrincipalsAsync(principalConnection, transaction, plan, cancellationToken);
                InitializerChecks.AssertPrincipals(plan.Services, principals, operatorOid, plan.Target.TenantId);
            },
            () => transaction.CommitAsync(cancellationToken),
            () => RecordAsync("five_runtime_roles_created_and_exact_mappings_checked"));
    }

    List<uint> connectedRoles = [];
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
        var runtimeOid = principals.Single(item => item.Role == service.Role).Oid;
        InitializerChecks.AssertBoundary(existingBoundary,
            await ReadExistingBoundaryAsync(connection, grants, plan, cancellationToken),
            [.. connectedRoles, runtimeOid]);
        await AssertFunctionGrantsAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
        await transactionCheckAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
        await grants.CommitAsync(cancellationToken);
        connectedRoles.Add(runtimeOid);
        await RecordAsync($"{service.Service}:exact_grants_checked_and_committed");
    }
    await RecordAsync("operator_initialization_finished_runtime_identity_checks_still_required");
    Console.WriteLine("P1_POSTGRES_OPERATOR_INITIALIZATION_FINISHED runtimeIdentityProof=false deploymentProof=false");
    return 0;
}
catch (Exception error)
{
    await InitializerChecks.ReportFailureAsync(error, receiptPath, RecordAsync, Console.Error,
        error is PostgresException postgres ? postgres.SqlState : null);
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

static Task<ExistingBoundary> ReadExistingBoundaryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
    PostgresPlan plan, CancellationToken cancellationToken)
{
    return ReadJsonAsync<ExistingBoundary>(connection, transaction, """
        WITH database AS (
          SELECT d.*, r.oid AS operator_oid, r.rolsuper
          FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r
          WHERE d.datname = 'agentweaver' AND r.rolname = current_user)
        SELECT jsonb_build_object(
          'databaseOid', database.oid, 'ownerOid', datdba,
          'connectGrantorOid', CASE WHEN rolsuper OR datdba = operator_oid THEN datdba ELSE operator_oid END,
          'grants', (SELECT coalesce(jsonb_agg(jsonb_build_object(
                     'grantor', a.grantor, 'grantee', a.grantee,
                     'privilege', a.privilege_type, 'grantable', a.is_grantable)
                     ORDER BY a.grantor, a.grantee, a.privilege_type, a.is_grantable), '[]'::jsonb)
                     FROM pg_catalog.aclexplode(coalesce(datacl, pg_catalog.acldefault('d', datdba))) a),
          'protectedState', jsonb_build_object(
          'schemas', (SELECT jsonb_agg(jsonb_build_array(nspname, nspowner, nspacl) ORDER BY nspname)
                     FROM pg_catalog.pg_namespace WHERE nspname <> ALL(@schemas)),
          'defaults', (SELECT jsonb_agg(to_jsonb(d) ORDER BY d.oid) FROM pg_catalog.pg_default_acl d),
          'objects', (SELECT jsonb_agg(jsonb_build_array(n.nspname, c.relname, c.relowner, c.relacl) ORDER BY n.nspname, c.relname)
                      FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname IN ('public', 'identity_broker', 'foundation_probe')),
          'functions', (SELECT jsonb_agg(jsonb_build_array(p.oid, p.pronamespace, p.proowner,
                        coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) ORDER BY p.oid)
                        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                        WHERE n.nspname <> ALL(@schemas)))::text)::text FROM database
        """, cancellationToken, new NpgsqlParameter("schemas", plan.Services.Select(item => item.Schema).ToArray()));
}

static async Task AssertPreflightAsync(NpgsqlConnection connection, PostgresPlan plan,
    ExistingBoundary baseline, uint operatorOid, CancellationToken cancellationToken)
{
    var state = await ReadJsonAsync<PreflightSnapshot>(connection, null, """
        SELECT jsonb_build_object(
          'operatorOid', r.oid,
          'operatorCanGrantConnect', r.rolsuper OR d.datdba = r.oid OR EXISTS (
            SELECT 1 FROM pg_catalog.aclexplode(coalesce(d.datacl, pg_catalog.acldefault('d', d.datdba))) a
            WHERE a.grantee = r.oid AND a.privilege_type = 'CONNECT' AND a.is_grantable),
          'foreignPublicAuthority',
            EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n,
              LATERAL pg_catalog.aclexplode(coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
              WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND a.grantee = 0 AND a.privilege_type = 'CREATE')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace,
              LATERAL pg_catalog.aclexplode(coalesce(c.relacl,
                pg_catalog.acldefault(CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END, c.relowner))) a
              WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S') AND a.grantee = 0)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p
              JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace,
              LATERAL pg_catalog.aclexplode(coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
              WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' AND a.grantee = 0),
          'incompatibleDefaults', EXISTS (
            SELECT 1 FROM pg_catalog.pg_default_acl defaults,
              LATERAL pg_catalog.aclexplode(defaults.defaclacl) a
            WHERE defaults.defaclrole = r.oid AND defaults.defaclnamespace = 0
              AND a.grantee <> r.oid AND NOT (defaults.defaclobjtype = 'f'
                AND a.grantee = 0 AND a.privilege_type = 'EXECUTE' AND NOT a.is_grantable)))
        ::text FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r
        WHERE d.datname = 'agentweaver' AND r.rolname = current_user
        """, cancellationToken);
    var current = await ReadExistingBoundaryAsync(connection, null, plan, cancellationToken);
    if (state.OperatorOid != operatorOid)
        throw new InvalidOperationException("The operator role changed before initialization. No writes are permitted.");
    InitializerChecks.AssertBoundary(baseline, current, []);
    InitializerChecks.AssertPreflight(state with { Boundary = current });
}

static Task<RuntimePrincipal[]> ReadPrincipalsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
    PostgresPlan plan, CancellationToken cancellationToken) =>
    ReadJsonAsync<RuntimePrincipal[]>(connection, transaction, """
        SELECT coalesce(jsonb_agg(jsonb_build_object(
          'oid', r.oid, 'role', r.rolname, 'objectId', p.object_id, 'type', p.principal_type,
          'tenantId', p.tenant_id, 'mfaFlag', p.is_mfa,
          'adminFlag', p.is_admin, 'canLogin', r.rolcanlogin, 'superuser', r.rolsuper,
          'createDatabase', r.rolcreatedb, 'createRole', r.rolcreaterole, 'replication', r.rolreplication,
          'inherit', r.rolinherit, 'bypassRls', r.rolbypassrls,
          'memberships', (SELECT coalesce(jsonb_agg(jsonb_build_object(
            'roleOid', m.roleid, 'memberOid', m.member, 'grantorOid', m.grantor,
            'admin', m.admin_option, 'inherit', m.inherit_option, 'set', m.set_option)
            ORDER BY m.roleid, m.member, m.grantor), '[]'::jsonb)
            FROM pg_catalog.pg_auth_members m WHERE m.roleid = r.oid OR m.member = r.oid))
          ORDER BY r.rolname), '[]'::jsonb)::text
        FROM pg_catalog.pg_roles r
        LEFT JOIN pg_catalog.pgaadauth_list_principals(false)
          AS p(role_name, principal_type, object_id, tenant_id, is_mfa, is_admin)
          ON p.role_name::text = r.rolname
        WHERE r.rolname = ANY(@roles)
        """, cancellationToken, new NpgsqlParameter("roles", plan.Services.Select(item => item.Role).ToArray()));

static async Task AssertOwnedObjectsAsync(NpgsqlConnection connection, ServicePlan service, string owner, CancellationToken cancellationToken)
{
    await using var query = new NpgsqlCommand("""
        SELECT n.nspowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
          AND (SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c
               WHERE c.relnamespace = n.oid AND c.relkind IN ('r', 'p', 'f', 'S')) = @tables
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
               WHERE c.relnamespace = n.oid AND c.relowner <> n.nspowner)
          AND (SELECT count(*) FROM pg_catalog.pg_proc p WHERE p.pronamespace = n.oid) = cardinality(@functions)
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p
                          WHERE p.pronamespace = n.oid AND p.proowner <> n.nspowner)
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

static async Task AssertFunctionGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
    ServicePlan service, uint ownerOid, uint runtimeOid, CancellationToken cancellationToken)
{
    var functions = await ReadJsonAsync<FunctionState[]>(connection, transaction, """
        SELECT coalesce(jsonb_agg(jsonb_build_object(
          'oid', p.oid, 'signature', required.signature, 'ownerOid', p.proowner,
          'runtimeCanExecute', pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE'),
          'grants', (SELECT coalesce(jsonb_agg(jsonb_build_object(
            'grantor', a.grantor, 'grantee', a.grantee,
            'privilege', a.privilege_type, 'grantable', a.is_grantable)
            ORDER BY a.grantor, a.grantee, a.privilege_type, a.is_grantable), '[]'::jsonb)
            FROM pg_catalog.aclexplode(coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a))
          ORDER BY required.signature), '[]'::jsonb)::text
        FROM unnest(@signatures) AS required(signature)
        JOIN pg_catalog.pg_proc p ON p.oid = pg_catalog.to_regprocedure(@schema || '.' || required.signature)
        """, cancellationToken, new NpgsqlParameter("role", service.Role),
        new NpgsqlParameter("schema", service.Schema),
        new NpgsqlParameter("signatures", service.Functions.Select(item => item.Signature).ToArray()));
    InitializerChecks.AssertFunctions(service, functions, ownerOid, runtimeOid);
}

static async Task<T> ReadJsonAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction,
    string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
{
    await using var query = new NpgsqlCommand(sql, connection, transaction);
    query.Parameters.AddRange(parameters);
    var json = await query.ExecuteScalarAsync(cancellationToken) as string
        ?? throw new InvalidOperationException("The required ownership and privilege facts could not be read.");
    return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("The required ownership and privilege facts are missing.");
}

static async Task transactionCheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
    ServicePlan service, uint ownerOid, uint runtimeOid, CancellationToken cancellationToken)
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
        {
            await using var columnCheck = new NpgsqlCommand("""
                SELECT has_column_privilege(@role, @table, @column, 'UPDATE')
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_attribute a
                    WHERE a.attrelid = pg_catalog.to_regclass(@table)
                      AND a.attnum > 0 AND NOT a.attisdropped AND a.attname <> @column
                      AND pg_catalog.has_column_privilege(@role, a.attrelid, a.attnum, 'UPDATE'))
                """, connection, transaction);
            columnCheck.Parameters.AddWithValue("role", service.Role);
            columnCheck.Parameters.AddWithValue("table", $"{service.Schema}.{table.Table}");
            columnCheck.Parameters.AddWithValue("column", table.LockColumn);
            if (await columnCheck.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException(
                    $"The {service.Service} role does not have exactly its planned UPDATE column on {service.Schema}.{table.Table}.");
        }
    }
    var mutationGuards = await ReadMutationGuardStatesAsync(connection, transaction, service, cancellationToken);
    InitializerChecks.AssertMutationDenialGuards(service, mutationGuards, ownerOid, runtimeOid);
}

static Task<MutationGuardState[]> ReadMutationGuardStatesAsync(NpgsqlConnection connection,
    NpgsqlTransaction transaction, ServicePlan service, CancellationToken cancellationToken)
{
    var protectedTables = service.Tables.Where(table => table.MutationDenialRequired).ToArray();
    if (protectedTables.Length == 0) return Task.FromResult(Array.Empty<MutationGuardState>());
    return ReadJsonAsync<MutationGuardState[]>(connection, transaction, """
        SELECT coalesce(jsonb_agg(jsonb_build_object(
          'table', expected.table_name,
          'triggerName', t.tgname,
          'functionSchema', function_namespace.nspname,
          'functionName', CASE WHEN p.oid IS NULL THEN NULL ELSE p.proname || '()' END,
          'triggerType', t.tgtype,
          'enabled', t.tgenabled::text,
          'isInternal', t.tgisinternal,
          'noColumnFilter', CASE WHEN t.oid IS NULL THEN NULL ELSE t.tgattr = ''::int2vector END,
          'noCondition', CASE WHEN t.oid IS NULL THEN NULL ELSE t.tgqual IS NULL END,
          'argumentCount', t.tgnargs,
          'functionOwnerOid', p.proowner,
          'runtimeCanExecute', CASE WHEN p.oid IS NULL THEN NULL
            ELSE pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE') END,
          'functionSource', p.prosrc)
          ORDER BY expected.table_name), '[]'::jsonb)::text
        FROM unnest(@tables, @triggers, @functions) AS expected(table_name, trigger_name, function_name)
        LEFT JOIN pg_catalog.pg_namespace service_namespace ON service_namespace.nspname = @schema
        LEFT JOIN pg_catalog.pg_class c
          ON c.relnamespace = service_namespace.oid AND c.relname = expected.table_name
        LEFT JOIN pg_catalog.pg_trigger t
          ON t.tgrelid = c.oid AND t.tgname = expected.trigger_name
        LEFT JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
        LEFT JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = p.pronamespace
        """, cancellationToken,
        new NpgsqlParameter("tables", protectedTables.Select(table => table.Table).ToArray()),
        new NpgsqlParameter("triggers", protectedTables.Select(table => table.MutationDenialGuard!.Trigger).ToArray()),
        new NpgsqlParameter("functions", protectedTables.Select(table => table.MutationDenialGuard!.Function).ToArray()),
        new NpgsqlParameter("schema", service.Schema),
        new NpgsqlParameter("role", service.Role));
}
