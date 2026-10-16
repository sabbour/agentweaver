using System.Text.Json;
using Agentweaver.Environment;
using Agentweaver.EventsAndSessions;
using Agentweaver.Knowledge;
using Agentweaver.Orchestrator;
using Agentweaver.Projects.Config;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.P1PostgresInitializer;

/// <summary>Verifies or resumes the narrowly permitted PostgreSQL initialization sequence.</summary>
internal static class P1PostgresContinuation
{
    private static readonly string[] ExpectedServices =
        ["projects-config", "environment", "knowledge", "events-and-sessions", "orchestrator"];

    /// <summary>Reads the planned role identities and memberships from the supplied connection.</summary>
    internal static async Task<RuntimePrincipal[]> ReadPrincipalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PostgresPlan plan,
        CancellationToken cancellationToken,
        string principalFunction = InitializerChecks.AadPrincipalFunction)
    {
        await using var command = new NpgsqlCommand(
            InitializerChecks.PrincipalReadbackQuery(principalFunction), connection, transaction);
        command.Parameters.AddWithValue("roles", plan.Services.Select(item => item.Role).ToArray());
        var json = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("The required runtime principal facts could not be read.");
        return JsonSerializer.Deserialize<RuntimePrincipal[]>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The required runtime principal facts are missing.");
    }

    /// <summary>Proves that the target contains exactly the migrated, owner-only Projects prefix.</summary>
    /// <remarks>The checks bind database identity, schema ownership, migration history, objects, guards, and grants.</remarks>
    internal static async Task AssertProjectsPrefixAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection connection,
        PostgresPlan plan,
        RuntimePrincipal[] principals,
        uint operatorOid,
        string operatorName,
        string tenantId,
        CancellationToken cancellationToken,
        bool requireOnlyProjectsSchema = true)
    {
        InitializerChecks.AssertOwnerOnlyObjectPlan(plan.Services);
        if (!plan.Services.Select(item => item.Service).SequenceEqual(ExpectedServices, StringComparer.Ordinal))
            throw new InvalidOperationException("The exact five-service continuation order is required.");

        var projects = plan.Services[0];
        var grantors = InitializerChecks.CaptureMembershipGrantors(
            plan.Services, principals, operatorOid, tenantId);
        InitializerChecks.AssertPrincipals(plan.Services, principals, operatorOid, tenantId, grantors);

        await using (var database = new NpgsqlCommand("""
            SELECT current_database() = @database
               AND current_user = @operator
               AND (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user) = @operator_oid
            """, connection))
        {
            database.Parameters.AddWithValue("database", plan.Target.Database);
            database.Parameters.AddWithValue("operator", operatorName);
            database.Parameters.AddWithValue("operator_oid", NpgsqlDbType.Oid, operatorOid);
            if (await database.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The continuation connection is not the exact target operator and database.");
        }

        if (requireOnlyProjectsSchema)
        {
            await using var otherSchemas = new NpgsqlCommand("""
                SELECT NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_namespace
                    WHERE nspname = ANY(@schemas))
                """, connection);
            otherSchemas.Parameters.AddWithValue("schemas",
                plan.Services.Skip(1).Select(item => item.Schema).ToArray());
            if (await otherSchemas.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The continuation target contains a service schema beyond the Projects-only prefix.");
        }

        await using (var schemaAcl = new NpgsqlCommand("""
            SELECT n.nspowner = @owner_oid
               AND (SELECT array_agg(a.privilege_type ORDER BY a.privilege_type)
                    FROM pg_catalog.aclexplode(coalesce(n.nspacl,
                        pg_catalog.acldefault('n', n.nspowner))) AS a) = ARRAY['CREATE', 'USAGE']::text[]
               AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.aclexplode(coalesce(n.nspacl,
                        pg_catalog.acldefault('n', n.nspowner))) AS a
                    WHERE a.grantee <> n.nspowner OR a.grantor <> n.nspowner OR a.is_grantable)
            FROM pg_catalog.pg_namespace AS n
            WHERE n.nspname = @schema
            """, connection))
        {
            schemaAcl.Parameters.AddWithValue("owner_oid", NpgsqlDbType.Oid, operatorOid);
            schemaAcl.Parameters.AddWithValue("schema", projects.Schema);
            if (await schemaAcl.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The Projects schema owner or ACL differs from the exact continuation prefix.");
        }

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", projects.Schema))
            .Options;
        await using (var context = new ProjectsConfigDbContext(options))
        {
            var expected = context.Database.GetMigrations().ToArray();
            var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            InitializerChecks.AssertExactMigrationHistory(expected, applied, projects.Service);
        }

        await AssertOwnedObjectsAsync(connection, projects, operatorName, cancellationToken).ConfigureAwait(false);
        await AssertRelationAclsAsync(connection, projects.Schema, cancellationToken).ConfigureAwait(false);
        await AssertIdentitySequenceAsync(connection, projects.Schema, cancellationToken).ConfigureAwait(false);
        await AssertProjectsSetupGuardsAsync(connection, projects.Schema, operatorOid, cancellationToken)
            .ConfigureAwait(false);
        await AssertNoRuntimePrivilegesAsync(connection, projects, principals.Select(item => item.Role).ToArray(),
            cancellationToken).ConfigureAwait(false);
        await AssertFunctionGrantsAsync(
            connection, projects, principals, operatorOid, cancellationToken, beforeRuntimeGrants: true)
            .ConfigureAwait(false);
    }

    /// <summary>Creates and verifies only the missing service schemas after rechecking the existing prefix.</summary>
    internal static async Task MigrateRemainingServicesAsync(
        NpgsqlDataSource dataSource,
        PostgresPlan plan,
        string owner,
        Func<string, Task> record,
        Func<Task> verifyBeforeEachSchema,
        Func<ServicePlan, Task> verifyOwnedObjects,
        CancellationToken cancellationToken)
    {
        if (!plan.Services.Select(item => item.Service).SequenceEqual(ExpectedServices, StringComparer.Ordinal))
            throw new InvalidOperationException("The exact five-service continuation order is required.");

        foreach (var service in plan.Services.Skip(1))
        {
            await verifyBeforeEachSchema().ConfigureAwait(false);
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, $"CREATE SCHEMA {InitializerChecks.Quote(service.Schema)} AUTHORIZATION {InitializerChecks.Quote(owner)}",
                    cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, $"REVOKE ALL ON SCHEMA {InitializerChecks.Quote(service.Schema)} FROM PUBLIC",
                    cancellationToken).ConfigureAwait(false);
            }
            await record($"{service.Service}:schema_created").ConfigureAwait(false);

            await MigrateServiceAsync(dataSource, service, cancellationToken).ConfigureAwait(false);
            await VerifyServiceMigrationsAsync(dataSource, service, cancellationToken).ConfigureAwait(false);
            await record($"{service.Service}:migrated").ConfigureAwait(false);
            await verifyOwnedObjects(service).ConfigureAwait(false);
            await record($"{service.Service}:exact_inventory_checked").ConfigureAwait(false);
        }
    }

    /// <summary>Dispatches one planned service to its own migration implementation.</summary>
    internal static async Task MigrateServiceAsync(
        NpgsqlDataSource dataSource,
        ServicePlan service,
        CancellationToken cancellationToken)
    {
        switch (service.Service)
        {
            case "projects-config":
                await ProjectsConfigMigrator.MigrateAsync(dataSource,
                    new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                        .UseNpgsql(dataSource, settings =>
                            settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "environment":
                await EnvironmentMigrator.MigrateAsync(dataSource,
                    new DbContextOptionsBuilder<EnvironmentDbContext>()
                        .UseNpgsql(dataSource, settings =>
                            settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "knowledge":
                await KnowledgeMigrator.MigrateAsync(dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            case "events-and-sessions":
                await EventsAndSessionsMigrator.MigrateAsync(dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            case "orchestrator":
                await CoordinationOwnerMigrator.MigrateAsync(dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("The source plan contains an unsupported service.");
        }
    }

    /// <summary>Verifies the exact applied migration inventory for one service without mutating it.</summary>
    internal static async Task VerifyServiceMigrationsAsync(
        NpgsqlDataSource dataSource,
        ServicePlan service,
        CancellationToken cancellationToken)
    {
        switch (service.Service)
        {
            case "projects-config":
            {
                var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                        .UseNpgsql(dataSource, settings =>
                            settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options;
                await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(dataSource, options, cancellationToken)
                    .ConfigureAwait(false);
                await using var context = new ProjectsConfigDbContext(options);
                InitializerChecks.AssertExactMigrationHistory(
                    context.Database.GetMigrations().ToArray(),
                    (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray(),
                    service.Service);
                break;
            }
            case "environment":
            {
                var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
                        .UseNpgsql(dataSource, settings =>
                            settings.MigrationsHistoryTable("__ef_migrations_history", service.Schema)).Options;
                await EnvironmentMigrator.VerifyMigrationsAppliedAsync(dataSource, options, cancellationToken)
                    .ConfigureAwait(false);
                await using var context = new EnvironmentDbContext(options);
                InitializerChecks.AssertExactMigrationHistory(
                    context.Database.GetMigrations().ToArray(),
                    (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray(),
                    service.Service);
                break;
            }
            case "knowledge":
                await KnowledgeMigrator.VerifyMigrationsAppliedAsync(
                    dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            case "events-and-sessions":
                await EventsAndSessionsMigrator.VerifyAsync(dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            case "orchestrator":
                await CoordinationOwnerMigrator.VerifyAsync(dataSource, service.Schema, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("The source plan contains an unsupported service.");
        }
    }

    /// <summary>Requires a service schema to contain only the planned objects, all owned by the operator.</summary>
    internal static async Task AssertOwnedObjectsAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand("""
            SELECT n.nspowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
              AND coalesce((SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c
                   WHERE c.relnamespace = n.oid AND c.relkind IN ('r', 'p', 'f')), ARRAY[]::text[]) = @tables
              AND coalesce((SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c
                   WHERE c.relnamespace = n.oid AND c.relkind = 'S'), ARRAY[]::text[]) = @sequences
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
                   WHERE c.relnamespace = n.oid AND c.relkind IN ('v', 'm'))
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
        query.Parameters.AddWithValue("tables", service.Tables.Select(item => item.Table)
            .Concat(service.OwnerOnlyTables).Order(StringComparer.Ordinal).ToArray());
        query.Parameters.AddWithValue("sequences", service.OwnerOnlySequences.Order(StringComparer.Ordinal).ToArray());
        query.Parameters.AddWithValue("functions",
            service.Functions.Select(item => $"{service.Schema}.{item.Signature}").ToArray());
        if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException($"The {service.Service} schema contains unexpected or foreign-owned objects. No grants were applied.");
    }

    /// <summary>Rejects relation and column ACL entries not owned by the schema owner.</summary>
    private static async Task AssertRelationAclsAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace,
                LATERAL pg_catalog.aclexplode(coalesce(c.relacl,
                    pg_catalog.acldefault(CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END,
                        c.relowner))) AS a
                WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'f', 'S', 'v', 'm')
                  AND (a.grantee <> c.relowner OR a.grantor <> c.relowner OR a.is_grantable))
              AND NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_attribute AS attribute
                JOIN pg_catalog.pg_class AS c ON c.oid = attribute.attrelid
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace,
                LATERAL pg_catalog.aclexplode(attribute.attacl) AS a
                WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'f', 'v', 'm')
                  AND attribute.attnum > 0 AND NOT attribute.attisdropped
                  AND (a.grantee <> c.relowner OR a.grantor <> c.relowner OR a.is_grantable))
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("The Projects relation ACLs differ from the owner-only continuation prefix.");
    }

    /// <summary>Confirms a migrated service remains owner-only before runtime grants are applied.</summary>
    internal static async Task AssertServiceUnprivilegedAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        RuntimePrincipal[] principals,
        uint ownerOid,
        CancellationToken cancellationToken)
    {
        await AssertRelationAclsAsync(connection, service.Schema, cancellationToken).ConfigureAwait(false);
        await AssertNoRuntimePrivilegesAsync(connection, service, principals.Select(item => item.Role).ToArray(),
            cancellationToken).ConfigureAwait(false);
        await AssertFunctionGrantsAsync(
            connection, service, principals, ownerOid, cancellationToken, beforeRuntimeGrants: true).ConfigureAwait(false);
    }

    /// <summary>Checks that the Projects revision identity column uses its exact planned sequence.</summary>
    private static async Task AssertIdentitySequenceAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_serial_sequence(@table, 'revision') = @sequence
            """, connection);
        command.Parameters.AddWithValue("table", $"{schema}.platform_runtime_revisions");
        command.Parameters.AddWithValue("sequence", $"{schema}.platform_runtime_revisions_revision_seq");
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("The Projects identity sequence is not attached to the exact source column.");
    }

    /// <summary>Verifies the owner-only triggers that prevent mutation of setup-operation history.</summary>
    private static async Task AssertProjectsSetupGuardsAsync(
        NpgsqlConnection connection,
        string schema,
        uint ownerOid,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(trigger.oid) = 2 AND bool_and(
                trigger.tgfoid = function.oid
                AND trigger.tgtype = expected.trigger_type
                AND trigger.tgenabled = 'O'
                AND NOT trigger.tgisinternal
                AND trigger.tgattr = ''::int2vector
                AND trigger.tgqual IS NULL
                AND trigger.tgnargs = 0
                AND function.proowner = @owner_oid
                AND function.prokind = 'f'
                AND function.pronargs = 0
                AND function.prosecdef = false
                AND function.prorettype = 'pg_catalog.trigger'::pg_catalog.regtype
                AND language.lanname = 'plpgsql'
                AND btrim(pg_catalog.regexp_replace(function.prosrc, '\s+', ' ', 'g')) =
                    'BEGIN RAISE EXCEPTION ''project setup operation records are immutable'' USING ERRCODE = ''55000''; END;')
            FROM (VALUES
                ('project_setup_operations_immutable', 27),
                ('project_setup_operations_no_truncate', 34)
            ) AS expected(trigger_name, trigger_type)
            LEFT JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = @schema
            LEFT JOIN pg_catalog.pg_class AS relation
                ON relation.relnamespace = namespace.oid AND relation.relname = 'project_setup_operations'
            LEFT JOIN pg_catalog.pg_trigger AS trigger
                ON trigger.tgrelid = relation.oid AND trigger.tgname = expected.trigger_name
            LEFT JOIN pg_catalog.pg_proc AS function ON function.oid = trigger.tgfoid
            LEFT JOIN pg_catalog.pg_language AS language ON language.oid = function.prolang
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("owner_oid", NpgsqlDbType.Oid, ownerOid);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("The Projects setup-operation mutation guards are not exact and operator-owned.");
    }

    /// <summary>Rejects any pre-existing schema, table, or owner-only sequence authority for service roles.</summary>
    private static async Task AssertNoRuntimePrivilegesAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        string[] runtimeRoles,
        CancellationToken cancellationToken)
    {
        foreach (var role in runtimeRoles)
        {
            await using (var schema = new NpgsqlCommand("""
                SELECT NOT has_schema_privilege(@role, @schema, 'USAGE')
                   AND NOT has_schema_privilege(@role, @schema, 'CREATE')
                """, connection))
            {
                schema.Parameters.AddWithValue("role", role);
                schema.Parameters.AddWithValue("schema", service.Schema);
                if (await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException($"Runtime role {role} already has schema authority on {service.Schema}.");
            }

            var tables = service.Tables.Select(item => item.Table).Concat(service.OwnerOnlyTables).ToArray();
            await using (var tablesCommand = new NpgsqlCommand("""
                SELECT NOT EXISTS (
                    SELECT 1
                    FROM unnest(@tables) AS table_name(name)
                    CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege(name)
                    WHERE pg_catalog.has_table_privilege(@role, @schema || '.' || table_name.name, privilege.name))
                """, connection))
            {
                tablesCommand.Parameters.AddWithValue("role", role);
                tablesCommand.Parameters.AddWithValue("schema", service.Schema);
                tablesCommand.Parameters.AddWithValue("tables", tables);
                if (await tablesCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException($"Runtime role {role} already has table authority on {service.Schema}.");
            }

            foreach (var sequence in service.OwnerOnlySequences)
            {
                foreach (var privilege in new[] { "USAGE", "SELECT", "UPDATE" })
                {
                    await using var sequenceCommand = new NpgsqlCommand(
                        "SELECT pg_catalog.has_sequence_privilege(@role, @sequence, @privilege)", connection);
                    sequenceCommand.Parameters.AddWithValue("role", role);
                    sequenceCommand.Parameters.AddWithValue("sequence", $"{service.Schema}.{sequence}");
                    sequenceCommand.Parameters.AddWithValue("privilege", privilege);
                    if (await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                        throw new InvalidOperationException($"Runtime role {role} already has sequence authority on {service.Schema}.{sequence}.");
                }
            }
        }
    }

    /// <summary>Checks every planned function grant for each runtime role.</summary>
    private static async Task AssertFunctionGrantsAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        RuntimePrincipal[] principals,
        uint ownerOid,
        CancellationToken cancellationToken,
        bool beforeRuntimeGrants = false)
    {
        foreach (var principal in principals)
        {
            var functions = await ReadFunctionStatesAsync(connection, service, principal.Role, cancellationToken)
                .ConfigureAwait(false);
            InitializerChecks.AssertFunctions(
                service, functions, ownerOid, principal.Oid, beforeRuntimeGrants);
        }
    }

    /// <summary>Reads function owners, execute ACLs, and effective runtime execution rights.</summary>
    private static async Task<FunctionState[]> ReadFunctionStatesAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        string role,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(jsonb_agg(jsonb_build_object(
              'oid', p.oid, 'signature', required.signature, 'ownerOid', p.proowner,
              'runtimeCanExecute', pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE'),
              'grants', (SELECT coalesce(jsonb_agg(jsonb_build_object(
                'grantor', a.grantor, 'grantee', a.grantee,
                'privilege', a.privilege_type, 'grantable', a.is_grantable)
                ORDER BY a.grantor, a.grantee, a.privilege_type, a.is_grantable), '[]'::jsonb)
                FROM pg_catalog.aclexplode(coalesce(p.proacl,
                    pg_catalog.acldefault('f', p.proowner))) a))
              ORDER BY required.signature), '[]'::jsonb)::text
            FROM unnest(@signatures) AS required(signature)
            JOIN pg_catalog.pg_proc p
                ON p.oid = pg_catalog.to_regprocedure(@schema || '.' || required.signature)
            """, connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("schema", service.Schema);
        command.Parameters.AddWithValue("signatures",
            service.Functions.Select(item => item.Signature).ToArray());
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("The Projects function ACLs could not be read.");
        return JsonSerializer.Deserialize<FunctionState[]>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The Projects function ACL readback is missing.");
    }

    /// <summary>Executes one SQL statement on an existing connection without changing its transaction scope.</summary>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
