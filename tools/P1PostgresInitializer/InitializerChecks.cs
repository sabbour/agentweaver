using System.Text.Json.Serialization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Agentweaver.P1PostgresInitializer;

/// <summary>Builds exact database readbacks and enforces the initializer's ownership and grant invariants.</summary>
internal static class InitializerChecks
{
    internal const string MutationGuardReadbackQuery = """
        SELECT coalesce(jsonb_agg(jsonb_build_object(
          'table', expected.table_name,
          'triggerName', t.tgname,
          'functionSchema', function_namespace.nspname,
          'functionName', CASE WHEN p.oid IS NULL THEN NULL ELSE p.proname || '()' END,
          'triggerType', t.tgtype,
          'enabled', t.tgenabled::text,
          'isInternal', t.tgisinternal,
          'deferrable', t.tgdeferrable,
          'initiallyDeferred', t.tginitdeferred,
          'noColumnFilter', CASE WHEN t.oid IS NULL THEN NULL ELSE t.tgattr = ''::int2vector END,
          'noCondition', CASE WHEN t.oid IS NULL THEN NULL ELSE t.tgqual IS NULL END,
          'columnFilter', CASE WHEN cardinality(t.tgattr::smallint[]) = 1 THEN
            (SELECT a.attname FROM pg_catalog.pg_attribute a
             WHERE a.attrelid = t.tgrelid AND a.attnum = t.tgattr[0]) END,
          'condition', substring(pg_catalog.pg_get_triggerdef(t.oid) from ' WHEN (.*) EXECUTE FUNCTION '),
          'argumentCount', t.tgnargs,
          'functionOwnerOid', p.proowner,
          'runtimeCanExecute', CASE WHEN p.oid IS NULL THEN NULL
            ELSE pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE') END,
          'functionSource', p.prosrc,
          'volatileInvoker', p.provolatile = 'v' AND NOT p.prosecdef,
          'pinnedSearchPath', p.proconfig = ARRAY['search_path=pg_catalog']::text[],
          'defaultSearchPath', p.proconfig IS NULL,
          'typeSevenTriggers', (SELECT coalesce(jsonb_agg(other.tgname::text ORDER BY other.tgname::text COLLATE "C"), '[]'::jsonb)
            FROM pg_catalog.pg_trigger other
            WHERE other.tgrelid = c.oid AND NOT other.tgisinternal AND other.tgtype = 7))
          ORDER BY expected.table_name), '[]'::jsonb)::text
        FROM unnest(@tables, @triggers) AS expected(table_name, trigger_name)
        LEFT JOIN pg_catalog.pg_namespace service_namespace ON service_namespace.nspname = @schema
        LEFT JOIN pg_catalog.pg_class c
          ON c.relnamespace = service_namespace.oid AND c.relname = expected.table_name
        LEFT JOIN pg_catalog.pg_trigger t
          ON t.tgrelid = c.oid AND t.tgname = expected.trigger_name
        LEFT JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
        LEFT JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = p.pronamespace
        """;
    private const string ReservationIdentitySource = """
        BEGIN
            PERFORM pg_advisory_xact_lock(hashtextextended(NEW.target_id, 0));
            IF EXISTS (
                SELECT 1
                FROM environment.platform_policy_apply_reservations existing
                WHERE existing.target_id = NEW.target_id
                  AND existing.source_epoch = NEW.source_epoch
                  AND existing.generation = NEW.generation
                  AND (
                      existing.source_id IS DISTINCT FROM NEW.source_id OR
                      existing.source_revision IS DISTINCT FROM NEW.source_revision OR
                      existing.source_hash IS DISTINCT FROM NEW.source_hash OR
                      existing.intent_hash IS DISTINCT FROM NEW.intent_hash OR
                      existing.provider_request_json IS DISTINCT FROM NEW.provider_request_json
                  )
            ) THEN
                RAISE EXCEPTION 'platform policy apply reservation content conflicts'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string ReservationHoldFenceSource = """
        BEGIN
            IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
            END IF;
            PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
            IF EXISTS (SELECT 1 FROM environment.platform_policy_proof_holds h WHERE h.target_id = NEW.target_id) THEN
                RAISE EXCEPTION 'target has a policy hold'
                    USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_reservation_held';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string HoldReservationFenceSource = """
        BEGIN
            IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
            END IF;
            PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
            IF NEW.reservation_id IS DISTINCT FROM (
                SELECT r.reservation_id
                FROM environment.platform_policy_apply_reservations r
                WHERE r.target_id = NEW.target_id
                ORDER BY r.source_epoch DESC, r.generation DESC, r.created_at DESC, r.reservation_id DESC
                LIMIT 1
            ) THEN
                RAISE EXCEPTION 'policy hold does not match the latest reservation'
                    USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_hold_current_reservation';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string ProofReceiptIdentitySource = """
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM environment.platform_policy_proof_holds hold
                WHERE hold.hold_id = NEW.hold_id
                  AND hold.target_id = NEW.target_id
                  AND hold.reservation_id = NEW.reservation_id
                  AND hold.profile_digest = NEW.profile_digest
                  AND hold.guard_digest = NEW.guard_digest
            ) THEN
                RAISE EXCEPTION 'platform policy proof receipt does not match its hold'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string GovernanceDecisionHeadTable = "project_governance_decision_stream_heads";
    private const string GovernanceDecisionHeadInitializeFunctionSource = """
        BEGIN
            IF NEW.sequence <> 0 OR NEW.fence_generation <> 0
               OR NEW.head_hash <> repeat('0', 64) THEN
                RAISE EXCEPTION 'governance decision stream head must start at genesis'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string GovernanceCheckpointAnchorFunctionSource = """
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM projects_config.project_governance_decision_stream_heads AS head
                WHERE head.scope_type = NEW.scope_type
                  AND head.scope_id = NEW.scope_id
                  AND head.resource_type = NEW.resource_type
                  AND head.resource_id = NEW.resource_id
                  AND head.sequence >= NEW.sequence
                  AND (
                      NEW.sequence = 0 AND head.sequence = 0
                          AND NEW.head_hash = repeat('0', 64)
                      OR NEW.sequence > 0 AND EXISTS (
                          SELECT 1
                          FROM projects_config.project_governance_decisions AS decision
                          WHERE decision.scope_type = NEW.scope_type
                            AND decision.scope_id = NEW.scope_id
                            AND decision.resource_type = NEW.resource_type
                            AND decision.resource_id = NEW.resource_id
                            AND decision.sequence = NEW.sequence
                            AND decision.record_hash = NEW.head_hash
                      )
                  )
            ) THEN
                RAISE EXCEPTION 'governance checkpoint must reference a stream record'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        """;
    private const string GovernanceDecisionHeadAdvanceFunctionSource = """
        BEGIN
            IF NEW.sequence <> OLD.sequence + 1
               OR NEW.fence_generation <> OLD.fence_generation + 1
               OR NEW.head_hash !~ '^[0-9a-f]{64}$'
               OR NOT EXISTS (
                   SELECT 1
                   FROM projects_config.project_governance_decisions AS decision
                   JOIN projects_config.project_governance_decision_outbox AS outbox
                     ON outbox.decision_id = decision.decision_id
                   WHERE decision.scope_type = NEW.scope_type
                     AND decision.scope_id = NEW.scope_id
                     AND decision.resource_type = NEW.resource_type
                     AND decision.resource_id = NEW.resource_id
                     AND decision.sequence = NEW.sequence
                     AND decision.previous_hash = OLD.head_hash
                     AND decision.record_hash = NEW.head_hash
                     AND decision.write_transaction_id = pg_current_xact_id()::text
                     AND outbox.sequence = decision.sequence
                     AND outbox.record_hash = decision.record_hash
                     AND outbox.write_transaction_id = pg_current_xact_id()::text
               ) THEN
                RAISE EXCEPTION 'governance decision head must advance with its record and outbox'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        """;
    private static readonly string[] GovernanceDecisionHeadUpdateColumns =
        ["sequence", "fence_generation", "head_hash", "updated_at"];

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

    /// <summary>Requires the exact protected-table trigger inventory and owner-only functions.</summary>
    internal static void AssertMutationDenialPlan(ServicePlan[] services)
    {
        var protectedTables = services.SelectMany(service =>
            service.Tables.Where(table => table.LockColumn is not null ||
                table.MutationDenialRequired || table.MutationDenialGuard is not null)
                .Select(table => (Service: service, Table: table))).ToArray();
        var lockTables = protectedTables.Where(item => item.Table.LockColumn is not null).ToArray();
        var appendOnlyLedgers = protectedTables.Count(item =>
            item.Table.MutationDenialGuards?.Any(guard => guard.TriggerType == 34) == true);
        // Two append-only ledgers also use row-lock columns, so the protected tables overlap.
        if (services.Length != 5 || protectedTables.Length != 38 ||
            lockTables.Length != 14 || appendOnlyLedgers != 25)
            throw new InvalidOperationException(
                $"The exact fourteen row-lock columns, twenty-five append-only ledgers, and provider insert guard are required " +
                $"(services={services.Length}, protected={protectedTables.Length}, locks={lockTables.Length}, append-only={appendOnlyLedgers}).");

        foreach (var (service, table) in protectedTables)
        {
            var guards = GetMutationDenialGuards(table);
            var typeSevenGuards = guards.Where(guard => guard.TriggerType == 7).ToArray();
            var providerHead = service.Service == "projects-config" &&
                service.Schema == "projects_config" && table.Table == "model_providers";
            var providerReservation = service.Service == "projects-config" &&
                service.Schema == "projects_config" && table.Table == "model_provider_reference_reservations";
            var providerRevision = service.Service == "projects-config" &&
                service.Schema == "projects_config" && table.Table == "model_provider_revisions";
            bool IsProviderWriteGuard(MutationDenialGuardPlan guard)
            {
                var marker = guard.Function == "mark_model_provider_write()";
                var pair = guard.Function == "check_model_provider_pair()";
                var name = providerHead ? "model_providers" : "model_provider_revisions";
                var expectedType = marker ? (providerHead ? 23 : 7) : (providerHead ? 21 : 5);
                var expectedHash = marker
                    ? "251d3b422db7283268c1264b4743de38b0af2454e34fc23330e1a0d9eba11c18"
                    : "5499524241b6b4a25e0185ed40b6f552119a1521a4fd6191715f1fc737b77e93";
                return (providerHead || providerRevision) && (marker || pair)
                    && guard.Trigger == $"{name}_write_{(marker ? "marker" : "pair")}"
                    && guard.TriggerType == expectedType && guard.Deferrable == pair
                    && guard.Message == "provider transition is unavailable" && guard.SqlState == "23514"
                    && guard.FunctionSource is { } source
                    && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeGuardSource(source))))
                        .Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
            }
            var providerWriteGuards = guards.Where(IsProviderWriteGuard).ToArray();
            const string providerReferenceSource = """
                BEGIN
                    PERFORM pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'provider create requires read committed isolation'
                            USING ERRCODE = '40001';
                    END IF;
                    IF TG_TABLE_NAME = 'model_providers' THEN
                        IF EXISTS (
                            SELECT 1 FROM projects_config.model_provider_reference_reservations AS reservation
                            WHERE reservation.reference_folded =
                                pg_catalog.translate(NEW.provider_id COLLATE "C",
                                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                        ) THEN
                            RAISE EXCEPTION 'provider reference collision'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
                        END IF;
                    ELSE
                        IF EXISTS (
                            SELECT 1 FROM projects_config.model_providers AS provider
                            WHERE pg_catalog.translate(provider.provider_id COLLATE "C",
                                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = NEW.reference_folded
                        ) THEN
                            RAISE EXCEPTION 'provider reference collision'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                """;
            var hasProviderReferenceGuard =
                (providerHead || providerReservation) && typeSevenGuards.Length == (providerHead ? 1 : 2) &&
                typeSevenGuards[0].Trigger == (providerHead
                    ? "model_providers_reference_guard" : "model_provider_reference_cross_table_guard") &&
                typeSevenGuards[0].Function == "guard_model_provider_reference_cross_table()" &&
                typeSevenGuards[0].Message == "provider reference collision" &&
                typeSevenGuards[0].SqlState == "23514" &&
                typeSevenGuards[0].FunctionSource is { } providerSource &&
                NormalizeGuardSource(providerSource) == NormalizeGuardSource(providerReferenceSource);
            bool HasGuard(string trigger, string function, string message, string source) =>
                typeSevenGuards.Count(guard => guard.Trigger == trigger && guard.Function == function &&
                    guard.Message == message && guard.SqlState == "23514" &&
                    guard.FunctionSource is not null &&
                    NormalizeGuardSource(guard.FunctionSource) == NormalizeGuardSource(source)) == 1;
            var environmentReservation = service.Service == "environment" &&
                table.Table == "platform_policy_apply_reservations";
            var environmentHold = service.Service == "environment" &&
                table.Table == "platform_policy_proof_holds";
            var environmentReceipt = service.Service == "environment" &&
                table.Table == "platform_policy_proof_receipts";
            var hasReservationIdentityGuard = environmentReservation && typeSevenGuards.Length == 2 &&
                HasGuard("platform_policy_apply_reservations_consistent_identity",
                    "enforce_platform_policy_apply_reservation_identity()",
                    "platform policy apply reservation content conflicts", ReservationIdentitySource) &&
                HasGuard("platform_policy_apply_reservations_active_hold_fence",
                    "enforce_platform_policy_reservation_hold_fence()", "target has a policy hold",
                    ReservationHoldFenceSource);
            var hasHoldReservationGuard = environmentHold && typeSevenGuards.Length == 1 &&
                HasGuard("platform_policy_proof_holds_reservation_fence",
                    "enforce_platform_policy_hold_reservation_fence()",
                    "policy hold does not match the latest reservation", HoldReservationFenceSource);
            const string installationSource = """
                DECLARE
                    current_installation uuid;
                BEGIN
                    PERFORM pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'provider create requires read committed isolation'
                            USING ERRCODE = '40001';
                    END IF;
                    SELECT installation_id INTO current_installation
                    FROM projects_config.installation_identity WHERE singleton = true;
                    IF current_installation IS NULL OR NEW.installation_id IS NULL OR
                       NEW.installation_id <> current_installation THEN
                        RAISE EXCEPTION 'provider reference installation mismatch'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_provider_reference_installation_id';
                    END IF;
                    RETURN NEW;
                END;
                """;
            var hasInstallationGuard = providerReservation && typeSevenGuards.Length == 2 &&
                typeSevenGuards[1].Trigger == "model_provider_reference_installation_guard" &&
                typeSevenGuards[1].Function == "guard_provider_reference_installation()" &&
                typeSevenGuards[1].Message == "provider reference installation mismatch" &&
                typeSevenGuards[1].SqlState == "23514" &&
                typeSevenGuards[1].FunctionSource is { } installationGuardSource &&
                NormalizeGuardSource(installationGuardSource) == NormalizeGuardSource(installationSource);
            var hasProofReceiptIdentityGuard =
                table.Table == "platform_policy_proof_receipts" &&
                typeSevenGuards.Length == 1 &&
                HasGuard("platform_policy_proof_receipts_hold_identity",
                    "enforce_platform_policy_proof_receipt_hold()",
                    "platform policy proof receipt does not match its hold", ProofReceiptIdentitySource);
            var hasNativeAcsRevisionGuard =
                service.Service == "projects-config" &&
                table.Table == "native_acs_policy_artifact_files" &&
                typeSevenGuards.Length == 1 &&
                typeSevenGuards[0].Trigger == "native_acs_policy_artifact_files_insert_with_revision" &&
                typeSevenGuards[0].Function == "require_native_acs_policy_file_insert_in_revision_transaction()" &&
                typeSevenGuards[0].Message == "native ACS policy files must be inserted with their revision" &&
                typeSevenGuards[0].SqlState == "55000" &&
                !string.IsNullOrWhiteSpace(typeSevenGuards[0].FunctionSource);
            var governanceStampTrigger = table.Table switch
            {
                "project_governance_decisions" => "project_governance_decisions_stamp_transaction",
                "project_governance_decision_outbox" => "project_governance_decision_outbox_stamp_transaction",
                _ => null,
            };
            const string governanceStampFunctionSource =
                "BEGIN NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text; RETURN NEW; END";
            var hasGovernanceStampGuard =
                service.Service == "projects-config" &&
                service.Schema == "projects_config" &&
                governanceStampTrigger is not null &&
                typeSevenGuards.Length == 1 &&
                typeSevenGuards[0].Trigger == governanceStampTrigger &&
                typeSevenGuards[0].Function == "stamp_governance_decision_transaction_id()" &&
                typeSevenGuards[0].Message is null &&
                typeSevenGuards[0].SqlState is null &&
                typeSevenGuards[0].FunctionSource == governanceStampFunctionSource;
            var governanceDecisionHead = service.Service == "projects-config" &&
                service.Schema == "projects_config" && table.Table == GovernanceDecisionHeadTable;
            var hasGovernanceHeadInitializeGuard =
                governanceDecisionHead &&
                typeSevenGuards.Length == 1 &&
                typeSevenGuards[0].Trigger == "project_governance_decision_head_initialize" &&
                typeSevenGuards[0].Function == "require_governance_decision_head_initialize()" &&
                typeSevenGuards[0].Message == "governance decision stream head must start at genesis" &&
                typeSevenGuards[0].SqlState == "55000" &&
                typeSevenGuards[0].FunctionSource is { } initializeSource &&
                NormalizeGuardSource(initializeSource) == NormalizeGuardSource(GovernanceDecisionHeadInitializeFunctionSource);
            var governanceCheckpoint =
                service.Service == "projects-config" &&
                service.Schema == "projects_config" &&
                table.Table == "project_governance_decision_checkpoints";
            var hasGovernanceCheckpointAnchorGuard =
                governanceCheckpoint &&
                typeSevenGuards.Length == 1 &&
                typeSevenGuards[0].Trigger == "project_governance_decision_checkpoints_anchor" &&
                typeSevenGuards[0].Function == "require_governance_checkpoint_anchor()" &&
                typeSevenGuards[0].Message == "governance checkpoint must reference a stream record" &&
                typeSevenGuards[0].SqlState == "55000" &&
                typeSevenGuards[0].FunctionSource is { } checkpointSource &&
                NormalizeGuardSource(checkpointSource) == NormalizeGuardSource(GovernanceCheckpointAnchorFunctionSource);
            var typeNineteenGuards = guards.Where(guard => guard.TriggerType == 19).ToArray();
            var hasProviderIdGuard = providerHead && typeNineteenGuards.Length == 1 &&
                typeNineteenGuards[0].Trigger == "model_providers_id_immutable" &&
                typeNineteenGuards[0].Function == "reject_immutable_record_mutation()" &&
                typeNineteenGuards[0].Message == "records in %.% are immutable" &&
                typeNineteenGuards[0].SqlState == "55000" &&
                typeNineteenGuards[0].Column == "provider_id" &&
                typeNineteenGuards[0].Condition == "((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text)";
            var hasGovernanceHeadAdvanceGuard =
                governanceDecisionHead &&
                typeNineteenGuards.Length == 1 &&
                typeNineteenGuards[0].Trigger == "project_governance_decision_head_advance" &&
                typeNineteenGuards[0].Function == "require_governance_decision_head_advance()" &&
                typeNineteenGuards[0].Message == "governance decision head must advance with its record and outbox" &&
                typeNineteenGuards[0].SqlState == "55000" &&
                typeNineteenGuards[0].FunctionSource is { } advanceSource &&
                NormalizeGuardSource(advanceSource) == NormalizeGuardSource(GovernanceDecisionHeadAdvanceFunctionSource);
            var hasKnownTypeSevenGuard =
                hasReservationIdentityGuard || hasHoldReservationGuard || hasProofReceiptIdentityGuard ||
                hasNativeAcsRevisionGuard || hasGovernanceStampGuard ||
                hasGovernanceHeadInitializeGuard || hasGovernanceCheckpointAnchorGuard ||
                hasProviderReferenceGuard ||
                (providerRevision && typeSevenGuards.Length == 1 && IsProviderWriteGuard(typeSevenGuards[0]));
            var preflightTable = service.Service == "projects-config" &&
                service.Schema == "projects_config" && table.Table == "model_provider_credential_preflights";
            var mutationGuardCount = guards.Count(guard => guard.TriggerType is 11 or 19 or 27 or 34);
            var invalidColumnUpdates = governanceDecisionHead
                ? !(table.UpdateColumns ?? []).SequenceEqual(GovernanceDecisionHeadUpdateColumns, StringComparer.Ordinal)
                : preflightTable
                    ? !(table.UpdateColumns ?? []).SequenceEqual(["consumed_at", "consumed_by_kind"], StringComparer.Ordinal)
                    : table.UpdateColumns is { Length: > 0 };
            if (!table.MutationDenialRequired || guards.Length == 0 ||
                (typeSevenGuards.Length > 0 && !hasKnownTypeSevenGuard) ||
                guards.Length != mutationGuardCount + typeSevenGuards.Length +
                    (providerHead ? 2 : providerRevision ? 1 : 0) ||
                (table.Privileges.Contains("UPDATE") && !providerHead) ||
                (environmentReservation && !hasReservationIdentityGuard) ||
                (environmentHold && !hasHoldReservationGuard) ||
                (environmentReceipt && !hasProofReceiptIdentityGuard) ||
                (table.LockColumn is not null &&
                 mutationGuardCount != (table.MutationDenialGuards is { Length: > 0 } ? 2 : 1)) ||
                (table.LockColumn is null && mutationGuardCount != (providerHead ? 1 : governanceDecisionHead ? 3 : 2)) ||
                ((providerHead || providerReservation) && !hasProviderReferenceGuard) ||
                (providerReservation && !hasInstallationGuard) ||
                (providerHead && !hasProviderIdGuard) ||
                ((providerHead || providerRevision) && providerWriteGuards.Length != 2) ||
                invalidColumnUpdates ||
                (governanceDecisionHead && table.LockColumn is not null) ||
                (governanceCheckpoint && !hasGovernanceCheckpointAnchorGuard) ||
                (governanceDecisionHead &&
                 (!hasGovernanceHeadInitializeGuard || !hasGovernanceHeadAdvanceGuard)) ||
                (table.LockColumn is not null && table.MutationDenialGuards is { Length: > 0 } &&
                 (table.MutationDenialGuards.Length != 1 || table.MutationDenialGuards[0].TriggerType != 34)) ||
                guards.Any(guard =>
                {
                    var matchingFunctions = service.Functions
                        .Where(function => function.Signature == guard.Function).ToArray();
                    if (IsProviderWriteGuard(guard))
                        return matchingFunctions.Length != 1 || matchingFunctions[0].Execute ||
                            guard.Column is not null || guard.Condition is not null;
                    return string.IsNullOrWhiteSpace(guard.Trigger) ||
                        string.IsNullOrWhiteSpace(guard.Function) ||
                        (!hasProviderIdGuard || guard.Trigger != "model_providers_id_immutable") &&
                            (guard.Column is not null || guard.Condition is not null) ||
                        guard.TriggerType is not (7 or 11 or 19 or 27 or 34) ||
                        (guard.TriggerType == 7 &&
                         (guard.FunctionSource is null ||
                          (!hasGovernanceStampGuard &&
                           (!guard.FunctionSource.Contains($"RAISE EXCEPTION '{guard.Message}'", StringComparison.Ordinal) ||
                            !guard.FunctionSource.Contains($"USING ERRCODE = '{guard.SqlState}'", StringComparison.Ordinal))))) ||
                        (guard.TriggerType != 7 && guard.FunctionSource is not null &&
                         !(hasGovernanceHeadAdvanceGuard && guard.TriggerType == 19 &&
                           guard.Trigger == "project_governance_decision_head_advance" &&
                           guard.Function == "require_governance_decision_head_advance()")) ||
                        (string.IsNullOrWhiteSpace(guard.Message) && !hasGovernanceStampGuard) ||
                        (guard.SqlState is not null &&
                         !System.Text.RegularExpressions.Regex.IsMatch(guard.SqlState, "^[0-9A-Z]{5}$")) ||
                        matchingFunctions.Length != 1 || matchingFunctions[0].Execute;
                }))
                throw new InvalidOperationException(
                    $"The {service.Service} {table.Table} has no exact non-executable mutation guard.");
        }
    }

    internal static MutationDenialGuardPlan[] GetMutationDenialGuards(TablePlan table)
    {
        var additional = table.MutationDenialGuards ?? [];
        return table.MutationDenialGuard is { } single ? [single, .. additional] : additional;
    }

    /// <summary>
    /// Requires the exact reviewed column-scoped UPDATE inventory, without table-level UPDATE grants.
    /// </summary>
    internal static void AssertColumnUpdatePlan(ServicePlan[] services)
    {
        (string Service, string Schema, string Table, string[] Privileges, string[] Columns, bool Guarded)[]
            expected =
            [
                ("projects-config", "projects_config", "project_role_assignments",
                    ["SELECT", "INSERT"], ["state", "revision", "revoked_by", "revoked_at"], false),
                ("projects-config", "projects_config", "native_acs_policy_heads",
                    ["SELECT", "INSERT"], ["current_revision", "updated_at"], false),
                ("projects-config", "projects_config", GovernanceDecisionHeadTable,
                    ["SELECT", "INSERT"], GovernanceDecisionHeadUpdateColumns, true),
                ("projects-config", "projects_config", "model_provider_credential_preflights",
                    ["SELECT", "INSERT"], ["consumed_at", "consumed_by_kind"], true),
            ];
        var planned = services.SelectMany(service => service.Tables
            .Where(table => table.UpdateColumns is not { Length: 0 })
            .Select(table => (Service: service, Table: table))).ToArray();
        if (planned.Length != expected.Length || expected.Any(item => !planned.Any(candidate =>
                candidate.Service.Service == item.Service && candidate.Service.Schema == item.Schema &&
                candidate.Table.Table == item.Table &&
                candidate.Table.Privileges.SequenceEqual(item.Privileges, StringComparer.Ordinal) &&
                candidate.Table.UpdateColumns.SequenceEqual(item.Columns, StringComparer.Ordinal))))
            throw new InvalidOperationException(
                "The planned column-scoped UPDATE grants must match the reviewed Projects tables and columns.");

        foreach (var (service, table) in planned)
        {
            var governanceGuarded = service.Service == "projects-config" && service.Schema == "projects_config" &&
                table.Table == GovernanceDecisionHeadTable;
            var preflightGuarded = service.Service == "projects-config" && service.Schema == "projects_config" &&
                table.Table == "model_provider_credential_preflights";
            var guarded = governanceGuarded || preflightGuarded;
            var expectedGuardCount = governanceGuarded ? 4 : preflightGuarded ? 2 : 0;
            if (table.LockColumn is not null || table.MutationDenialRequired != guarded ||
                GetMutationDenialGuards(table).Length != expectedGuardCount)
                throw new InvalidOperationException(
                    "Column-scoped UPDATE grants require the reviewed governance-head guards and no row-lock guard.");
        }
    }

    /// <summary>Returns every column the plan lets the runtime role UPDATE on a table without table-level UPDATE.</summary>
    internal static string[] PlannedUpdateColumns(TablePlan table) =>
        [.. (table.LockColumn is null ? [] : new[] { table.LockColumn }), .. table.UpdateColumns ?? []];

    /// <summary>Builds the GRANT statements for one planned table: table privileges, then any column UPDATE.</summary>
    internal static string[] TableGrantStatements(ServicePlan service, TablePlan table)
    {
        var relation = $"{Quote(service.Schema)}.{Quote(table.Table)}";
        var statements = new List<string>
        {
            $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {Quote(service.Role)}",
        };
        // A row-lock table and a column-scoped table each get one column-list UPDATE grant; no table gets both,
        // because AssertColumnUpdatePlan rejects that shape before any connection.
        var columns = PlannedUpdateColumns(table);
        if (columns.Length != 0)
            statements.Add(
                $"GRANT UPDATE ({string.Join(", ", columns.Select(Quote))}) ON TABLE {relation} TO {Quote(service.Role)}");
        return [.. statements];
    }

    /// <summary>
    /// True only when the runtime role can UPDATE every planned column and no other column of the table.
    /// Table-level UPDATE also passes has_column_privilege, so callers check table privileges separately.
    /// </summary>
    internal const string ExactUpdateColumnsQuery = """
        SELECT (SELECT pg_catalog.bool_and(pg_catalog.has_column_privilege(@role, @table, c, 'UPDATE'))
                FROM pg_catalog.unnest(@columns::text[]) AS c)
          AND NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = pg_catalog.to_regclass(@table)
              AND a.attnum > 0 AND NOT a.attisdropped AND a.attname::text <> ALL(@columns::text[])
              AND pg_catalog.has_column_privilege(@role, a.attrelid, a.attnum, 'UPDATE'))
        """;

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
        // The reviewed plan must list exactly the versions compiled into the migrator, in strictly rising order.
        // Duplicates or reordering would make "pending" ambiguous, so they are rejected too.
        if (planned is null || planned.Length == 0 || !planned.SequenceEqual(supported) ||
            planned.Distinct().Count() != planned.Length || !planned.Zip(planned.Skip(1)).All(pair => pair.First < pair.Second))
            throw new InvalidOperationException($"The reviewed {service} migration plan differs from the source migrator catalog.");
        // The operator names the highest version they approved. If the source has moved past it, stop rather
        // than apply only part of the catalog, because the migrator cannot be told to stop early.
        if (maximum is { } value && planned[^1] != value)
            throw new InvalidOperationException(
                $"The {service} source catalog ends at {planned[^1]}, not the approved maximum {value}. Refusing to apply unapproved versions.");
    }

    /// <summary>Reads one schema's owner and effective ACL; returns no row when the schema is absent.</summary>
    // A schema nobody has granted on has a NULL ACL, which means the owner's default CREATE and USAGE.
    // Expanding the default keeps that state equal to the explicit owner-only ACL.
    internal const string SchemaAclQuery = """
        SELECT pg_catalog.json_build_object(
            'ownerOid', n.nspowner::bigint,
            'grants', COALESCE((SELECT pg_catalog.json_agg(pg_catalog.json_build_object(
                'grantor', a.grantor::bigint, 'grantee', a.grantee::bigint,
                'privilege', a.privilege_type, 'grantable', a.is_grantable))
                FROM pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a), '[]'::json))::text
        FROM pg_catalog.pg_namespace n WHERE n.nspname = @schema
        """;

    /// <summary>Lists column-level grants in one schema that reach the runtime role directly or through PUBLIC.</summary>
    internal const string RuntimeColumnGrantsQuery = """
        SELECT c.relname, a.attname, x.privilege_type, x.grantee = 0
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) x
        WHERE n.nspname = @schema AND a.attnum > 0 AND NOT a.attisdropped
          AND x.grantee IN (@runtime, 0)
        ORDER BY c.relname, a.attname, x.privilege_type
        """;

    /// <summary>Allows a runtime column grant only as UPDATE on a planned lock column or planned update column.</summary>
    internal static void AssertRuntimeColumnGrants(ServicePlan service, IEnumerable<ColumnGrant> grants)
    {
        foreach (var grant in grants)
        {
            // PUBLIC column grants reach every role, so none is ever planned. A direct grant is planned
            // only when it is UPDATE on a column the plan names for that table.
            var planned = !grant.Public && grant.Privilege == "UPDATE" &&
                service.Tables.Any(table => table.Table == grant.Relation &&
                    PlannedUpdateColumns(table).Contains(grant.Column, StringComparer.Ordinal));
            if (!planned)
                throw new InvalidOperationException(
                    $"The {service.Service} runtime role has unexpected column {grant.Privilege} authority on {service.Schema}.{grant.Relation}.{grant.Column}.");
        }
    }

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

    /// <summary>Requires a service schema to be owned by the expected owner with only owner CREATE/USAGE and runtime USAGE.</summary>
    internal static void AssertServiceSchemaAcl(ServicePlan service, SchemaAclState? state, uint operatorOid, uint runtimeOid,
        bool allowMissingRuntimeUsage = false)
    {
        // The only allowed schema grants: the operator's own owner privileges, and USAGE for the runtime role.
        // Grant options are never allowed, so the runtime role cannot pass privileges on.
        AclGrant[] expected =
        [
            new(operatorOid, operatorOid, "CREATE", false),
            new(operatorOid, operatorOid, "USAGE", false),
            new(operatorOid, runtimeOid, "USAGE", false),
        ];
        // The grant step may run before the runtime role ever received USAGE. It then accepts the owner grants
        // alone, because it adds USAGE itself. Any other grant still fails.
        var withoutRuntimeUsage = expected[..2];
        // A missing schema, an unresolved role id, a different owner, or any extra grant fails closed.
        if (state is null || operatorOid == 0 || runtimeOid == 0 || state.OwnerOid != operatorOid ||
            !(SameGrants(expected, state.Grants) ||
                (allowMissingRuntimeUsage && SameGrants(withoutRuntimeUsage, state.Grants))))
            throw new InvalidOperationException(
                $"The {service.Schema} schema is missing or has unexpected ownership or privileges. Refusing incremental continuation.");
    }

    /// <summary>Returns the planned versions missing from live history after proving that history is an exact plan prefix.</summary>
    internal static int[] PendingMigrationVersions(int[] planned, int[] applied, string service)
    {
        // Live history must be the first N planned versions. For example, live 1..23 against plan 1..27 is
        // fine, but live 1..21 plus 23, or a version the plan does not know, means the database was changed
        // outside this tool and needs a person to look at it.
        var ordered = applied.Order().ToArray();
        if (ordered.Distinct().Count() != ordered.Length || ordered.Length > planned.Length ||
            !planned.Take(ordered.Length).SequenceEqual(ordered))
            throw new InvalidOperationException(
                $"The live {service} migration history is not an exact prefix of the reviewed plan. Refusing to continue.");
        // Whatever follows the applied prefix is what the migrator will add. This may be empty.
        return planned.Skip(ordered.Length).ToArray();
    }

    /// <summary>Requires each runtime role to already hold exactly one non-grantable database CONNECT from the expected grantor.</summary>
    internal static void AssertRuntimeDatabaseConnect(ExistingBoundary boundary, uint[] runtimeOids)
    {
        // There are exactly five distinct runtime roles. Each must have one CONNECT grant on the database, from
        // the grantor recorded at initialization, with no grant option and no other database privilege.
        // This mode does not add CONNECT, so a role without it means initialization never finished.
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
            // Work out the most the runtime role may hold on this relation: its planned table privileges,
            // or nothing for owner-only tables and sequences. Anything the plan does not list gets null.
            var table = service.Tables.SingleOrDefault(item => item.Table == relation.Name);
            string[]? allowed = relation.Kind switch
            {
                "r" or "p" or "f" when table is not null => table.Privileges,
                "r" or "p" or "f" when service.OwnerOnlyTables.Contains(relation.Name) => [],
                "S" when service.OwnerOnlySequences.Contains(relation.Name) => [],
                _ => null,
            };
            // Fail on an unplanned relation, an owner other than the operator, or any runtime privilege beyond
            // the plan. Having fewer privileges is allowed, because the grant step adds the missing ones.
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

    internal static async Task<MutationGuardState[]> ReadMutationGuardStatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ServicePlan service, CancellationToken cancellationToken)
    {
        var protectedGuards = service.Tables.Where(table => table.MutationDenialRequired)
            .SelectMany(table => GetMutationDenialGuards(table)
                .Select(guard => (Table: table.Table, Guard: guard))).ToArray();
        if (protectedGuards.Length == 0) return [];
        await using var command = new NpgsqlCommand(MutationGuardReadbackQuery, connection, transaction);
        command.Parameters.AddWithValue("tables", protectedGuards.Select(item => item.Table).ToArray());
        command.Parameters.AddWithValue("triggers", protectedGuards.Select(item => item.Guard.Trigger).ToArray());
        command.Parameters.AddWithValue("schema", service.Schema);
        command.Parameters.AddWithValue("role", service.Role);
        var json = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return JsonSerializer.Deserialize<MutationGuardState[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>Verifies guard trigger shape, owner, source body, and runtime non-executability.</summary>
    internal static void AssertMutationDenialGuards(ServicePlan service, MutationGuardState[] guards,
        uint ownerOid, uint runtimeOid)
    {
        var expectedGuards = service.Tables.Where(table => table.MutationDenialRequired)
            .SelectMany(table => GetMutationDenialGuards(table)
                .Select(expected => (Table: table.Table, Guard: expected))).ToArray();
        if (guards.Length != expectedGuards.Length)
            throw new InvalidOperationException($"The {service.Service} row-lock mutation guards are incomplete.");

        foreach (var (table, expected) in expectedGuards)
        {
            var matches = guards.Where(guard =>
                guard.Table == table && guard.TriggerName == expected.Trigger).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"The {service.Service} {table} mutation guard is missing or duplicated.");
            var actual = matches[0];
            var expectedBody = NormalizeGuardSource(expected.FunctionSource ??
                $"BEGIN RAISE EXCEPTION '{expected.Message}'" +
                    (service.Schema == "projects_config" && expected.Function == "reject_immutable_record_mutation()"
                        ? ", TG_TABLE_SCHEMA, TG_TABLE_NAME" : string.Empty) +
                    (expected.SqlState is null ? string.Empty : $" USING ERRCODE = '{expected.SqlState}'") +
                    "; END");
            var proofTable = service.Service == "environment" &&
                table is "platform_policy_apply_reservations" or "platform_policy_proof_holds" or "platform_policy_proof_receipts";
            var expectedTypeSevenTriggers = table == "platform_policy_apply_reservations"
                ? new[] { "platform_policy_apply_reservations_active_hold_fence", "platform_policy_apply_reservations_consistent_identity" }
                : new[] { expected.Trigger };
            if (actual.TriggerName != expected.Trigger || actual.FunctionSchema != service.Schema ||
                actual.FunctionName != expected.Function || actual.TriggerType != expected.TriggerType ||
                actual.Enabled != "O" || actual.IsInternal != false ||
                actual.Deferrable != expected.Deferrable || actual.InitiallyDeferred != expected.Deferrable ||
                (expected.Column is null ? actual.NoColumnFilter != true :
                    actual.NoColumnFilter != false || actual.ColumnFilter != expected.Column) ||
                (expected.Condition is null ? actual.NoCondition != true :
                    actual.NoCondition != false ||
                    actual.Condition != $"({expected.Condition})") ||
                actual.ArgumentCount != 0 ||
                actual.FunctionOwnerOid != ownerOid || actual.RuntimeCanExecute != false ||
                actual.FunctionSource is null || NormalizeGuardSource(actual.FunctionSource) != expectedBody ||
                (proofTable && expected.TriggerType == 7 &&
                 (actual.VolatileInvoker != true || actual.TypeSevenTriggers is null ||
                  !actual.TypeSevenTriggers.SequenceEqual(expectedTypeSevenTriggers, StringComparer.Ordinal) ||
                  (expected.Trigger == "platform_policy_apply_reservations_consistent_identity"
                      ? actual.DefaultSearchPath != true : actual.PinnedSearchPath != true))) ||
                ownerOid == 0 || runtimeOid == 0)
                throw new InvalidOperationException(
                    $"The {service.Service} {table} mutation-denial trigger or function changed. The grants are not committed.");
        }
    }

    /// <summary>Rejects incompatible PUBLIC or default privileges, and an operator unable to grant CONNECT.</summary>
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
                planned.Signature is not
                    ("lock_casting_authority(uuid, text, text, text, bigint, text, boolean)" or
                     "issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)" or
                     "read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)")))
                throw new InvalidOperationException("Only approved Projects owner functions may receive runtime EXECUTE.");
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
/// <summary>Source-bound target, service grants, and exact Core, Events, and Environment migration versions.</summary>
internal sealed record PostgresPlan(
    int FormatVersion, TargetPlan Target, ServicePlan[] Services, int[] CoreVersions, int[] EventsVersions,
    int[] EnvironmentVersions);
/// <summary>Connection and Entra operator identity selected for a PostgreSQL initialization.</summary>
internal sealed record TargetPlan(string ResourceGroup, string TenantId, string PostgresHost, string Database,
    string OperatorName, string OperatorObjectId, string ConnectionHost, int ConnectionPort);
/// <summary>Exact schema, runtime principal, relation, and function contract for one service.</summary>
internal sealed record ServicePlan(string Service, string Schema, string Role, string PrincipalObjectId,
    TablePlan[] Tables, FunctionPlan[] Functions)
{
    public string[] OwnerOnlyTables { get; init; } = [];
    public string[] OwnerOnlySequences { get; init; } = [];
}
/// <summary>One column-level grant that reaches the runtime role directly or, when Public is true, through PUBLIC.</summary>
internal sealed record ColumnGrant(string Relation, string Column, string Privilege, bool Public);
/// <summary>Runtime table privileges, any one-column lock update, and any planned column-scoped updates.</summary>
internal sealed record TablePlan(string Table, string[] Privileges, string? LockColumn, bool MutationDenialRequired,
    MutationDenialGuardPlan? MutationDenialGuard, MutationDenialGuardPlan[]? MutationDenialGuards = null)
{
    /// <summary>Columns the runtime role may UPDATE on a table that has no table-level UPDATE; empty when none.</summary>
    public string[] UpdateColumns { get; init; } = [];
}
/// <summary>Expected trigger and owner-only function for a protected table.</summary>
internal sealed record MutationDenialGuardPlan(string Trigger, string Function, int TriggerType,
    string? Message = null, string? SqlState = null, string? FunctionSource = null,
    string? Column = null, string? Condition = null, bool Deferrable = false);
/// <summary>Readback of a mutation guard's catalog state and function source.</summary>
internal sealed record MutationGuardState(string Table, string? TriggerName, string? FunctionSchema,
    string? FunctionName, int? TriggerType, string? Enabled, bool? IsInternal, bool? NoColumnFilter,
    bool? NoCondition, int? ArgumentCount,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint? FunctionOwnerOid,
    bool? RuntimeCanExecute,
    string? FunctionSource, string? ColumnFilter = null, string? Condition = null,
    bool? VolatileInvoker = null, bool? PinnedSearchPath = null,
    bool? DefaultSearchPath = null, string[]? TypeSevenTriggers = null,
    bool? Deferrable = false, bool? InitiallyDeferred = false);
/// <summary>One live relation in a service schema with its owner and effective runtime privileges.</summary>
internal sealed record RelationState(string Name, string Kind, string Owner, string[]? RuntimePrivileges);
/// <summary>Observed owner and effective ACL for one service schema.</summary>
internal sealed record SchemaAclState(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] uint OwnerOid, AclGrant[] Grants);
/// <summary>Function signature and whether its execute privilege is granted to the runtime principal.</summary>
internal sealed record FunctionPlan(string Signature, bool Execute);
/// <summary>Durable stage marker bound to a source SHA, Core merge SHA, and source-plan hash.</summary>
internal sealed record ContinuationReceiptEntry(string Stage, string SourceSha, string CoreMergeSha,
    string PlanHash, DateTimeOffset At);
