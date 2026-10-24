using System.Text.Json;
using Agentweaver.P1PostgresInitializer;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class P1PostgresInitializerTests
{
    private const uint Owner = 10;
    private const uint Runtime = 100;
    private const string Tenant = "00000000-0000-0000-0000-000000000099";
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
    public void MutationDenialPlanRequiresAllEightOneColumnGrantsToNameNonExecutableGuards()
    {
        var services = MutationServices();
        InitializerChecks.AssertMutationDenialPlan(services);

        var missingGuard = MutationServices();
        missingGuard[3] = missingGuard[3] with
        {
            Tables = [missingGuard[3].Tables[0] with { MutationDenialGuard = null },
                .. missingGuard[3].Tables.Skip(1)],
        };
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertMutationDenialPlan(missingGuard));

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
    public void MutationDenialReadbackRequiresEnabledExactTriggersAndNonExecutableRejectingFunctions()
    {
        foreach (var service in MutationServices().Skip(3))
        {
            var guards = service.Tables.Select(table => MutationGuardReadback(service, table)).ToArray();
            InitializerChecks.AssertMutationDenialGuards(service, guards, Owner, Runtime);
            MutationGuardState[][] changedCases =
            {
                guards.Skip(1).ToArray(),
                [guards[0] with { TriggerName = "foreign_trigger" }, .. guards.Skip(1)],
                [guards[0] with { FunctionName = "allow_mutation()" }, .. guards.Skip(1)],
                [guards[0] with { TriggerType = 27 }, .. guards.Skip(1)],
                [guards[0] with { Enabled = "D" }, .. guards.Skip(1)],
                [guards[0] with { IsInternal = true }, .. guards.Skip(1)],
                [guards[0] with { NoColumnFilter = false }, .. guards.Skip(1)],
                [guards[0] with { NoCondition = false }, .. guards.Skip(1)],
                [guards[0] with { ArgumentCount = 1 }, .. guards.Skip(1)],
                [guards[0] with { FunctionOwnerOid = Owner + 1 }, .. guards.Skip(1)],
                [guards[0] with { RuntimeCanExecute = true }, .. guards.Skip(1)],
                [guards[0] with { FunctionSource = "BEGIN RETURN NEW; END" }, .. guards.Skip(1)],
            };
            foreach (var changed in changedCases)
                Assert.Throws<InvalidOperationException>(() =>
                    InitializerChecks.AssertMutationDenialGuards(service, changed, Owner, Runtime));
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
    public void FunctionOwnersAclAndEffectiveExecuteMatchOnlyTheCastingPlan()
    {
        var service = Services()[0] with
        {
            Schema = "projects_config",
            Functions = [new("helper()", false),
                new("lock_casting_authority(uuid, text, text, text, bigint, text, boolean)", true)],
        };
        FunctionState helper = new(1, "helper()", Owner, [new(Owner, Owner, "EXECUTE", false)], false);
        FunctionState casting = new(2, service.Functions[1].Signature, Owner,
            [new(Owner, Runtime, "EXECUTE", false), new(Owner, Owner, "EXECUTE", false)], true);
        InitializerChecks.AssertFunctions(service, [casting, helper], Owner, Runtime);
        var castingBeforeGrant = casting with
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
            service, [castingBeforeGrant, defaultPublicHelper], Owner, Runtime, beforeRuntimeGrants: true);
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertFunctions(
            service, [castingBeforeGrant, defaultPublicHelper with
            {
                Grants = [.. defaultPublicHelper.Grants, new(Owner, Runtime, "EXECUTE", false)],
            }], Owner, Runtime, beforeRuntimeGrants: true));
        Assert.Throws<InvalidOperationException>(() => InitializerChecks.AssertFunctions(
            service, [castingBeforeGrant with
            {
                Grants = [.. castingBeforeGrant.Grants, new(Owner, 0, "EXECUTE", false)],
                RuntimeCanExecute = true,
            }, helper], Owner, Runtime, beforeRuntimeGrants: true));
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
            Assert.Throws<InvalidOperationException>(() =>
                InitializerChecks.AssertFunctions(service, [casting, wrong], Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service, [helper], Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service, [helper, casting with { Grants = helper.Grants }], Owner, Runtime));
        Assert.Throws<InvalidOperationException>(() =>
            InitializerChecks.AssertFunctions(service, [helper, casting with { RuntimeCanExecute = false }], Owner, Runtime));
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
    }

    private static ServicePlan[] Services() => Enumerable.Range(0, 5).Select(index =>
        new ServicePlan($"service-{index}", $"schema_{index}", $"runtime_{index}",
            $"00000000-0000-0000-0000-{index + 1:000000000000}", [], [])).ToArray();

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
        services[3] = services[3] with
        {
            Service = "events-and-sessions",
            Schema = "events_sessions",
            Tables =
            [
                LockTable("sessions", "session_id", "sessions_no_update", "reject_row_lock_record_update()",
                    19, "Session identity, provider binding, and fork lineage records cannot be updated.", "55000"),
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
            ],
            Functions =
            [
                new("reject_row_lock_record_update()", false),
                new("reject_session_consistency_manifest_mutation()", false),
            ],
        };
        return services;
    }

    private static TablePlan LockTable(string table, string column, string trigger, string function,
        int triggerType, string message, string? sqlState) => new(table, ["SELECT", "INSERT"], column, true,
        new(trigger, function, triggerType, message, sqlState));

    private static MutationGuardState MutationGuardReadback(ServicePlan service, TablePlan table)
    {
        var guard = table.MutationDenialGuard!;
        var source = $"BEGIN RAISE EXCEPTION '{guard.Message}'" +
            (guard.SqlState is null ? string.Empty : $" USING ERRCODE = '{guard.SqlState}'") +
            "; END";
        return new MutationGuardState(table.Table, guard.Trigger, service.Schema, guard.Function,
            guard.TriggerType, "O", false, true, true, 0, Owner, false, source);
    }

    private static RuntimePrincipal[] Principals(ServicePlan[] services) => services.Select((service, index) =>
        new RuntimePrincipal(Runtime + (uint)index, service.Role, service.PrincipalObjectId, "service",
            0, true, false, false, false, false, false, false, Tenant, 0,
            [new(Runtime + (uint)index, Owner, Owner, true, false, false)])).ToArray();
}
