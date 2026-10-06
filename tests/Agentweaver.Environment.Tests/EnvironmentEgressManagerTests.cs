using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentEgressManagerTests
{
    private const string ProjectId = "project-a";
    private const string RunId = "run-a";
    private const string EnvironmentId = "environment-a";
    private const string TenantId = "tenant-a";
    private const string ProviderRevision = "cilium-options-1";

    private static NetworkEgressRule Fqdn(string host) =>
        new(NetworkEgressPurpose.SourceControl,
            NetworkEgressDestinationKind.Fqdn,
            host,
            443,
            EgressProtocol.Tcp);

    private static NetworkEgressRule Cidr(string cidr) =>
        new(NetworkEgressPurpose.SourceControl,
            NetworkEgressDestinationKind.Cidr,
            cidr,
            443,
            EgressProtocol.Tcp);

    private static NetworkEgressRule Dns(EgressProtocol protocol) =>
        new(NetworkEgressPurpose.DnsResolver,
            NetworkEgressDestinationKind.KubernetesService,
            "kube-system/kube-dns",
            53,
            protocol);

    private static ImmutableArray<NetworkEgressRule> Rules() =>
    [
        Fqdn("api.github.com"),
        Cidr("203.0.113.0/24"),
        Dns(EgressProtocol.Tcp),
        Dns(EgressProtocol.Udp)
    ];

    private static EffectiveNetworkPolicySelection Selection(
        ImmutableArray<EffectiveProviderCandidate>? candidates = null) =>
        new(
            ProjectId,
            RunId,
            1,
            2,
            3,
            "signed-selection-revision-1",
            [
                new EffectiveProviderSelection(
                    ProviderCardinality.Layered,
                    ProviderSeam.NetworkPolicy,
                    candidates ?? [CiliumCandidate()])
            ],
            Rules(),
            null,
            Rules(),
            Rules());

    private static EffectiveProviderCandidate CiliumCandidate(
        ImmutableArray<string>? requiredCapabilities = null) =>
        new(
            ProviderSeam.NetworkPolicy,
            CiliumEgressPolicyAdapter.ProviderId,
            "1.0.0",
            1,
            ProviderRevision,
            ProviderHostingPattern.KubernetesController,
            CiliumCapabilities(),
            requiredCapabilities ?? CiliumCapabilities(),
            NetworkPolicyLayer.L3L4);

    private static ImmutableArray<string> CiliumCapabilities() =>
    [
        CiliumEgressCapabilities.L3L4,
        CiliumEgressCapabilities.Fqdn,
        CiliumEgressCapabilities.Cidr,
        CiliumEgressCapabilities.Dns
    ];

    private static CiliumEgressProviderOptions Options() =>
        new(
            "agentweaver",
            new Version(1, 0, 0),
            1,
            ProviderRevision,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [
                    new KeyValuePair<string, ImmutableDictionary<string, string>>(
                        "kube-system/kube-dns",
                        ImmutableDictionary.CreateRange(
                            StringComparer.Ordinal,
                            [
                                new KeyValuePair<string, string>("k8s:io.kubernetes.pod.namespace", "kube-system"),
                                new KeyValuePair<string, string>("k8s:k8s-app", "kube-dns")
                            ]))
                ]));

    private static CurrentCallerRequest Caller() => new("validated.jwt.token", TenantId);

    private static ProjectAuthorizationContextResponse AuthorizationContext(
        string projectId = ProjectId,
        string runId = RunId,
        bool canReadProjects = false,
        bool canReadSelection = true,
        bool canWrite = true,
        string? boundProjectId = null,
        string? boundRunId = null)
    {
        var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
        if (canReadProjects)
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadProjects,
                4));
        if (canReadSelection)
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadRunSelection,
                5));
        if (canWrite)
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.WriteProjects,
                6));
        var authority = permissions.Count == 0
            ? ImmutableArray<EffectiveProjectAuthorization>.Empty
            : [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    projectId,
                    permissions.ToImmutable())
            ];
        return new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://identity.example",
            "actor-a",
            TenantId,
            4,
            boundProjectId,
            boundRunId,
            authority);
    }

    private static ApplyEnvironmentEgressRequest ApplyRequest(
        string environmentId = EnvironmentId,
        string projectId = ProjectId,
        string runId = RunId,
        long generation = 1,
        long expectedPrevious = 0,
        long lifecycleGeneration = 1,
        string? idempotencyKey = null) =>
        new(
            new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity(TenantId, projectId, runId, environmentId),
                lifecycleGeneration),
            generation,
            expectedPrevious,
            idempotencyKey ?? $"policy-{generation}-after-{expectedPrevious}");

    private static EnvironmentEgressManager Manager(
        FakeProjectsConfigClient projects,
        FakeCiliumPolicyResourceStore store,
        FakeEnvironmentLifecycleStore? lifecycleStore = null)
    {
        var options = Options();
        var cilium = new CiliumEgressPolicyAdapter(store, options);
        return new EnvironmentEgressManager(
            projects,
            cilium,
            options,
            lifecycleStore ?? new FakeEnvironmentLifecycleStore(ApplyRequest().Fence));
    }

    [Fact]
    public async Task ApplyReadsCurrentProjectsAuthorityAndPinsOnlyTheObservedGeneration()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        using var activity = new Activity("http.request").Start();

        var result = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.True(result.ReadyForDispatch, result.FailureMessage);
        Assert.Equal(1, result.Binding!.AppliedIntentGeneration);
        Assert.Equal(1, result.Binding.Layers.Single().Binding.Resource.Generation);
        Assert.Equal(ProviderRevision, result.Binding.Layers.Single().Binding.OptionsRevision);
        Assert.True(result.AppliedState!.ObjectVerified);
        Assert.False(result.AppliedState.DatapathEnforcementVerified);
        Assert.Equal(4, projects.AuthorizationReads);
        Assert.Equal(1, projects.SelectionReads);
        Assert.Equal("apply", activity.GetTagItem("agentweaver.network_policy.operation"));
        Assert.Equal(1L, activity.GetTagItem("agentweaver.network_policy.requested_intent_generation"));
        Assert.Equal("L3L4", activity.GetTagItem("agentweaver.network_policy.layer.L3L4.name"));
        Assert.Equal(
            CiliumEgressPolicyAdapter.ProviderId,
            activity.GetTagItem("agentweaver.network_policy.layer.L3L4.provider_id"));
        Assert.Equal("1.0.0", activity.GetTagItem("agentweaver.network_policy.layer.L3L4.adapter_version"));
        Assert.Equal(1L, activity.GetTagItem("agentweaver.network_policy.applied_intent_generation"));
        Assert.Equal(1L, activity.GetTagItem("agentweaver.network_policy.resource_generation"));
        Assert.DoesNotContain(
            activity.TagObjects,
            tag => tag.Value is string value &&
                new[] { TenantId, ProjectId, RunId, "validated.jwt.token", "api.github.com" }
                    .Contains(value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PolicyGenerationIsDistinctFromEnvironmentLifecycleGeneration()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(
            projects,
            store,
            new FakeEnvironmentLifecycleStore(ApplyRequest(lifecycleGeneration: 7).Fence));
        var request = ApplyRequest(generation: 1, expectedPrevious: 0, lifecycleGeneration: 7);

        var result = await manager.ApplyAndVerifyAsync(
            Caller(),
            request,
            CancellationToken.None);

        var policy = Assert.Single(store.Resources.Values);
        Assert.Equal(7, request.Fence.LifecycleGeneration);
        Assert.True(result.ReadyForDispatch, result.FailureMessage);
        Assert.Equal(1, result.AppliedState!.AppliedIntentGeneration);
        Assert.Equal("1", policy.Metadata.Annotations[CiliumEgressPolicyAdapter.IntentGenerationAnnotation]);
    }

    [Fact]
    public async Task CiliumManifestUsesExactPerEnvironmentSelectorAndOnlyAdmittedRules()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        var result = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);
        var expectedSelector = EnvironmentEgressSelector.Create(
            EnvironmentId,
            TenantId,
            ProjectId,
            RunId,
            "agentweaver");

        Assert.True(result.ReadyForDispatch);
        Assert.Equal(expectedSelector.MatchLabels, policy.Spec.EndpointSelector.MatchLabels);
        Assert.Equal(4, policy.Spec.Egress.Length);
        Assert.All(policy.Metadata.Labels.Values, value => Assert.InRange(value.Length, 1, 63));
        Assert.Contains(policy.Spec.Egress, rule =>
            rule.ToFqDns?.Any(fqdn => fqdn.MatchName == "api.github.com") == true);
        Assert.Contains(policy.Spec.Egress, rule =>
            rule.ToCidrSet?.Any(cidr => cidr.Cidr == "203.0.113.0/24") == true);
        Assert.Contains(policy.Spec.Egress, rule =>
            rule.ToEndpoints?.Single().MatchLabels["k8s:k8s-app"] == "kube-dns" &&
            rule.ToPorts?.Single().Rules?.Dns.Single().MatchPattern == "*");
        Assert.DoesNotContain(policy.Spec.Egress, rule =>
            rule.ToCidrSet?.Any(cidr => cidr.Cidr is "0.0.0.0/0" or "::/0") == true);
        Assert.Contains("agentweaver.dev/egress-generation", policy.Metadata.Annotations.Keys);
        Assert.DoesNotContain("validated.jwt.token", System.Text.Json.JsonSerializer.Serialize(policy));
    }

    [Fact]
    public async Task CiliumRulesPreserveEachDestinationProtocolPair()
    {
        var rules = ImmutableArray.Create(
            new NetworkEgressRule(
                NetworkEgressPurpose.SourceControl,
                NetworkEgressDestinationKind.Cidr,
                "203.0.113.0/24",
                443,
                EgressProtocol.Tcp),
            new NetworkEgressRule(
                NetworkEgressPurpose.SourceControl,
                NetworkEgressDestinationKind.Cidr,
                "198.51.100.0/24",
                443,
                EgressProtocol.Udp));
        var selection = Selection([CiliumCandidate([
            CiliumEgressCapabilities.L3L4,
            CiliumEgressCapabilities.Cidr
        ])]) with
        {
            EgressBaseline = rules,
            RequiredEgress = rules,
            EgressAllowlist = rules
        };
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(
                new FakeProjectsConfigClient(AuthorizationContext(), selection),
                store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);

        Assert.True(result.ReadyForDispatch, result.FailureMessage);
        Assert.Equal(2, policy.Spec.Egress.Length);
        Assert.Contains(policy.Spec.Egress, rule =>
            rule.ToCidrSet?.Single().Cidr == "203.0.113.0/24" &&
            rule.ToPorts?.Single().Ports.Single().Protocol == "TCP");
        Assert.Contains(policy.Spec.Egress, rule =>
            rule.ToCidrSet?.Single().Cidr == "198.51.100.0/24" &&
            rule.ToPorts?.Single().Ports.Single().Protocol == "UDP");
    }

    [Fact]
    public async Task EmptyIntentAndRevocationUseExplicitEgressDenyAllRules()
    {
        var emptyRules = ImmutableArray<NetworkEgressRule>.Empty;
        var selection = Selection([CiliumCandidate([CiliumEgressCapabilities.L3L4])]) with
        {
            EgressBaseline = emptyRules,
            RequiredEgress = emptyRules,
            EgressAllowlist = emptyRules
        };
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(new FakeProjectsConfigClient(AuthorizationContext(), selection), store);

        var applied = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var appliedPolicy = Assert.Single(store.Resources.Values);

        Assert.True(applied.ReadyForDispatch, applied.FailureMessage);
        Assert.Empty(appliedPolicy.Spec.Egress);
        Assert.Equal(new CiliumEgressRuleDocument(), appliedPolicy.Spec.EgressDeny!.Value.Single());
        Assert.Equal(new CiliumDefaultDeny(Ingress: false, Egress: true), appliedPolicy.Spec.EnableDefaultDeny);

        var revoked = await manager.RevokeAsync(
            Caller(),
            ApplyRequest(generation: 2, expectedPrevious: 1),
            CancellationToken.None);
        var tombstone = Assert.Single(store.Resources.Values);

        Assert.True(revoked.Revoked, revoked.FailureMessage);
        Assert.Empty(tombstone.Spec.Egress);
        Assert.Equal(new CiliumEgressRuleDocument(), tombstone.Spec.EgressDeny!.Value.Single());
        Assert.Equal(new CiliumDefaultDeny(Ingress: false, Egress: true), tombstone.Spec.EnableDefaultDeny);
    }

    [Fact]
    public async Task NonSequentialPolicyGenerationsCanBeAppliedAndRetriedIdempotently()
    {
        var selection = Selection();
        var compilation = EgressIntentCompiler.Compile(selection);
        Assert.True(compilation.IsSuccess, compilation.Failure?.Message);
        var intent = compilation.Intent!;
        var selector = EnvironmentEgressSelector.Create(
            EnvironmentId,
            TenantId,
            ProjectId,
            RunId,
            Options().Namespace);
        var store = new FakeCiliumPolicyResourceStore();
        var adapter = new CiliumEgressPolicyAdapter(store, Options());

        _ = await adapter.ApplyAsync(selector, intent, 41, 0, CancellationToken.None);
        _ = await adapter.ApplyAsync(selector, intent, 47, 41, CancellationToken.None);
        var appliedReplay = await adapter.ApplyAsync(selector, intent, 47, 41, CancellationToken.None);

        Assert.Equal(47, long.Parse(appliedReplay.Metadata.Annotations[
            CiliumEgressPolicyAdapter.IntentGenerationAnnotation]));
        var revoked = await adapter.RevokeAsync(selector, 60, 47, CancellationToken.None);
        var revokeReplay = await adapter.RevokeAsync(selector, 60, 47, CancellationToken.None);

        Assert.True(revoked.Revoked);
        Assert.True(revokeReplay.Revoked);
        Assert.Equal(60, revokeReplay.AppliedIntentGeneration);
    }

    [Fact]
    public async Task ReadbackRejectsDisabledDefaultDenyAndUnmodeledPolicySpecs()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        var applied = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);
        Assert.True(applied.ReadyForDispatch);

        store.Update(policy with
        {
            Spec = policy.Spec with
            {
                EnableDefaultDeny = new CiliumDefaultDeny(Ingress: false, Egress: false)
            }
        });
        var defaultDenyDisabled = await manager.VerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        Assert.False(defaultDenyDisabled.ReadyForDispatch);
        Assert.Equal("policy_generation_unverified", defaultDenyDisabled.FailureCode);

        using var specs = JsonDocument.Parse("[{}]");
        store.Update(policy with
        {
            AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["specs"] = specs.RootElement.Clone()
            }
        });
        var additionalPolicy = await manager.VerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        Assert.False(additionalPolicy.ReadyForDispatch);
        Assert.Equal("policy_generation_unverified", additionalPolicy.FailureCode);
    }

    [Fact]
    public async Task VerifyCannotPinProviderStateWhileOwnerEffectIsUnresolved()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var fence = ApplyRequest().Fence;
        var lifecycle = new FakeEnvironmentLifecycleStore(fence);
        var manager = Manager(projects, store, lifecycle);
        var applied = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);
        Assert.True(applied.ReadyForDispatch);

        _ = await lifecycle.ReserveNetworkEffectAsync(
            fence,
            $"{policy.Metadata.Namespace}/{policy.Metadata.Name}",
            policyGeneration: 2,
            expectedPreviousPolicyGeneration: 1,
            EnvironmentNetworkEffectKind.Apply,
            "unresolved-provider-effect",
            CancellationToken.None);
        var verified = await manager.VerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(verified.ReadyForDispatch);
        Assert.Equal("environment_effect_reconciliation_required", verified.FailureCode);
        Assert.Null(verified.Binding);
    }

    [Fact]
    public async Task SecondEnvironmentCannotSelectOrRevokeTheFirstEnvironmentPolicy()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        var first = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        var selectionB = Selection() with { ProjectId = "project-b", RunId = "run-b" };
        var projectsB = new FakeProjectsConfigClient(
            AuthorizationContext("project-b", "run-b"),
            selectionB);
        var requestB = ApplyRequest("environment-a", "project-b", "run-b");
        var second = await Manager(
            projectsB,
            store,
            new FakeEnvironmentLifecycleStore(requestB.Fence)).ApplyAndVerifyAsync(
                Caller(),
                requestB,
                CancellationToken.None);

        Assert.True(first.ReadyForDispatch);
        Assert.True(second.ReadyForDispatch);
        Assert.Equal(2, store.Resources.Count);
        Assert.NotEqual(
            store.Resources.Values.First().Spec.EndpointSelector.MatchLabels["agentweaver.dev/environment-owner"],
            store.Resources.Values.Last().Spec.EndpointSelector.MatchLabels["agentweaver.dev/environment-owner"]);
        Assert.Equal(
            store.Resources.Values.First().Spec.EndpointSelector.MatchLabels["agentweaver.dev/environment-id"],
            store.Resources.Values.Last().Spec.EndpointSelector.MatchLabels["agentweaver.dev/environment-id"]);
    }

    [Fact]
    public async Task StaleGenerationCallbackCannotReplaceOrRevokeTheNewPolicy()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        Assert.True((await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(), CancellationToken.None)).ReadyForDispatch);
        Assert.True((await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(generation: 2, expectedPrevious: 1), CancellationToken.None)).ReadyForDispatch);

        var staleApply = await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(generation: 2, expectedPrevious: 0), CancellationToken.None);
        var staleRevoke = await manager.RevokeAsync(
            Caller(), ApplyRequest(generation: 1), CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);

        Assert.False(staleApply.ReadyForDispatch);
        Assert.Equal("stale_policy_generation", staleApply.FailureCode);
        Assert.False(staleRevoke.Revoked);
        Assert.Equal("stale_policy_generation", staleRevoke.FailureCode);
        Assert.Equal("2", policy.Metadata.Annotations[CiliumEgressPolicyAdapter.IntentGenerationAnnotation]);
    }

    [Fact]
    public async Task ApplyFailureAndUnverifiedReadbackNeverMarkTheEnvironmentReady()
    {
        var failingProjects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var failingStore = new FakeCiliumPolicyResourceStore { FailCreate = true };
        var failed = await Manager(failingProjects, failingStore)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        Assert.False(failed.ReadyForDispatch);
        Assert.Equal("upstream_unavailable", failed.FailureCode);

        var unreadableProjects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var unreadableStore = new FakeCiliumPolicyResourceStore { HideReads = true };
        var unreadable = await Manager(unreadableProjects, unreadableStore)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        Assert.False(unreadable.ReadyForDispatch);
        Assert.Equal("policy_generation_unverified", unreadable.FailureCode);
        Assert.False(unreadable.AppliedState!.ObjectVerified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureIsSurfacedWhenOwnerCannotRecordCompletion(bool revoke)
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var provider = new FakeCiliumPolicyResourceStore { FailCreate = true };
        var lifecycle = new FakeEnvironmentLifecycleStore(ApplyRequest().Fence)
        {
            FailCompletion = true
        };
        var manager = Manager(projects, provider, lifecycle);

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            if (revoke)
                await manager.RevokeAsync(Caller(), ApplyRequest(), CancellationToken.None);
            else
                await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        });

        Assert.Contains("could not record its outcome", exception.Message);
        Assert.Collection(
            exception.InnerExceptions,
            providerException => Assert.IsType<HttpRequestException>(providerException),
            ownerException => Assert.Equal(
                "environment_effect_store_unavailable",
                Assert.IsType<EnvironmentLifecycleException>(ownerException).Code));
        Assert.Equal(EnvironmentNetworkEffectState.Reserved, lifecycle.LastEffect!.State);
    }

    [Fact]
    public async Task EveryPrivilegedOperationReReadsAuthorizationAndUsesOnlyRequiredSelectionReads()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var manager = Manager(projects, new FakeCiliumPolicyResourceStore());

        var applied = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var verified = await manager.VerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var revoked = await manager.RevokeAsync(
            Caller(),
            ApplyRequest(generation: 2, expectedPrevious: 1),
            CancellationToken.None);

        Assert.True(applied.ReadyForDispatch);
        Assert.True(verified.ReadyForDispatch);
        Assert.True(revoked.Revoked);
        Assert.Equal(2, revoked.AppliedState!.AppliedIntentGeneration);
        Assert.True(revoked.AppliedState.Revoked);
        Assert.Equal(10, projects.AuthorizationReads);
        Assert.Equal(2, projects.SelectionReads);
    }

    [Fact]
    public async Task RevocationTombstoneFencesLateApplyCallbacks()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        Assert.True((await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(), CancellationToken.None)).ReadyForDispatch);

        var revoked = await manager.RevokeAsync(
            Caller(),
            ApplyRequest(generation: 2, expectedPrevious: 1),
            CancellationToken.None);
        var policy = Assert.Single(store.Resources.Values);
        var lateSameGenerationApply = await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(generation: 2, expectedPrevious: 1), CancellationToken.None);
        var lateOlderApply = await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(generation: 1, expectedPrevious: 0), CancellationToken.None);

        Assert.True(revoked.Revoked);
        Assert.True(revoked.AppliedState!.ObjectVerified);
        Assert.True(revoked.AppliedState.Revoked);
        Assert.Empty(policy.Spec.Egress);
        Assert.Equal(new CiliumEgressRuleDocument(), policy.Spec.EgressDeny!.Value.Single());
        Assert.Equal("true", policy.Metadata.Annotations[CiliumEgressPolicyAdapter.IntentRevokedAnnotation]);
        Assert.False(lateSameGenerationApply.ReadyForDispatch);
        Assert.Equal("stale_policy_generation", lateSameGenerationApply.FailureCode);
        Assert.False(lateOlderApply.ReadyForDispatch);
        Assert.Equal("stale_generation", lateOlderApply.FailureCode);
    }

    [Fact]
    public async Task RevokingAbsentPolicyWritesFenceBeforeReturningSuccess()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);

        var revoked = await manager.RevokeAsync(
            Caller(),
            ApplyRequest(generation: 1, expectedPrevious: 0),
            CancellationToken.None);
        var lateApply = await manager.ApplyAndVerifyAsync(
            Caller(), ApplyRequest(), CancellationToken.None);

        Assert.True(revoked.Revoked);
        Assert.True(revoked.AppliedState!.Revoked);
        Assert.False(lateApply.ReadyForDispatch);
        Assert.Equal("stale_policy_generation", lateApply.FailureCode);
        Assert.Single(store.Resources);
    }

    [Fact]
    public async Task RevokingAbsentPolicyRejectsNonInitialGenerationWithoutMutation()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);

        var revoked = await manager.RevokeAsync(
            Caller(),
            ApplyRequest(generation: 2, expectedPrevious: 1),
            CancellationToken.None);

        Assert.False(revoked.Revoked);
        Assert.Equal("stale_policy_generation", revoked.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task RevocationDoesNotDependOnTheCurrentSelectionBeingExpressible()
    {
        var l7Rule = Fqdn("mcp.example.com") with { Purpose = NetworkEgressPurpose.RemoteMcp };
        var rules = ImmutableArray.Create(l7Rule, Dns(EgressProtocol.Tcp), Dns(EgressProtocol.Udp));
        var selection = Selection() with
        {
            EgressBaseline = rules,
            RequiredEgress = rules,
            EgressAllowlist = rules
        };
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(canReadSelection: false, canWrite: true),
            selection);
        var store = new FakeCiliumPolicyResourceStore();

        var revoked = await Manager(projects, store).RevokeAsync(
            Caller(),
            ApplyRequest(generation: 1, expectedPrevious: 0),
            CancellationToken.None);

        Assert.True(revoked.Revoked, revoked.FailureMessage);
        Assert.True(revoked.AppliedState!.Revoked);
        Assert.Equal(0, projects.SelectionReads);
    }

    [Fact]
    public async Task CallerWithMismatchedRunBindingCannotReachKubernetes()
    {
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(
                runId: "different-run",
                canWrite: false,
                boundProjectId: ProjectId,
                boundRunId: "different-run"),
            Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("authorization_context_mismatch", result.FailureCode);
        Assert.Equal(0, projects.SelectionReads);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task EnvironmentOwnerTenantMustMatchTheFreshProjectsContext()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var request = ApplyRequest() with
        {
            Fence = new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity("tenant-b", ProjectId, RunId, EnvironmentId),
                lifecycleGeneration: 1)
        };

        var result = await Manager(projects, store, new FakeEnvironmentLifecycleStore(request.Fence))
            .ApplyAndVerifyAsync(Caller(), request, CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("authorization_context_mismatch", result.FailureCode);
        Assert.Equal(0, projects.SelectionReads);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task ProjectWritePermissionDoesNotImplyRunSelectionAccess()
    {
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(canReadSelection: false, canWrite: true),
            Selection());
        var store = new FakeCiliumPolicyResourceStore();

        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("run_selection_not_authorized", result.FailureCode);
        Assert.Equal(1, projects.AuthorizationReads);
        Assert.Equal(0, projects.SelectionReads);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task ProjectReadPermissionDoesNotAuthorizeNetworkPolicyMutation()
    {
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(canReadProjects: true, canReadSelection: false, canWrite: false),
            Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var manager = Manager(projects, store);
        var applied = await manager.ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var verified = await manager.VerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);
        var revoked = await manager.RevokeAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(applied.ReadyForDispatch);
        Assert.Equal("project_write_not_authorized", applied.FailureCode);
        Assert.False(verified.ReadyForDispatch);
        Assert.Equal("project_write_not_authorized", verified.FailureCode);
        Assert.False(revoked.Revoked);
        Assert.Equal("project_write_not_authorized", revoked.FailureCode);
        Assert.Equal(0, projects.SelectionReads);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task UnboundProjectAndSelectionGrantsAreNarrowedToTheRequestedEnvironmentRun()
    {
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(boundProjectId: null, boundRunId: null),
            Selection());
        var store = new FakeCiliumPolicyResourceStore();

        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.True(result.ReadyForDispatch, result.FailureMessage);
        Assert.Equal(4, projects.AuthorizationReads);
        Assert.Equal(1, projects.SelectionReads);
    }

    [Fact]
    public async Task AuthorizationRevocationAfterSelectionPreventsKubernetesMutation()
    {
        var projects = new FakeProjectsConfigClient(
            [
                AuthorizationContext(),
                AuthorizationContext(canReadSelection: true, canWrite: false)
            ],
            Selection());
        var store = new FakeCiliumPolicyResourceStore();

        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("authorization_changed", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task AuthorizationChangeAfterApplyReadbackPreventsPinAndDispatch()
    {
        var projects = new FakeProjectsConfigClient(
            [
                AuthorizationContext(),
                AuthorizationContext(),
                AuthorizationContext(),
                AuthorizationContext(canReadSelection: true, canWrite: false)
            ],
            Selection());
        var store = new FakeCiliumPolicyResourceStore();

        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("authorization_changed", result.FailureCode);
        Assert.True(result.AppliedState!.ObjectVerified);
        Assert.Null(result.Binding);
        Assert.Single(store.Resources);
    }

    [Fact]
    public async Task AuthorizationChangeAfterRevokeReadbackReturnsObservedTombstoneButRejectsCallback()
    {
        var projects = new FakeProjectsConfigClient(
            [
                AuthorizationContext(canReadSelection: false, canWrite: true),
                AuthorizationContext(canReadSelection: false, canWrite: true),
                AuthorizationContext(canReadSelection: false, canWrite: false)
            ],
            Selection());
        var store = new FakeCiliumPolicyResourceStore();

        var result = await Manager(projects, store)
            .RevokeAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.True(result.Revoked);
        Assert.Equal("authorization_changed", result.FailureCode);
        Assert.True(result.AppliedState!.ObjectVerified);
        Assert.True(result.AppliedState.Revoked);
        Assert.Equal(0, projects.SelectionReads);
    }

    [Fact]
    public async Task SelectedCiliumOptionsRevisionMustMatchBeforePolicyApply()
    {
        var candidate = CiliumCandidate() with { OptionsRevision = "stale-options-revision" };
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(),
            Selection([candidate]));
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("provider_options_mismatch", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task UnsupportedSelectedL3CapabilityFailsBeforePolicyApply()
    {
        var candidate = CiliumCandidate() with
        {
            RequiredCapabilities = [CiliumEgressCapabilities.L3L4, "networkpolicy.unsupported"]
        };
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(),
            Selection([candidate]));
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("network_provider_resolution_failed", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task ProviderMustAdvertiseCapabilitiesRequiredByTheCompiledIntentBeforeApply()
    {
        var candidate = CiliumCandidate() with
        {
            AdvertisedCapabilities = [CiliumEgressCapabilities.L3L4],
            RequiredCapabilities = [CiliumEgressCapabilities.L3L4]
        };
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(),
            Selection([candidate]));
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("network_provider_capability_unavailable", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Theory]
    [InlineData(NetworkEgressPurpose.RemoteMcp)]
    [InlineData(NetworkEgressPurpose.PublicHttps)]
    public async Task CiliumDoesNotPretendToProvideL7ForPublicHttpsOrRemoteMcp(
        NetworkEgressPurpose purpose)
    {
        var l7Rule = Fqdn("public.example.com") with { Purpose = purpose };
        var rules = ImmutableArray.Create(l7Rule, Dns(EgressProtocol.Tcp), Dns(EgressProtocol.Udp));
        var selection = Selection() with
        {
            EgressBaseline = rules,
            RequiredEgress = rules,
            EgressAllowlist = rules
        };
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), selection);
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("l7_provider_required", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task OptionalL7CandidateIsRejectedRatherThanSilentlyIgnored()
    {
        var selectedProviders = ImmutableArray.Create(
            CiliumCandidate(),
            CiliumCandidate() with
            {
                ProviderId = "gateway",
                Layer = NetworkPolicyLayer.L7,
                RequiredCapabilities = ["networkpolicy.http"]
            });
        var projects = new FakeProjectsConfigClient(
            AuthorizationContext(),
            Selection(selectedProviders));
        var store = new FakeCiliumPolicyResourceStore();
        var result = await Manager(projects, store)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("l7_provider_unavailable", result.FailureCode);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task LifecycleSnapshotUsesFreshReadProjectsWithoutRunSelectionOrWritePermission()
    {
        var fence = ApplyRequest().Fence;
        var readOnly = AuthorizationContext(canReadProjects: true, canReadSelection: false, canWrite: false);
        var projects = new FakeProjectsConfigClient([readOnly, readOnly, readOnly], Selection());
        var lifecycle = new FakeEnvironmentLifecycleStore(fence);

        var snapshot = await Manager(projects, new FakeCiliumPolicyResourceStore(), lifecycle)
            .InspectLifecycleAsync(
                Caller(),
                fence.Owner.ProjectId,
                fence.Owner.RunId,
                fence.Owner.EnvironmentId,
                CancellationToken.None);

        Assert.Equal(new EnvironmentLifecycleSnapshot(fence, EnvironmentLifecycleState.Active), snapshot);
        Assert.Equal(3, projects.AuthorizationReads);
        Assert.Equal(0, projects.SelectionReads);
        Assert.Equal(1, lifecycle.OwnerSnapshotReads);
    }

    [Fact]
    public async Task LifecycleSnapshotRejectsAuthorityChangesAfterReadingOwner()
    {
        var fence = ApplyRequest().Fence;
        var readOnly = AuthorizationContext(canReadProjects: true, canReadSelection: false, canWrite: false);
        var changed = readOnly with { MembershipRevision = readOnly.MembershipRevision + 1 };
        var projects = new FakeProjectsConfigClient([readOnly, readOnly, changed], Selection());
        var lifecycle = new FakeEnvironmentLifecycleStore(fence);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            Manager(projects, new FakeCiliumPolicyResourceStore(), lifecycle)
                .InspectLifecycleAsync(
                    Caller(),
                    fence.Owner.ProjectId,
                    fence.Owner.RunId,
                    fence.Owner.EnvironmentId,
                    CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(1, lifecycle.OwnerSnapshotReads);
    }

    [Fact]
    public async Task LifecycleSnapshotRejectsMismatchedProjectBindingBeforeOwnerRead()
    {
        var fence = ApplyRequest().Fence;
        var authorization = AuthorizationContext(
            canReadProjects: true,
            canReadSelection: false,
            canWrite: false,
            boundProjectId: "another-project");
        var projects = new FakeProjectsConfigClient(authorization, Selection());
        var lifecycle = new FakeEnvironmentLifecycleStore(fence);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            Manager(projects, new FakeCiliumPolicyResourceStore(), lifecycle)
                .InspectLifecycleAsync(
                    Caller(),
                    fence.Owner.ProjectId,
                    fence.Owner.RunId,
                    fence.Owner.EnvironmentId,
                    CancellationToken.None));

        Assert.Equal("authorization_context_mismatch", exception.Code);
        Assert.Equal(1, projects.AuthorizationReads);
        Assert.Equal(0, lifecycle.OwnerSnapshotReads);
    }

    [Fact]
    public async Task UnknownEnvironmentCannotReachProjectsOrKubernetes()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var lifecycle = new FakeEnvironmentLifecycleStore();
        var result = await Manager(projects, store, lifecycle)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Equal("environment_unknown", result.FailureCode);
        Assert.Equal(0, projects.AuthorizationReads);
        Assert.Empty(store.Resources);
    }

    [Fact]
    public async Task StaleOwnerCompletionDoesNotPinOrReportReadiness()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var store = new FakeCiliumPolicyResourceStore();
        var lifecycle = new FakeEnvironmentLifecycleStore(ApplyRequest().Fence)
        {
            CompleteAsStale = true
        };
        var result = await Manager(projects, store, lifecycle)
            .ApplyAndVerifyAsync(Caller(), ApplyRequest(), CancellationToken.None);

        Assert.False(result.ReadyForDispatch);
        Assert.Null(result.Binding);
        Assert.Equal("environment_fence_changed", result.FailureCode);
        Assert.True(result.AppliedState!.ObjectVerified);
        Assert.Single(store.Resources);
    }

    [Fact]
    public async Task OwnerReconciliationVerifiesStaleCompletionBeforePinningCurrentFence()
    {
        var projects = new FakeProjectsConfigClient(AuthorizationContext(), Selection());
        var ciliumStore = new FakeCiliumPolicyResourceStore();
        var request = ApplyRequest();
        var lifecycle = new FakeEnvironmentLifecycleStore(request.Fence)
        {
            CompleteAsStale = true
        };
        var manager = Manager(projects, ciliumStore, lifecycle);

        var first = await manager.ApplyAndVerifyAsync(Caller(), request, CancellationToken.None);
        Assert.False(first.ReadyForDispatch);
        Assert.Null(first.Binding);
        var unresolved = Assert.IsType<EnvironmentNetworkEffectReservation>(lifecycle.LastEffect);
        Assert.Equal(EnvironmentNetworkEffectState.ReconciliationRequired, unresolved.State);

        var currentFence = new EnvironmentGenerationFence(
            request.Fence.Owner,
            request.Fence.LifecycleGeneration + 1);
        var reconciled = await manager.ReconcileAsync(
            Caller(),
            new ReconcileEnvironmentEgressRequest(currentFence, unresolved.OperationId),
            CancellationToken.None);

        Assert.True(reconciled.ReadyForDispatch);
        Assert.NotNull(reconciled.Binding);
        Assert.True(reconciled.AppliedState!.ObjectVerified);
        Assert.Equal(EnvironmentNetworkEffectState.Reconciled, lifecycle.LastEffect!.State);
        Assert.Equal(1, reconciled.Binding.AppliedIntentGeneration);
    }

    private sealed class FakeProjectsConfigClient(
        IEnumerable<ProjectAuthorizationContextResponse> authorizations,
        EffectiveNetworkPolicySelection selection) : IProjectsConfigClient
    {
        private readonly Queue<ProjectAuthorizationContextResponse> _authorizations = new(authorizations);
        private ProjectAuthorizationContextResponse? _lastAuthorization;

        public FakeProjectsConfigClient(
            ProjectAuthorizationContextResponse authorization,
            EffectiveNetworkPolicySelection selection)
            : this([authorization], selection)
        {
        }

        public int AuthorizationReads { get; private set; }
        public int SelectionReads { get; private set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            AuthorizationReads++;
            if (_authorizations.TryDequeue(out var authorization))
                _lastAuthorization = authorization;
            return Task.FromResult(_lastAuthorization ??
                throw new InvalidOperationException("At least one authorization context is required."));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            SelectionReads++;
            return Task.FromResult(selection);
        }
    }

    private sealed class FakeEnvironmentLifecycleStore : IEnvironmentLifecycleStore
    {
        private readonly Dictionary<(string Tenant, string Project, string Run, string Environment), EnvironmentLifecycleSnapshot>
            _owners = [];
        private readonly Dictionary<Guid, EnvironmentNetworkEffectReservation> _effects = [];
        private readonly Dictionary<((string Tenant, string Project, string Run, string Environment) Owner, string Key), Guid>
            _effectKeys = [];

        public FakeEnvironmentLifecycleStore(params EnvironmentGenerationFence[] registeredFences)
        {
            foreach (var fence in registeredFences)
                _owners[Key(fence.Owner)] = new EnvironmentLifecycleSnapshot(fence, EnvironmentLifecycleState.Active);
        }

        public bool CompleteAsStale { get; init; }
        public bool FailCompletion { get; init; }
        public int OwnerSnapshotReads { get; private set; }
        public EnvironmentNetworkEffectReservation? LastEffect { get; private set; }

        public Task<EnvironmentLifecycleSnapshot?> GetAsync(
            EnvironmentOwnerIdentity owner,
            CancellationToken cancellationToken)
        {
            OwnerSnapshotReads++;
            return Task.FromResult(_owners.GetValueOrDefault(Key(owner)));
        }

        public Task<EnvironmentLifecycleSnapshot> RequireActiveAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            if (!_owners.TryGetValue(Key(fence.Owner), out var snapshot))
                throw new EnvironmentLifecycleException("environment_unknown", "Unknown Environment.");
            if (snapshot.State != EnvironmentLifecycleState.Active)
                throw new EnvironmentLifecycleException("environment_released", "Released Environment.");
            if (snapshot.Fence != fence)
                throw new EnvironmentLifecycleException("environment_fence_stale", "Stale Environment fence.");
            return Task.FromResult(snapshot);
        }

        public Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
            EnvironmentLifecycleTransitionRequest request,
            CancellationToken cancellationToken)
        {
            var key = Key(request.Owner);
            var expectedGeneration = _owners.TryGetValue(key, out var current)
                ? current.Fence.LifecycleGeneration
                : 0;
            if (expectedGeneration != request.ExpectedLifecycleGeneration)
                throw new EnvironmentLifecycleException("environment_lifecycle_conflict", "Stale lifecycle transition.");
            var snapshot = new EnvironmentLifecycleSnapshot(
                new EnvironmentGenerationFence(request.Owner, expectedGeneration + 1),
                request.TargetState);
            _owners[key] = snapshot;
            return Task.FromResult(new EnvironmentLifecycleTransitionResult(snapshot, false));
        }

        public Task<EnvironmentNetworkEffectReservation> ReserveNetworkEffectAsync(
            EnvironmentGenerationFence fence,
            string resourceId,
            long policyGeneration,
            long expectedPreviousPolicyGeneration,
            EnvironmentNetworkEffectKind kind,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            _ = RequireActiveAsync(fence, cancellationToken);
            var idempotency = (Key(fence.Owner), idempotencyKey);
            if (_effectKeys.TryGetValue(idempotency, out var priorOperationId))
            {
                var prior = _effects[priorOperationId];
                if (prior.Fence != fence ||
                    !string.Equals(prior.ResourceId, resourceId, StringComparison.Ordinal) ||
                    prior.PolicyGeneration != policyGeneration ||
                    prior.ExpectedPreviousPolicyGeneration != expectedPreviousPolicyGeneration ||
                    prior.Kind != kind)
                    throw new EnvironmentLifecycleException(
                        "environment_effect_idempotency_conflict",
                        "The effect idempotency key was reused for a different operation.");
                var replayed = prior with { Replayed = true };
                LastEffect = replayed;
                return Task.FromResult(replayed);
            }

            var resourceEffects = _effects.Values.Where(effect =>
                effect.Fence.Owner == fence.Owner &&
                string.Equals(effect.ResourceId, resourceId, StringComparison.Ordinal)).ToArray();
            if (resourceEffects.Any(effect => effect.State is EnvironmentNetworkEffectState.Reserved or
                EnvironmentNetworkEffectState.ReconciliationRequired))
                throw new EnvironmentLifecycleException(
                    "environment_effect_reconciliation_required",
                    "A pending or unresolved provider effect blocks this network-policy resource.");
            var currentGeneration = resourceEffects
                .Where(effect => effect.State is EnvironmentNetworkEffectState.Completed or
                    EnvironmentNetworkEffectState.Reconciled)
                .MaxBy(effect => effect.PolicyGeneration)?.PolicyGeneration ?? 0;
            if (currentGeneration != expectedPreviousPolicyGeneration)
                throw new EnvironmentLifecycleException(
                    "stale_policy_generation",
                    "The expected previous policy generation does not match the owner's latest verified generation.");

            var reservation = new EnvironmentNetworkEffectReservation(
                Guid.NewGuid(),
                fence,
                resourceId,
                policyGeneration,
                expectedPreviousPolicyGeneration,
                kind,
                EnvironmentNetworkEffectState.Reserved,
                false);
            _effects[reservation.OperationId] = reservation;
            _effectKeys[idempotency] = reservation.OperationId;
            LastEffect = reservation;
            return Task.FromResult(reservation);
        }

        public Task<EnvironmentNetworkEffectReservation> GetNetworkEffectAsync(
            Guid operationId,
            EnvironmentGenerationFence currentFence,
            CancellationToken cancellationToken)
        {
            _ = RequireActiveAsync(currentFence, cancellationToken);
            if (!_effects.TryGetValue(operationId, out var reservation) ||
                reservation.Fence.Owner != currentFence.Owner)
                throw new EnvironmentLifecycleException("environment_effect_unknown", "Unknown effect.");
            return Task.FromResult(reservation);
        }

        public Task RequireVerifiedNetworkPolicyGenerationAsync(
            EnvironmentGenerationFence fence,
            string resourceId,
            long policyGeneration,
            CancellationToken cancellationToken)
        {
            _ = RequireActiveAsync(fence, cancellationToken);
            var effects = _effects.Values.Where(effect =>
                effect.Fence.Owner == fence.Owner &&
                string.Equals(effect.ResourceId, resourceId, StringComparison.Ordinal)).ToArray();
            if (effects.Any(effect => effect.State is EnvironmentNetworkEffectState.Reserved or
                EnvironmentNetworkEffectState.ReconciliationRequired))
                throw new EnvironmentLifecycleException(
                    "environment_effect_reconciliation_required",
                    "A pending or unresolved provider effect blocks network-policy verification.");
            if (effects.Where(effect => effect.State is EnvironmentNetworkEffectState.Completed or
                    EnvironmentNetworkEffectState.Reconciled)
                .MaxBy(effect => effect.PolicyGeneration)?.PolicyGeneration != policyGeneration)
                throw new EnvironmentLifecycleException(
                    "environment_policy_generation_untracked",
                    "The requested policy generation is not the Environment owner's latest verified generation.");
            return Task.CompletedTask;
        }

        public Task<EnvironmentNetworkEffectReservation> CompleteNetworkEffectAsync(
            Guid operationId,
            EnvironmentGenerationFence fence,
            bool effectMayHaveApplied,
            bool exactGenerationVerified,
            CancellationToken cancellationToken)
        {
            if (FailCompletion)
                throw new EnvironmentLifecycleException(
                    "environment_effect_store_unavailable",
                    "The owner ledger is unavailable.");
            if (!_effects.TryGetValue(operationId, out var reservation) || reservation.Fence != fence)
                throw new EnvironmentLifecycleException("environment_effect_unknown", "Unknown effect.");
            var state = CompleteAsStale
                ? EnvironmentNetworkEffectState.ReconciliationRequired
                : effectMayHaveApplied
                    ? exactGenerationVerified
                        ? EnvironmentNetworkEffectState.Completed
                        : EnvironmentNetworkEffectState.ReconciliationRequired
                    : EnvironmentNetworkEffectState.Failed;
            reservation = reservation with { State = state };
            _effects[operationId] = reservation;
            if (CompleteAsStale &&
                _owners.TryGetValue(Key(fence.Owner), out var currentOwner) &&
                currentOwner.Fence == fence)
            {
                _owners[Key(fence.Owner)] = currentOwner with
                {
                    Fence = new EnvironmentGenerationFence(
                        fence.Owner,
                        fence.LifecycleGeneration + 1)
                };
            }
            LastEffect = reservation;
            return Task.FromResult(reservation);
        }

        public Task<EnvironmentNetworkEffectReservation> MarkNetworkEffectReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence currentFence,
            EnvironmentNetworkEffectObservation observation,
            CancellationToken cancellationToken)
        {
            _ = RequireActiveAsync(currentFence, cancellationToken);
            if (!_effects.TryGetValue(operationId, out var reservation) ||
                reservation.Fence.Owner != currentFence.Owner)
                throw new EnvironmentLifecycleException("environment_effect_unknown", "Unknown effect.");
            if (!observation.ObjectVerified ||
                observation.AppliedIntentGeneration != reservation.PolicyGeneration ||
                observation.Revoked != (reservation.Kind == EnvironmentNetworkEffectKind.Revoke))
                throw new EnvironmentLifecycleException(
                    "environment_effect_observation_mismatch",
                    "Invalid provider observation.");
            reservation = reservation with { State = EnvironmentNetworkEffectState.Reconciled };
            _effects[operationId] = reservation;
            LastEffect = reservation;
            return Task.FromResult(reservation);
        }

        public Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(null);

        public Task<EnvironmentWorkspaceVolumeSnapshot> CreateWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            System.Text.Json.JsonElement specification,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeProvisionAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeProvisionAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeProvisionReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReplaceAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReplaceAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReplaceReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeBindAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeBindAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeBindReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeUnbindAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeUnbindAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeUnbindReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeAttachReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeDetachReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeFlushAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            long nextDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeFlushAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            ProviderResourceRef? providerResource,
            bool effectVerified,
            bool durableFlushVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeFlushReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReleaseAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            long expectedTransitionRevision,
            long expectedResourceGeneration,
            long expectedDataGeneration,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReleaseAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            bool effectMayHaveApplied,
            bool releaseVerified,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReleaseReconciledAsync(
            Guid operationId,
            EnvironmentGenerationFence environmentFence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static (string Tenant, string Project, string Run, string Environment) Key(
            EnvironmentOwnerIdentity owner) =>
            (owner.TenantId, owner.ProjectId, owner.RunId, owner.EnvironmentId);
    }

    private sealed class FakeCiliumPolicyResourceStore : ICiliumPolicyResourceStore
    {
        private readonly Dictionary<(string Namespace, string Name), CiliumNetworkPolicyDocument> _resources = [];
        private long _version;

        public bool FailCreate { get; init; }
        public bool HideReads { get; init; }
        public IReadOnlyDictionary<(string Namespace, string Name), CiliumNetworkPolicyDocument> Resources => _resources;

        public void Update(CiliumNetworkPolicyDocument policy) =>
            _resources[(policy.Metadata.Namespace, policy.Metadata.Name)] = policy;

        public Task<CiliumNetworkPolicyDocument?> GetAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken)
        {
            if (HideReads)
                return Task.FromResult<CiliumNetworkPolicyDocument?>(null);
            _resources.TryGetValue((@namespace, name), out var policy);
            return Task.FromResult(policy);
        }

        public Task<CiliumNetworkPolicyDocument> CreateAsync(
            CiliumNetworkPolicyDocument policy,
            CancellationToken cancellationToken)
        {
            if (FailCreate)
                throw new HttpRequestException("kubernetes api unavailable");
            var key = (policy.Metadata.Namespace, policy.Metadata.Name);
            if (_resources.ContainsKey(key))
                throw new CiliumPolicyException("kubernetes_conflict", "Already exists.");
            var persisted = policy with
            {
                Metadata = policy.Metadata with
                {
                    ResourceVersion = NextVersion(),
                    Generation = 1
                }
            };
            _resources.Add(key, persisted);
            return Task.FromResult(persisted);
        }

        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy,
            string expectedResourceVersion,
            CancellationToken cancellationToken)
        {
            var key = (policy.Metadata.Namespace, policy.Metadata.Name);
            if (!_resources.TryGetValue(key, out var current) ||
                current.Metadata.ResourceVersion != expectedResourceVersion)
                throw new CiliumPolicyException("stale_generation", "Resource version changed.");
            var persisted = policy with
            {
                Metadata = policy.Metadata with
                {
                    ResourceVersion = NextVersion(),
                    Generation = (current.Metadata.Generation ?? 0) + 1
                }
            };
            _resources[key] = persisted;
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(
            string @namespace,
            string name,
            string expectedResourceVersion,
            CancellationToken cancellationToken)
        {
            var key = (@namespace, name);
            if (!_resources.TryGetValue(key, out var current) ||
                current.Metadata.ResourceVersion != expectedResourceVersion)
                throw new CiliumPolicyException("stale_generation", "Resource version changed.");
            _resources.Remove(key);
            return Task.CompletedTask;
        }

        private string NextVersion() => (++_version).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
