extern alias AzureIdentity;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
// Inspection is read-only and needs no Core merge or receipt.
var inspectMode = args.Length == 5 && args[0] == "--inspect" &&
    args[1] == "--source-sha" && args[3] == "--plan-sha256";
// Incremental continuation of an already initialized database: --dry-run writes nothing; --receipt applies.
var pendingMode = (args.Length == 12 || args.Length == 13) && args[0] == "--apply-pending-migrations" &&
    args[1] == "--source-sha" && args[3] == "--core-merge-sha" && args[5] == "--plan-sha256" &&
    args[7] == "--max-core-version" && args[9] == "--max-events-version" &&
    (args.Length == 12 ? args[11] == "--dry-run" : args[11] == "--receipt");
// Projects & Config runtime grants after that service's separate migration Job: --dry-run writes nothing;
// --receipt applies. It never migrates and changes no other service, so it takes no Core merge or maxima.
var projectsGrantsMode = (args.Length == 6 || args.Length == 7) && args[0] == "--apply-projects-config-grants" &&
    args[1] == "--source-sha" && args[3] == "--plan-sha256" &&
    (args.Length == 6 ? args[5] == "--dry-run" : args[5] == "--receipt");
// Each mode accepts one exact argument shape. Positional checks keep a typo from silently selecting
// a different mode. The 12-argument pending form and the 6-argument grant form end with --dry-run,
// so they are read-only too.
var readOnlyMode = inspectMode || (pendingMode && args.Length == 12) || (projectsGrantsMode && args.Length == 6);
if (!freshMode && !continuationMode && !inspectMode && !pendingMode && !projectsGrantsMode)
    throw new ArgumentException(
        "Use --plan; --inspect --source-sha SHA --plan-sha256 SHA256; --apply-pending-migrations --source-sha SHA --core-merge-sha SHA --plan-sha256 SHA256 --max-core-version N --max-events-version N (--dry-run | --receipt PATH); --apply-projects-config-grants --source-sha SHA --plan-sha256 SHA256 (--dry-run | --receipt PATH); --initialize --source-sha SHA --core-merge-sha SHA --plan-sha256 SHA256 --receipt PATH; or the reviewed --continue-after-projects-config mode with its prior source, plan, and receipt hashes.");

// Argument positions differ by mode. Inspection and the grant step have no Core merge, so their plan
// hash sits at index 4. Only writing modes get a receipt path; read-only runs leave receiptArgument null.
var sourceSha = args[2];
var coreMergeSha = inspectMode || projectsGrantsMode ? null : args[4];
var expectedPlanHash = inspectMode || projectsGrantsMode ? args[4] : args[6];
var priorSourceSha = continuationMode ? args[8] : null;
var priorPlanHash = continuationMode ? args[10] : null;
var priorReceiptArgument = continuationMode ? args[12] : null;
var priorReceiptHash = continuationMode ? args[14] : null;
var receiptArgument = continuationMode ? args[16] : freshMode ? args[8] : pendingMode && !readOnlyMode ? args[12]
    : projectsGrantsMode && !readOnlyMode ? args[6] : null;
int? maxCoreVersion = pendingMode ? ParseVersion(args[8]) : null;
int? maxEventsVersion = pendingMode ? ParseVersion(args[10]) : null;
if (!Regex.IsMatch(sourceSha, "^[0-9a-f]{40}$") ||
    (coreMergeSha is not null && !Regex.IsMatch(coreMergeSha, "^[0-9a-f]{40}$")) ||
    !Regex.IsMatch(expectedPlanHash, "^[0-9a-f]{64}$") ||
    (continuationMode && (!Regex.IsMatch(priorSourceSha!, "^[0-9a-f]{40}$") ||
        !Regex.IsMatch(priorPlanHash!, "^[0-9a-f]{64}$") ||
        !Regex.IsMatch(priorReceiptHash!, "^[0-9a-f]{64}$"))))
    throw new ArgumentException("Full lowercase source, Core merge, and plan hashes are required.");
// Never initialize from a dirty or stale checkout, and require the separately reviewed Core merge to be an ancestor.
var worktreeStatus = await RunLocalAsync("git", ["status", "--porcelain", "--untracked-files=all"]);
var statusEntries = worktreeStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries);
// Fresh initialization needs a fully clean checkout. Later modes may also see receipts that earlier
// runs left under Temp/, so untracked files there are allowed. Any other change is rejected.
var invalidStatus = continuationMode || inspectMode || pendingMode || projectsGrantsMode
    ? statusEntries.Any(line => !line.StartsWith("?? Temp/", StringComparison.Ordinal) &&
        !line.StartsWith("?? Temp\\", StringComparison.Ordinal))
    : statusEntries.Length != 0;
if (invalidStatus ||
    await RunLocalAsync("git", ["rev-parse", "HEAD"]) != sourceSha ||
    await RunLocalAsync("git", ["rev-parse", "origin/v1"]) != sourceSha)
    throw new InvalidOperationException("Initialization requires the clean, locally fetched, reviewed current v1 tip.");
if (continuationMode)
    await RunLocalAsync("git", ["merge-base", "--is-ancestor", priorSourceSha!, sourceSha]);
if (coreMergeSha is not null)
{
    // Inspection skips this because it writes nothing. Writing modes require the reviewed Core merge
    // to be in this source's history. git exits non-zero, and RunLocalAsync throws, when it is not.
    await RunLocalAsync("git", ["merge-base", "--is-ancestor", coreMergeSha, sourceSha]);
    // A real merge commit has exactly two parents. This rejects an ordinary commit SHA passed by mistake.
    var coreParents = await RunLocalAsync("git", ["show", "--format=%P", "--no-patch", coreMergeSha]);
    if (coreParents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
        throw new InvalidOperationException("The supplied Core commit must be the actual separately reviewed merge.");
}

var planJson = await RunLocalAsync("node", ["scripts/azure/lib/p1-postgres-plan.mjs", "--json"]);
// The reviewed plan hash binds every later role, schema, relation, and grant decision to source output.
var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))).ToLowerInvariant();
if (planHash != expectedPlanHash)
    throw new InvalidOperationException("The reviewed plan hash differs from the exact source plan.");
var plan = JsonSerializer.Deserialize<PostgresPlan>(planJson, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
}) ?? throw new InvalidOperationException("The exact source plan is missing.");
// The plan catalog, not pinned source text, defines the versions; it must equal what the compiled migrators would apply.
// The migrators always apply every missing version they know. So when a maximum is given, it must
// be the last catalog version. A source that contains a newer migration then stops here, before any
// connection, instead of applying more than the operator approved.
InitializerChecks.AssertMigrationCatalog(plan.CoreVersions, CoordinationOwnerMigrator.SupportedVersions,
    maxCoreVersion, "Core/Orchestrator");
InitializerChecks.AssertMigrationCatalog(plan.EventsVersions, EventsAndSessionsMigrator.SupportedVersions,
    maxEventsVersion, "Events");
// EF migration IDs use an ordinal plan version so the runner can verify Environment grants too.
InitializerChecks.AssertMigrationCatalog(plan.EnvironmentVersions,
    Agentweaver.Environment.EnvironmentMigrator.SupportedVersions, null, "Environment");
InitializerChecks.AssertPrincipalObjectIds(plan.Services);
InitializerChecks.AssertMutationDenialPlan(plan.Services);
InitializerChecks.AssertColumnUpdatePlan(plan.Services);
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

var receiptRoot = Path.GetFullPath("Temp") + Path.DirectorySeparatorChar;
// Read-only modes never create a receipt; every writing mode records each stage durably.
var receiptPath = receiptArgument is null ? null : Path.GetFullPath(receiptArgument);
if (receiptPath is not null && !receiptPath.StartsWith(receiptRoot, StringComparison.OrdinalIgnoreCase))
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
    InitializerChecks.AssertContinuationReceipt(priorEntries, priorSourceSha!, coreMergeSha!, priorPlanHash!);
}
// Create the receipt once and flush every stage so an interruption never looks like an unrecorded success.
FileStream? receipt = null;
if (receiptPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
    receipt = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
}
await using var receiptOwner = receipt;
async Task RecordAsync(string stage)
{
    if (receipt is null) return;
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
            // Read-only modes start every session read-only; RESET/DISCARD keeps this startup default.
            Options = readOnlyMode ? "-c default_transaction_read_only=on" : null,
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
    if (readOnlyMode)
    {
        // Confirm the server applied the read-only startup option on both connections. If it ignored
        // the option, stop before the first catalog read rather than trusting the request.
        foreach (var session in new[] { principalConnection, connection })
        {
            await using var readOnly = new NpgsqlCommand(
                "SELECT pg_catalog.current_setting('default_transaction_read_only') = 'on'", session);
            if (await readOnly.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("The read-only session setting was not accepted. No reads or writes were attempted.");
        }
    }
    if (freshMode)
    {
        // Only fresh initialization requires an empty target. The other modes expect the roles and
        // schemas to exist already and check them in their own branches.
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

    if (inspectMode)
    {
        // Inspection reports live facts for operator review and never asserts or changes them.
        var planPrincipals = await P1PostgresContinuation.ReadPrincipalsAsync(
            principalConnection, null, plan, cancellationToken);
        string principalStatus;
        try
        {
            // Run the same principal checks the writing modes use, but record a mismatch in the report
            // instead of stopping. The operator needs to see every difference, not only the first one.
            var grantors = InitializerChecks.CaptureMembershipGrantors(
                plan.Services, planPrincipals, operatorOid, plan.Target.TenantId);
            InitializerChecks.AssertPrincipals(
                plan.Services, planPrincipals, operatorOid, plan.Target.TenantId, grantors);
            principalStatus = "exact";
        }
        catch (InvalidOperationException error)
        {
            principalStatus = $"mismatch: {error.Message}";
        }
        var services = new JsonArray();
        foreach (var service in plan.Services)
        {
            // A missing role or schema is reported as such. Schema details are read only when the schema exists.
            var roleExists = planPrincipals.Any(item => item.Role == service.Role);
            var schema = await ReadNodeAsync(connection, InitializerChecks.SchemaStateQuery, cancellationToken,
                new NpgsqlParameter("schema", service.Schema));
            var item = new JsonObject
            {
                ["service"] = service.Service,
                ["schema"] = service.Schema,
                ["role"] = service.Role,
                ["roleExists"] = roleExists,
                ["schemaState"] = schema,
            };
            if (schema is not null)
            {
                foreach (var (table, column) in HistoryTables(service))
                    item[table] = column == "version"
                        ? ToNode(await ReadIntHistoryAsync(connection, service.Schema, table, cancellationToken))
                        : ToNode(await ReadTextHistoryAsync(connection, service.Schema, table, cancellationToken));
                var planned = service.Service switch
                {
                    "orchestrator" => plan.CoreVersions,
                    "events-and-sessions" => plan.EventsVersions,
                    _ => null,
                };
                if (planned is not null)
                {
                    // Only Core and Events use integer version histories that can be partly applied.
                    // Pending versions are planned but absent; unplanned versions exist live but not in this source.
                    var applied = await ReadIntHistoryAsync(connection, service.Schema,
                        HistoryTables(service)[0].Table, cancellationToken) ?? [];
                    item["plannedVersions"] = ToNode(planned);
                    item["pendingVersions"] = ToNode(planned.Except(applied).ToArray());
                    item["unplannedAppliedVersions"] = ToNode(applied.Except(planned).ToArray());
                }
                var relations = await ReadRelationsAsync(connection, null, service, roleExists, cancellationToken);
                // Show live runtime privileges next to the planned ones. A null plan value means the relation
                // is not in the plan at all; an empty list means it is planned as owner-only.
                item["relations"] = JsonSerializer.SerializeToNode(relations.Select(relation => new
                {
                    relation.Name,
                    relation.Kind,
                    relation.Owner,
                    relation.RuntimePrivileges,
                    PlannedPrivileges = service.Tables.SingleOrDefault(table => table.Table == relation.Name)?.Privileges
                        ?? (service.OwnerOnlyTables.Contains(relation.Name) || service.OwnerOnlySequences.Contains(relation.Name)
                            ? Array.Empty<string>() : (string[]?)null),
                }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                item["missingPlannedRelations"] = ToNode(service.Tables.Select(table => table.Table)
                    .Concat(service.OwnerOnlyTables).Concat(service.OwnerOnlySequences)
                    .Except(relations.Select(relation => relation.Name)).ToArray());
                try
                {
                    // Reuse the service migrator's own startup verification. Failure is reported, not thrown,
                    // because an older live schema is an expected inspection result.
                    await P1PostgresContinuation.VerifyServiceMigrationsAsync(source, service, cancellationToken);
                    item["sourceMigrationVerification"] = "complete";
                }
                catch (Exception error) when (error is InvalidOperationException or PostgresException)
                {
                    item["sourceMigrationVerification"] = $"incomplete: {error.GetType().Name}";
                }
            }
            services.Add(item);
        }
        var report = new JsonObject
        {
            ["mode"] = "inspect",
            ["writes"] = false,
            ["sourceSha"] = sourceSha,
            ["planSha256"] = planHash,
            ["postgresHost"] = plan.Target.PostgresHost,
            ["database"] = plan.Target.Database,
            ["operator"] = plan.Target.OperatorName,
            ["planPrincipalStatus"] = principalStatus,
            ["planPrincipals"] = JsonSerializer.SerializeToNode(planPrincipals,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            ["entraServicePrincipals"] = await ReadNodeAsync(principalConnection, InitializerChecks.ServicePrincipalsQuery, cancellationToken),
            ["databaseGrants"] = await ReadNodeAsync(connection, InitializerChecks.DatabaseGrantsQuery, cancellationToken),
            ["identityBroker"] = new JsonObject
            {
                ["schemaState"] = await ReadNodeAsync(connection, InitializerChecks.SchemaStateQuery, cancellationToken,
                    new NpgsqlParameter("schema", "identity_broker")),
                ["__ef_migrations_history"] = ToNode(await ReadTextHistoryAsync(
                    connection, "identity_broker", "__ef_migrations_history", cancellationToken)),
            },
            ["services"] = services,
        };
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("P1_POSTGRES_INSPECTION_FINISHED writes=false");
        return 0;
    }

    if (projectsGrantsMode)
    {
        // The Projects & Config migration Job creates operator-owned tables with no runtime grants. This mode
        // applies only that service's planned runtime grants. It never migrates, never creates or alters
        // roles, and never writes another service's objects.
        var projects = plan.Services.Single(item => item.Service == "projects-config");
        // Step 1: the five runtime roles must still map exactly to their Entra principals and already hold
        // CONNECT. This mode repairs neither, so any difference stops the run before a write.
        principals = await P1PostgresContinuation.ReadPrincipalsAsync(principalConnection, null, plan, cancellationToken);
        var grantors = InitializerChecks.CaptureMembershipGrantors(
            plan.Services, principals, operatorOid, plan.Target.TenantId);
        InitializerChecks.AssertPrincipals(plan.Services, principals, operatorOid, plan.Target.TenantId, grantors);
        InitializerChecks.AssertRuntimeDatabaseConnect(existingBoundary, principals.Select(item => item.Oid).ToArray());
        await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
        var runtimeOid = principals.Single(item => item.Role == projects.Role).Oid;
        // Step 2: the operator must own the schema, which may grant the runtime role only USAGE. Every live
        // relation must be planned and operator-owned, and no live runtime privilege may exceed the plan.
        // Holding fewer privileges is expected: that is the gap this mode closes.
        InitializerChecks.AssertServiceSchemaAcl(projects,
            await ReadJsonAsync<SchemaAclState>(connection, null, InitializerChecks.SchemaAclQuery, cancellationToken,
                new NpgsqlParameter("schema", projects.Schema)),
            operatorOid, runtimeOid, allowMissingRuntimeUsage: true);
        InitializerChecks.AssertRelationsWithinPlan(projects,
            await ReadRelationsAsync(connection, null, projects, true, cancellationToken), plan.Target.OperatorName);
        // Step 3: the live EF history must equal this source's compiled migrations, and the schema must hold
        // exactly the planned operator-owned objects. Every grant then targets an object this reviewed
        // source created, and a missing or extra migration stops the run.
        await P1PostgresContinuation.VerifyServiceMigrationsAsync(source, projects, cancellationToken);
        await P1PostgresContinuation.AssertOwnedObjectsAsync(
            connection, projects, plan.Target.OperatorName, cancellationToken);
        // Step 4: report whether the live grants already match the plan. The readback runs in a transaction
        // that is always rolled back. A mismatch is reported, not thrown, because it is the expected input.
        string grantsBefore;
        await using (var readback = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await AssertFunctionGrantsAsync(connection, readback, projects, operatorOid, runtimeOid, cancellationToken);
                await transactionCheckAsync(connection, readback, projects, operatorOid, runtimeOid, cancellationToken);
                grantsBefore = "exact";
            }
            catch (InvalidOperationException error)
            {
                grantsBefore = $"incomplete: {error.Message}";
            }
            await readback.RollbackAsync(cancellationToken);
        }
        var grantSummary = JsonSerializer.Serialize(new
        {
            service = projects.Service, schema = projects.Schema, role = projects.Role, grantsBefore,
        });
        if (readOnlyMode)
        {
            Console.WriteLine(grantSummary);
            Console.WriteLine("P1_POSTGRES_PROJECTS_CONFIG_GRANTS_DRY_RUN writes=false");
            return 0;
        }

        // Step 5: apply the planned grants and read every grant back in one transaction. Any difference throws
        // before commit, so the database keeps either its previous grants or exactly the planned set.
        await RecordAsync($"projects_config_state_checked:{grantSummary}");
        await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
        await using (var grants = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ApplyPlannedServiceGrantsAsync(connection, grants, projects, cancellationToken);
            // CONNECT already exists, so the database ACL and protected state must be unchanged.
            InitializerChecks.AssertBoundary(existingBoundary,
                await ReadExistingBoundaryAsync(connection, grants, plan, cancellationToken), []);
            await AssertFunctionGrantsAsync(connection, grants, projects, operatorOid, runtimeOid, cancellationToken);
            await transactionCheckAsync(connection, grants, projects, operatorOid, runtimeOid, cancellationToken);
            await grants.CommitAsync(cancellationToken);
        }
        await RecordAsync("projects_config_exact_grants_committed");
        Console.WriteLine(grantSummary);
        Console.WriteLine("P1_POSTGRES_PROJECTS_CONFIG_GRANTS_APPLIED runtimeIdentityProof=false deploymentProof=false");
        return 0;
    }

    if (pendingMode)
    {
        // Incremental continuation proves the whole initialized state before its first write and never creates,
        // drops, or resets roles or schemas. Only Core/Orchestrator and Events may have pending versions.
        // Step 1: the five runtime roles must map exactly to their Entra principals, and each must already
        // have CONNECT. This mode never creates roles or grants CONNECT, so anything missing stops the run.
        principals = await P1PostgresContinuation.ReadPrincipalsAsync(principalConnection, null, plan, cancellationToken);
        var grantors = InitializerChecks.CaptureMembershipGrantors(
            plan.Services, principals, operatorOid, plan.Target.TenantId);
        InitializerChecks.AssertPrincipals(plan.Services, principals, operatorOid, plan.Target.TenantId, grantors);
        InitializerChecks.AssertRuntimeDatabaseConnect(existingBoundary, principals.Select(item => item.Oid).ToArray());
        await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
        // Step 2: check each service before any write. "before" keeps the live history for the receipt;
        // "pending" keeps the planned versions that are not yet applied.
        var before = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var pending = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var service in plan.Services)
        {
            // The schema must be owned by the operator and grant the runtime role only USAGE, and every
            // live table or sequence must be one the plan knows about. Unknown objects stop the run.
            var runtimeOid = principals.Single(item => item.Role == service.Role).Oid;
            InitializerChecks.AssertServiceSchemaAcl(service,
                await ReadJsonAsync<SchemaAclState>(connection, null, InitializerChecks.SchemaAclQuery, cancellationToken,
                    new NpgsqlParameter("schema", service.Schema)),
                operatorOid, runtimeOid);
            InitializerChecks.AssertRelationsWithinPlan(service,
                await ReadRelationsAsync(connection, null, service, true, cancellationToken), plan.Target.OperatorName);
            var planned = PlannedVersions(service);
            if (planned is not null)
            {
                // An initialized database always has a history table. Pending versions must extend the live
                // history as a suffix; a gap or an unknown live version fails here.
                var applied = await ReadIntHistoryAsync(connection, service.Schema,
                    HistoryTables(service)[0].Table, cancellationToken)
                    ?? throw new InvalidOperationException($"The {service.Service} migration history is missing.");
                before[service.Service] = [.. applied.Order()];
                pending[service.Service] = InitializerChecks.PendingMigrationVersions(planned, applied, service.Service);
                // Services with pending versions are verified after they migrate, not now.
                if (pending[service.Service].Length != 0) continue;
            }
            // Services with nothing pending must already match the reviewed migrations, inventory, and grants exactly.
            await P1PostgresContinuation.VerifyServiceMigrationsAsync(source, service, cancellationToken);
            await P1PostgresContinuation.AssertOwnedObjectsAsync(
                connection, service, plan.Target.OperatorName, cancellationToken);
            // The grant readback runs inside a transaction that is always rolled back, so it cannot change anything.
            await using var readback = await connection.BeginTransactionAsync(cancellationToken);
            await AssertFunctionGrantsAsync(connection, readback, service, operatorOid, runtimeOid, cancellationToken);
            await transactionCheckAsync(connection, readback, service, operatorOid, runtimeOid, cancellationToken);
            await readback.RollbackAsync(cancellationToken);
        }
        // A dry run stops here after printing what it would apply. It has made no writes and has no receipt.
        var summary = JsonSerializer.Serialize(new { before, pending, maxCoreVersion, maxEventsVersion });
        if (readOnlyMode)
        {
            Console.WriteLine(summary);
            Console.WriteLine("P1_POSTGRES_PENDING_MIGRATIONS_DRY_RUN writes=false");
            return 0;
        }

        // Step 3: migrate each service that has pending versions. The receipt records the start and the end
        // of each one so an interrupted run shows exactly where it stopped.
        await RecordAsync($"pending_state_checked:{summary}");
        foreach (var service in plan.Services.Where(item => pending.GetValueOrDefault(item.Service)?.Length > 0))
        {
            // Repeat the boundary preflight right before each migration in case another session changed it.
            await AssertPreflightAsync(connection, plan, existingBoundary, operatorOid, cancellationToken);
            await RecordAsync($"{service.Service}:migration_begin:{string.Join(',', pending[service.Service])}");
            // The service's own migrator applies all of its missing versions. The catalog check earlier
            // proved that this set is exactly the reviewed plan, so nothing beyond it can run.
            await P1PostgresContinuation.MigrateServiceAsync(source, service, cancellationToken);
            await P1PostgresContinuation.VerifyServiceMigrationsAsync(source, service, cancellationToken);
            // After migrating, the live history must equal the planned versions exactly.
            var after = await ReadIntHistoryAsync(connection, service.Schema,
                HistoryTables(service)[0].Table, cancellationToken) ?? [];
            if (!after.Order().SequenceEqual(PlannedVersions(service)!))
                throw new InvalidOperationException($"The {service.Service} history after migration differs from the reviewed plan.");
            // New tables must be owned by the operator, not by the runtime role.
            await P1PostgresContinuation.AssertOwnedObjectsAsync(
                connection, service, plan.Target.OperatorName, cancellationToken);
            await RecordAsync($"{service.Service}:migrated:{string.Join(',', after.Order())}");
        }

        // Re-apply the exact planned grants for the two incrementally migrated services in one checked transaction.
        await using (var grants = await connection.BeginTransactionAsync(cancellationToken))
        {
            // New tables from the pending migrations get their planned privileges here.
            foreach (var service in plan.Services.Where(item => PlannedVersions(item) is not null))
                await ApplyPlannedServiceGrantsAsync(connection, grants, service, cancellationToken);
            // CONNECT already exists, so the database ACL and protected state must be byte-for-byte unchanged.
            InitializerChecks.AssertBoundary(existingBoundary,
                await ReadExistingBoundaryAsync(connection, grants, plan, cancellationToken), []);
            // Read every service's grants back inside the same transaction. Any difference throws, and the
            // transaction is disposed without commit, so no partial grant change is kept.
            foreach (var service in plan.Services)
            {
                var runtimeOid = principals.Single(item => item.Role == service.Role).Oid;
                await AssertFunctionGrantsAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
                await transactionCheckAsync(connection, grants, service, operatorOid, runtimeOid, cancellationToken);
            }
            await grants.CommitAsync(cancellationToken);
        }
        // Step 4: record the final histories. Runtime identity sign-in and deployment are proven later, not here.
        var final = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var service in plan.Services.Where(item => PlannedVersions(item) is not null))
            final[service.Service] = [.. (await ReadIntHistoryAsync(connection, service.Schema,
                HistoryTables(service)[0].Table, cancellationToken) ?? []).Order()];
        var finalSummary = JsonSerializer.Serialize(new { before, after = final });
        await RecordAsync($"pending_migrations_and_exact_grants_committed:{finalSummary}");
        Console.WriteLine(finalSummary);
        Console.WriteLine("P1_POSTGRES_PENDING_MIGRATIONS_APPLIED runtimeIdentityProof=false deploymentProof=false");
        return 0;
    }

    // Only Core/Orchestrator and Events have integer version lists in the plan. Other services return null,
    // which means "no incremental migrations allowed; verify only".
    int[]? PlannedVersions(ServicePlan service) => service.Service switch
    {
        "orchestrator" => plan.CoreVersions,
        "events-and-sessions" => plan.EventsVersions,
        _ => null,
    };

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
    else if (freshMode)
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
                    foreach (var statement in InitializerChecks.TableGrantStatements(service, table))
                        await ExecuteAsync(connection, grants, statement, cancellationToken);
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
            foreach (var statement in InitializerChecks.TableGrantStatements(service, table))
                await ExecuteAsync(connection, grants, statement, cancellationToken);
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
    await InitializerChecks.ReportFailureAsync(error, receiptPath ?? "(none: read-only mode wrote no receipt)",
        RecordAsync, Console.Error,
        error is PostgresException postgres ? postgres.SqlState : null);
    return 1;
}

// Quotes identifiers using the same source-checked rules as the invariant helpers.
static string Quote(string value) => InitializerChecks.Quote(value);

// Issues one service's planned runtime grants inside the caller's transaction. The caller must read every
// grant back before commit; this helper only issues the statements.
static async Task ApplyPlannedServiceGrantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
    ServicePlan service, CancellationToken cancellationToken)
{
    // Grants are additive and idempotent, so re-granting privileges that already exist changes nothing.
    await ExecuteAsync(connection, transaction,
        $"GRANT USAGE ON SCHEMA {Quote(service.Schema)} TO {Quote(service.Role)}", cancellationToken);
    foreach (var table in service.Tables)
        // Each table gets its table privileges, plus one column-list UPDATE for a row-lock column or for
        // planned column-scoped updates such as the role-assignment revoke columns.
        foreach (var statement in InitializerChecks.TableGrantStatements(service, table))
            await ExecuteAsync(connection, transaction, statement, cancellationToken);
    foreach (var function in service.Functions)
    {
        var signature = $"{Quote(service.Schema)}.{function.Signature}";
        // PostgreSQL grants EXECUTE to PUBLIC by default. Remove it, then grant EXECUTE only to the runtime
        // role when the plan allows it.
        await ExecuteAsync(connection, transaction, $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken);
        if (function.Execute)
            await ExecuteAsync(connection, transaction,
                $"GRANT EXECUTE ON FUNCTION {signature} TO {Quote(service.Role)}", cancellationToken);
    }
}

// Parses an explicit approved maximum migration version.
static int ParseVersion(string value) => Regex.IsMatch(value, "^[1-9][0-9]{0,3}$")
    ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
    : throw new ArgumentException("Maximum migration versions must be positive integers.");

// Lists each service's migration history tables; the first is the service's own versioned history.
static (string Table, string Column)[] HistoryTables(ServicePlan service) => service.Service switch
{
    "projects-config" or "environment" => [("__ef_migrations_history", "MigrationId")],
    "knowledge" => [("knowledge_schema_migrations", "version"), ("outbox_schema_migrations", "version")],
    "events-and-sessions" => [("sessions_schema_migrations", "version"), ("outbox_schema_migrations", "version")],
    "orchestrator" => [("coordination_schema_migrations", "version"), ("outbox_schema_migrations", "version")],
    _ => throw new InvalidOperationException("The source plan contains an unsupported service."),
};

// Reads an integer migration history, or null when the history table does not exist.
static async Task<int[]?> ReadIntHistoryAsync(NpgsqlConnection connection, string schema, string table,
    CancellationToken cancellationToken)
{
    // Null (no table) differs from an empty list (table exists, nothing applied). Callers treat them differently.
    if (!await RelationExistsAsync(connection, schema, table, cancellationToken)) return null;
    await using var query = new NpgsqlCommand(
        $"SELECT version FROM {Quote(schema)}.{Quote(table)} ORDER BY version", connection);
    var versions = new List<int>();
    await using var reader = await query.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt32(0));
    return [.. versions];
}

// Reads an Entity Framework migration history, or null when the history table does not exist.
static async Task<string[]?> ReadTextHistoryAsync(NpgsqlConnection connection, string schema, string table,
    CancellationToken cancellationToken)
{
    // Same null-versus-empty rule as the integer history. Entity Framework ids are text, so they are read as strings.
    if (!await RelationExistsAsync(connection, schema, table, cancellationToken)) return null;
    await using var query = new NpgsqlCommand(
        $"SELECT \"MigrationId\" FROM {Quote(schema)}.{Quote(table)} ORDER BY 1", connection);
    var migrations = new List<string>();
    await using var reader = await query.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) migrations.Add(reader.GetString(0));
    return [.. migrations];
}

// Checks relation existence without failing on a missing schema.
static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string schema, string table,
    CancellationToken cancellationToken)
{
    // to_regclass returns NULL instead of raising an error when the schema or table is missing. The name is
    // passed as a parameter, so it is never spliced into the SQL text.
    await using var exists = new NpgsqlCommand("SELECT pg_catalog.to_regclass(@name) IS NOT NULL", connection);
    exists.Parameters.AddWithValue("name", $"{Quote(schema)}.{Quote(table)}");
    return await exists.ExecuteScalarAsync(cancellationToken) is true;
}

// Reads every relation in one service schema with its owner and the runtime role's effective privileges.
static Task<RelationState[]> ReadRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
    ServicePlan service, bool roleExists, CancellationToken cancellationToken) =>
    ReadJsonAsync<RelationState[]>(connection, transaction, InitializerChecks.RelationStateQuery, cancellationToken,
        new NpgsqlParameter("schema", service.Schema), new NpgsqlParameter("role", service.Role),
        new NpgsqlParameter("has_role", roleExists));

// Reads an optional JSON readback for the inspection report.
static async Task<JsonNode?> ReadNodeAsync(NpgsqlConnection connection, string sql,
    CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
{
    // The queries build their JSON in SQL. No row, or a NULL value, becomes null in the report instead of an error.
    await using var query = new NpgsqlCommand(sql, connection);
    query.Parameters.AddRange(parameters);
    return await query.ExecuteScalarAsync(cancellationToken) is string json ? JsonNode.Parse(json) : null;
}

// Converts a value for the inspection report, keeping null as JSON null.
static JsonNode? ToNode<T>(T? value) where T : class => value is null ? null : JsonSerializer.SerializeToNode(value);

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
        // Row-lock tables and column-scoped tables must hold UPDATE on exactly their planned columns.
        var updateColumns = InitializerChecks.PlannedUpdateColumns(table);
        if (updateColumns.Length != 0)
        {
            await using var columnCheck = new NpgsqlCommand(
                InitializerChecks.ExactUpdateColumnsQuery, connection, transaction);
            columnCheck.Parameters.AddWithValue("role", service.Role);
            columnCheck.Parameters.AddWithValue("table", $"{service.Schema}.{table.Table}");
            columnCheck.Parameters.AddWithValue("columns", updateColumns);
            if (await columnCheck.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException(
                    $"The {service.Service} role does not have exactly its planned UPDATE columns on {service.Schema}.{table.Table}.");
        }
    }
    // Table-level checks can't see column grants, such as UPDATE on one column of a read-only table.
    // The service's startup authority check rejects those, so they must fail here too.
    var columnGrants = new List<ColumnGrant>();
    await using (var columnQuery = new NpgsqlCommand(InitializerChecks.RuntimeColumnGrantsQuery, connection, transaction))
    {
        columnQuery.Parameters.AddWithValue("schema", service.Schema);
        columnQuery.Parameters.AddWithValue("runtime", NpgsqlTypes.NpgsqlDbType.Oid, runtimeOid);
        await using var reader = await columnQuery.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columnGrants.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
    }
    InitializerChecks.AssertRuntimeColumnGrants(service, columnGrants);
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
