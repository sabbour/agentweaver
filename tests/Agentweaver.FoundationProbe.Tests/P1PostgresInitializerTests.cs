using System.Diagnostics;
using System.Text.Json;
using Agentweaver.P1PostgresInitializer;
using Agentweaver.Environment;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class P1PostgresInitializerTests
{
    private const uint Owner = 10;
    private const uint Runtime = 100;
    private const string Tenant = "00000000-0000-0000-0000-000000000099";
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
    private static readonly AclGrant OwnerConnect = new(Owner, Owner, "CONNECT", false);
    private static readonly AclGrant PublicConnect = new(Owner, 0, "CONNECT", false);
    private static readonly AclGrant RuntimeConnect = new(Owner, Runtime, "CONNECT", false);
    private static readonly ExistingBoundary Baseline = new(20, Owner, Owner,
        [OwnerConnect, PublicConnect], "unchanged schemas, Broker, Probe, defaults and functions");

    [Fact]
    public void CanonicalEffectiveAclAllowsOnlyTheExactCumulativeConnectDelta()
    {
        InitializerChecks.AssertBoundary(Baseline, Baseline with
        {
            Grants = [RuntimeConnect, PublicConnect, OwnerConnect],
        }, [Runtime]);
        InitializerChecks.AssertBoundary(Baseline, Baseline with
        {
            Grants = [RuntimeConnect, new(Owner, Runtime + 1, "CONNECT", false), PublicConnect, OwnerConnect],
        }, [Runtime, Runtime + 1]);
        InitializerChecks.AssertBoundary(Baseline, Baseline with { Grants = [PublicConnect, OwnerConnect] }, []);
    }

    [Fact]
    public void ChangedMissingExtraOrGrantableDatabasePrivilegesAreRejected()
    {
        AclGrant[][] cases =
        [
            [OwnerConnect, PublicConnect],
            [OwnerConnect, RuntimeConnect],
            [OwnerConnect, PublicConnect, RuntimeConnect, new(Owner, Runtime, "CREATE", false)],
            [OwnerConnect, PublicConnect, RuntimeConnect, new(Owner, Runtime, "TEMPORARY", false)],
            [OwnerConnect, PublicConnect, RuntimeConnect with { Grantable = true }],
            [OwnerConnect, PublicConnect, RuntimeConnect with { Grantor = Owner + 1 }],
            [OwnerConnect, PublicConnect, RuntimeConnect with { Grantee = Runtime + 1 }],
            [OwnerConnect, PublicConnect, RuntimeConnect, RuntimeConnect],
            [OwnerConnect, PublicConnect with { Grantable = true }, RuntimeConnect],
        ];
        foreach (var grants in cases)
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertBoundary(Baseline, Baseline with { Grants = grants }, [Runtime]));
    }

    [Fact]
    public void OwnerObjectIdentityAndEveryProtectedStateFactRemainExact()
    {
        foreach (var changed in new[]
        {
            Baseline with { OwnerOid = Owner + 1 },
            Baseline with { DatabaseOid = 21 },
            Baseline with { ConnectGrantorOid = Owner + 1 },
            Baseline with { ProtectedState = "changed existing function owner or effective ACL" },
        })
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertBoundary(Baseline, changed, []));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertBoundary(Baseline, Baseline, [Runtime, Runtime]));
    }

    [Theory]
    [InlineData("CREATE")]
    [InlineData("TEMPORARY")]
    public void ExistingPublicDatabaseAuthorityIsRejectedBeforeMutation(string privilege) =>
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertPreflight(
            new(Baseline with { Grants = [OwnerConnect, PublicConnect, new(Owner, 0, privilege, false)] },
                Owner, true, false, false)));

    [Fact]
    public void CompatiblePublicConnectDoesNotPermitInheritedGrantorOrForeignAuthority()
    {
        var accepted = new PreflightSnapshot(Baseline, Owner, true, false, false);
        InitializerChecks.AssertPreflight(accepted);
        foreach (var rejected in new[]
        {
            accepted with { OperatorCanGrantConnect = false },
            accepted with { ForeignPublicAuthority = true },
            accepted with { IncompatibleDefaults = true },
            accepted with { Boundary = null },
        })
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertPreflight(rejected));
    }

    [Fact]
    public void MutationDenialPlanRequiresExactRowLockGrantsAndLedgerGuards()
    {
        var services = MutationServices();
        InitializerChecks.AssertMutationDenialPlan(services);

        // Dropping any one protected table changes the reviewed count, so the plan is rejected.
        var missingTable = MutationServices();
        missingTable[4] = missingTable[4] with { Tables = missingTable[4].Tables[..^1] };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(missingTable));

        var missingGuard = MutationServices();
        missingGuard[3] = missingGuard[3] with
        {
            Tables = [missingGuard[3].Tables[0] with { MutationDenialGuard = null },
                .. missingGuard[3].Tables.Skip(1)],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(missingGuard));

        var missingCheckpointAnchorGuard = MutationServices();
        var projects = missingCheckpointAnchorGuard[0];
        var checkpointIndex = Array.FindIndex(projects.Tables,
            table => table.Table == "project_governance_decision_checkpoints");
        projects.Tables[checkpointIndex] = projects.Tables[checkpointIndex] with
        {
            MutationDenialGuards = projects.Tables[checkpointIndex].MutationDenialGuards!
                .Where(guard => guard.Trigger != "project_governance_decision_checkpoints_anchor")
                .ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMutationDenialPlan(missingCheckpointAnchorGuard));

        var missingLedgerGuard = MutationServices();
        var orchestrator = missingLedgerGuard[4];
        var ledgerIndex = Array.FindIndex(orchestrator.Tables,
            table => table.Table == "maf_runtime_evidence_receipts");
        orchestrator.Tables[ledgerIndex] = orchestrator.Tables[ledgerIndex] with
        {
            MutationDenialGuards = [orchestrator.Tables[ledgerIndex].MutationDenialGuards![0]],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(missingLedgerGuard));

        var broadUpdate = MutationServices();
        broadUpdate[3] = broadUpdate[3] with
        {
            Tables = [broadUpdate[3].Tables[0] with { Privileges = ["SELECT", "INSERT", "UPDATE"] },
                .. broadUpdate[3].Tables.Skip(1)],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(broadUpdate));

        var executableGuard = MutationServices();
        executableGuard[3] = executableGuard[3] with
        {
            Functions = [executableGuard[3].Functions[0] with { Execute = true },
                .. executableGuard[3].Functions.Skip(1)],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(executableGuard));

        var executableLedgerGuard = MutationServices();
        orchestrator = executableLedgerGuard[4];
        var ledgerFunctionIndex = Array.FindIndex(orchestrator.Functions,
            function => function.Signature == "reject_maf_runtime_evidence_mutation()");
        orchestrator.Functions[ledgerFunctionIndex] = orchestrator.Functions[ledgerFunctionIndex] with
        {
            Execute = true,
        };
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMutationDenialPlan(executableLedgerGuard));

        foreach (var alteration in new[] { "source", "identity-source", "missing", "order" })
        {
            var changed = MutationServices();
            var table = changed[1].Tables[0];
            var guards = table.MutationDenialGuards!.ToArray();
            var index = Array.FindIndex(guards,
                guard => guard.Trigger == "platform_policy_apply_reservations_active_hold_fence");
            guards = alteration switch
            {
                "source" => guards.Select((guard, i) => i == index
                    ? guard with { FunctionSource = "BEGIN RETURN NEW; END" } : guard).ToArray(),
                "identity-source" => guards.Select(guard =>
                    guard.Trigger == "platform_policy_apply_reservations_consistent_identity"
                        ? guard with { FunctionSource = "BEGIN RETURN NEW; END" } : guard).ToArray(),
                "missing" => guards.Where((_, i) => i != index).ToArray(),
                _ => guards.Select((guard, i) => i == index
                    ? guard with { Trigger = "platform_policy_apply_reservations_z_fence" } : guard).ToArray(),
            };
            changed[1].Tables[0] = table with { MutationDenialGuards = guards };
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(changed));
        }
    }

    [Fact]
    public async Task EnvironmentGuardReadbackRejectsLiveCatalogChanges()
    {
        var fixture = new ProbePostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
                .UseNpgsql(fixture.AdminDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", EnvironmentDbContext.Schema)).Options;
            await EnvironmentMigrator.MigrateAsync(fixture.AdminDataSource, options);
            await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
            var role = "fence_reader_" + Guid.NewGuid().ToString("N");
            await using (var setup = new NpgsqlCommand(
                $"""
                CREATE ROLE "{role}" NOLOGIN;
                REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA environment FROM PUBLIC;
                """, connection))
                await setup.ExecuteNonQueryAsync();
            var service = MutationServices()[1] with { Role = role };
            await using var idQuery = new NpgsqlCommand(
                "SELECT (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user), (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=@role)",
                connection);
            idQuery.Parameters.AddWithValue("role", role);
            await using var idReader = await idQuery.ExecuteReaderAsync();
            await idReader.ReadAsync();
            var owner = idReader.GetFieldValue<uint>(0);
            var runtime = idReader.GetFieldValue<uint>(1);
            await idReader.DisposeAsync();

            async Task<MutationGuardState[]> ReadAsync(NpgsqlTransaction transaction)
            {
                var guards = service.Tables.SelectMany(table => InitializerChecks.GetMutationDenialGuards(table)
                    .Select(guard => (Table: table.Table, Guard: guard))).ToArray();
                await using var command = new NpgsqlCommand(
                    InitializerChecks.MutationGuardReadbackQuery, connection, transaction);
                command.Parameters.AddWithValue("tables", guards.Select(item => item.Table).ToArray());
                command.Parameters.AddWithValue("triggers", guards.Select(item => item.Guard.Trigger).ToArray());
                command.Parameters.AddWithValue("schema", service.Schema);
                command.Parameters.AddWithValue("role", role);
                return JsonSerializer.Deserialize<MutationGuardState[]>(
                    (string)(await command.ExecuteScalarAsync())!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            }

            var mutations = new[]
            {
                "DROP TRIGGER platform_policy_apply_reservations_active_hold_fence ON environment.platform_policy_apply_reservations",
                "ALTER TABLE environment.platform_policy_apply_reservations DISABLE TRIGGER platform_policy_apply_reservations_active_hold_fence",
                "ALTER TRIGGER platform_policy_apply_reservations_active_hold_fence ON environment.platform_policy_apply_reservations RENAME TO platform_policy_apply_reservations_z_fence",
                "CREATE OR REPLACE FUNCTION environment.enforce_platform_policy_reservation_hold_fence() RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $$ BEGIN RETURN NEW; END; $$",
                "CREATE OR REPLACE FUNCTION environment.enforce_platform_policy_apply_reservation_identity() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$",
                "ALTER FUNCTION environment.enforce_platform_policy_reservation_hold_fence() RESET search_path",
                "DROP TRIGGER platform_policy_proof_holds_reservation_fence ON environment.platform_policy_proof_holds",
                "ALTER TABLE environment.platform_policy_proof_holds DISABLE TRIGGER platform_policy_proof_holds_reservation_fence",
                "CREATE OR REPLACE FUNCTION environment.enforce_platform_policy_hold_reservation_fence() RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $$ BEGIN RETURN NEW; END; $$",
            };
            foreach (var mutation in mutations)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                InitializerChecks.AssertMutationDenialGuards(service, await ReadAsync(transaction), owner, runtime);
                await using (var change = new NpgsqlCommand(mutation, connection, transaction))
                    await change.ExecuteNonQueryAsync();
                var changed = await ReadAsync(transaction);
                Assert.Throws<InvalidOperationException>(() =>
                    InitializerChecks.AssertMutationDenialGuards(service, changed, owner, runtime));
                await transaction.RollbackAsync();
            }
            foreach (var (before, after, accepted) in new[]
            {
                ("'read committed'", "'read  committed'", false),
                ("'read committed'", "'read''  committed'", false),
                ("IF pg_catalog.current_setting", "IF  pg_catalog.current_setting", true),
            })
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using var definition = new NpgsqlCommand(
                    "SELECT pg_catalog.pg_get_functiondef('environment.enforce_platform_policy_reservation_hold_fence()'::regprocedure)",
                    connection, transaction);
                var original = (string)(await definition.ExecuteScalarAsync())!;
                Assert.Contains(before, original);
                await using (var change = new NpgsqlCommand(original.Replace(before, after, StringComparison.Ordinal), connection, transaction))
                    await change.ExecuteNonQueryAsync();
                var changed = await ReadAsync(transaction);
                if (accepted)
                    InitializerChecks.AssertMutationDenialGuards(service, changed, owner, runtime);
                else
                    Assert.Throws<InvalidOperationException>(() =>
                        InitializerChecks.AssertMutationDenialGuards(service, changed, owner, runtime));
                await transaction.RollbackAsync();
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public void RuntimePrincipalObjectIdsMustBeDistinctEntraGuids()
    {
        var services = Services();
        InitializerChecks.AssertPrincipalObjectIds(services);

        var unresolved = services.ToArray();
        unresolved[0] = unresolved[0] with { PrincipalObjectId = "CHANGEME-PRINCIPAL-OBJECT-ID-projects-config" };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertPrincipalObjectIds(unresolved));

        var duplicate = services.ToArray();
        duplicate[0] = duplicate[0] with { PrincipalObjectId = duplicate[1].PrincipalObjectId };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertPrincipalObjectIds(duplicate));
    }

    [Fact]
    public void OwnerOnlyProjectsRelationsAreExactAndCannotEnterRuntimePlan()
    {
        var services = OwnerOnlyServices();
        InitializerChecks.AssertOwnerOnlyObjectPlan(services);

        foreach (var invalid in new[]
        {
            ReplaceProjects(services, service => service with { OwnerOnlyTables = [] }),
            ReplaceProjects(services, service => service with { OwnerOnlyTables = ["unexpected"] }),
            ReplaceProjects(services, service => service with { OwnerOnlySequences = ["unexpected"] }),
            ReplaceProjects(services, service => service with
            {
                Tables = [new("project_setup_operations", ["SELECT"], null, false, null)],
            }),
            ReplaceProjects(services, service => service with
            {
                Functions = [new("reject_project_setup_operation_mutation()", true)],
            }),
        })
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertOwnerOnlyObjectPlan(invalid));
    }

    [Fact]
    public void ContinuationReceiptMustProveOnlyTheExactProjectsMigratedPrefix()
    {
        var valid = ContinuationReceipt();
        InitializerChecks.AssertContinuationReceipt(valid, "source", "core", "plan");

        foreach (var invalid in new[]
        {
            valid[..^1],
            [.. valid, valid[^1] with { Stage = "environment:schema_created" }],
            [.. valid, valid[^1] with { Stage = "projects-config:exact_grants_checked_and_committed" }],
            [.. valid, valid[^1] with { SourceSha = "different" }],
            [.. valid, valid[^1] with { PlanHash = "different" }],
        })
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertContinuationReceipt(invalid, "source", "core", "plan"));
    }

    [Fact]
    public void ContinuationRequiresTheExactKnownMigrationSet()
    {
        var expected = new[] { "001_initial", "002_authority", "003_setup" };
        InitializerChecks.AssertExactMigrationHistory(expected, expected, "Projects");
        foreach (var actual in new[]
        {
            new[] { "003_setup", "001_initial", "002_authority" },
            new[] { "001_initial", "002_authority" },
            new[] { "001_initial", "002_authority", "003_setup", "004_unknown" },
            new[] { "001_initial", "002_authority", "002_authority" },
        })
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertExactMigrationHistory(expected, actual, "Projects"));
    }

    [Fact]
    public void MutationGuardReadbackRequiresEnabledExactTriggersAndNonExecutableFunctions()
    {
        foreach (var service in MutationServices().Where(service => service.Tables.Any(table =>
            table.LockColumn is not null || table.MutationDenialRequired || table.MutationDenialGuard is not null)))
        {
            var guards = service.Tables.SelectMany(table => MutationGuardReadbacks(service, table)).ToArray();
            InitializerChecks.AssertMutationDenialGuards(service, guards, Owner, Runtime);
            for (var index = 0; index < guards.Length; index++)
            {
                Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialGuards(
                    service, guards.Where((_, guardIndex) => guardIndex != index).ToArray(), Owner, Runtime));
                var guard = guards[index];
                MutationGuardState[] invalidGuards =
                {
                    guard with { TriggerName = "foreign_trigger" },
                    guard with { FunctionName = "allow_mutation()" },
                    guard with { TriggerType = guard.TriggerType == 27 ? 19 : 27 },
                    guard with { Enabled = "D" },
                    guard with { IsInternal = true },
                    guard with { Deferrable = !guard.Deferrable },
                    guard with { InitiallyDeferred = !guard.InitiallyDeferred },
                    guard with { NoColumnFilter = !guard.NoColumnFilter },
                    guard with { NoCondition = !guard.NoCondition },
                    guard with { ArgumentCount = 1 },
                    guard with { FunctionOwnerOid = Owner + 1 },
                    guard with { RuntimeCanExecute = true },
                    guard with { FunctionSource = "BEGIN RETURN NEW; END" },
                };
                foreach (var invalidGuard in invalidGuards)
                {
                    var invalid = guards.ToArray();
                    invalid[index] = invalidGuard;
                    Assert.Throws<InvalidOperationException>(() =>
                        InitializerChecks.AssertMutationDenialGuards(service, invalid, Owner, Runtime));
                }
            }
        }
    }

    [Fact]
    public void FivePrincipalReadbacksBindObjectsFlagsAndOnlyTheExactOperatorMembership()
    {
        var services = Services();
        var principals = Principals(services);
        InitializerChecks.AssertPrincipals(services, principals, Owner, Tenant);
        foreach (var wrong in new[]
        {
            principals[0] with { ObjectId = Guid.NewGuid().ToString() },
            principals[0] with { ObjectId = null },
            principals[0] with { Type = "user" },
            principals[0] with { AdminFlag = 1 },
            principals[0] with { AdminFlag = -1 },
            principals[0] with { AdminFlag = 2 },
            principals[0] with { AdminFlag = null },
            principals[0] with { MfaFlag = 1 },
            principals[0] with { MfaFlag = -1 },
            principals[0] with { MfaFlag = 2 },
            principals[0] with { MfaFlag = null },
            principals[0] with { TenantId = Guid.NewGuid().ToString() },
            principals[0] with { TenantId = null },
            principals[0] with { CanLogin = false },
            principals[0] with { Superuser = true },
            principals[0] with { CreateDatabase = true },
            principals[0] with { CreateRole = true },
            principals[0] with { Replication = true },
            principals[0] with { Inherit = true },
            principals[0] with { BypassRls = true },
            principals[0] with { Oid = 0 },
            principals[0] with { Memberships = [] },
            principals[0] with { Memberships = [new(Runtime, Owner, Owner, true, true, false)] },
            principals[0] with { Memberships = [new(Runtime, Owner, Owner, true, false, true)] },
            principals[0] with { Memberships = [new(Runtime, Owner, Owner, false, false, false)] },
            principals[0] with { Memberships = [new(Runtime, Owner, Owner + 1, true, false, false)] },
            principals[0] with { Memberships = [new(Runtime, Owner + 1, Owner, true, false, false)] },
            principals[0] with { Memberships = [principals[0].Memberships[0],
                new(900, Runtime, Owner, false, false, false)] },
        })
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertPrincipals(services, [wrong, .. principals.Skip(1)], Owner, Tenant));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertPrincipals(services, principals.Skip(1).ToArray(), Owner, Tenant));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertPrincipals(services, [principals[0], principals[0], .. principals.Skip(2)], Owner, Tenant));
    }

    [Fact]
    public void PrincipalFlagReadbackRequiresIntegerColumnsNotBooleanValues()
    {
        var json = JsonSerializer.Serialize(Principals(Services())[0]);
        var principal = JsonSerializer.Deserialize<RuntimePrincipal>(json)!;
        Assert.Equal(0, principal.AdminFlag);
        Assert.Equal(0, principal.MfaFlag);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimePrincipal>(
            json.Replace("\"AdminFlag\":0", "\"AdminFlag\":false", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimePrincipal>(
            json.Replace("\"MfaFlag\":0", "\"MfaFlag\":false", StringComparison.Ordinal)));
    }

    [Fact]
    public void PostgresOidJsonStringsDeserializeForCatalogReadbacks()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var boundary = JsonSerializer.Deserialize<ExistingBoundary>("""
            {"databaseOid":"20","ownerOid":"10","connectGrantorOid":"10","grants":[{"grantor":"10","grantee":"0","privilege":"CONNECT","grantable":false}],"protectedState":"unchanged"}
            """, options)!;
        Assert.Equal(20u, boundary.DatabaseOid);
        Assert.Equal(0u, boundary.Grants[0].Grantee);

        var preflight = JsonSerializer.Deserialize<PreflightSnapshot>("""
            {"boundary":null,"operatorOid":"10","operatorCanGrantConnect":true,"foreignPublicAuthority":false,"incompatibleDefaults":false}
            """, options)!;
        Assert.Equal(10u, preflight.OperatorOid);

        var principal = JsonSerializer.Deserialize<RuntimePrincipal>("""
            {"oid":"100","role":"runtime","objectId":null,"type":"service","adminFlag":0,"canLogin":true,"superuser":false,"createDatabase":false,"createRole":false,"replication":false,"inherit":false,"bypassRls":false,"tenantId":null,"mfaFlag":0,"memberships":[{"roleOid":"100","memberOid":"10","grantorOid":"10","admin":true,"inherit":false,"set":false}]}
            """, options)!;
        Assert.Equal(100u, principal.Oid);
        Assert.Equal(10u, principal.Memberships[0].MemberOid);

        var function = JsonSerializer.Deserialize<FunctionState>("""
            {"oid":"200","signature":"helper()","ownerOid":"10","grants":[{"grantor":"10","grantee":"100","privilege":"EXECUTE","grantable":false}],"runtimeCanExecute":false}
            """, options)!;
        Assert.Equal(200u, function.Oid);
        Assert.Equal(100u, function.Grants[0].Grantee);

        var mutationGuard = JsonSerializer.Deserialize<MutationGuardState>("""
            {"table":"sessions","triggerName":"sessions_no_update","functionSchema":"events_sessions","functionName":"reject_row_lock_record_update()","triggerType":19,"enabled":"O","isInternal":false,"noColumnFilter":true,"noCondition":true,"argumentCount":0,"functionOwnerOid":"10","runtimeCanExecute":false,"functionSource":"BEGIN"}
            """, options)!;
        Assert.Equal(10u, mutationGuard.FunctionOwnerOid);
        Assert.Equal(10u, JsonSerializer.Deserialize<uint>("10"));
    }

    [Fact]
    public void FunctionOwnersAclAndEffectiveExecuteMatchOnlyTheApprovedProjectsFunctions()
    {
        var service = Services()[0] with
        {
            Schema = "projects_config",
            Functions =
            [
                new("helper()", false),
                new("lock_casting_authority(uuid, text, text, text, bigint, text, boolean)", true),
                new("issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)", true),
                new("read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)", true),
                new("require_native_acs_policy_head_advance()", false),
            ],
        };
        FunctionState helper = new(1, "helper()", Owner, [new(Owner, Owner, "EXECUTE", false)], false);
        FunctionState casting = new(2, service.Functions[1].Signature, Owner,
            [new(Owner, Runtime, "EXECUTE", false), new(Owner, Owner, "EXECUTE", false)], true);
        FunctionState issue = new(3, service.Functions[2].Signature, Owner,
            [new(Owner, Runtime, "EXECUTE", false), new(Owner, Owner, "EXECUTE", false)], true);
        FunctionState read = new(4, service.Functions[3].Signature, Owner,
            [new(Owner, Runtime, "EXECUTE", false), new(Owner, Owner, "EXECUTE", false)], true);
        FunctionState headAdvance = new(5, service.Functions[4].Signature, Owner,
            [new(Owner, Owner, "EXECUTE", false)], false);
        FunctionState[] WithHeadAdvance(params FunctionState[] functions) => [.. functions, headAdvance];
        InitializerChecks.AssertFunctions(service, WithHeadAdvance(casting, helper, issue, read), Owner, Runtime);
        var castingBeforeGrant = casting with
        {
            Grants = [new(Owner, Owner, "EXECUTE", false)],
            RuntimeCanExecute = false,
        };
        var issueBeforeGrant = issue with
        {
            Grants = [new(Owner, Owner, "EXECUTE", false)],
            RuntimeCanExecute = false,
        };
        var readBeforeGrant = read with
        {
            Grants = [new(Owner, Owner, "EXECUTE", false)],
            RuntimeCanExecute = false,
        };
        var defaultPublicHelper = helper with
        {
            Grants = [.. helper.Grants, new(Owner, 0, "EXECUTE", false)],
            RuntimeCanExecute = true,
        };
        InitializerChecks.AssertFunctions(
            service, WithHeadAdvance(castingBeforeGrant, defaultPublicHelper, issueBeforeGrant, readBeforeGrant),
            Owner, Runtime, beforeRuntimeGrants: true);
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertFunctions(
            service, WithHeadAdvance(castingBeforeGrant, defaultPublicHelper with
            {
                Grants = [.. defaultPublicHelper.Grants, new(Owner, Runtime, "EXECUTE", false)],
            }, issueBeforeGrant, readBeforeGrant), Owner, Runtime, beforeRuntimeGrants: true));
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertFunctions(
            service, WithHeadAdvance(castingBeforeGrant with
            {
                Grants = [.. castingBeforeGrant.Grants, new(Owner, 0, "EXECUTE", false)],
                RuntimeCanExecute = true,
            }, defaultPublicHelper, issueBeforeGrant, readBeforeGrant),
            Owner, Runtime, beforeRuntimeGrants: true));
        foreach (var wrong in new[]
        {
            helper with { OwnerOid = Owner + 1 },
            helper with { Oid = 0 },
            helper with { Signature = "substituted()" },
            helper with { RuntimeCanExecute = true },
            helper with { Grants = [.. helper.Grants, new(Owner, 0, "EXECUTE", false)] },
            helper with { Grants = [.. helper.Grants, new(Owner, Runtime, "EXECUTE", false)] },
            helper with { Grants = [.. helper.Grants, new(Owner, 500, "EXECUTE", false)] },
            helper with { Grants = [] },
            helper with { Grants = [new(Owner + 1, Owner, "EXECUTE", false)] },
            helper with { Grants = [new(Owner, Owner, "EXECUTE", true)] },
        })
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertFunctions(
                service, WithHeadAdvance(casting, wrong, issue, read), Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service, WithHeadAdvance(helper, issue, read), Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service,
                WithHeadAdvance(helper, casting with { Grants = helper.Grants }, issue, read), Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service,
                WithHeadAdvance(helper, casting with { RuntimeCanExecute = false }, issue, read), Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service with { Functions = [new("helper()", true)] },
                [helper with { RuntimeCanExecute = true }], Owner, Runtime));
    }

    [Theory]
    [InlineData("preflight", "preflight")]
    [InlineData("mutate", "preflight,mutate")]
    [InlineData("verify", "preflight,mutate,verify")]
    [InlineData("commit", "preflight,mutate,verify,commit")]
    [InlineData("record", "preflight,mutate,verify,commit,record")]
    public async Task FailedStagePreservesOriginalErrorAndNeverRunsLaterEffects(string failAt, string expected)
    {
        var calls = new List<string>();
        var original = new InvalidOperationException("controlled stage failure");
        Task Step(string stage)
        {
            calls.Add(stage);
            return stage == failAt ? Task.FromException(original) : Task.CompletedTask;
        }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InitializerChecks.RunStageAsync(() => Step("preflight"), () => Step("mutate"),
                () => Step("verify"), () => Step("commit"), () => Step("record")));
        Assert.Same(original, failure);
        Assert.Equal(expected, string.Join(",", calls));
    }

    [Fact]
    public async Task PartialStageReceiptsRemainWhenALaterStageFailsAndNoSuccessIsInvented()
    {
        var receipts = new List<string>();
        var committed = 0;
        Task Commit() { committed++; return Task.CompletedTask; }
        Task Record() { receipts.Add("roles_committed"); return Task.CompletedTask; }
        await InitializerChecks.RunStageAsync(() => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.CompletedTask, Commit, Record);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InitializerChecks.RunStageAsync(() => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.FromException(new InvalidOperationException()), Commit, Record));
        using var errors = new StringWriter();
        await InitializerChecks.ReportFailureAsync(failure, "Temp/receipt.jsonl", stage =>
        {
            receipts.Add(stage);
            return Task.CompletedTask;
        }, errors);
        Assert.Equal(1, committed);
        Assert.Equal(new[] { "roles_committed", "failed_requires_explicit_reconciliation" }, receipts);
        Assert.Contains("do not retry or reset partial state", errors.ToString());
    }

    [Fact]
    public async Task FailureReceiptErrorCannotReplaceOriginalFailureOrExposeErrorMessages()
    {
        using var errors = new StringWriter();
        await InitializerChecks.ReportFailureAsync(new InvalidOperationException("private original detail"),
            "Temp/receipt.jsonl", _ => Task.FromException(new IOException("private receipt detail")),
            errors, "42501");
        var output = errors.ToString();
        Assert.Contains("stopped: InvalidOperationException", output);
        Assert.Contains("SQLSTATE: 42501", output);
        Assert.Contains("could not be written: IOException", output);
        Assert.DoesNotContain("private", output);
        Assert.True(output.IndexOf("InvalidOperationException", StringComparison.Ordinal) <
            output.IndexOf("IOException", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationCatalogMustEqualCompiledSourceAndEndAtTheApprovedMaximum()
    {
        int[] core = [.. Enumerable.Range(1, 16), 18, 19, 20, 21, 22, 23];
        InitializerChecks.AssertMigrationCatalog(core, core, null, "Core");
        InitializerChecks.AssertMigrationCatalog(core, core, 23, "Core");
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMigrationCatalog(core, core, 22, "Core"));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMigrationCatalog(core, [.. core, 24], 23, "Core"));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMigrationCatalog([.. core, 24], [.. core, 24], 23, "Core"));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMigrationCatalog(null, core, 23, "Core"));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertMigrationCatalog([2, 1], [2, 1], 1, "Events"));
    }

    [Fact]
    public void PendingVersionsRequireLiveHistoryToBeAnExactPlanPrefix()
    {
        int[] core = [.. Enumerable.Range(1, 16), 18, 19, 20, 21, 22, 23];
        Assert.Equal([22, 23], InitializerChecks.PendingMigrationVersions(core, core[..^2], "Core"));
        Assert.Equal([10], InitializerChecks.PendingMigrationVersions(
            [.. Enumerable.Range(1, 10)], [9, 8, 7, 6, 5, 4, 3, 2, 1], "Events"));
        Assert.Empty(InitializerChecks.PendingMigrationVersions(core, core, "Core"));
        int[][] invalid =
        [
            [.. core, 24],
            [.. Enumerable.Range(1, 17)],
            [1, 2, 4],
            [1, 1, 2],
            [2, 3],
        ];
        foreach (var applied in invalid)
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.PendingMigrationVersions(core, applied, "Core"));
    }

    [Fact]
    public void IncrementalContinuationRequiresEachRuntimeRolesExactExistingConnect()
    {
        uint[] runtimes = [Runtime, Runtime + 1, Runtime + 2, Runtime + 3, Runtime + 4];
        var connected = Baseline with
        {
            Grants = [OwnerConnect, PublicConnect, .. runtimes.Select(oid => new AclGrant(Owner, oid, "CONNECT", false))],
        };
        InitializerChecks.AssertRuntimeDatabaseConnect(connected, runtimes);
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertRuntimeDatabaseConnect(Baseline, runtimes));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertRuntimeDatabaseConnect(connected with
            {
                Grants = [.. connected.Grants, new(Owner, Runtime, "TEMPORARY", false)],
            }, runtimes));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertRuntimeDatabaseConnect(connected with
            {
                Grants = [.. connected.Grants.Where(grant => grant.Grantee != Runtime), new(Owner, Runtime, "CONNECT", true)],
            }, runtimes));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertRuntimeDatabaseConnect(connected, runtimes[..4]));
    }

    [Fact]
    public void RuntimeColumnGrantsAreAllowedOnlyAsUpdateOnPlannedLockColumns()
    {
        var service = Services()[0] with
        {
            Tables =
            [
                new TablePlan("requests", ["SELECT", "INSERT"], null, false, null),
                new TablePlan("locks", ["SELECT"], "lock_owner", false, null),
                new TablePlan("assignments", ["SELECT", "INSERT"], null, false, null)
                {
                    UpdateColumns = ["state", "revision"],
                },
            ],
            OwnerOnlyTables = ["setup"],
        };
        InitializerChecks.AssertRuntimeColumnGrants(service, []);
        InitializerChecks.AssertRuntimeColumnGrants(service, [new("locks", "lock_owner", "UPDATE", false)]);
        InitializerChecks.AssertRuntimeColumnGrants(service,
            [new("assignments", "state", "UPDATE", false), new("assignments", "revision", "UPDATE", false)]);

        // Each of these passes table-level checks but would fail the service's startup authority check.
        ColumnGrant[] unexpected =
        [
            new("requests", "status", "UPDATE", false),
            new("locks", "other_column", "UPDATE", false),
            new("locks", "lock_owner", "INSERT", false),
            new("locks", "lock_owner", "UPDATE", true),
            new("assignments", "tenant_id", "UPDATE", false),
            new("assignments", "state", "SELECT", false),
            new("assignments", "state", "UPDATE", true),
            new("setup", "value", "SELECT", false),
            new("unplanned", "value", "UPDATE", false),
        ];
        foreach (var grant in unexpected)
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertRuntimeColumnGrants(service, [grant]));
    }

    [Fact]
    public void ColumnUpdatePlanAllowsOnlyTheReviewedProjectsColumns()
    {
        InitializerChecks.AssertColumnUpdatePlan(ColumnUpdateServices());

        var none = ColumnUpdateServices();
        none[0] = none[0] with
        {
            Tables = none[0].Tables.Select(table => table with { UpdateColumns = [] }).ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertColumnUpdatePlan(none));

        (string Table, TablePlan Replacement)[] rejectedTables =
        [
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { UpdateColumns = ["state", "revision", "revoked_by"] }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { UpdateColumns = ["state", "revision", "revoked_by", "tenant_id"] }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { Privileges = ["SELECT", "INSERT", "UPDATE"] }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { Privileges = ["SELECT", "INSERT", "DELETE"] }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { LockColumn = "state" }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { MutationDenialRequired = true }),
            ("project_role_assignments",
                ColumnUpdateServices()[0].Tables[0] with { Table = "authority_audit" }),
            ("native_acs_policy_heads",
                ColumnUpdateServices()[0].Tables[1] with { UpdateColumns = ["current_revision"] }),
            ("native_acs_policy_heads",
                ColumnUpdateServices()[0].Tables[1] with { UpdateColumns = ["current_revision", "updated_at", "policy_id"] }),
            ("project_governance_decision_stream_heads",
                ColumnUpdateServices()[0].Tables[2] with { UpdateColumns = ["sequence", "fence_generation", "head_hash"] }),
            ("project_governance_decision_stream_heads",
                ColumnUpdateServices()[0].Tables[2] with
                {
                    UpdateColumns = ["sequence", "fence_generation", "head_hash", "updated_at", "scope_id"],
                }),
            ("project_governance_decision_stream_heads",
                ColumnUpdateServices()[0].Tables[2] with { Privileges = ["SELECT", "INSERT", "UPDATE"] }),
            ("project_governance_decision_stream_heads",
                ColumnUpdateServices()[0].Tables[2] with { MutationDenialRequired = false }),
            ("project_governance_decision_stream_heads",
                ColumnUpdateServices()[0].Tables[2] with { MutationDenialGuards = [] }),
        ];
        foreach (var (tableName, replacement) in rejectedTables)
        {
            var services = ColumnUpdateServices();
            var index = Array.FindIndex(services[0].Tables, table => table.Table == tableName);
            services[0] = services[0] with
            {
                Tables = [.. services[0].Tables[..index], replacement, .. services[0].Tables[(index + 1)..]],
            };
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertColumnUpdatePlan(services));
        }

        var otherSchema = ColumnUpdateServices();
        otherSchema[0] = otherSchema[0] with { Schema = "orchestrator" };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertColumnUpdatePlan(otherSchema));
        var second = ColumnUpdateServices();
        second[1] = second[1] with
        {
            Tables =
            [
                new TablePlan("other", ["SELECT", "INSERT"], null, false, null)
                {
                    UpdateColumns = ["value"],
                },
            ],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertColumnUpdatePlan(second));
    }

    [Fact]
    public async Task EmittedRuntimeGrantPlanPassesInitializerChecks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine("scripts", "azure", "lib", "p1-postgres-plan.mjs"));
        startInfo.ArgumentList.Add("--json");
        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);

        var plan = JsonSerializer.Deserialize<PostgresPlan>(output,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(plan);
        InitializerChecks.AssertPrincipalObjectIds(plan.Services);
        InitializerChecks.AssertMutationDenialPlan(plan.Services);
        InitializerChecks.AssertColumnUpdatePlan(plan.Services);
        InitializerChecks.AssertOwnerOnlyObjectPlan(plan.Services);
        var projects = plan.Services.Single(service => service.Service == "projects-config");
        var head = projects.Tables.Single(table => table.Table == "model_providers");
        var identity = head.MutationDenialGuards!.Single(guard => guard.Trigger == "model_providers_id_immutable");
        foreach (var replacement in new MutationDenialGuardPlan?[]
        {
            null, identity with { Column = "display_name" },
            identity with { Condition = "(old.provider_id IS NOT DISTINCT FROM new.provider_id)" },
            identity with { Function = "guard_model_provider_reference_cross_table()" },
        })
        {
            var changed = head with
            {
                MutationDenialGuards = head.MutationDenialGuards!.Where(guard => guard != identity)
                    .Concat(replacement is null ? [] : [replacement]).ToArray(),
            };
            var services = plan.Services.Select(service => service == projects
                ? service with { Tables = service.Tables.Select(table => table == head ? changed : table).ToArray() }
                : service).ToArray();
            Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(services));
        }
    }

    [Fact]
    public void TableGrantStatementsAddOneQuotedColumnUpdateForLockOrPlannedColumns()
    {
        var service = ColumnUpdateServices()[0];
        Assert.Equal(
        [
            "GRANT SELECT, INSERT ON TABLE \"projects_config\".\"project_role_assignments\" TO \"runtime_0\"",
            "GRANT UPDATE (\"state\", \"revision\", \"revoked_by\", \"revoked_at\") ON TABLE " +
                "\"projects_config\".\"project_role_assignments\" TO \"runtime_0\"",
        ], InitializerChecks.TableGrantStatements(service, service.Tables[0]));
        Assert.Equal(
        [
            "GRANT SELECT ON TABLE \"projects_config\".\"locks\" TO \"runtime_0\"",
            "GRANT UPDATE (\"lock_owner\") ON TABLE \"projects_config\".\"locks\" TO \"runtime_0\"",
        ], InitializerChecks.TableGrantStatements(service, new TablePlan("locks", ["SELECT"], "lock_owner", false, null)));
        Assert.Equal(
            ["GRANT SELECT ON TABLE \"projects_config\".\"audit\" TO \"runtime_0\""],
            InitializerChecks.TableGrantStatements(service, new TablePlan("audit", ["SELECT"], null, false, null)));
    }

    [Fact]
    public void ExistingRelationsMustBePlannedOperatorOwnedAndWithinPlannedRuntimeAuthority()
    {
        var service = Services()[0] with
        {
            Tables = [new TablePlan("requests", ["SELECT", "INSERT"], null, false, null)],
            OwnerOnlyTables = ["setup"],
            OwnerOnlySequences = ["setup_seq"],
        };
        InitializerChecks.AssertRelationsWithinPlan(service,
        [
            new("requests", "r", "operator", ["SELECT"]),
            new("setup", "r", "operator", []),
            new("setup_seq", "S", "operator", []),
        ], "operator");
        // A planned relation may still be absent because its migration is pending.
        InitializerChecks.AssertRelationsWithinPlan(service, [], "operator");
        RelationState[] invalid =
        [
            new("requests", "r", "operator", ["SELECT", "DELETE"]),
            new("requests", "r", "other", ["SELECT"]),
            new("requests", "r", "operator", null),
            new("setup", "r", "operator", ["SELECT"]),
            new("setup_seq", "S", "operator", ["USAGE"]),
            new("unplanned", "r", "operator", []),
            new("requests_view", "v", "operator", []),
        ];
        foreach (var relation in invalid)
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertRelationsWithinPlan(service, [relation], "operator"));
    }

    [Fact]
    public void ServiceSchemaAclMustBeOperatorOwnedWithOnlyRuntimeUsage()
    {
        var service = Services()[0];
        AclGrant[] exact =
        [
            new(Owner, Owner, "CREATE", false),
            new(Owner, Owner, "USAGE", false),
            new(Owner, Runtime, "USAGE", false),
        ];
        InitializerChecks.AssertServiceSchemaAcl(service, new SchemaAclState(Owner, exact), Owner, Runtime);
        SchemaAclState?[] invalid =
        [
            null,
            new(Owner + 1, exact),
            new(Owner, exact[..2]),
            new(Owner, [.. exact, new(Owner, 0, "USAGE", false)]),
            new(Owner, [.. exact, new(Owner, Runtime, "CREATE", false)]),
            new(Owner, [exact[0], exact[1], exact[2] with { Grantable = true }]),
        ];
        foreach (var state in invalid)
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertServiceSchemaAcl(service, state, Owner, Runtime));

        // The Projects & Config grant step also accepts owner grants alone, because it adds runtime USAGE
        // itself. Every other invalid state still fails in that mode.
        InitializerChecks.AssertServiceSchemaAcl(service, new SchemaAclState(Owner, exact[..2]), Owner, Runtime,
            allowMissingRuntimeUsage: true);
        InitializerChecks.AssertServiceSchemaAcl(service, new SchemaAclState(Owner, exact), Owner, Runtime,
            allowMissingRuntimeUsage: true);
        foreach (var state in invalid.Where(item => item is null || item.Grants.Length != 2 || item.OwnerOid != Owner))
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertServiceSchemaAcl(service, state, Owner, Runtime, allowMissingRuntimeUsage: true));
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertServiceSchemaAcl(service,
            new SchemaAclState(Owner, [exact[0], new(Owner, Runtime, "USAGE", false)]), Owner, Runtime,
            allowMissingRuntimeUsage: true));
    }

    private static ServicePlan[] Services() => Enumerable.Range(0, 5).Select(index =>
        new ServicePlan($"service-{index}", $"schema_{index}", $"runtime_{index}",
            $"00000000-0000-0000-0000-{index + 1:000000000000}", [], [])).ToArray();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "azure", "lib", "p1-postgres-plan.mjs")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The P1 PostgreSQL source plan was not found above the test output directory.");
    }

    private static MutationDenialGuardPlan[] GovernanceDecisionHeadGuards() =>
    [
        new("project_governance_decision_head_initialize",
            "require_governance_decision_head_initialize()", 7,
            "governance decision stream head must start at genesis", "55000",
            GovernanceDecisionHeadInitializeFunctionSource),
        new("project_governance_decision_head_advance",
            "require_governance_decision_head_advance()", 19,
            "governance decision head must advance with its record and outbox", "55000",
            GovernanceDecisionHeadAdvanceFunctionSource),
        new("project_governance_decision_heads_no_delete",
            "reject_governance_decision_head_removal()", 11,
            "governance decision stream heads cannot be removed", "55000"),
        new("project_governance_decision_heads_no_truncate",
            "reject_governance_decision_head_removal()", 34,
            "governance decision stream heads cannot be removed", "55000"),
    ];

    private static ServicePlan[] ColumnUpdateServices()
    {
        var services = Services();
        services[0] = services[0] with
        {
            Service = "projects-config",
            Schema = "projects_config",
            Tables =
            [
                new TablePlan("project_role_assignments", ["SELECT", "INSERT"], null, false, null)
                {
                    UpdateColumns = ["state", "revision", "revoked_by", "revoked_at"],
                },
                new TablePlan("native_acs_policy_heads", ["SELECT", "INSERT"], null, false, null)
                {
                    UpdateColumns = ["current_revision", "updated_at"],
                },
                new TablePlan("project_governance_decision_stream_heads", ["SELECT", "INSERT"], null, true, null,
                    GovernanceDecisionHeadGuards())
                {
                    UpdateColumns = ["sequence", "fence_generation", "head_hash", "updated_at"],
                },
            ],
        };
        return services;
    }

    private static ServicePlan[] OwnerOnlyServices()
    {
        var services = Services();
        services[0] = services[0] with
        {
            Service = "projects-config",
            Schema = "projects_config",
            OwnerOnlyTables = ["project_setup_operations"],
            OwnerOnlySequences = ["platform_runtime_revisions_revision_seq"],
            Functions =
            [
                new("reject_project_setup_operation_mutation()", false),
                new("lock_casting_authority(uuid, text, text, text, bigint, text, boolean)", false),
                new("issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)", false),
                new("read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)", false),
            ],
        };
        return services;
    }

    private static ServicePlan[] ReplaceProjects(ServicePlan[] services, Func<ServicePlan, ServicePlan> replace)
    {
        var copy = services.ToArray();
        copy[0] = replace(copy[0]);
        return copy;
    }

    private static ContinuationReceiptEntry[] ContinuationReceipt()
    {
        var at = DateTimeOffset.Parse("2026-10-15T10:00:00Z");
        return
        [
            new("source_checked", "source", "core", "plan", at),
            new("fresh_target_checked", "source", "core", "plan", at),
            new("five_runtime_roles_created_and_exact_mappings_checked", "source", "core", "plan", at),
            new("projects-config:schema_created", "source", "core", "plan", at),
            new("projects-config:migrated", "source", "core", "plan", at),
            new("failed_requires_explicit_reconciliation", "source", "core", "plan", at),
        ];
    }

    private static ServicePlan[] MutationServices()
    {
        var services = Services();
        var reviewed = JsonSerializer.Deserialize<PostgresPlan>(
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "azure", "lib", "p1-postgres-plan.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var providerTables = reviewed.Services.Single(service => service.Schema == "projects_config").Tables;
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
        services[0] = services[0] with
        {
            Service = "projects-config",
            Schema = "projects_config",
            Tables =
            [
                new("model_providers", ["SELECT", "INSERT", "UPDATE"], null, true, null,
                [
                    new("model_providers_reference_guard", "guard_model_provider_reference_cross_table()",
                        7, "provider reference collision", "23514", providerReferenceSource),
                    new("model_providers_id_immutable", "reject_immutable_record_mutation()", 19,
                        "records in %.% are immutable", "55000", Column: "provider_id",
                        Condition: "((old.provider_id)::text IS DISTINCT FROM (new.provider_id)::text)"),
                    .. providerTables.Single(table => table.Table == "model_providers").MutationDenialGuards!
                        .Where(guard => guard.Trigger is "model_providers_write_marker" or "model_providers_write_pair"),
                ]),
                new("model_provider_reference_reservations", ["SELECT", "INSERT"], null, true, null,
                [
                    new("model_provider_reference_cross_table_guard", "guard_model_provider_reference_cross_table()",
                        7, "provider reference collision", "23514", providerReferenceSource),
                    new("model_provider_reference_reservations_immutable", "reject_immutable_record_mutation()",
                        27, "records in %.% are immutable", "55000"),
                    new("model_provider_reference_reservations_no_truncate", "reject_immutable_record_mutation()",
                        34, "records in %.% are immutable", "55000"),
                ]),
                new("model_provider_revisions", ["SELECT", "INSERT"], null, true, null,
                [
                    new("model_provider_revisions_immutable", "reject_immutable_record_mutation()",
                        27, "records in %.% are immutable", "55000"),
                    new("model_provider_revisions_no_truncate", "reject_immutable_record_mutation()",
                        34, "records in %.% are immutable", "55000"),
                    .. providerTables.Single(table => table.Table == "model_provider_revisions").MutationDenialGuards!
                        .Where(guard => guard.Trigger is "model_provider_revisions_write_marker" or "model_provider_revisions_write_pair"),
                ]),
                AppendOnlyTable("native_acs_policy_preparations"),
                AppendOnlyTable("native_acs_policy_revisions"),
                NativeAcsPolicyFilesTable(),
                AppendOnlyTable("native_acs_policy_idempotency"),
                AppendOnlyTable("native_acs_policy_change_audit"),
                AppendOnlyTable("native_acs_policy_outbox"),
                new TablePlan("project_governance_decision_stream_heads", ["SELECT", "INSERT"], null, true, null,
                    GovernanceDecisionHeadGuards()) with
                {
                    UpdateColumns = ["sequence", "fence_generation", "head_hash", "updated_at"],
                },
                AppendOnlyTable("project_governance_decisions") with
                {
                    MutationDenialGuards =
                    [
                        .. AppendOnlyTable("project_governance_decisions").MutationDenialGuards!,
                        new("project_governance_decisions_stamp_transaction",
                            "stamp_governance_decision_transaction_id()", 7,
                            FunctionSource: "BEGIN NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text; RETURN NEW; END"),
                    ],
                },
                AppendOnlyTable("project_governance_decision_outbox") with
                {
                    MutationDenialGuards =
                    [
                        .. AppendOnlyTable("project_governance_decision_outbox").MutationDenialGuards!,
                        new("project_governance_decision_outbox_stamp_transaction",
                            "stamp_governance_decision_transaction_id()", 7,
                            FunctionSource: "BEGIN NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text; RETURN NEW; END"),
                    ],
                },
                GovernanceCheckpointTable(),
                AppendOnlyTable("project_skill_trust_evidence"),
            ],
            Functions =
            [
                new("mark_model_provider_write()", false),
                new("check_model_provider_pair()", false),
                new("guard_model_provider_reference_cross_table()", false),
                new("reject_immutable_record_mutation()", false),
                new("require_native_acs_policy_file_insert_in_revision_transaction()", false),
                new("require_native_acs_policy_head_advance()", false),
                new("require_governance_decision_head_initialize()", false),
                new("require_governance_decision_head_advance()", false),
                new("reject_governance_decision_head_removal()", false),
                new("require_governance_checkpoint_anchor()", false),
                new("stamp_governance_decision_transaction_id()", false),
            ],
        };
        services[1] = services[1] with
        {
            Service = "environment",
            Schema = "environment",
            Tables =
            [
                new("platform_policy_apply_reservations", ["SELECT", "INSERT"], null, true, null,
                [
                    new("platform_policy_apply_reservations_consistent_identity",
                        "enforce_platform_policy_apply_reservation_identity()", 7,
                        "platform policy apply reservation content conflicts", "23514",
                        PlatformPolicyApplyReservationIdentityFunctionSource),
                    new("platform_policy_apply_reservations_active_hold_fence",
                        "enforce_platform_policy_reservation_hold_fence()", 7,
                        "target has a policy hold", "23514", ReservationHoldFenceSource),
                    new("platform_policy_apply_reservations_immutable",
                        "reject_platform_policy_apply_reservation_mutation()", 27,
                        "platform policy apply reservations are append-only", "55000"),
                    new("platform_policy_apply_reservations_no_truncate",
                        "reject_platform_policy_apply_reservation_mutation()", 34,
                        "platform policy apply reservations are append-only", "55000"),
                ]),
                new("platform_policy_proof_holds", ["SELECT", "INSERT"], null, true, null,
                [
                    new("platform_policy_proof_holds_reservation_fence",
                        "enforce_platform_policy_hold_reservation_fence()", 7,
                        "policy hold does not match the latest reservation", "23514", HoldReservationFenceSource),
                    new("platform_policy_proof_holds_immutable",
                        "reject_platform_policy_proof_mutation()", 27,
                        "platform policy proof records are append-only", "55000"),
                    new("platform_policy_proof_holds_no_truncate",
                        "reject_platform_policy_proof_mutation()", 34,
                        "platform policy proof records are append-only", "55000"),
                ]),
                new("platform_policy_proof_receipts", ["SELECT", "INSERT"], null, true, null,
                [
                    new("platform_policy_proof_receipts_hold_identity",
                        "enforce_platform_policy_proof_receipt_hold()", 7,
                        "platform policy proof receipt does not match its hold", "23514",
                        """
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
                        """),
                    new("platform_policy_proof_receipts_immutable",
                        "reject_platform_policy_proof_mutation()", 27,
                        "platform policy proof records are append-only", "55000"),
                    new("platform_policy_proof_receipts_no_truncate",
                        "reject_platform_policy_proof_mutation()", 34,
                        "platform policy proof records are append-only", "55000"),
                ]),
            ],
            Functions =
            [
                new("enforce_platform_policy_apply_reservation_identity()", false),
                new("enforce_platform_policy_reservation_hold_fence()", false),
                new("enforce_platform_policy_hold_reservation_fence()", false),
                new("enforce_platform_policy_proof_receipt_hold()", false),
                new("reject_platform_policy_apply_reservation_mutation()", false),
                new("reject_platform_policy_proof_mutation()", false),
            ],
        };
        services[3] = services[3] with
        {
            Service = "events-and-sessions",
            Schema = "events_sessions",
            Tables =
            [
                LockTable("sessions", "session_id", "sessions_no_update", "reject_row_lock_record_update()",
                    19, "Session identity, provider binding, and fork lineage records cannot be updated.", "55000"),
                LockTable("messaging_provider_bindings", "run_id", "messaging_provider_bindings_no_update",
                    "reject_row_lock_record_update()", 19,
                    "Session identity, provider binding, and fork lineage records cannot be updated.", "55000"),
                LockTable("session_provider_bindings", "run_id", "session_provider_bindings_no_update",
                    "reject_row_lock_record_update()", 19,
                    "Session identity, provider binding, and fork lineage records cannot be updated.", "55000"),
                LockTable("session_fork_lineage", "target_session_id", "session_fork_lineage_no_update",
                    "reject_row_lock_record_update()", 19,
                    "Session identity, provider binding, and fork lineage records cannot be updated.", "55000"),
                LockTable("usage_rate_cards", "card_id", "usage_rate_cards_immutable",
                    "reject_usage_history_mutation()", 27, "usage accounting history is append-only", null),
                LockTable("usage_ledger", "event_id", "usage_ledger_append_only",
                    "reject_usage_history_mutation()", 27, "usage accounting history is append-only", null),
            ],
            Functions =
            [
                new("reject_row_lock_record_update()", false),
                new("reject_usage_history_mutation()", false),
            ],
        };
        services[4] = services[4] with
        {
            Service = "orchestrator",
            Schema = "orchestrator",
            Tables =
            [
                LockTable("coordinator_decisions", "decision_id", "coordinator_decisions_no_update",
                    "reject_row_lock_record_update()", 19,
                    "Coordinator decision and MAF checkpoint records cannot be updated.", "55000"),
                LockTable("maf_workflow_checkpoints", "checkpoint_id", "maf_workflow_checkpoints_no_update",
                    "reject_row_lock_record_update()", 19,
                    "Coordinator decision and MAF checkpoint records cannot be updated.", "55000"),
                LockTable("session_consistency_manifests", "manifest_id", "session_consistency_manifests_immutable",
                    "reject_session_consistency_manifest_mutation()", 27,
                    "session consistency manifests are immutable", null),
                LockTable("maf_root_model_turn_admissions", "request_id", "maf_root_model_turn_admissions_no_update",
                    "reject_row_lock_record_update()", 27,
                    "Coordinator decision and MAF checkpoint records cannot be updated.", "55000"),
                LockTable("maf_child_spawn_authorization_receipts", "spawn_intent_id",
                    "maf_child_spawn_authorizations_no_update", "reject_row_lock_record_update()", 27,
                    "Coordinator decision and MAF checkpoint records cannot be updated.", "55000"),
                LockTable("maf_child_spawn_authorization_consumptions", "spawn_intent_id",
                    "maf_child_spawn_consumptions_no_update", "reject_row_lock_record_update()", 27,
                    "Coordinator decision and MAF checkpoint records cannot be updated.", "55000"),
                new("maf_runtime_evidence_receipts", ["SELECT", "INSERT"], null, true, null,
                [
                    new("maf_runtime_evidence_receipts_immutable",
                        "reject_maf_runtime_evidence_mutation()", 27,
                        "MAF runtime evidence receipts are append-only", null),
                    new("maf_runtime_evidence_receipts_no_truncate",
                        "reject_maf_runtime_evidence_mutation()", 34,
                        "MAF runtime evidence receipts are append-only", null),
                ]),
                LockTable("p1_run_request_provenance", "provenance_id",
                   "p1_run_request_provenance_immutable", "reject_p1_invocation_provenance_mutation()",
                   27, "P1 invocation provenance is append-only", null) with
                {
                   MutationDenialGuards =
                   [
                       new("p1_run_request_provenance_no_truncate",
                           "reject_p1_invocation_provenance_mutation()", 34,
                           "P1 invocation provenance is append-only", null),
                   ],
                },
                LockTable("p1_invocation_bindings", "core_invocation_id",
                   "p1_invocation_bindings_immutable", "reject_p1_invocation_provenance_mutation()",
                   27, "P1 invocation provenance is append-only", null) with
                {
                   MutationDenialGuards =
                   [
                       new("p1_invocation_bindings_no_truncate",
                           "reject_p1_invocation_provenance_mutation()", 34,
                           "P1 invocation provenance is append-only", null),
                   ],
                },
            ],
            Functions =
            [
                new("reject_row_lock_record_update()", false),
                new("reject_session_consistency_manifest_mutation()", false),
                new("reject_maf_runtime_evidence_mutation()", false),
                new("reject_p1_invocation_provenance_mutation()", false),
            ],
        };
        return services;
    }

    private static TablePlan LockTable(string table, string column, string trigger, string function,
        int triggerType, string message, string? sqlState) => new(table, ["SELECT", "INSERT"], column, true,
        new(trigger, function, triggerType, message, sqlState));

    private static TablePlan AppendOnlyTable(string table) =>
        new(table, ["SELECT", "INSERT"], null, true, null,
        [
            new($"{table}_immutable", "reject_immutable_record_mutation()",
                27, "records in %.% are immutable", "55000"),
            new($"{table}_no_truncate", "reject_immutable_record_mutation()",
                34, "records in %.% are immutable", "55000"),
        ]);

    private static TablePlan GovernanceCheckpointTable() =>
        new("project_governance_decision_checkpoints", ["SELECT", "INSERT"], null, true, null,
        [
            new("project_governance_decision_checkpoints_anchor",
                "require_governance_checkpoint_anchor()", 7,
                "governance checkpoint must reference a stream record", "55000",
                GovernanceCheckpointAnchorFunctionSource),
            new("project_governance_decision_checkpoints_immutable",
                "reject_immutable_record_mutation()", 27,
                "records in %.% are immutable", "55000"),
            new("project_governance_decision_checkpoints_no_truncate",
                "reject_immutable_record_mutation()", 34,
                "records in %.% are immutable", "55000"),
        ]);

    private const string PlatformPolicyApplyReservationIdentityFunctionSource =
        """
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

    private const string NativeAcsPolicyFileRevisionFunctionSource =
        """
        BEGIN
            IF NEW.write_transaction_id <> pg_catalog.pg_current_xact_id()::text
                OR NOT EXISTS (
                    SELECT 1
                    FROM projects_config.native_acs_policy_revisions AS revision
                    WHERE revision.scope_type = NEW.scope_type
                      AND revision.scope_id = NEW.scope_id
                      AND revision.policy_id = NEW.policy_id
                      AND revision.revision = NEW.revision
                      AND revision.write_transaction_id = NEW.write_transaction_id) THEN
                RAISE EXCEPTION 'native ACS policy files must be inserted with their revision'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        """;
    private static TablePlan NativeAcsPolicyFilesTable()
    {
        var table = AppendOnlyTable("native_acs_policy_artifact_files");
        return table with
        {
            MutationDenialGuards =
            [
                .. table.MutationDenialGuards!,
                new("native_acs_policy_artifact_files_insert_with_revision",
                    "require_native_acs_policy_file_insert_in_revision_transaction()", 7,
                    "native ACS policy files must be inserted with their revision", "55000",
                   FunctionSource: NativeAcsPolicyFileRevisionFunctionSource),
            ],
        };
    }

    private static MutationGuardState[] MutationGuardReadbacks(ServicePlan service, TablePlan table)
    {
        return InitializerChecks.GetMutationDenialGuards(table).Select(guard =>
        {
            var source = guard.FunctionSource ?? $"BEGIN RAISE EXCEPTION '{guard.Message}'" +
                (service.Schema == "projects_config" && guard.Function == "reject_immutable_record_mutation()"
                    ? ", TG_TABLE_SCHEMA, TG_TABLE_NAME" : string.Empty) +
                (guard.SqlState is null ? string.Empty : $" USING ERRCODE = '{guard.SqlState}'") +
                "; END";
            return new MutationGuardState(table.Table, guard.Trigger, service.Schema, guard.Function,
                guard.TriggerType, "O", false, guard.Column is null, guard.Condition is null, 0, Owner, false,
                source, guard.Column, guard.Condition is null ? null : $"({guard.Condition})",
                true, guard.Trigger != "platform_policy_apply_reservations_consistent_identity",
                guard.Trigger == "platform_policy_apply_reservations_consistent_identity",
                InitializerChecks.GetMutationDenialGuards(table).Where(item => item.TriggerType == 7)
                    .Select(item => item.Trigger).Order(StringComparer.Ordinal).ToArray(),
                guard.Deferrable, guard.Deferrable);
        }).ToArray();
    }

    private static RuntimePrincipal[] Principals(ServicePlan[] services) => services.Select((service, index) =>
        new RuntimePrincipal(Runtime + (uint)index, service.Role, service.PrincipalObjectId, "service",
            0, true, false, false, false, false, false, false, Tenant, 0,
            [new(Runtime + (uint)index, Owner, Owner, true, false, false)])).ToArray();
}
