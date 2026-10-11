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

    private static ServicePlan[] Services() => Enumerable.Range(0, 5).Select(index =>
        new ServicePlan($"service-{index}", $"schema_{index}", $"runtime_{index}",
            $"00000000-0000-0000-0000-{index + 1:000000000000}", [], [])).ToArray();

    private static RuntimePrincipal[] Principals(ServicePlan[] services) => services.Select((service, index) =>
        new RuntimePrincipal(Runtime + (uint)index, service.Role, service.PrincipalObjectId, "service",
            0, true, false, false, false, false, false, false, Tenant, 0,
            [new(Runtime + (uint)index, Owner, Owner, true, false, false)])).ToArray();
}
