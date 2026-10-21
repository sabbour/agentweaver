using System.Text.Json.Serialization;

namespace Agentweaver.P1PostgresInitializer;

/// <summary>Builds exact database readbacks and enforces the initializer's ownership and grant invariants.</summary>
internal static class InitializerChecks
{
    // Captures the pre-existing database ACL and protected objects so every committed stage can prove its delta.
    internal const string ExistingBoundaryQuery = """
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
                        coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner)),
                        p.prorettype, p.prokind, p.prolang, p.prosecdef, p.pronargs) ORDER BY p.oid)
                        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                        WHERE n.nspname <> ALL(@schemas)))::text)::text FROM database
        """;

    internal const string PreflightQuery = """
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
              WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                AND a.grantee = 0
                AND NOT (a.privilege_type = 'EXECUTE' AND NOT a.is_grantable
                  AND p.prokind = 'f' AND p.pronargs = 0 AND NOT p.prosecdef
                  AND p.prorettype = 'pg_catalog.trigger'::pg_catalog.regtype
                  AND EXISTS (SELECT 1 FROM pg_catalog.pg_language l
                    WHERE l.oid = p.prolang AND l.lanname = 'plpgsql'))),
          'incompatibleDefaults', EXISTS (
            SELECT 1 FROM pg_catalog.pg_default_acl defaults,
              LATERAL pg_catalog.aclexplode(defaults.defaclacl) a
            WHERE defaults.defaclrole = r.oid AND defaults.defaclnamespace = 0
              AND a.grantee <> r.oid AND NOT (defaults.defaclobjtype = 'f'
                AND a.grantee = 0 AND a.privilege_type = 'EXECUTE' AND NOT a.is_grantable)))
        ::text FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_roles r
        WHERE d.datname = 'agentweaver' AND r.rolname = current_user
        """;

    internal const string AadPrincipalFunction = "pg_catalog.pgaadauth_list_principals";

    /// <summary>Rejects unresolved or repeated principal IDs before the initializer requests credentials.</summary>
    internal static void AssertPrincipalObjectIds(ServicePlan[] services)
    {
        if (services.Length != 5 ||
            services.Any(service => !Guid.TryParseExact(service.PrincipalObjectId, "D", out _)) ||
            services.Select(service => service.PrincipalObjectId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != services.Length)
            throw new InvalidOperationException(
                "The five runtime principal object IDs must be distinct Entra GUIDs from the current target deployment.");
    }

    /// <summary>Builds a readback of planned runtime principals and their membership flags.</summary>
    internal static string PrincipalReadbackQuery(string principalFunction = AadPrincipalFunction) => $"""
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
        LEFT JOIN {principalFunction}(false)
          AS p(role_name, principal_type, object_id, tenant_id, is_mfa, is_admin)
          ON p.role_name::text = r.rolname
        WHERE r.rolname = ANY(@roles)
        """;

    /// <summary>Quotes a database identifier by escaping embedded quote characters.</summary>
    internal static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Returns the one permitted runtime-role flag change.</summary>
    // Only INHERIT is changed: the operator may manage it through CREATEROLE ADMIN OPTION. Reserved
    // flags (SUPERUSER, CREATEDB, REPLICATION, BYPASSRLS) need matching privileges even when set to
    // false, so they are verified by readback before COMMIT instead of being rewritten.
    internal static string AlterRoleSql(string role) => $"ALTER ROLE {Quote(role)} NOINHERIT";

    /// <summary>Returns the operator membership SQL while leaving the original administrator grant intact.</summary>
    // Preserve the existing ADMIN grant; PostgreSQL 16 rejects granting it back to the original grantor.
    internal static string GrantOperatorMembershipSql(string role, string operatorName) =>
        $"GRANT {Quote(role)} TO {Quote(operatorName)} WITH INHERIT FALSE, SET FALSE";

    /// <summary>Removes only a temporary membership row attributed to the current operator.</summary>
    // Remove only the temporary grant attributed to the current operator, never the original grantor's row.
    internal static string RevokeOperatorMembershipSql(string role, string operatorName) =>
        $"REVOKE {Quote(role)} FROM {Quote(operatorName)} GRANTED BY {Quote(operatorName)}";

    internal const string RoleCreationStage = "role_creation";
    internal const string RoleFlagChangeStage = "role_flag_change";
    internal const string RoleMembershipChangeStage = "role_membership_change";
    internal const string RoleMembershipCleanupStage = "role_membership_cleanup";
    internal const string RoleReadbackStage = "role_readback";
    internal const string RoleCommitStage = "role_commit";

    /// <summary>Builds the ordered role-flag and membership statements using the observed grantor identity.</summary>
    internal static (string Stage, string Sql)[] RolePostCreateStatements(string role, string operatorName,
        uint initialGrantorOid, uint operatorOid)
    {
        if (initialGrantorOid == 0 || operatorOid == 0)
            throw new InvalidOperationException("The runtime-role membership grantor identity is unavailable.");
        var statements = new List<(string Stage, string Sql)>
        {
            (RoleFlagChangeStage, AlterRoleSql(role)),
            (RoleMembershipChangeStage, GrantOperatorMembershipSql(role, operatorName)),
        };
        if (initialGrantorOid != operatorOid)
            statements.Add((RoleMembershipCleanupStage, RevokeOperatorMembershipSql(role, operatorName)));
        return [.. statements];
    }

    /// <summary>Requires the exact planned row-lock protection set and owner-only denial functions.</summary>
    internal static void AssertMutationDenialPlan(ServicePlan[] services)
    {
        var protectedTables = services.SelectMany(service =>
            service.Tables.Where(table => table.LockColumn is not null ||
                table.MutationDenialRequired || table.MutationDenialGuard is not null)
                .Select(table => (Service: service, Table: table))).ToArray();
        if (services.Length != 5 || protectedTables.Length != 8)
            throw new InvalidOperationException("The exact eight protected row-lock columns are required.");

        foreach (var (service, table) in protectedTables)
        {
            var guard = table.MutationDenialGuard;
            var matchingFunctions = guard is null
                ? []
                : service.Functions.Where(function => function.Signature == guard.Function).ToArray();
            if (!table.MutationDenialRequired || string.IsNullOrWhiteSpace(table.LockColumn) ||
                table.Privileges.Contains("UPDATE") || guard is null ||
                string.IsNullOrWhiteSpace(guard.Trigger) || string.IsNullOrWhiteSpace(guard.Function) ||
                guard.TriggerType is not (19 or 27) || string.IsNullOrWhiteSpace(guard.Message) ||
                (guard.SqlState is not null && !System.Text.RegularExpressions.Regex.IsMatch(guard.SqlState, "^[0-9A-Z]{5}$")) ||
                matchingFunctions.Length != 1 || matchingFunctions[0].Execute)
                throw new InvalidOperationException(
                    $"The {service.Service} {table.Table} one-column lock grant has no exact non-executable mutation guard.");
        }
    }

    /// <summary>Restricts the owner-only relation and sequence inventory to the reviewed Projects objects.</summary>
    internal static void AssertOwnerOnlyObjectPlan(ServicePlan[] services)
    {
        var projects = services.SingleOrDefault(service => service.Schema == "projects_config");
        if (services.Length != 5 || projects is null ||
            !projects.OwnerOnlyTables.SequenceEqual(["project_setup_operations"], StringComparer.Ordinal) ||
            !projects.OwnerOnlySequences.SequenceEqual(
                ["platform_runtime_revisions_revision_seq"], StringComparer.Ordinal) ||
            services.Where(service => service.Schema != "projects_config")
                .Any(service => service.OwnerOnlyTables.Length != 0 || service.OwnerOnlySequences.Length != 0) ||
            projects.OwnerOnlyTables.Intersect(projects.Tables.Select(table => table.Table), StringComparer.Ordinal).Any() ||
            projects.OwnerOnlyTables.Distinct(StringComparer.Ordinal).Count() != projects.OwnerOnlyTables.Length ||
            projects.OwnerOnlySequences.Distinct(StringComparer.Ordinal).Count() != projects.OwnerOnlySequences.Length)
            throw new InvalidOperationException("The exact owner-only Projects relations are required and may not be runtime-granted.");

        var setupGuard = projects.Functions.SingleOrDefault(function =>
            function.Signature == "reject_project_setup_operation_mutation()");
        if (setupGuard is null || setupGuard.Execute)
            throw new InvalidOperationException("The Projects setup-operation guard must remain owner-only.");
    }

    /// <summary>Requires applied migration IDs to exactly match the source inventory, in order.</summary>
    internal static void AssertExactMigrationHistory(string[] expected, string[] actual, string service)
    {
        if (expected.Length == 0 || actual.Length != expected.Length ||
            expected.Any(string.IsNullOrWhiteSpace) || actual.Any(string.IsNullOrWhiteSpace) ||
            expected.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"The {service} migration history does not match the exact source migration set.");
    }

    /// <summary>
    /// Requires the reviewed plan to equal the migrator's compiled catalog and, when supplied, to end exactly at the
    /// operator's explicit maximum version.
    /// </summary>
    /// <remarks>
    /// Migrators apply every missing catalog version, so this equality is what prevents a newer source from
    /// silently applying versions beyond the approved cutoff.
    /// </remarks>
    internal static void AssertMigrationCatalog(int[]? planned, IReadOnlyList<int> supported, int? maximum, string service)
    {
        if (planned is null || planned.Length == 0 || !planned.SequenceEqual(supported) ||
            planned.Distinct().Count() != planned.Length || !planned.Zip(planned.Skip(1)).All(pair => pair.First < pair.Second))
            throw new InvalidOperationException($"The reviewed {service} migration plan differs from the source migrator catalog.");
        if (maximum is { } value && planned[^1] != value)
            throw new InvalidOperationException(
                $"The {service} source catalog ends at {planned[^1]}, not the approved maximum {value}. Refusing to apply unapproved versions.");
    }

    /// <summary>Reads one schema's owner and explicit ACL; returns no row when the schema is absent.</summary>
    internal const string SchemaAclQuery = """
        SELECT pg_catalog.json_build_object(
            'ownerOid', n.nspowner::bigint,
            'grants', COALESCE((SELECT pg_catalog.json_agg(pg_catalog.json_build_object(
                'grantor', a.grantor::bigint, 'grantee', a.grantee::bigint,
                'privilege', a.privilege_type, 'grantable', a.is_grantable))
                FROM pg_catalog.aclexplode(n.nspacl) a), '[]'::json))::text
        FROM pg_catalog.pg_namespace n WHERE n.nspname = @schema
        """;

    /// <summary>Reads one schema's relations, owners, and the runtime role's effective privileges when the role exists.</summary>
    internal const string RelationStateQuery = """
        SELECT COALESCE(pg_catalog.json_agg(pg_catalog.json_build_object(
            'name', c.relname,
            'kind', c.relkind::text,
            'owner', pg_catalog.pg_get_userbyid(c.relowner),
            'runtimePrivileges', CASE WHEN NOT @has_role THEN NULL
                WHEN c.relkind = 'S' THEN ARRAY(SELECT p FROM unnest(ARRAY['USAGE','SELECT','UPDATE']) p
                    WHERE pg_catalog.has_sequence_privilege(@role, c.oid, p))
                ELSE ARRAY(SELECT p FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) p
                    WHERE pg_catalog.has_table_privilege(@role, c.oid, p)) END)
            ORDER BY c.relname), '[]'::json)::text
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'f', 'S', 'v', 'm')
        """;

    /// <summary>Reads schema owner and ACL by name for the read-only inspection report.</summary>
    internal const string SchemaStateQuery = """
        SELECT pg_catalog.json_build_object(
            'owner', pg_catalog.pg_get_userbyid(n.nspowner),
            'grants', COALESCE((SELECT pg_catalog.json_agg(pg_catalog.json_build_object(
                'grantor', pg_catalog.pg_get_userbyid(a.grantor),
                'grantee', CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                'privilege', a.privilege_type, 'grantable', a.is_grantable))
                FROM pg_catalog.aclexplode(n.nspacl) a), '[]'::json))::text
        FROM pg_catalog.pg_namespace n WHERE n.nspname = @schema
        """;

    /// <summary>Reads the current database ACL by role name for the read-only inspection report.</summary>
    internal const string DatabaseGrantsQuery = """
        SELECT pg_catalog.json_build_object(
            'owner', pg_catalog.pg_get_userbyid(d.datdba),
            'grants', COALESCE((SELECT pg_catalog.json_agg(pg_catalog.json_build_object(
                'grantor', pg_catalog.pg_get_userbyid(a.grantor),
                'grantee', CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                'privilege', a.privilege_type, 'grantable', a.is_grantable))
                FROM pg_catalog.aclexplode(d.datacl) a), '[]'::json))::text
        FROM pg_catalog.pg_database d WHERE d.datname = pg_catalog.current_database()
        """;

    /// <summary>Lists every Entra service principal role so inspection can expose naming drift from the plan.</summary>
    internal const string ServicePrincipalsQuery = """
        SELECT COALESCE(pg_catalog.json_agg(pg_catalog.json_build_object(
            'role', p.role_name::text, 'objectId', p.object_id, 'type', p.principal_type)
            ORDER BY p.role_name::text), '[]'::json)::text
        FROM pg_catalog.pgaadauth_list_principals(false)
          AS p(role_name, principal_type, object_id, tenant_id, is_mfa, is_admin)
        WHERE p.principal_type = 'service'
        """;

    /// <summary>Requires a service schema to be operator-owned with only owner CREATE/USAGE and runtime USAGE.</summary>
    internal static void AssertServiceSchemaAcl(ServicePlan service, SchemaAclState? state, uint operatorOid, uint runtimeOid)
    {
        AclGrant[] expected =
        [
            new(operatorOid, operatorOid, "CREATE", false),
            new(operatorOid, operatorOid, "USAGE", false),
            new(operatorOid, runtimeOid, "USAGE", false),
        ];
        if (state is null || operatorOid == 0 || runtimeOid == 0 || state.OwnerOid != operatorOid ||
            !SameGrants(expected, state.Grants))
            throw new InvalidOperationException(
                $"The {service.Schema} schema is missing or has unexpected ownership or privileges. Refusing incremental continuation.");
    }

    /// <summary>Returns the planned versions missing from live history after proving that history is an exact plan prefix.</summary>
    internal static int[] PendingMigrationVersions(int[] planned, int[] applied, string service)
    {
        var ordered = applied.Order().ToArray();
        if (ordered.Distinct().Count() != ordered.Length || ordered.Length > planned.Length ||
            !planned.Take(ordered.Length).SequenceEqual(ordered))
            throw new InvalidOperationException(
                $"The live {service} migration history is not an exact prefix of the reviewed plan. Refusing to continue.");
        return planned.Skip(ordered.Length).ToArray();
    }

    /// <summary>Requires each runtime role to already hold exactly one non-grantable database CONNECT from the expected grantor.</summary>
    internal static void AssertRuntimeDatabaseConnect(ExistingBoundary boundary, uint[] runtimeOids)
    {
        if (runtimeOids.Length != 5 || runtimeOids.Any(oid => oid == 0) ||
            runtimeOids.Distinct().Count() != runtimeOids.Length ||
            runtimeOids.Any(oid => !boundary.Grants.Where(grant => grant.Grantee == oid)
                .SequenceEqual([new AclGrant(boundary.ConnectGrantorOid, oid, "CONNECT", false)])))
            throw new InvalidOperationException(
                "The runtime roles do not hold exactly their initialized database CONNECT grants. Refusing incremental continuation.");
    }

    /// <summary>Requires every existing service relation to be planned, operator-owned, and within planned runtime authority.</summary>
    /// <remarks>Planned relations may be absent only because their migration is still pending.</remarks>
    internal static void AssertRelationsWithinPlan(ServicePlan service, RelationState[] relations, string owner)
    {
        foreach (var relation in relations)
        {
            var table = service.Tables.SingleOrDefault(item => item.Table == relation.Name);
            string[]? allowed = relation.Kind switch
            {
                "r" or "p" or "f" when table is not null => table.Privileges,
                "r" or "p" or "f" when service.OwnerOnlyTables.Contains(relation.Name) => [],
                "S" when service.OwnerOnlySequences.Contains(relation.Name) => [],
                _ => null,
            };
            if (allowed is null || relation.Owner != owner || relation.RuntimePrivileges is null ||
                relation.RuntimePrivileges.Except(allowed, StringComparer.Ordinal).Any())
                throw new InvalidOperationException(
                    $"The {service.Service} relation {service.Schema}.{relation.Name} is unplanned, foreign-owned, or exceeds planned runtime authority.");
        }
    }

    /// <summary>Accepts only a receipt proving the failed Projects-only prefix and binding it to exact source hashes.</summary>
    internal static void AssertContinuationReceipt(ContinuationReceiptEntry[] entries,
        string sourceSha, string coreMergeSha, string planHash)
    {
        if (entries.Length == 0 || entries.Any(entry =>
                entry.SourceSha != sourceSha || entry.CoreMergeSha != coreMergeSha ||
                entry.PlanHash != planHash || entry.At == default))
            throw new InvalidOperationException("The continuation receipt is not bound to the exact source, Core merge, and plan.");

        var stages = entries.Select(entry => entry.Stage).ToArray();
        var required = new[]
        {
            "source_checked",
            "fresh_target_checked",
            "five_runtime_roles_created_and_exact_mappings_checked",
            "projects-config:schema_created",
            "projects-config:migrated",
            "failed_requires_explicit_reconciliation",
        };
        var previous = -1;
        foreach (var stage in required)
        {
            var matches = Array.FindIndex(stages, previous + 1, value => value == stage);
            if (matches <= previous || stages.Count(value => value == stage) != 1)
                throw new InvalidOperationException("The continuation receipt does not prove the exact Projects-only failed prefix.");
            previous = matches;
        }

        var projectsMigratedIndex = Array.IndexOf(stages, "projects-config:migrated");
        if (stages[^1] != "failed_requires_explicit_reconciliation" ||
            stages.Contains("projects-config:exact_grants_checked_and_committed") ||
            stages.Any(stage => stage.StartsWith("environment:", StringComparison.Ordinal) ||
                stage.StartsWith("knowledge:", StringComparison.Ordinal) ||
                stage.StartsWith("events-and-sessions:", StringComparison.Ordinal) ||
                stage.StartsWith("orchestrator:", StringComparison.Ordinal)) ||
            stages.Skip(projectsMigratedIndex + 1).Any(stage => stage != "failed_requires_explicit_reconciliation"))
            throw new InvalidOperationException("The continuation receipt records work beyond the exact Projects-only prefix.");
    }

    /// <summary>Verifies guard trigger shape, owner, source body, and runtime non-executability before grants.</summary>
    internal static void AssertMutationDenialGuards(ServicePlan service, MutationGuardState[] guards,
        uint ownerOid, uint runtimeOid)
    {
        var protectedTables = service.Tables.Where(table => table.MutationDenialRequired).ToArray();
        if (guards.Length != protectedTables.Length)
            throw new InvalidOperationException($"The {service.Service} row-lock mutation guards are incomplete.");

        foreach (var table in protectedTables)
        {
            var matches = guards.Where(guard => guard.Table == table.Table).ToArray();
            var expected = table.MutationDenialGuard;
            if (matches.Length != 1 || expected is null)
                throw new InvalidOperationException($"The {service.Service} {table.Table} row-lock mutation guard is missing or duplicated.");
            var actual = matches[0];
            var expectedBody = NormalizeGuardSource(
                $"BEGIN RAISE EXCEPTION '{expected.Message}'" +
                (expected.SqlState is null ? string.Empty : $" USING ERRCODE = '{expected.SqlState}'") +
                "; END");
            if (actual.TriggerName != expected.Trigger || actual.FunctionSchema != service.Schema ||
                actual.FunctionName != expected.Function || actual.TriggerType != expected.TriggerType ||
                actual.Enabled != "O" || actual.IsInternal != false || actual.NoColumnFilter != true ||
                actual.NoCondition != true || actual.ArgumentCount != 0 ||
                actual.FunctionOwnerOid != ownerOid || actual.RuntimeCanExecute != false ||
                actual.FunctionSource is null || NormalizeGuardSource(actual.FunctionSource) != expectedBody ||
                ownerOid == 0 || runtimeOid == 0)
                throw new InvalidOperationException(
                    $"The {service.Service} {table.Table} mutation-denial trigger or function changed. The grants are not committed.");
        }
    }

    /// <summary>Rejects pre-existing public authority, incompatible defaults, or an operator unable to grant Connect.</summary>
    internal static void AssertPreflight(PreflightSnapshot state)
    {
        var boundary = state.Boundary;
        if (boundary is null || boundary.DatabaseOid == 0 || boundary.OwnerOid == 0 || state.OperatorOid == 0 ||
            boundary.ConnectGrantorOid == 0 || !state.OperatorCanGrantConnect ||
            boundary.Grants.Any(grant => grant.Grantee == 0 &&
                (grant.Privilege != "CONNECT" || grant.Grantable)) ||
            state.ForeignPublicAuthority || state.IncompatibleDefaults)
            throw new InvalidOperationException(
                "Existing public, inherited, or default privileges are incompatible with the planned runtime limits. No initialization writes are permitted.");
    }

    /// <summary>Allows only the exact planned database Connect grants; all protected state must remain unchanged.</summary>
    internal static void AssertBoundary(ExistingBoundary before, ExistingBoundary after, uint[] connectedRoles)
    {
        var expected = before.Grants.Concat(connectedRoles.Select(role =>
            new AclGrant(before.ConnectGrantorOid, role, "CONNECT", false))).ToArray();
        if (connectedRoles.Any(role => role == 0) || connectedRoles.Distinct().Count() != connectedRoles.Length ||
            before.DatabaseOid != after.DatabaseOid || before.OwnerOid != after.OwnerOid ||
            before.ConnectGrantorOid != after.ConnectGrantorOid || before.ProtectedState != after.ProtectedState ||
            !SameGrants(expected, after.Grants))
            throw new InvalidOperationException(
                "Existing ownership or privileges changed, or the database CONNECT delta differs from the exact planned roles. The current grant stage is not committed.");
    }

    /// <summary>Requires all five runtime identities to have no direct database ACL before continuation.</summary>
    internal static void AssertNoRuntimeDatabaseGrants(ExistingBoundary boundary, uint[] runtimeOids)
    {
        if (runtimeOids.Length != 5 || runtimeOids.Any(oid => oid == 0) ||
            runtimeOids.Distinct().Count() != runtimeOids.Length ||
            boundary.Grants.Any(grant => runtimeOids.Contains(grant.Grantee)))
            throw new InvalidOperationException("A runtime role already has a direct database grant before continuation.");
    }

    /// <summary>Captures each service role's existing administrator grantor after validating its Entra mapping.</summary>
    internal static IReadOnlyDictionary<string, uint> CaptureMembershipGrantors(ServicePlan[] services,
        RuntimePrincipal[] principals, uint operatorOid, string tenantId)
    {
        if (services.Length != 5 || principals.Length != services.Length || operatorOid == 0 ||
            principals.Select(item => item.Oid).Distinct().Count() != services.Length)
            throw new InvalidOperationException("The five runtime principal mappings are not exact.");
        var grantors = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var matches = principals.Where(item => item.Role == service.Role).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("A planned runtime principal is missing or duplicated.");
            var principal = matches[0];
            var membership = principal.Memberships.Length == 1 ? principal.Memberships[0] : null;
            if (principal.Oid == 0 || !Guid.TryParse(principal.ObjectId, out var actualObject) ||
                !Guid.TryParse(service.PrincipalObjectId, out var expectedObject) || actualObject != expectedObject ||
                !Guid.TryParse(principal.TenantId, out var actualTenant) ||
                !Guid.TryParse(tenantId, out var expectedTenant) || actualTenant != expectedTenant ||
                principal.Type != "service" || principal.AdminFlag != 0 || principal.MfaFlag != 0 || !principal.CanLogin ||
                principal.Superuser || principal.CreateDatabase || principal.CreateRole ||
                principal.Replication || principal.BypassRls || membership is null ||
                membership.RoleOid != principal.Oid || membership.MemberOid != operatorOid ||
                membership.GrantorOid == 0 || !membership.Admin)
                throw new InvalidOperationException($"The {service.Service} runtime principal has an unexpected mapping, flag, or membership. Role creation is not committed.");
            grantors.Add(service.Role, membership.GrantorOid);
        }
        return grantors;
    }

    /// <summary>Checks exact role mappings, disabled elevated flags, and operator-only non-inheriting membership.</summary>
    internal static void AssertPrincipals(ServicePlan[] services, RuntimePrincipal[] principals,
        uint operatorOid, string tenantId, IReadOnlyDictionary<string, uint>? expectedGrantorOids = null)
    {
        expectedGrantorOids ??= services.ToDictionary(item => item.Role, _ => operatorOid, StringComparer.Ordinal);
        if (services.Length != 5 || principals.Length != services.Length || operatorOid == 0 ||
            principals.Select(item => item.Oid).Distinct().Count() != services.Length ||
            expectedGrantorOids.Count != services.Length)
            throw new InvalidOperationException("The five runtime principal mappings are not exact.");
        foreach (var service in services)
        {
            var matches = principals.Where(item => item.Role == service.Role).ToArray();
            if (matches.Length != 1 || !expectedGrantorOids.TryGetValue(service.Role, out var grantorOid) ||
                grantorOid == 0)
                throw new InvalidOperationException("A planned runtime principal is missing or duplicated.");
            var principal = matches[0];
            var membership = new RoleMembership(principal.Oid, operatorOid, grantorOid, true, false, false);
            if (principal.Oid == 0 || !Guid.TryParse(principal.ObjectId, out var actualObject) ||
                !Guid.TryParse(service.PrincipalObjectId, out var expectedObject) || actualObject != expectedObject ||
                !Guid.TryParse(principal.TenantId, out var actualTenant) ||
                !Guid.TryParse(tenantId, out var expectedTenant) || actualTenant != expectedTenant ||
                principal.Type != "service" || principal.AdminFlag != 0 || principal.MfaFlag != 0 || !principal.CanLogin ||
                principal.Superuser || principal.CreateDatabase || principal.CreateRole ||
                principal.Replication || principal.Inherit || principal.BypassRls ||
                principal.Memberships.Length != 1 || principal.Memberships[0] != membership)
                throw new InvalidOperationException($"The {service.Service} runtime principal has an unexpected mapping, flag, or membership. Role creation is not committed.");
        }
    }

    /// <summary>Compares every function owner and execute ACL with the exact pre-grant or committed plan.</summary>
    internal static void AssertFunctions(
        ServicePlan service,
        FunctionState[] functions,
        uint ownerOid,
        uint runtimeOid,
        bool beforeRuntimeGrants = false)
    {
        if (ownerOid == 0 || runtimeOid == 0 || functions.Length != service.Functions.Length ||
            functions.Select(item => item.Oid).Distinct().Count() != functions.Length)
            throw new InvalidOperationException("The exact function inventory is missing or duplicated.");
        foreach (var planned in service.Functions)
        {
            if (planned.Execute && (service.Schema != "projects_config" ||
                planned.Signature != "lock_casting_authority(uuid, text, text, text, bigint, text, boolean)"))
                throw new InvalidOperationException("Only the existing casting authority function may receive runtime EXECUTE.");
            var matches = functions.Where(item => item.Signature == planned.Signature).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("A planned function is missing or duplicated.");
            var function = matches[0];
            if (beforeRuntimeGrants)
            {
                var ownerOnly = new AclGrant[] { new(ownerOid, ownerOid, "EXECUTE", false) };
                var ownerAndPublic = new AclGrant[]
                {
                    new(ownerOid, ownerOid, "EXECUTE", false),
                    new(ownerOid, 0, "EXECUTE", false),
                };
                var publicExecute = !planned.Execute &&
                    SameGrants(ownerAndPublic, function.Grants);
                if (function.Oid == 0 || function.OwnerOid != ownerOid ||
                    !(SameGrants(ownerOnly, function.Grants) || publicExecute) ||
                    function.RuntimeCanExecute != publicExecute)
                    throw new InvalidOperationException(
                        $"Unexpected pre-grant owner or EXECUTE privileges on {service.Schema}.{planned.Signature}.");
                continue;
            }

            AclGrant[] expected = planned.Execute
                ? [new(ownerOid, ownerOid, "EXECUTE", false), new(ownerOid, runtimeOid, "EXECUTE", false)]
                : [new(ownerOid, ownerOid, "EXECUTE", false)];
            if (function.Oid == 0 || function.OwnerOid != ownerOid ||
                function.RuntimeCanExecute != planned.Execute || !SameGrants(expected, function.Grants))
                throw new InvalidOperationException($"Unexpected owner or EXECUTE privileges on {service.Schema}.{planned.Signature}.");
        }
    }

    private static bool SameGrants(AclGrant[] expected, AclGrant[] actual) =>
        expected.Length == actual.Length && expected.Distinct().Count() == expected.Length &&
        actual.Distinct().Count() == actual.Length && expected.ToHashSet().SetEquals(actual);

    private static string NormalizeGuardSource(string source) =>
        string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    /// <summary>Runs preflight, mutation, readback, verification, commit, then durable receipt recording in order.</summary>
    internal static async Task RunStageAsync(Func<Task> preflight, Func<Task> mutate,
        Func<Task> verify, Func<Task> commit, Func<Task> record, Func<string, Task>? observe = null)
    {
        await preflight();
        await mutate();
        if (observe is not null) await observe(RoleReadbackStage);
        await verify();
        if (observe is not null) await observe(RoleCommitStage);
        await commit();
        await record();
    }

    /// <summary>Reports failure without implying rollback and attempts to append an explicit-reconciliation receipt.</summary>
    internal static async Task ReportFailureAsync(Exception original, string receiptPath,
        Func<string, Task> record, TextWriter errors, string? sqlState = null)
    {
        await errors.WriteLineAsync(
            $"P1 PostgreSQL initialization stopped: {original.GetType().Name}. Earlier receipts remain in {receiptPath}; do not retry or reset partial state. A failed receipt does not prove rollback.");
        if (sqlState is not null)
            await errors.WriteLineAsync($"PostgreSQL SQLSTATE: {sqlState}");
        try
        {
            await record("failed_requires_explicit_reconciliation");
        }
        catch (Exception receiptError)
        {
            await errors.WriteLineAsync(
                $"The failure receipt could not be written: {receiptError.GetType().Name}. The original initialization failure above still applies; committed state requires explicit reconciliation.");
        }
    }
}

/// <summary>One observed PostgreSQL ACL grant, including its grantor and grantability.</summary>
internal sealed record AclGrant(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint Grantor,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint Grantee,
    string Privilege, bool Grantable);
/// <summary>Database owner, ACL, and protected-object snapshot used to detect unauthorized changes.</summary>
internal sealed record ExistingBoundary(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint DatabaseOid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint OwnerOid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint ConnectGrantorOid,
    AclGrant[] Grants, string ProtectedState);
/// <summary>Read-only checks required before initializer mutations may begin.</summary>
internal sealed record PreflightSnapshot(ExistingBoundary? Boundary,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint OperatorOid,
    bool OperatorCanGrantConnect, bool ForeignPublicAuthority, bool IncompatibleDefaults);
/// <summary>One role membership edge including PostgreSQL's grant, inherit, and set options.</summary>
internal sealed record RoleMembership(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint RoleOid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint MemberOid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint GrantorOid,
    bool Admin, bool Inherit, bool Set);
/// <summary>Entra principal identity, role flags, and memberships read back from PostgreSQL.</summary>
internal sealed record RuntimePrincipal(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint Oid,
    string Role, string? ObjectId, string? Type,
    int? AdminFlag, bool CanLogin, bool Superuser, bool CreateDatabase, bool CreateRole,
    bool Replication, bool Inherit, bool BypassRls, string? TenantId, int? MfaFlag, RoleMembership[] Memberships);
/// <summary>Observed owner and execute grants for one planned database function.</summary>
internal sealed record FunctionState(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint Oid,
    string Signature,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint OwnerOid,
    AclGrant[] Grants, bool RuntimeCanExecute);
/// <summary>Source-generated target and exact per-service role, schema, relation, and function plan.</summary>
/// <summary>Source-bound service grants and exact Core/Events migration versions requiring separate live approval.</summary>
internal sealed record PostgresPlan(
    int FormatVersion, TargetPlan Target, ServicePlan[] Services, int[] CoreVersions, int[] EventsVersions);
/// <summary>Connection and Entra operator identity selected for a PostgreSQL initialization.</summary>
internal sealed record TargetPlan(string TenantId, string PostgresHost, string Database, string OperatorName,
    string OperatorObjectId, string ConnectionHost, int ConnectionPort);
/// <summary>Exact schema, runtime principal, relation, and function contract for one service.</summary>
internal sealed record ServicePlan(string Service, string Schema, string Role, string PrincipalObjectId,
    TablePlan[] Tables, FunctionPlan[] Functions)
{
    public string[] OwnerOnlyTables { get; init; } = [];
    public string[] OwnerOnlySequences { get; init; } = [];
}
/// <summary>Runtime table privileges and any one-column lock update allowed for the service.</summary>
internal sealed record TablePlan(string Table, string[] Privileges, string? LockColumn, bool MutationDenialRequired,
    MutationDenialGuardPlan? MutationDenialGuard);
/// <summary>Expected trigger and owner-only function that denies mutation of a protected row-lock table.</summary>
internal sealed record MutationDenialGuardPlan(string Trigger, string Function, int TriggerType,
    string Message, string? SqlState);
/// <summary>Readback of a mutation guard's catalog state and function source.</summary>
internal sealed record MutationGuardState(string Table, string? TriggerName, string? FunctionSchema,
    string? FunctionName, int? TriggerType, string? Enabled, bool? IsInternal, bool? NoColumnFilter,
    bool? NoCondition, int? ArgumentCount,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint? FunctionOwnerOid,
    bool? RuntimeCanExecute,
    string? FunctionSource);
/// <summary>One live relation in a service schema with its owner and effective runtime privileges.</summary>
internal sealed record RelationState(string Name, string Kind, string Owner, string[]? RuntimePrivileges);
/// <summary>Observed owner and explicit ACL for one service schema.</summary>
internal sealed record SchemaAclState(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint OwnerOid, AclGrant[] Grants);
/// <summary>Function signature and whether its execute privilege is granted to the runtime principal.</summary>
internal sealed record FunctionPlan(string Signature, bool Execute);
/// <summary>Durable stage marker bound to a source SHA, Core merge SHA, and source-plan hash.</summary>
internal sealed record ContinuationReceiptEntry(string Stage, string SourceSha, string CoreMergeSha,
    string PlanHash, DateTimeOffset At);
