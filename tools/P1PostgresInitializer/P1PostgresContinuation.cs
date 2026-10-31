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
    internal const string ProjectsConfigMigrationOwner = "agentweaver-id-projects-config-migration";
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

    /// <summary>Proves that the target contains the migrated Projects prefix with its reviewed runtime grants.</summary>
    /// <remarks>The checks bind database identity, dedicated schema ownership, migration history, objects, guards, and grants.</remarks>
    internal static async Task AssertProjectsPrefixAsync(
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
        var projectsOwnerOid = await ReadRoleOidAsync(
            connection, ProjectsConfigMigrationOwner, cancellationToken).ConfigureAwait(false);
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

        var runtimeOid = principals.Single(item => item.Role == projects.Role).Oid;
        await using (var schemaAcl = new NpgsqlCommand(InitializerChecks.SchemaAclQuery, connection))
        {
            schemaAcl.Parameters.AddWithValue("schema", projects.Schema);
            var schemaJson = await schemaAcl.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            var schemaState = schemaJson is null
                ? null
                : JsonSerializer.Deserialize<SchemaAclState>(schemaJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            InitializerChecks.AssertServiceSchemaAcl(projects, schemaState, projectsOwnerOid, runtimeOid);
        }

        await AssertProjectsMigrationHistoryAsync(connection, projects, cancellationToken).ConfigureAwait(false);

        await AssertOwnedObjectsAsync(
            connection, projects, ProjectsConfigMigrationOwner, cancellationToken).ConfigureAwait(false);
        await AssertIdentitySequenceAsync(connection, projects.Schema, cancellationToken).ConfigureAwait(false);
        await AssertProjectsSetupGuardsAsync(connection, projects.Schema, projectsOwnerOid, cancellationToken)
            .ConfigureAwait(false);
        await AssertNoRuntimePrivilegesAsync(connection, projects,
            principals.Where(item => item.Role != projects.Role).Select(item => item.Role).ToArray(),
            cancellationToken).ConfigureAwait(false);
        await AssertNoRuntimeFunctionPrivilegesAsync(connection, projects,
            principals.Where(item => item.Role != projects.Role).Select(item => item.Role).ToArray(),
            cancellationToken)
            .ConfigureAwait(false);
        await ProjectsConfigRuntimeGrantPlan.VerifyRuntimeGrantsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Proves the operator can assume the dedicated Projects migration role without committing temporary membership.</summary>
    internal static async Task AssertProjectsMigrationOwnerReadyAsync(
        NpgsqlConnection connection,
        string operatorName,
        CancellationToken cancellationToken)
    {
        await using (var identity = new NpgsqlCommand("""
            SELECT current_user = @operator
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = @owner AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
                     AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
               AND NOT has_database_privilege(@owner, current_database(), 'CREATE')
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_auth_members
                   WHERE member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner))
            """, connection))
        {
            identity.Parameters.AddWithValue("operator", operatorName);
            identity.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
            if (await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException(
                    "Fresh initialization requires the exact, dedicated Projects migration role with no memberships granted to that owner.");
        }

        if (await CanAssumeProjectsMigrationOwnerAsync(connection, null, cancellationToken).ConfigureAwait(false))
            return;

        if (await HasDirectMigrationOwnerMembershipAsync(connection, null, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The operator has a Projects migration-owner membership without SET permission; reconcile it before initialization.");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction,
                $"GRANT {InitializerChecks.Quote(ProjectsConfigMigrationOwner)} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE",
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                "The operator must have SET permission on the dedicated Projects migration role, or be able to grant temporary SET membership, before initialization writes begin.",
                error);
        }
        await using (var canAssume = new NpgsqlCommand(
            "SELECT pg_catalog.pg_has_role(current_user, @owner, 'SET')", connection, transaction))
        {
            canAssume.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
            if (await canAssume.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException(
                    "The initializer operator cannot temporarily assume the Projects migration role.");
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Migrates a fresh Projects schema and reconciles its grants atomically as its dedicated owner.</summary>
    internal static async Task MigrateProjectsConfigAsDedicatedOwnerAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        CancellationToken cancellationToken)
    {
        if (service.Service != "projects-config")
            throw new InvalidOperationException("Only Projects & Config uses the dedicated migration owner.");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var temporaryMembership = !await CanAssumeProjectsMigrationOwnerAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        if (temporaryMembership)
        {
            if (await HasDirectMigrationOwnerMembershipAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The operator has a Projects migration-owner membership without SET permission; reconcile it before initialization.");
            await ExecuteAsync(connection, transaction,
                $"GRANT {InitializerChecks.Quote(ProjectsConfigMigrationOwner)} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE",
                cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction,
            $"CREATE SCHEMA {InitializerChecks.Quote(service.Schema)} AUTHORIZATION {InitializerChecks.Quote(ProjectsConfigMigrationOwner)}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"SET LOCAL ROLE {InitializerChecks.Quote(ProjectsConfigMigrationOwner)}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"REVOKE ALL ON SCHEMA {InitializerChecks.Quote(service.Schema)} FROM PUBLIC",
            cancellationToken).ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", service.Schema))
            .Options;
        await ProjectsConfigMigrator.MigrateAsync(connection, transaction, options, cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
        if (temporaryMembership)
            await ExecuteAsync(connection, transaction,
                $"REVOKE {InitializerChecks.Quote(ProjectsConfigMigrationOwner)} FROM CURRENT_USER",
                cancellationToken).ConfigureAwait(false);
        await using (var membership = new NpgsqlCommand("""
            SELECT CASE WHEN @temporary
                THEN NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_auth_members
                    WHERE roleid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
                      AND member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user))
                ELSE pg_catalog.pg_has_role(current_user, @owner, 'SET')
            END
            """, connection, transaction))
        {
            membership.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
            membership.Parameters.AddWithValue("temporary", temporaryMembership);
            if (await membership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Projects migration-owner membership changed unexpectedly.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await AssertProjectsMigrationHistoryAsync(connection, service, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Requires a service schema to contain only the planned objects, all owned by the expected owner.</summary>
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
                              WHERE NOT EXISTS (
                                  SELECT 1 FROM pg_catalog.pg_proc p
                                  WHERE p.pronamespace = n.oid
                                    AND format('%I(%s)', p.proname, coalesce((
                                        SELECT string_agg(pg_catalog.format_type(argument.type_oid, NULL),
                                            ', ' ORDER BY argument.ordinality)
                                        FROM unnest(p.proargtypes) WITH ORDINALITY
                                            AS argument(type_oid, ordinality)), '')) = required.signature))
            FROM pg_catalog.pg_namespace n WHERE n.nspname = @schema
            """, connection);
        query.Parameters.AddWithValue("owner", owner);
        query.Parameters.AddWithValue("schema", service.Schema);
        query.Parameters.AddWithValue("tables", service.Tables.Select(item => item.Table)
            .Concat(service.OwnerOnlyTables).Order(StringComparer.Ordinal).ToArray());
        query.Parameters.AddWithValue("sequences", service.OwnerOnlySequences.Order(StringComparer.Ordinal).ToArray());
        query.Parameters.AddWithValue("functions",
            service.Functions.Select(item => item.Signature).ToArray());
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
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS sequence
                JOIN pg_catalog.pg_namespace AS sequence_schema ON sequence_schema.oid = sequence.relnamespace
                JOIN pg_catalog.pg_depend AS dependency
                  ON dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                 AND dependency.objid = sequence.oid
                 AND dependency.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                 AND dependency.deptype IN ('a', 'i')
                JOIN pg_catalog.pg_class AS relation ON relation.oid = dependency.refobjid
                JOIN pg_catalog.pg_namespace AS relation_schema ON relation_schema.oid = relation.relnamespace
                JOIN pg_catalog.pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                 AND attribute.attnum = dependency.refobjsubid
                WHERE sequence.relkind = 'S'
                  AND sequence_schema.nspname = @schema
                  AND sequence.relname = 'platform_runtime_revisions_revision_seq'
                  AND relation_schema.nspname = @schema
                  AND relation.relname = 'platform_runtime_revisions'
                  AND attribute.attname = 'revision')
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
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
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = @schema
                    JOIN pg_catalog.pg_class AS relation
                      ON relation.relnamespace = namespace.oid AND relation.relname = table_name.name
                    CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilege(name)
                    WHERE pg_catalog.has_table_privilege(@role, relation.oid, privilege.name))
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
                    await using var sequenceCommand = new NpgsqlCommand("""
                        SELECT pg_catalog.has_sequence_privilege(@role, sequence.oid, @privilege)
                        FROM pg_catalog.pg_class AS sequence
                        JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = sequence.relnamespace
                        WHERE namespace.nspname = @schema AND sequence.relname = @sequence
                          AND sequence.relkind = 'S'
                        """, connection);
                    sequenceCommand.Parameters.AddWithValue("role", role);
                    sequenceCommand.Parameters.AddWithValue("schema", service.Schema);
                    sequenceCommand.Parameters.AddWithValue("sequence", sequence);
                    sequenceCommand.Parameters.AddWithValue("privilege", privilege);
                    if (await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                        throw new InvalidOperationException($"Runtime role {role} already has sequence authority on {service.Schema}.{sequence}.");
                }
            }
        }
    }

    private static async Task AssertNoRuntimeFunctionPrivilegesAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        string[] runtimeRoles,
        CancellationToken cancellationToken)
    {
        if (runtimeRoles.Length == 0) return;
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1
                FROM unnest(@roles) AS runtime(role_name)
                CROSS JOIN pg_catalog.pg_proc AS p
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = p.pronamespace
                WHERE namespace.nspname = @schema
                  AND pg_catalog.has_function_privilege(runtime.role_name, p.oid, 'EXECUTE'))
            """, connection);
        command.Parameters.AddWithValue("roles", runtimeRoles);
        command.Parameters.AddWithValue("schema", service.Schema);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                $"A non-Projects runtime role has unexpected EXECUTE privileges on {service.Schema}.");
    }

    private static async Task<uint> ReadRoleOidAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role", connection);
        command.Parameters.AddWithValue("role", role);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is uint oid
            ? oid
            : throw new InvalidOperationException($"The dedicated Projects & Config migration owner {role} is missing.");
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

    private static async Task AssertProjectsMigrationHistoryAsync(
        NpgsqlConnection connection,
        ServicePlan service,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryMembership = !await CanAssumeProjectsMigrationOwnerAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (temporaryMembership)
            {
                if (await HasDirectMigrationOwnerMembershipAsync(connection, transaction, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "The operator has a Projects migration-owner membership without SET permission; reconcile it before verification.");
                try
                {
                    await ExecuteAsync(connection, transaction,
                        $"GRANT {InitializerChecks.Quote(ProjectsConfigMigrationOwner)} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                {
                    throw new InvalidOperationException(
                        "The operator must have SET permission on the dedicated Projects migration role, or be able to grant temporary SET membership, to verify Projects migration history.",
                        error);
                }
            }
            await ExecuteAsync(connection, transaction,
                $"SET LOCAL ROLE {InitializerChecks.Quote(ProjectsConfigMigrationOwner)}", cancellationToken)
                .ConfigureAwait(false);
            await using (var identity = new NpgsqlCommand(
                "SELECT current_user = @owner", connection, transaction))
            {
                identity.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
                if (await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("Projects migration history must be read as its dedicated owner.");
            }

            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(connection, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", service.Schema))
                .Options;
            await using (var context = new ProjectsConfigDbContext(options))
            {
                await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
                InitializerChecks.AssertExactMigrationHistory(
                    context.Database.GetMigrations().ToArray(),
                    (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray(),
                    service.Service);
            }

            await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
            if (temporaryMembership)
                await ExecuteAsync(connection, transaction,
                    $"REVOKE {InitializerChecks.Quote(ProjectsConfigMigrationOwner)} FROM CURRENT_USER",
                    cancellationToken).ConfigureAwait(false);
            await using (var membership = new NpgsqlCommand("""
                SELECT CASE WHEN @temporary
                    THEN NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_auth_members
                        WHERE roleid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
                          AND member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user))
                    ELSE pg_catalog.pg_has_role(current_user, @owner, 'SET')
                END
                """, connection, transaction))
            {
                membership.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
                membership.Parameters.AddWithValue("temporary", temporaryMembership);
                if (await membership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("Projects migration-owner membership changed unexpectedly during history verification.");
            }
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> HasDirectMigrationOwnerMembershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_auth_members
                WHERE roleid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @owner)
                  AND member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user))
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<bool> CanAssumeProjectsMigrationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user)
               AND pg_catalog.pg_has_role(current_user, @owner, 'SET')
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", ProjectsConfigMigrationOwner);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
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

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
