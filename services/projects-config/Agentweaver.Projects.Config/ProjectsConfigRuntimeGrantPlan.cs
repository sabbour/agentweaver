using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal static partial class ProjectsConfigRuntimeGrantPlan
{
    private const string MigrationOwner = "agentweaver-id-projects-config-migration";
    private const string ResourceName = "Agentweaver.Projects.Config.Resources.P1PostgresPlan.json";
    private static readonly string[] TablePrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static readonly string[] ColumnPrivileges = ["SELECT", "INSERT", "UPDATE", "REFERENCES"];
    private static readonly Lazy<ServicePlan> Plan = new(LoadPlan);

    internal static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var plan = Plan.Value;
        // The dedicated migration owner and exact schema inventory are checked before any grants are applied.
        await VerifyMigrationOwnerAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            $"GRANT USAGE ON SCHEMA {Quote(plan.Schema)} TO {Quote(plan.Role)}", cancellationToken).ConfigureAwait(false);
        foreach (var table in plan.Tables)
        {
            var relation = $"{Quote(plan.Schema)}.{Quote(table.Name)}";
            if (table.Privileges.Length > 0)
                await ExecuteAsync(connection, transaction,
                    $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {Quote(plan.Role)}",
                    cancellationToken).ConfigureAwait(false);
            if (table.UpdateColumns.Length > 0)
                await ExecuteAsync(connection, transaction,
                    $"GRANT UPDATE ({string.Join(", ", table.UpdateColumns.Select(Quote))}) ON TABLE {relation} TO {Quote(plan.Role)}",
                    cancellationToken).ConfigureAwait(false);
        }
        foreach (var function in plan.Functions)
        {
            var signature = $"{Quote(plan.Schema)}.{function.Signature}";
            await ExecuteAsync(connection, transaction,
                $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
            if (function.Execute)
                await ExecuteAsync(connection, transaction,
                    $"GRANT EXECUTE ON FUNCTION {signature} TO {Quote(plan.Role)}", cancellationToken).ConfigureAwait(false);
        }
        await VerifyRuntimeGrantsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyRuntimeGrantsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var plan = Plan.Value;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await VerifyRuntimeGrantsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyMigrationPrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_user = @owner
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = current_user AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
                     AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
               AND NOT pg_catalog.has_database_privilege(current_user, current_database(), 'CREATE')
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_auth_members
                   WHERE member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user))
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_namespace
                   WHERE nspname = 'projects_config'
                     AND pg_catalog.pg_get_userbyid(nspowner) = @owner)
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", MigrationOwner);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "Projects & Config migrations require the dedicated schema owner; no migrations or grants were applied.");
    }

    private static async Task VerifyMigrationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServicePlan plan,
        CancellationToken cancellationToken)
    {
        var tables = plan.Tables.Select(table => table.Name).Concat(plan.OwnerOnlyTables)
            .Order(StringComparer.Ordinal).ToArray();
        var functions = plan.Functions.Select(function => function.Signature).Order(StringComparer.Ordinal).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT current_user = @owner
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = current_user AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
                     AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls
               )
               AND NOT has_database_privilege(current_user, current_database(), 'CREATE')
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_auth_members
                   WHERE member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
               )
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_namespace AS n
                   JOIN pg_catalog.pg_roles AS owner ON owner.oid = n.nspowner
                   WHERE n.nspname = @schema
                     AND owner.rolname = @owner
                     AND coalesce((
                         SELECT array_agg(c.relname::text ORDER BY c.relname)
                         FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind IN ('r', 'p', 'f')
                     ), ARRAY[]::text[]) = @tables
                     AND coalesce((
                         SELECT array_agg(c.relname::text ORDER BY c.relname)
                         FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind = 'S'
                     ), ARRAY[]::text[]) = @sequences
                     AND (SELECT count(*) FROM pg_catalog.pg_proc AS p
                          WHERE p.pronamespace = n.oid AND p.prokind IN ('f', 'p')) = cardinality(@functions)
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_proc AS p
                         WHERE p.pronamespace = n.oid AND p.proowner <> owner.oid
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM unnest(@functions) AS signature
                         WHERE pg_catalog.to_regprocedure(format('%I.%s', @schema, signature)) IS NULL
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind IN ('v', 'm')
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relowner <> owner.oid
                     )
               )
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", MigrationOwner);
        command.Parameters.AddWithValue("schema", plan.Schema);
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("sequences", plan.OwnerOnlySequences.Order(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("functions", functions);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "Projects & Config migrations require the dedicated schema owner and the exact reviewed object inventory; no runtime grants were applied.");
    }

    private static async Task VerifyRuntimeGrantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServicePlan plan,
        CancellationToken cancellationToken)
    {
        await using (var guard = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM pg_catalog.pg_trigger t
              JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
              JOIN pg_catalog.pg_namespace pn ON pn.oid = p.pronamespace
              JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attname = 'provider_id'
              WHERE n.nspname = 'projects_config' AND c.relname = 'model_providers'
                AND t.tgname = 'model_providers_id_immutable' AND t.tgtype = 19
                AND t.tgenabled = 'O' AND NOT t.tgisinternal AND t.tgnargs = 0
                AND t.tgattr = (a.attnum::text)::int2vector
                AND substring(pg_catalog.pg_get_triggerdef(t.oid) from ' WHEN (.*) EXECUTE FUNCTION ') =
                    '(((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text))'
                AND pn.nspname = 'projects_config' AND p.proname = 'reject_immutable_record_mutation'
                AND p.proowner = n.nspowner
                AND btrim(pg_catalog.regexp_replace(p.prosrc, '\s+', ' ', 'g'), ' ;') =
                    'BEGIN RAISE EXCEPTION ''records in %.% are immutable'', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = ''55000''; END'
            )
            """, connection, transaction))
        {
            if (await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The model provider identity guard differs from the reviewed plan.");
        }

        await VerifyRuntimeGrantInventoryAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        await VerifyProviderGuardsAsync(connection, transaction, plan.Role, cancellationToken).ConfigureAwait(false);
    }

        internal static async Task VerifyProviderGuardsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string? role,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("""
                WITH RECURSIVE owner AS (
                    SELECT oid, nspowner FROM pg_catalog.pg_namespace WHERE nspname = 'projects_config'
                ), relations AS (
                    SELECT oid, relname, relowner FROM pg_catalog.pg_class
                    WHERE relnamespace = (SELECT oid FROM owner)
                      AND relname IN ('model_providers', 'model_provider_revisions')
                ), reachable_roles(oid) AS (
                    SELECT oid FROM pg_catalog.pg_roles WHERE rolname = coalesce(@role, current_user)
                    UNION
                    SELECT membership.roleid FROM pg_catalog.pg_auth_members membership
                    JOIN reachable_roles r ON r.oid = membership.member
                    WHERE membership.set_option
                ), expected(relation, trigger_name, trigger_type, function_name, deferred) AS (
                    VALUES
                        ('model_providers', 'model_providers_write_marker', 23, 'mark_model_provider_write', false),
                        ('model_providers', 'model_providers_write_pair', 21, 'check_model_provider_pair', true),
                        ('model_providers', 'model_providers_reference_guard', 7, 'guard_model_provider_reference_cross_table', false),
                        ('model_providers', 'model_providers_id_immutable', 19, 'reject_immutable_record_mutation', false),
                        ('model_provider_revisions', 'model_provider_revisions_write_marker', 7, 'mark_model_provider_write', false),
                        ('model_provider_revisions', 'model_provider_revisions_write_pair', 5, 'check_model_provider_pair', true),
                        ('model_provider_revisions', 'model_provider_revisions_immutable', 27, 'reject_immutable_record_mutation', false),
                        ('model_provider_revisions', 'model_provider_revisions_no_truncate', 34, 'reject_immutable_record_mutation', false)
                )
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = coalesce(@role, current_user)
                               AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls)
                   AND NOT pg_catalog.pg_has_role(coalesce(@role, current_user),
                       pg_catalog.pg_get_userbyid((SELECT nspowner FROM owner)), 'MEMBER')
                   AND NOT pg_catalog.has_parameter_privilege(coalesce(@role, current_user), 'session_replication_role', 'SET')
                   AND NOT pg_catalog.has_schema_privilege(coalesce(@role, current_user), 'projects_config', 'CREATE')
                   AND (SELECT pg_catalog.pg_get_userbyid(nspowner) FROM owner) = @owner_name
                   AND coalesce((SELECT array_agg(c.relname::text ORDER BY c.relname)
                       FROM pg_catalog.pg_class c WHERE c.relnamespace = (SELECT oid FROM owner)
                         AND c.relkind IN ('r', 'p', 'f')), ARRAY[]::text[]) = @tables
                   AND coalesce((SELECT array_agg(c.relname::text ORDER BY c.relname)
                       FROM pg_catalog.pg_class c WHERE c.relnamespace = (SELECT oid FROM owner)
                         AND c.relkind = 'S'), ARRAY[]::text[]) = @sequences
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_class c WHERE c.relnamespace = (SELECT oid FROM owner)
                         AND (c.relkind IN ('v', 'm') OR c.relowner <> (SELECT nspowner FROM owner)))
                   AND (SELECT count(*) FROM pg_catalog.pg_proc p
                       WHERE p.pronamespace = (SELECT oid FROM owner) AND p.prokind IN ('f', 'p'))
                       = cardinality(@functions)
                   AND NOT EXISTS (
                       SELECT 1 FROM unnest(@functions, @executables) AS expected_function(signature, executable)
                       WHERE NOT EXISTS (
                           SELECT 1 FROM pg_catalog.pg_proc p
                           WHERE p.pronamespace = (SELECT oid FROM owner)
                             AND format('%I(%s)', p.proname, pg_catalog.oidvectortypes(p.proargtypes)) = expected_function.signature
                             AND p.proowner = (SELECT nspowner FROM owner)
                             AND pg_catalog.has_function_privilege(coalesce(@role, current_user), p.oid, 'EXECUTE') =
                                 expected_function.executable
                             AND NOT EXISTS (
                                 SELECT 1 FROM pg_catalog.aclexplode(
                                     coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                                 WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')))
                   AND NOT EXISTS (
                       SELECT 1 FROM relations c WHERE c.relowner <> (SELECT nspowner FROM owner))
                   AND (SELECT count(*) FROM relations) = 2
                   AND NOT EXISTS (
                       SELECT 1 FROM relations c
                       JOIN pg_catalog.pg_class relation ON relation.oid = c.oid
                       CROSS JOIN LATERAL pg_catalog.aclexplode(
                           coalesce(relation.relacl, pg_catalog.acldefault('r', relation.relowner))) acl
                       WHERE acl.grantee = 0)
                   AND NOT EXISTS (
                       SELECT 1 FROM relations c
                       JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                       CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) acl
                       WHERE acl.grantee = 0)
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_class c
                       CROSS JOIN LATERAL pg_catalog.aclexplode(
                           coalesce(c.relacl, pg_catalog.acldefault('r', c.relowner))) acl
                       WHERE c.relnamespace = (SELECT oid FROM owner)
                         AND c.relname IN ('tenant_memberships', 'project_role_assignments', 'authority_audit')
                         AND acl.grantee = 0 AND acl.privilege_type <> 'SELECT')
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_class c
                       JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                       CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) acl
                       WHERE c.relnamespace = (SELECT oid FROM owner)
                         AND c.relname IN ('tenant_memberships', 'project_role_assignments', 'authority_audit')
                         AND acl.grantee = 0 AND acl.privilege_type <> 'SELECT')
                   AND NOT EXISTS (
                       SELECT 1 FROM expected e
                       WHERE NOT EXISTS (
                           SELECT 1 FROM pg_catalog.pg_trigger t
                           JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
                           JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                           JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
                           JOIN pg_catalog.pg_namespace pn ON pn.oid = p.pronamespace
                           WHERE n.nspname = 'projects_config' AND c.relname = e.relation
                             AND t.tgname = e.trigger_name AND t.tgtype = e.trigger_type
                             AND t.tgenabled = 'O' AND NOT t.tgisinternal AND t.tgnargs = 0
                             AND ((e.trigger_name = 'model_providers_id_immutable'
                                   AND t.tgattr = (SELECT a.attnum::text::int2vector
                                       FROM pg_catalog.pg_attribute a
                                       WHERE a.attrelid = c.oid AND a.attname = 'provider_id')
                                   AND substring(pg_catalog.pg_get_triggerdef(t.oid)
                                       from ' WHEN (.*) EXECUTE FUNCTION ') =
                                       '(((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text))')
                                  OR (e.trigger_name <> 'model_providers_id_immutable'
                                      AND t.tgattr = ''::int2vector AND t.tgqual IS NULL))
                             AND t.tgdeferrable = e.deferred AND t.tginitdeferred = e.deferred
                             AND pn.nspname = 'projects_config' AND p.proname = e.function_name
                             AND p.proowner = (SELECT nspowner FROM owner)))
                   AND (SELECT count(*) FROM pg_catalog.pg_trigger
                        WHERE tgrelid = (SELECT oid FROM relations WHERE relname = 'model_providers') AND NOT tgisinternal) = 4
                   AND (SELECT count(*) FROM pg_catalog.pg_trigger
                        WHERE tgrelid = (SELECT oid FROM relations WHERE relname = 'model_provider_revisions') AND NOT tgisinternal) = 4
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_proc p
                       WHERE p.pronamespace = (SELECT oid FROM owner)
                         AND p.proname IN ('mark_model_provider_write', 'check_model_provider_pair',
                                           'guard_model_provider_reference_cross_table', 'reject_immutable_record_mutation')
                         AND p.pronargs = 0
                         AND (p.proowner <> (SELECT nspowner FROM owner) OR p.prosecdef
                              OR (p.proname <> 'reject_immutable_record_mutation'
                                  AND p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[])
                              OR pg_catalog.has_function_privilege(coalesce(@role, current_user), p.oid, 'EXECUTE')
                              OR EXISTS (
                                  SELECT 1 FROM pg_catalog.aclexplode(
                                      coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                                  WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')))
                   AND NOT pg_catalog.has_table_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_providers'), 'TRIGGER')
                   AND NOT pg_catalog.has_table_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_providers'), 'REFERENCES')
                   AND NOT pg_catalog.has_any_column_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_providers'), 'REFERENCES')
                   AND NOT pg_catalog.has_table_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_provider_revisions'), 'TRIGGER')
                   AND NOT pg_catalog.has_table_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_provider_revisions'), 'REFERENCES')
                   AND NOT pg_catalog.has_any_column_privilege(coalesce(@role, current_user), (SELECT oid FROM relations WHERE relname = 'model_provider_revisions'), 'REFERENCES')
                   AND NOT EXISTS (
                       SELECT 1 FROM reachable_roles reachable
                       JOIN pg_catalog.pg_roles r ON r.oid = reachable.oid
                       WHERE r.rolsuper OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
                          OR pg_catalog.pg_has_role(r.oid, (SELECT nspowner FROM owner), 'MEMBER')
                          OR pg_catalog.has_schema_privilege(r.oid, (SELECT oid FROM owner), 'CREATE')
                          OR pg_catalog.has_parameter_privilege(r.oid, 'session_replication_role', 'SET')
                          OR pg_catalog.has_table_privilege(r.oid, (SELECT oid FROM relations WHERE relname = 'model_providers'),
                                                            'DELETE, TRUNCATE, REFERENCES, TRIGGER')
                          OR pg_catalog.has_any_column_privilege(r.oid, (SELECT oid FROM relations WHERE relname = 'model_providers'), 'REFERENCES')
                          OR pg_catalog.has_table_privilege(r.oid, (SELECT oid FROM relations WHERE relname = 'model_provider_revisions'),
                                                            'UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                          OR pg_catalog.has_any_column_privilege(r.oid, (SELECT oid FROM relations WHERE relname = 'model_provider_revisions'),
                                                                 'UPDATE, REFERENCES')
                          OR EXISTS (
                              SELECT 1 FROM pg_catalog.pg_proc p
                              WHERE p.pronamespace = (SELECT oid FROM owner) AND p.pronargs = 0
                                AND p.proname IN ('mark_model_provider_write', 'check_model_provider_pair',
                                                  'guard_model_provider_reference_cross_table', 'reject_immutable_record_mutation')
                                AND pg_catalog.has_function_privilege(r.oid, p.oid, 'EXECUTE')))
                   AND NOT EXISTS (
                       SELECT 1 FROM reachable_roles reachable
                       JOIN pg_catalog.pg_class c ON c.relnamespace = (SELECT oid FROM owner)
                       CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privilege
                       WHERE c.relkind IN ('r', 'p', 'f')
                         AND pg_catalog.has_table_privilege(reachable.oid, c.oid, privilege)
                         AND NOT EXISTS (
                             SELECT 1 FROM jsonb_array_elements(@allowed_tables::jsonb) planned
                             WHERE planned->>'name' = c.relname AND planned->'privileges' ? privilege))
                   AND NOT EXISTS (
                       SELECT 1 FROM reachable_roles reachable
                       JOIN pg_catalog.pg_class c ON c.relnamespace = (SELECT oid FROM owner)
                       JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                       CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','REFERENCES']) privilege
                       WHERE c.relkind IN ('r', 'p', 'f')
                         AND pg_catalog.has_column_privilege(reachable.oid, c.oid, a.attnum, privilege)
                         AND NOT EXISTS (
                             SELECT 1 FROM jsonb_array_elements(@allowed_tables::jsonb) planned
                             WHERE planned->>'name' = c.relname
                               AND (planned->'privileges' ? privilege OR
                                   (privilege = 'UPDATE' AND planned->'updateColumns' ? a.attname))))
                   AND NOT EXISTS (
                       SELECT 1 FROM reachable_roles reachable
                       JOIN pg_catalog.pg_proc p ON p.pronamespace = (SELECT oid FROM owner)
                       WHERE pg_catalog.has_function_privilege(reachable.oid, p.oid, 'EXECUTE')
                         AND NOT pg_catalog.has_function_privilege(coalesce(@role, current_user), p.oid, 'EXECUTE'))
                   AND NOT EXISTS (
                       SELECT 1 FROM reachable_roles reachable
                       JOIN pg_catalog.pg_class c ON c.relnamespace = (SELECT oid FROM owner) AND c.relkind = 'S'
                       CROSS JOIN unnest(ARRAY['USAGE','SELECT','UPDATE']) privilege
                       WHERE pg_catalog.has_sequence_privilege(reachable.oid, c.oid, privilege)
                         AND NOT pg_catalog.has_sequence_privilege(coalesce(@role, current_user), c.oid, privilege))
                """, connection, transaction);
            command.Parameters.Add("role", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)role ?? DBNull.Value;
            var plan = Plan.Value;
            command.Parameters.AddWithValue("owner_name", MigrationOwner);
            command.Parameters.AddWithValue("tables",
                plan.Tables.Select(item => item.Name).Concat(plan.OwnerOnlyTables).Order(StringComparer.Ordinal).ToArray());
            command.Parameters.AddWithValue("sequences", plan.OwnerOnlySequences.Order(StringComparer.Ordinal).ToArray());
            command.Parameters.AddWithValue("functions", plan.Functions.Select(item => item.Signature).ToArray());
            command.Parameters.AddWithValue("executables", plan.Functions.Select(item => item.Execute).ToArray());
            command.Parameters.AddWithValue("allowed_tables", JsonSerializer.Serialize(plan.Tables.Select(item => new
            {
                name = item.Name,
                privileges = item.Privileges,
                updateColumns = item.UpdateColumns,
            })));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The model provider guards or runtime privileges differ from the reviewed plan.");

            await using var sources = new NpgsqlCommand("""
                SELECT p.proname, p.prosrc FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'projects_config' AND p.pronargs = 0
                  AND p.proname IN ('mark_model_provider_write', 'check_model_provider_pair',
                                    'guard_model_provider_reference_cross_table', 'reject_immutable_record_mutation')
                """, connection, transaction);
            await using var reader = await sources.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var checkedSources = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var expected = reader.GetString(0) switch
                {
                    "mark_model_provider_write" => "251d3b422db7283268c1264b4743de38b0af2454e34fc23330e1a0d9eba11c18",
                    "check_model_provider_pair" => "04c257420b1b56dec4436415bf3a063b4482936ce841630f32bdbee8d87d0964",
                    "guard_model_provider_reference_cross_table" => "d83b113d3eacaddfa44b872440903b00f366179822b7fc915e3c401ad4b46447",
                    "reject_immutable_record_mutation" => "c6c7bd8d7525928ec038d60c6e986bd8148e9c5bf4d59668a712e832bd549175",
                    _ => "",
                };
                var normalized = NormalizeGuardSource(reader.GetString(1));
                var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The model provider guard function differs from the reviewed plan.");
                checkedSources++;
            }
            if (checkedSources != 4)
                throw new InvalidOperationException("The model provider guard functions are missing.");
        }

    private static string NormalizeGuardSource(string source)
    {
        var result = new StringBuilder();
        var quote = '\0';
        var whitespace = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (quote != '\0')
            {
                if (c == '\\') return source;
                result.Append(c);
                if (c == quote)
                {
                    if (i + 1 < source.Length && source[i + 1] == quote)
                        result.Append(source[++i]);
                    else
                        quote = '\0';
                }
            }
            else if (char.IsWhiteSpace(c))
                whitespace = result.Length > 0;
            else
            {
                if (c == '$') return source;
                if (whitespace) result.Append(' ');
                whitespace = false;
                result.Append(c);
                if (c is '\'' or '"') quote = c;
            }
        }
        return result.ToString().TrimEnd(';');
    }

    private static async Task VerifyRuntimeGrantInventoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServicePlan plan,
        CancellationToken cancellationToken)
    {
        // Effective privilege checks catch inherited or pre-existing rights that exceed the reviewed plan.
        await using (var schema = new NpgsqlCommand("""
            SELECT has_schema_privilege(@role, @schema, 'USAGE')
               AND NOT has_schema_privilege(@role, @schema, 'CREATE')
            """, connection, transaction))
        {
            schema.Parameters.AddWithValue("role", plan.Role);
            schema.Parameters.AddWithValue("schema", plan.Schema);
            if (await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The Projects & Config runtime schema grants differ from the reviewed plan.");
        }

        foreach (var table in plan.Tables)
        {
            var expected = table.Privileges.ToHashSet(StringComparer.Ordinal);
            foreach (var privilege in TablePrivileges)
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_table_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                    """,
                    plan.Role, plan.Schema, table.Name, privilege,
                    expected.Contains(privilege), cancellationToken).ConfigureAwait(false);

            var columns = await ReadColumnsAsync(connection, transaction, plan.Schema, table.Name, cancellationToken)
                .ConfigureAwait(false);
            var updateColumns = table.UpdateColumns.ToHashSet(StringComparer.Ordinal);
            foreach (var column in columns)
                foreach (var privilege in ColumnPrivileges)
                {
                    var expectedColumn = expected.Contains(privilege) ||
                        (privilege == "UPDATE" && updateColumns.Contains(column));
                    await AssertPrivilegeAsync(connection, transaction,
                        """
                        SELECT pg_catalog.has_column_privilege(@role, relation.oid, @column, @privilege)
                        FROM pg_catalog.pg_class AS relation
                        JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                        WHERE namespace.nspname = @schema AND relation.relname = @relation
                        """,
                        plan.Role, plan.Schema, table.Name, privilege,
                        expectedColumn, cancellationToken, column).ConfigureAwait(false);
                }
        }

        foreach (var table in plan.OwnerOnlyTables)
            foreach (var privilege in TablePrivileges)
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_table_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                    """,
                    plan.Role, plan.Schema, table, privilege, false, cancellationToken)
                    .ConfigureAwait(false);

        await using (var columnAcl = new NpgsqlCommand("""
            SELECT format('%I.%I %s to %s', relation.relname, attribute.attname,
                acl.privilege_type,
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     ELSE coalesce(pg_catalog.quote_ident(grantee.rolname), acl.grantee::text) END)
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS acl
            LEFT JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = acl.grantee
            WHERE namespace.nspname = @schema
              AND relation.relname = ANY(@tables)
              AND attribute.attnum > 0 AND NOT attribute.attisdropped
              AND acl.grantee <> relation.relowner
            ORDER BY relation.relname, attribute.attname, acl.grantee, acl.privilege_type
            LIMIT 1
            """, connection, transaction))
        {
            columnAcl.Parameters.AddWithValue("schema", plan.Schema);
            columnAcl.Parameters.AddWithValue("tables", plan.OwnerOnlyTables);
            if (await columnAcl.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string unexpected)
                throw new InvalidOperationException(
                    $"The owner-only Projects & Config receipt has an unexpected column privilege ({unexpected}); no grants were committed.");
        }

        foreach (var sequence in plan.OwnerOnlySequences)
            foreach (var privilege in new[] { "USAGE", "SELECT", "UPDATE" })
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_sequence_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                      AND relation.relkind = 'S'
                    """,
                    plan.Role, plan.Schema, sequence, privilege, false, cancellationToken)
                    .ConfigureAwait(false);

        foreach (var function in plan.Functions)
        {
            var signature = $"{Quote(plan.Schema)}.{function.Signature}";
            await AssertPrivilegeAsync(connection, transaction,
                """
                SELECT pg_catalog.has_function_privilege(@role, routine.oid, 'EXECUTE')
                FROM pg_catalog.pg_proc AS routine
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
                WHERE namespace.nspname = @schema
                  AND format('%I(%s)', routine.proname,
                      pg_catalog.oidvectortypes(routine.proargtypes)) = @relation
                """,
                plan.Role, plan.Schema, function.Signature, null, function.Execute, cancellationToken)
                .ConfigureAwait(false);
            await using var publicGrant = new NpgsqlCommand("""
                SELECT NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_proc AS p
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = p.pronamespace
                    CROSS JOIN LATERAL pg_catalog.aclexplode(
                        coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) AS acl
                    WHERE namespace.nspname = @schema
                      AND format('%I(%s)', p.proname,
                          pg_catalog.oidvectortypes(p.proargtypes)) = @signature
                      AND acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'
                )
                """, connection, transaction);
            publicGrant.Parameters.AddWithValue("schema", plan.Schema);
            publicGrant.Parameters.AddWithValue("signature", function.Signature);
            if (await publicGrant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException(
                    $"PUBLIC can execute the Projects & Config function {function.Signature}; no grants were committed.");
        }
    }

    private static async Task AssertPrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string role,
        string schema,
        string relation,
        string? privilege,
        bool expected,
        CancellationToken cancellationToken,
        string? column = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("relation", relation);
        if (privilege is not null) command.Parameters.AddWithValue("privilege", privilege);
        if (column is not null) command.Parameters.AddWithValue("column", column);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not bool actual ||
            actual != expected)
            throw new InvalidOperationException(
                $"The Projects & Config runtime grants differ from the reviewed plan for {schema}.{relation} {privilege ?? column ?? "EXECUTE"}; no grants were committed.");
    }

    private static async Task<string[]> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname::text
            FROM pg_catalog.pg_attribute AS a
            JOIN pg_catalog.pg_class AS c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attname
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(reader.GetString(0));
        return [.. columns];
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

    private static ServicePlan LoadPlan()
    {
        using var stream = typeof(ProjectsConfigRuntimeGrantPlan).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The reviewed PostgreSQL grant plan is missing from this build.");
        using var document = JsonDocument.Parse(stream);
        var services = document.RootElement.GetProperty("services").EnumerateArray();
        var service = services.SingleOrDefault(item =>
            item.GetProperty("service").GetString() == "projects-config");
        if (service.ValueKind == JsonValueKind.Undefined ||
            service.GetProperty("schema").GetString() != "projects_config" ||
            service.GetProperty("role").GetString() != "agentweaver-id-projects-config")
            throw new InvalidOperationException("The reviewed PostgreSQL grant plan has no approved Projects & Config entry.");

        var providerHead = service.GetProperty("tables").EnumerateArray().Single(table =>
            table.GetProperty("table").GetString() == "model_providers");
        var identityGuards = providerHead.GetProperty("mutationDenialGuards").EnumerateArray()
            .Where(guard => guard.GetProperty("trigger").GetString() == "model_providers_id_immutable").ToArray();
        if (identityGuards.Length != 1 ||
            RequiredString(identityGuards[0], "function") != "reject_immutable_record_mutation()" ||
            identityGuards[0].GetProperty("triggerType").GetInt32() != 19 ||
            RequiredString(identityGuards[0], "message") != "records in %.% are immutable" ||
            RequiredString(identityGuards[0], "sqlState") != "55000" ||
            RequiredString(identityGuards[0], "column") != "provider_id" ||
            RequiredString(identityGuards[0], "condition") != "((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text)")
            throw new InvalidOperationException("The reviewed PostgreSQL plan requires the exact model provider identity guard.");

        // Plan values become SQL identifiers or function signatures, so validate them before building grant commands.
        var tables = service.GetProperty("tables").EnumerateArray().Select(table => new TablePlan(
            RequiredString(table, "table"),
            ReadStrings(table.GetProperty("privileges")),
            table.TryGetProperty("updateColumns", out var columns) ? ReadStrings(columns) : []))
            .ToArray();
        var ownerOnlyTables = ReadStrings(service.GetProperty("ownerOnlyTables"));
        var ownerOnlySequences = ReadStrings(service.GetProperty("ownerOnlySequences"));
        var functions = service.GetProperty("functions").EnumerateArray().Select(function =>
            new FunctionPlan(RequiredString(function, "signature"), function.GetProperty("execute").GetBoolean()))
            .ToArray();
        var allTables = tables.Select(table => table.Name).Concat(ownerOnlyTables).ToArray();
        if (allTables.Distinct(StringComparer.Ordinal).Count() != allTables.Length ||
            tables.Any(table => table.Privileges.Any(privilege => !TablePrivileges.Contains(privilege, StringComparer.Ordinal)) ||
                table.UpdateColumns.Any(column => !IdentifierPattern().IsMatch(column))) ||
            allTables.Any(table => !IdentifierPattern().IsMatch(table)) ||
            ownerOnlySequences.Any(sequence => !IdentifierPattern().IsMatch(sequence)) ||
            functions.Any(function => !FunctionSignaturePattern().IsMatch(function.Signature)))
            throw new InvalidOperationException("The reviewed Projects & Config grant plan contains an invalid object or privilege.");

        return new ServicePlan("projects_config", "agentweaver-id-projects-config", tables,
            ownerOnlyTables, ownerOnlySequences, functions);
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The reviewed Projects & Config grant plan is missing {property}.");

    private static string[] ReadStrings(JsonElement element) =>
        element.EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidOperationException("The reviewed Projects & Config grant plan contains a null value."))
            .ToArray();

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_]*\\([a-z0-9_ ,\\[\\]]*\\)$")]
    private static partial Regex FunctionSignaturePattern();

    private sealed record TablePlan(string Name, string[] Privileges, string[] UpdateColumns);
    private sealed record FunctionPlan(string Signature, bool Execute);
    private sealed record ServicePlan(
        string Schema,
        string Role,
        TablePlan[] Tables,
        string[] OwnerOnlyTables,
        string[] OwnerOnlySequences,
        FunctionPlan[] Functions);
}
