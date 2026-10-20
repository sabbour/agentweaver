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

// Performs a reviewed, source-bound initialization or a narrowly scoped continuation; all other arguments are rejected.
if (args.Length == 1 && args[0] == "--plan")
{
    Console.WriteLine(await RunLocalAsync("node",
        ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]));
    return 0;
}
var continuationMode = args.Length == 17 && args[0] == "--continue-after-projects-config" &&
    args[1] == "--source-sha" && args[3] == "--core-merge-sha" &&
    args[5] == "--plan-sha256" && args[7] == "--prior-source-sha" &&
    args[9] == "--prior-plan-sha256" && args[11] == "--prior-receipt" &&
    args[13] == "--prior-receipt-sha256" && args[15] == "--receipt";
var freshMode = args.Length == 9 && args[0] == "--initialize" &&
    args[1] == "--source-sha" && args[3] == "--core-merge-sha" &&
    args[5] == "--plan-sha256" && args[7] == "--receipt";
if (!freshMode && !continuationMode)
    throw new ArgumentException(
        "Use --plan, --initialize --source-sha SHA --core-merge-sha SHA --plan-sha256 SHA256 --receipt PATH, or the reviewed --continue-after-projects-config mode with its prior source, plan, and receipt hashes.");

var sourceSha = args[2];
var coreMergeSha = args[4];
var expectedPlanHash = args[6];
var priorSourceSha = continuationMode ? args[8] : null;
var priorPlanHash = continuationMode ? args[10] : null;
var priorReceiptArgument = continuationMode ? args[12] : null;
var priorReceiptHash = continuationMode ? args[14] : null;
var receiptArgument = continuationMode ? args[16] : args[8];
if (!Regex.IsMatch(sourceSha, "^[0-9a-f]{40}$") ||
    !Regex.IsMatch(coreMergeSha, "^[0-9a-f]{40}$") ||
    !Regex.IsMatch(expectedPlanHash, "^[0-9a-f]{64}$") ||
    (continuationMode && (!Regex.IsMatch(priorSourceSha!, "^[0-9a-f]{40}$") ||
        !Regex.IsMatch(priorPlanHash!, "^[0-9a-f]{64}$") ||
        !Regex.IsMatch(priorReceiptHash!, "^[0-9a-f]{64}$"))))
    throw new ArgumentException("Full lowercase source, Core merge, and plan hashes are required.");
// Never initialize from a dirty or stale checkout, and require the separately reviewed Core merge to be an ancestor.
var worktreeStatus = await RunLocalAsync("git", ["status", "--porcelain", "--untracked-files=all"]);
var statusEntries = worktreeStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries);
var invalidStatus = continuationMode
    ? statusEntries.Any(line => !line.StartsWith("?? Temp/", StringComparison.Ordinal) &&
        !line.StartsWith("?? Temp\\", StringComparison.Ordinal))
    : statusEntries.Length != 0;
if (invalidStatus ||
    await RunLocalAsync("git", ["rev-parse", "HEAD"]) != sourceSha ||
    await RunLocalAsync("git", ["rev-parse", "origin/v1"]) != sourceSha)
    throw new InvalidOperationException("Initialization requires the clean, locally fetched, reviewed current v1 tip.");
if (continuationMode)
    await RunLocalAsync("git", ["merge-base", "--is-ancestor", priorSourceSha!, sourceSha]);
await RunLocalAsync("git", ["merge-base", "--is-ancestor", coreMergeSha, sourceSha]);
var coreParents = await RunLocalAsync("git", ["show", "--format=%P", "--no-patch", coreMergeSha]);
if (coreParents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
    throw new InvalidOperationException("The supplied Core commit must be the actual separately reviewed merge.");
var migratorSource = await File.ReadAllTextAsync(
    Path.Combine("services", "orchestrator", "Agentweaver.Orchestrator", "CoordinationOwnerMigrator.cs"));
// Core versions include durable interactive requests and must match the reviewed plan; 017 stays excluded.
if (!Regex.IsMatch(migratorSource, @"Enumerable\.Range\(1,\s*16\)\.Concat\(\[18,\s*19,\s*20,\s*21,\s*22,\s*23\]\)") ||
    !migratorSource.Contains("16 => \"project_backlog\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("18 => \"reviewed_remote_tool_snapshots\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("21 => \"row_lock_record_updates\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("22 => \"run_list_completion\"", StringComparison.Ordinal) ||
    !migratorSource.Contains("23 => \"project_run_requests\"", StringComparison.Ordinal))
    throw new InvalidOperationException("Reviewed Core 016/018/021/022/023 migration wiring is required before initialization; 017 is not permitted.");

var planJson = await RunLocalAsync("node", ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]);
// The reviewed plan hash binds every later role, schema, relation, and grant decision to source output.
var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))).ToLowerInvariant();
if (planHash != expectedPlanHash)
    throw new InvalidOperationException("The reviewed plan hash differs from the exact source plan.");
var plan = JsonSerializer.Deserialize<PostgresPlan>(planJson, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
}) ?? throw new InvalidOperationException("The exact source plan is missing.");
InitializerChecks.AssertPrincipalObjectIds(plan.Services);
InitializerChecks.AssertMutationDenialPlan(plan.Services);
InitializerChecks.AssertOwnerOnlyObjectPlan(plan.Services);

if (continuationMode)
{
    // A continuation may proceed only when the prior durable receipt proves the exact failed prefix on this same target.
    var oldPlanSource = await RunLocalAsync("git",
        ["show", $"{priorSourceSha}:scripts/azure/lib/p1-postgres-plan.mjs"]);
    var previousPlanJson = await RunLocalAsync("node",
        [
            "--input-type=module",
            "-e",
            "const m=await import(`data:text/javascript;base64,${process.argv[1]}`);const p=m.buildP1PostgresPlan();console.log(JSON.stringify({hash:m.p1PostgresPlanHash(p),target:p.target,services:p.services.map(({service,schema,role,principalObjectId})=>({service,schema,role,principalObjectId}))}));",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(oldPlanSource)),
        ]);
    using var previousPlan = JsonDocument.Parse(previousPlanJson);
    using var currentPlan = JsonDocument.Parse(planJson);
    if (previousPlan.RootElement.GetProperty("hash").GetString() != priorPlanHash ||
        previousPlan.RootElement.GetProperty("target").GetRawText() !=
            currentPlan.RootElement.GetProperty("target").GetRawText() ||
        previousPlan.RootElement.GetProperty("services").GetRawText() !=
            JsonSerializer.Serialize(plan.Services.Select(item => new
            {
                item.Service,
                item.Schema,
                item.Role,
                item.PrincipalObjectId,
            }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
        throw new InvalidOperationException("The prior receipt's plan is not the exact same target and runtime mapping as the current source.");
}

var receiptPath = Path.GetFullPath(receiptArgument);
var receiptRoot = Path.GetFullPath("Temp") + Path.DirectorySeparatorChar;
if (!receiptPath.StartsWith(receiptRoot, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Use a new receipt file beneath this source copy's Temp directory.");
if (continuationMode)
{
    var priorReceiptPath = Path.GetFullPath(priorReceiptArgument!);
    if (!priorReceiptPath.StartsWith(receiptRoot, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(priorReceiptPath, receiptPath, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("The prior receipt and new receipt must be distinct files beneath Temp.");
    var priorReceiptBytes = await File.ReadAllBytesAsync(priorReceiptPath);
    var actualPriorReceiptHash = Convert.ToHexString(SHA256.HashData(priorReceiptBytes)).ToLowerInvariant();
    if (actualPriorReceiptHash != priorReceiptHash)
        throw new InvalidOperationException("The prior receipt bytes do not match the explicitly supplied hash.");
    var receiptLines = await File.ReadAllLinesAsync(priorReceiptPath);
    if (receiptLines.Length == 0 || receiptLines.Any(string.IsNullOrWhiteSpace))
        throw new InvalidOperationException("The prior receipt is empty or contains an invalid blank record.");
    var priorEntries = receiptLines.Select(line =>
        JsonSerializer.Deserialize<ContinuationReceiptEntry>(line,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("The prior receipt contains an invalid record.")).ToArray();
    InitializerChecks.AssertContinuationReceipt(priorEntries, priorSourceSha!, coreMergeSha, priorPlanHash!);
}
Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
// Create the receipt once and flush every stage so an interruption never looks like an unrecorded success.
await using var receipt = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
async Task RecordAsync(string stage)
{
    var line = JsonSerializer.Serialize(new { stage, sourceSha, coreMergeSha, planHash, at = DateTimeOffset.UtcNow });
    await receipt.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
    receipt.Flush(flushToDisk: true);
}
Task RecordStageAsync(string stage) => RecordAsync($"stage_begin:{stage}");

await RecordAsync("source_checked");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var cancellationToken = cancellation.Token;
try
{
    // The token and both connections are constrained to the validated Entra operator and PostgreSQL target.
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
    if (!continuationMode)
    {
        await using var fresh = new NpgsqlCommand("""
            SELECT current_user = @operator AND current_database() = 'agentweaver'
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles))
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = ANY(@schemas))
            """, connection);
        fresh.Parameters.AddWithValue("operator", plan.Target.OperatorName);
        fresh.Parameters.AddWithValue("roles", plan.Services.Select(item => item.Role).ToArray());
        fresh.Parameters.AddWithValue("schemas", plan.Services.Select(item => item.Schema).ToArray());
        if (await fresh.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("The target contains existing, partial, or unapproved state. Refusing to adopt, reset, or retry it.");
    }
    var existingBoundary = await ReadExistingBoundaryAsync(connection, null, plan, cancellationToken);
    var operatorOid = await ReadJsonAsync<uint>(connection, null,
        "SELECT to_jsonb(oid::bigint)::text FROM pg_catalog.pg_roles WHERE rolname = current_user", cancellationToken);
    RuntimePrincipal[] principals = [];

    if (continuationMode)
    {
        // Re-read the principal mappings and Projects-only prefix before creating any remaining schemas.
        principals = await P1PostgresContinuation.ReadPrincipalsAsync(
            principalConnection, null, plan, cancellationToken);
        var expectedGrantorOids = InitializerChecks.CaptureMembershipGrantors(
            plan.Services, principals, operatorOid, plan.Target.TenantId);
        InitializerChecks.AssertPrincipals(
            plan.Services, principals, operatorOid, plan.Target.TenantId, expectedGrantorOids);
        InitializerChecks.AssertNoRuntimeDatabaseGrants(
            existingBoundary, principals.Select(item => item.Oid).ToArray());
        await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
        await P1PostgresContinuation.AssertProjectsPrefixAsync(
            source, connection, plan, principals, operatorOid, plan.Target.OperatorName,
            plan.Target.TenantId, cancellationToken);
        await RecordAsync("projects_config_prefix_checked");
    }
    else
    {
        // Fresh initialization accepts only an empty target, then verifies role mappings before committing role setup.
        await RecordAsync("fresh_target_checked");
        await using var transaction = await principalConnection.BeginTransactionAsync(cancellationToken);
        IReadOnlyDictionary<string, uint> expectedGrantorOids =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        await InitializerChecks.RunStageAsync(
            () => AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken),
            async () =>
            {
                foreach (var service in plan.Services)
                {
                    await RecordStageAsync(InitializerChecks.RoleCreationStage);
                    await using var create = new NpgsqlCommand(
                        "SELECT pg_catalog.pgaadauth_create_principal_with_oid(@role, @object, 'service', false, false)",
                        principalConnection, transaction);
                    create.Parameters.AddWithValue("role", service.Role);
                    create.Parameters.AddWithValue("object", service.PrincipalObjectId);
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
                var initialPrincipals = await ReadPrincipalsAsync(principalConnection, transaction, plan, cancellationToken);
                expectedGrantorOids = InitializerChecks.CaptureMembershipGrantors(
                    plan.Services, initialPrincipals, operatorOid, plan.Target.TenantId);
                foreach (var service in plan.Services)
                {
                    foreach (var (stage, sql) in InitializerChecks.RolePostCreateStatements(
                        service.Role, plan.Target.OperatorName, expectedGrantorOids[service.Role], operatorOid))
                    {
                        await RecordStageAsync(stage);
                        await ExecuteAsync(principalConnection, transaction, sql, cancellationToken);
                    }
                }
            },
            async () =>
            {
                principals = await ReadPrincipalsAsync(principalConnection, transaction, plan, cancellationToken);
                InitializerChecks.AssertPrincipals(
                    plan.Services, principals, operatorOid, plan.Target.TenantId, expectedGrantorOids);
            },
            () => transaction.CommitAsync(cancellationToken),
            () => RecordAsync("five_runtime_roles_created_and_exact_mappings_checked"),
            RecordStageAsync);
    }

    if (continuationMode)
    {
        // Each continuation stage revalidates the prior state; failures require explicit operator reconciliation.
        var completedServices = new List<ServicePlan>();
        async Task VerifyContinuationStateAsync()
        {
            await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
            principals = await P1PostgresContinuation.ReadPrincipalsAsync(
                principalConnection, null, plan, cancellationToken);
            var grantors = InitializerChecks.CaptureMembershipGrantors(
                plan.Services, principals, operatorOid, plan.Target.TenantId);
            InitializerChecks.AssertPrincipals(plan.Services, principals, operatorOid, plan.Target.TenantId, grantors);
            InitializerChecks.AssertNoRuntimeDatabaseGrants(
                await ReadExistingBoundaryAsync(connection, null, plan, cancellationToken),
                principals.Select(item => item.Oid).ToArray());
            await P1PostgresContinuation.AssertProjectsPrefixAsync(
                source, connection, plan, principals, operatorOid, plan.Target.OperatorName,
                plan.Target.TenantId, cancellationToken, requireOnlyProjectsSchema: completedServices.Count == 0);
            foreach (var completed in completedServices)
            {
                await P1PostgresContinuation.VerifyServiceMigrationsAsync(source, completed, cancellationToken);
                await P1PostgresContinuation.AssertOwnedObjectsAsync(
                    connection, completed, plan.Target.OperatorName, cancellationToken);
                await P1PostgresContinuation.AssertServiceUnprivilegedAsync(
                    connection, completed, principals, operatorOid, cancellationToken);
            }
        }

        await P1PostgresContinuation.MigrateRemainingServicesAsync(
            source,
            plan,
            plan.Target.OperatorName,
            RecordAsync,
            VerifyContinuationStateAsync,
            async service =>
            {
                await P1PostgresContinuation.AssertOwnedObjectsAsync(
                    connection, service, plan.Target.OperatorName, cancellationToken);
                await P1PostgresContinuation.AssertServiceUnprivilegedAsync(
                    connection, service, principals, operatorOid, cancellationToken);
                completedServices.Add(service);
            },
            cancellationToken);
        await VerifyContinuationStateAsync();
        await RecordAsync("all_missing_migrations_and_inventories_checked");

        var runtimeOids = principals.Select(item => item.Oid).ToArray();
        await using (var grants = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var service in plan.Services)
            {
                await ExecuteAsync(connection, grants,
                    $"GRANT CONNECT ON DATABASE {Quote(plan.Target.Database)} TO {Quote(service.Role)}",
                    cancellationToken);
                await ExecuteAsync(connection, grants,
                    $"GRANT USAGE ON SCHEMA {Quote(service.Schema)} TO {Quote(service.Role)}",
                    cancellationToken);
                foreach (var table in service.Tables)
                {
                    var relation = $"{Quote(service.Schema)}.{Quote(table.Table)}";
                    await ExecuteAsync(connection, grants,
                        $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {Quote(service.Role)}",
                        cancellationToken);
                    if (table.LockColumn is not null)
                        await ExecuteAsync(connection, grants,
                            $"GRANT UPDATE ({Quote(table.LockColumn)}) ON TABLE {relation} TO {Quote(service.Role)}",
                            cancellationToken);
                }
                foreach (var function in service.Functions)
                {
                    var signature = $"{Quote(service.Schema)}.{function.Signature}";
                    await ExecuteAsync(connection, grants,
                        $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken);
                    if (function.Execute)
                        await ExecuteAsync(connection, grants,
                            $"GRANT EXECUTE ON FUNCTION {signature} TO {Quote(service.Role)}", cancellationToken);
                }
            }

            InitializerChecks.AssertBoundary(existingBoundary,
                await ReadExistingBoundaryAsync(connection, grants, plan, cancellationToken), runtimeOids);
            foreach (var service in plan.Services)
            {
                var runtimeOid = principals.Single(item => item.Role == service.Role).Oid;
                await AssertFunctionGrantsAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
                await transactionCheckAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
            }
            await grants.CommitAsync(cancellationToken);
        }
        await RecordAsync("continuation_all_runtime_grants_committed");
        Console.WriteLine("P1_POSTGRES_CONTINUATION_FINISHED runtimeIdentityProof=false deploymentProof=false");
        return 0;
    }

    // Fresh execution migrates and verifies each service before granting only its planned runtime privileges.
    List<uint> connectedRoles = [];
    foreach (var service in plan.Services)
    {
        await ExecuteAsync(connection, null, $"CREATE SCHEMA {Quote(service.Schema)} AUTHORIZATION {Quote(plan.Target.OperatorName)}", cancellationToken);
        await ExecuteAsync(connection, null, $"REVOKE ALL ON SCHEMA {Quote(service.Schema)} FROM PUBLIC", cancellationToken);
        await RecordAsync($"{service.Service}:schema_created");
        await P1PostgresContinuation.MigrateServiceAsync(source, service, cancellationToken);
        await RecordAsync($"{service.Service}:migrated");
        await P1PostgresContinuation.AssertOwnedObjectsAsync(
            connection, service, plan.Target.OperatorName, cancellationToken);
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

// Quotes identifiers using the same source-checked rules as the invariant helpers.
static string Quote(string value) => InitializerChecks.Quote(value);

// Executes one command in the transaction selected by the caller.
static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

// Runs a local source-controlled planning command and fails with its stderr when it exits unsuccessfully.
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

// Reads the pre-existing ACL and protected-object boundary for the target database.
static Task<ExistingBoundary> ReadExistingBoundaryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
    PostgresPlan plan, CancellationToken cancellationToken)
{
    return ReadJsonAsync<ExistingBoundary>(connection, transaction, InitializerChecks.ExistingBoundaryQuery,
        cancellationToken, new NpgsqlParameter("schemas", plan.Services.Select(item => item.Schema).ToArray()));
}

// Rechecks operator identity, grants, and public/default authority before each write stage.
static async Task AssertPreflightAsync(NpgsqlConnection connection, PostgresPlan plan,
    ExistingBoundary baseline, uint operatorOid, CancellationToken cancellationToken)
{
    var state = await ReadJsonAsync<PreflightSnapshot>(connection, null, InitializerChecks.PreflightQuery,
        cancellationToken);
    var current = await ReadExistingBoundaryAsync(connection, null, plan, cancellationToken);
    if (state.OperatorOid != operatorOid)
        throw new InvalidOperationException("The operator role changed before initialization. No writes are permitted.");
    InitializerChecks.AssertBoundary(baseline, current, []);
    InitializerChecks.AssertPreflight(state with { Boundary = current });
}

// Reads the planned Entra principal mappings inside the caller's current transaction.
static Task<RuntimePrincipal[]> ReadPrincipalsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
    PostgresPlan plan, CancellationToken cancellationToken) =>
    ReadJsonAsync<RuntimePrincipal[]>(connection, transaction, InitializerChecks.PrincipalReadbackQuery(),
        cancellationToken, new NpgsqlParameter("roles", plan.Services.Select(item => item.Role).ToArray()));

// Verifies function ACL readback against the exact role and service plan.
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

// Converts one source query result to a typed contract, rejecting missing or invalid readbacks.
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

// Verifies runtime role flags and every planned schema, table, column, owner-only object, and trigger grant.
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
    foreach (var table in service.OwnerOnlyTables)
    {
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
        {
            await using var check = new NpgsqlCommand(
                "SELECT has_table_privilege(@role, @table, @privilege)", connection, transaction);
            check.Parameters.AddWithValue("role", service.Role);
            check.Parameters.AddWithValue("table", $"{service.Schema}.{table}");
            check.Parameters.AddWithValue("privilege", privilege);
            if (Equals(await check.ExecuteScalarAsync(cancellationToken), true))
                throw new InvalidOperationException($"The {service.Service} runtime role has unexpected {privilege} authority on owner-only table {service.Schema}.{table}.");
        }
    }
    foreach (var sequence in service.OwnerOnlySequences)
    {
        foreach (var privilege in new[] { "USAGE", "SELECT", "UPDATE" })
        {
            await using var check = new NpgsqlCommand(
                "SELECT has_sequence_privilege(@role, @sequence, @privilege)", connection, transaction);
            check.Parameters.AddWithValue("role", service.Role);
            check.Parameters.AddWithValue("sequence", $"{service.Schema}.{sequence}");
            check.Parameters.AddWithValue("privilege", privilege);
            if (Equals(await check.ExecuteScalarAsync(cancellationToken), true))
                throw new InvalidOperationException($"The {service.Service} runtime role has unexpected {privilege} authority on owner-only sequence {service.Schema}.{sequence}.");
        }
    }
    var mutationGuards = await ReadMutationGuardStatesAsync(connection, transaction, service, cancellationToken);
    InitializerChecks.AssertMutationDenialGuards(service, mutationGuards, ownerOid, runtimeOid);
}

// Reads mutation-denial trigger metadata and source for the protected tables of one service.
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
