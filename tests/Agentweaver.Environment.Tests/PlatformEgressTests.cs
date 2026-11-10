using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformEgressValidationTests
{
    [Theory]
    [InlineData("api.openai.com", 443, null)]
    [InlineData("my-resource.openai.azure.com", 443, null)]
    [InlineData("api.openai.com", 80, "destination_port_not_allowed")]
    [InlineData("API.openai.com", 443, "destination_host_invalid")]
    [InlineData("*.openai.com", 443, "destination_host_invalid")]
    [InlineData("api.openai.com.", 443, "destination_host_invalid")]
    [InlineData("10.0.0.1", 443, "destination_host_invalid")]
    [InlineData("user@api.openai.com", 443, "destination_host_invalid")]
    [InlineData("localhost", 443, "destination_host_invalid")]
    [InlineData("kubernetes.default.svc", 443, "destination_host_reserved")]
    [InlineData("db.cluster.local", 443, "destination_host_reserved")]
    [InlineData("contoso.vault.azure.net", 443, "destination_host_protected")]
    [InlineData("login.microsoftonline.com", 443, "destination_host_protected")]
    public void HostValidatorAppliesTheOneHostRule(string host, int port, string? expectedCode) =>
        Assert.Equal(expectedCode, PlatformEgressHostValidator.Validate(host, port)?.Code);

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("fd00::1", true)]
    [InlineData("10.244.0.9", true)]
    [InlineData("20.42.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void ExcludedRangesIncludeMappedAddressesAndClusterRanges(string address, bool excluded)
    {
        var ranges = PlatformEgressExcludedRanges.Create(["10.244.0.0/16"]);
        Assert.Equal(excluded, PlatformEgressExcludedRanges.Contains(ranges, IPAddress.Parse(address)));
    }

    [Fact]
    public void ExcludedRangesRejectNonCanonicalClusterRange() =>
        Assert.Throws<ArgumentException>(() => PlatformEgressExcludedRanges.Create(["10.244.0.1/16"]));

    [Fact]
    public void AllowlistNeedsExactHostAndBothRequiredCapabilities()
    {
        var publicOnly = new HashSet<string>
        {
            PlatformEgressCapabilities.PrivateRangeExclusion,
            PlatformEgressCapabilities.Status,
            PlatformEgressCapabilities.PublicHttps,
        };
        Assert.True(PlatformEgressCapabilities.CanApply(publicOnly, PlatformEgressMode.PublicHttps));
        Assert.False(PlatformEgressCapabilities.CanApply(publicOnly, PlatformEgressMode.Allowlist));
        publicOnly.Remove(PlatformEgressCapabilities.Status);
        Assert.False(PlatformEgressCapabilities.CanApply(publicOnly, PlatformEgressMode.PublicHttps));
    }

    [Fact]
    public void IntentHashIgnoresOrderAndDuplicatesButNotMode()
    {
        var a = new PlatformEgressDestination("a.example.com", 443);
        var b = new PlatformEgressDestination("b.example.com", 443);
        Assert.Equal(
            PlatformEgressPolicy.ComputeIntentHash(PlatformEgressMode.Allowlist, [a, b]),
            PlatformEgressPolicy.ComputeIntentHash(PlatformEgressMode.Allowlist, [b, a, b]));
        Assert.NotEqual(
            PlatformEgressPolicy.ComputeIntentHash(PlatformEgressMode.Allowlist, [a]),
            PlatformEgressPolicy.ComputeIntentHash(PlatformEgressMode.PublicHttps, [a]));
    }

    [Fact]
    public void SnapshotValidationRejectsIncompleteSourceAndInvalidHosts()
    {
        Assert.Null(PlatformEgressFixtures.Snapshot().Validate());
        Assert.NotNull((PlatformEgressFixtures.Snapshot() with { SourceHash = "ABC" }).Validate());
        Assert.NotNull((PlatformEgressFixtures.Snapshot() with { Checkpoint = " " }).Validate());
        Assert.Contains(
            "destination_host_protected",
            PlatformEgressFixtures.Snapshot(hosts: ["contoso.vault.azure.net"]).Validate());
    }
}

/// <summary>
/// Conformance kit that every <see cref="IPlatformEgressProvider"/> must pass. A provider test class
/// derives from it and supplies the provider and its evidence for the excluded ranges.
/// </summary>
public abstract class PlatformEgressProviderConformanceTests
{
    /// <summary>Creates a provider whose platform policy object exists and has no applied generation.</summary>
    protected abstract IPlatformEgressProvider CreateProvider(bool exactHost);

    /// <summary>Returns the ranges that the provider denies for AgentHost workloads after the last apply.</summary>
    protected abstract Task<IReadOnlySet<string>> ReadDeniedRangesAsync();

    [Fact]
    public void AdvertisesRequiredCapabilities()
    {
        var capabilities = CreateProvider(exactHost: false).Descriptor.AdvertisedCapabilities;
        Assert.Contains(PlatformEgressCapabilities.PrivateRangeExclusion, capabilities);
        Assert.Contains(PlatformEgressCapabilities.Status, capabilities);
        Assert.Equal(ProviderSeam.NetworkPolicy, CreateProvider(exactHost: false).Descriptor.Seam);
    }

    [Fact]
    public async Task ModeWithoutCapabilityFailsAndKeepsThePriorPolicy()
    {
        var provider = CreateProvider(exactHost: false);
        Assert.DoesNotContain(PlatformEgressCapabilities.ExactHost, provider.Descriptor.AdvertisedCapabilities);
        var first = Policy(1, PlatformEgressMode.PublicHttps);
        await provider.ApplyAsync(first, CancellationToken.None);

        var refused = await provider.ApplyAsync(Policy(2, PlatformEgressMode.Allowlist), CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, refused.State);
        Assert.Equal("capability_unavailable", refused.Message);
        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, status.Generation);
        Assert.True(status.Matches(first.IntentHash, first.SourceHash));
    }

    [Theory]
    [InlineData(PlatformEgressMode.PublicHttps)]
    [InlineData(PlatformEgressMode.Allowlist)]
    public async Task StatusReportsTheAppliedGenerationAndHashes(PlatformEgressMode mode)
    {
        var provider = CreateProvider(exactHost: true);
        var policy = Policy(1, mode);

        var applied = await provider.ApplyAsync(policy, CancellationToken.None);
        var status = await provider.GetStatusAsync(CancellationToken.None);

        foreach (var observed in new[] { applied, status })
        {
            Assert.Equal(1, observed.Generation);
            Assert.True(observed.Matches(policy.IntentHash, policy.SourceHash));
            if (!provider.Descriptor.AdvertisedCapabilities.Contains(PlatformEgressCapabilities.EnforcedStatus))
                Assert.Equal(PlatformEgressApplyState.Accepted, observed.State);
        }
    }

    [Fact]
    public async Task SameGenerationIsIdempotentOnlyForTheSameContent()
    {
        var provider = CreateProvider(exactHost: true);
        var policy = Policy(1, PlatformEgressMode.Allowlist);
        await provider.ApplyAsync(policy, CancellationToken.None);

        Assert.True((await provider.ApplyAsync(policy, CancellationToken.None))
            .Matches(policy.IntentHash, policy.SourceHash));
        var conflict = await provider.ApplyAsync(
            Policy(1, PlatformEgressMode.Allowlist, "other.example.com"), CancellationToken.None);
        Assert.Equal(PlatformEgressApplyState.Failed, conflict.State);
        var stale = await provider.ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);
        Assert.Equal(PlatformEgressApplyState.Failed, stale.State);
        Assert.True((await provider.GetStatusAsync(CancellationToken.None))
            .Matches(policy.IntentHash, policy.SourceHash));
    }

    [Fact]
    public async Task LowerGenerationIsRejected()
    {
        var provider = CreateProvider(exactHost: false);
        var current = Policy(2, PlatformEgressMode.PublicHttps);
        await provider.ApplyAsync(current, CancellationToken.None);

        var stale = await provider.ApplyAsync(
            Policy(1, PlatformEgressMode.PublicHttps, sourceHash: PlatformEgressFixtures.Hash("other")),
            CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, stale.State);
        Assert.True((await provider.GetStatusAsync(CancellationToken.None))
            .Matches(current.IntentHash, current.SourceHash));
    }

    [Theory]
    [InlineData(PlatformEgressMode.PublicHttps)]
    [InlineData(PlatformEgressMode.Allowlist)]
    public async Task ExcludedRangesAreDeniedInBothModes(PlatformEgressMode mode)
    {
        var provider = CreateProvider(exactHost: true);
        await provider.ApplyAsync(Policy(1, mode), CancellationToken.None);

        var denied = await ReadDeniedRangesAsync();

        Assert.All(PlatformEgressExcludedRanges.Fixed, range => Assert.Contains(range, denied));
    }

    [Fact]
    public async Task OptionalOperationsFollowTheAdvertisedCapabilities()
    {
        var provider = CreateProvider(exactHost: false);
        var capabilities = provider.Descriptor.AdvertisedCapabilities;
        if (!capabilities.Contains(PlatformEgressCapabilities.BlockedAttempts))
            Assert.Throws<NotSupportedException>(() =>
                provider.ReadFlowsAsync(null, includeAllowed: false, CancellationToken.None));
        if (!capabilities.Contains(PlatformEgressCapabilities.TlsTrust))
            Assert.Null(await provider.GetTrustBundleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoveClearsTheAppliedPolicy()
    {
        var provider = CreateProvider(exactHost: false);
        var policy = Policy(1, PlatformEgressMode.PublicHttps);
        await provider.ApplyAsync(policy, CancellationToken.None);

        await provider.RemoveAsync(CancellationToken.None);

        Assert.False((await provider.GetStatusAsync(CancellationToken.None))
            .Matches(policy.IntentHash, policy.SourceHash));
    }

    private static PlatformEgressPolicy Policy(
        long generation,
        PlatformEgressMode mode,
        string host = "api.example.com",
        string? sourceHash = null) =>
        PlatformEgressPolicy.Create(
            generation,
            mode,
            [new PlatformEgressDestination(host, 443)],
            sourceHash ?? PlatformEgressFixtures.Hash("source"));
}

public sealed class CiliumPlatformEgressConformanceTests : PlatformEgressProviderConformanceTests
{
    private readonly FakeCiliumPolicyStore _store = new();

    protected override IPlatformEgressProvider CreateProvider(bool exactHost) =>
        PlatformEgressFixtures.Cilium(_store, exactHost);

    protected override Task<IReadOnlySet<string>> ReadDeniedRangesAsync() =>
        Task.FromResult<IReadOnlySet<string>>(_store.Current!.Spec.EgressDeny!.Value
            .SelectMany(rule => rule.ToCidrSet ?? [])
            .Select(selector => selector.Cidr)
            .ToHashSet(StringComparer.Ordinal));
}

public sealed class CiliumPlatformEgressProviderTests
{
    private readonly FakeCiliumPolicyStore _store = new();

    [Fact]
    public async Task PublicHttpsRendersOneCidrRuleWithExceptionsAndDnsWithoutNameRules()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: false, ["10.244.0.0/16"]);
        await provider.ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);

        var spec = _store.Current!.Spec;
        Assert.Equal("agenthost", spec.EndpointSelector.MatchLabels["agentweaver.io/workload"]);
        Assert.Equal(2, spec.Egress.Length);
        Assert.Null(spec.Egress[0].ToPorts!.Value[0].Rules);
        var cidrs = spec.Egress[1].ToCidrSet!.Value;
        Assert.Equal(["0.0.0.0/0", "::/0"], cidrs.Select(selector => selector.Cidr));
        Assert.Contains("10.244.0.0/16", cidrs[0].Except!.Value);
        Assert.Contains("169.254.0.0/16", cidrs[0].Except!.Value);
        Assert.Contains("fc00::/7", cidrs[1].Except!.Value);
        Assert.Equal("443", spec.Egress[1].ToPorts!.Value[0].Ports[0].Port);
        Assert.Contains(spec.EgressDeny!.Value[0].ToCidrSet!.Value, selector => selector.Cidr == "10.244.0.0/16");
    }

    [Fact]
    public async Task AllowlistRendersExactDnsAndFqdnNamesOnly()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: true);
        await provider.ApplyAsync(
            Policy(1, PlatformEgressMode.Allowlist, "b.example.com", "a.example.com"),
            CancellationToken.None);

        var spec = _store.Current!.Spec;
        Assert.Equal(
            ["a.example.com", "b.example.com"],
            spec.Egress[0].ToPorts!.Value[0].Rules!.Dns.Select(rule => rule.MatchName));
        Assert.All(spec.Egress[0].ToPorts!.Value[0].Rules!.Dns, rule => Assert.Null(rule.MatchPattern));
        Assert.Equal(
            ["a.example.com", "b.example.com"],
            spec.Egress[1].ToFqDns!.Value.Select(selector => selector.MatchName));
        Assert.DoesNotContain(spec.Egress, rule => rule.ToCidrSet is not null);
    }

    [Fact]
    public async Task ApplyWritesAnnotationsAndKeepsDeployMetadata()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: false);
        var policy = Policy(1, PlatformEgressMode.PublicHttps);
        await provider.ApplyAsync(policy, CancellationToken.None);

        var metadata = _store.Current!.Metadata;
        Assert.Equal("agenthost", metadata.Labels["agentweaver.io/service"]);
        Assert.Equal("1", metadata.Annotations[CiliumPlatformEgressProvider.GenerationAnnotation]);
        Assert.Equal(policy.IntentHash, metadata.Annotations[CiliumPlatformEgressProvider.IntentHashAnnotation]);
        Assert.Equal(policy.SourceHash, metadata.Annotations[CiliumPlatformEgressProvider.SourceHashAnnotation]);
    }

    [Fact]
    public async Task MissingObjectFailsWithoutCreatingIt()
    {
        _store.Current = null;
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: false);

        var status = await provider.ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);

        Assert.Equal("policy_object_missing", status.Message);
        Assert.Null(_store.Current);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task ResourceVersionConflictFailsTheApply()
    {
        _store.ConflictOnNextReplace = true;
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: false);

        var status = await provider.ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, status.State);
        Assert.Equal("kubernetes_conflict", status.Message);
        Assert.Equal("policy_not_applied", (await provider.GetStatusAsync(CancellationToken.None)).Message);
    }

    [Fact]
    public async Task ReadbackThatDiffersFromTheAnnotationsIsFailed()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: true);
        await provider.ApplyAsync(Policy(1, PlatformEgressMode.Allowlist), CancellationToken.None);
        var current = _store.Current!;
        _store.Current = current with
        {
            Spec = current.Spec with
            {
                Egress = current.Spec.Egress.SetItem(1, current.Spec.Egress[1] with
                {
                    ToFqDns = [new CiliumFqdnSelector(MatchName: "api.example.com"), new CiliumFqdnSelector(MatchName: "extra.example.com")],
                }),
            },
        };

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, status.State);
        Assert.Equal("readback_mismatch", status.Message);
    }

    [Fact]
    public async Task PublicHttpsSpecUnderAllowlistAnnotationsIsFailed()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: true);
        await provider.ApplyAsync(Policy(1, PlatformEgressMode.Allowlist), CancellationToken.None);
        var publicStore = new FakeCiliumPolicyStore();
        await PlatformEgressFixtures.Cilium(publicStore, exactHost: true)
            .ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);
        _store.Current = _store.Current! with { Spec = publicStore.Current!.Spec };

        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressApplyState.Failed, status.State);
        Assert.Equal("readback_mismatch", status.Message);
    }

    [Fact]
    public async Task EmptyAllowlistRendersNoAllowRuleAndReadsBackWithoutTheEgressList()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: true);
        var policy = PlatformEgressPolicy.Create(
            1, PlatformEgressMode.Allowlist, [], PlatformEgressFixtures.Hash("source"));

        await provider.ApplyAsync(policy, CancellationToken.None);
        Assert.True(_store.Current!.Spec.Egress.IsDefaultOrEmpty);
        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Matches(policy.IntentHash, policy.SourceHash));
    }

    [Fact]
    public void ExactHostIsAdvertisedOnlyWithKataEvidence()
    {
        Assert.DoesNotContain(
            PlatformEgressCapabilities.ExactHost,
            PlatformEgressFixtures.Cilium(_store, exactHost: false).Descriptor.AdvertisedCapabilities);
        Assert.Contains(
            PlatformEgressCapabilities.ExactHost,
            PlatformEgressFixtures.Cilium(_store, exactHost: true).Descriptor.AdvertisedCapabilities);
    }

    [Fact]
    public async Task RemoveRestoresTheDnsOnlySeed()
    {
        var provider = PlatformEgressFixtures.Cilium(_store, exactHost: false);
        await provider.ApplyAsync(Policy(1, PlatformEgressMode.PublicHttps), CancellationToken.None);

        await provider.RemoveAsync(CancellationToken.None);

        Assert.Single(_store.Current!.Spec.Egress);
        Assert.Null(_store.Current.Spec.EgressDeny);
        Assert.False(_store.Current.Metadata.Annotations.ContainsKey(CiliumPlatformEgressProvider.GenerationAnnotation));
    }

    private static PlatformEgressPolicy Policy(long generation, PlatformEgressMode mode, params string[] hosts) =>
        PlatformEgressPolicy.Create(
            generation,
            mode,
            (hosts.Length == 0 ? ["api.example.com"] : hosts).Select(host => new PlatformEgressDestination(host, 443)),
            PlatformEgressFixtures.Hash("source"));
}

public sealed class PlatformEgressReconcilerTests
{
    private readonly FakeCiliumPolicyStore _store = new();
    private readonly FakePolicySource _source = new();

    [Fact]
    public async Task AbsentSourceFailsClosedWithoutTouchingTheProvider()
    {
        _source.Next = [null];

        var result = await Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Failed, result.Outcome);
        Assert.Equal("policy_source_unavailable", result.Code);
        Assert.Equal(0, _store.Reads + _store.Writes);
    }

    [Fact]
    public async Task UnreadableSourceFailsClosed()
    {
        _source.Throw = true;

        var result = await Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Equal("policy_source_unavailable", result.Code);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task InvalidSnapshotIsNotApplied()
    {
        _source.Next = [PlatformEgressFixtures.Snapshot(hosts: ["10.0.0.1"])];

        var result = await Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Equal("policy_source_invalid", result.Code);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task AllowlistWithoutExactHostEvidenceIsRefused()
    {
        _source.Next = [PlatformEgressFixtures.Snapshot(PlatformEgressMode.Allowlist)];

        var result = await Reconciler(exactHost: false).ReconcileAsync(CancellationToken.None);

        Assert.Equal("capability_unavailable", result.Code);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task FirstPassAppliesAndSecondPassIsConverged()
    {
        var reconciler = Reconciler();

        var first = await reconciler.ReconcileAsync(CancellationToken.None);
        var second = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Applied, first.Outcome);
        Assert.Equal(1, first.Status!.Generation);
        Assert.Equal(PlatformEgressReconcileOutcome.Converged, second.Outcome);
        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public async Task SourceOnlyChangeCreatesANewGeneration()
    {
        var reconciler = Reconciler();
        await reconciler.ReconcileAsync(CancellationToken.None);
        _source.Next = [PlatformEgressFixtures.Snapshot(sourceHash: PlatformEgressFixtures.Hash("revision-2"))];

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.Status!.Generation);
        Assert.Equal(PlatformEgressFixtures.Hash("revision-2"), result.Status.SourceHash);
        Assert.False(result.DriftCorrected);
    }

    [Fact]
    public async Task DriftIsCorrectedWithANewGeneration()
    {
        _source.Next = [PlatformEgressFixtures.Snapshot(PlatformEgressMode.Allowlist)];
        var reconciler = Reconciler(exactHost: true);
        await reconciler.ReconcileAsync(CancellationToken.None);
        var current = _store.Current!;
        _store.Current = current with
        {
            Spec = current.Spec with { Egress = current.Spec.Egress.RemoveAt(1) },
        };

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Applied, result.Outcome);
        Assert.True(result.DriftCorrected);
        Assert.Equal(2, result.Status!.Generation);
        Assert.Equal(2, _store.Current!.Spec.Egress.Length);
    }

    [Fact]
    public async Task SourceChangeBeforeApplyUsesTheCurrentSnapshot()
    {
        var revoked = PlatformEgressFixtures.Snapshot(sourceHash: PlatformEgressFixtures.Hash("revoked"));
        var current = PlatformEgressFixtures.Snapshot(sourceHash: PlatformEgressFixtures.Hash("current"));
        _source.Next = [revoked, current, current];

        var result = await Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Applied, result.Outcome);
        Assert.Equal(current.SourceHash, result.Status!.SourceHash);
        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public async Task SourceLossBeforeApplyAppliesNothing()
    {
        _source.Next = [PlatformEgressFixtures.Snapshot(), null];

        var result = await Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Equal("policy_source_unavailable", result.Code);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task ApplyFailureKeepsThePriorPolicy()
    {
        var reconciler = Reconciler();
        await reconciler.ReconcileAsync(CancellationToken.None);
        var before = _store.Current;
        _store.ConflictOnNextReplace = true;
        _source.Next = [PlatformEgressFixtures.Snapshot(sourceHash: PlatformEgressFixtures.Hash("revision-2"))];

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(PlatformEgressReconcileOutcome.Failed, result.Outcome);
        Assert.Equal("kubernetes_conflict", result.Code);
        Assert.Same(before, _store.Current);
    }

    private PlatformEgressReconciler Reconciler(bool exactHost = false) =>
        new(_source, PlatformEgressFixtures.Cilium(_store, exactHost), TimeProvider.System);

    private sealed class FakePolicySource : IPlatformEgressPolicySource
    {
        private int _reads;

        public PlatformEgressPolicySnapshot?[] Next { get; set; } = [PlatformEgressFixtures.Snapshot()];
        public bool Throw { get; set; }

        public Task<PlatformEgressPolicySnapshot?> GetAdmittedAsync(CancellationToken cancellationToken)
        {
            if (Throw)
                throw new InvalidOperationException("source down");
            return Task.FromResult(Next[Math.Min(_reads++, Next.Length - 1)]);
        }
    }
}

internal static class PlatformEgressFixtures
{
    public static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static PlatformEgressPolicySnapshot Snapshot(
        PlatformEgressMode mode = PlatformEgressMode.PublicHttps,
        string[]? hosts = null,
        string? sourceHash = null) =>
        new(
            "test",
            "1",
            sourceHash ?? Hash("source"),
            "compiler-1",
            "checkpoint-1",
            mode,
            (hosts ?? ["api.example.com"]).Select(host => new PlatformEgressDestination(host, 443)).ToImmutableArray());

    public static CiliumPlatformEgressProvider Cilium(
        ICiliumPolicyResourceStore store,
        bool exactHost,
        string[]? additionalRanges = null) =>
        new(
            store,
            new CiliumEgressProviderOptions(
                "agentweaver",
                new Version(1, 0, 0),
                1,
                "test-revision",
                ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty),
            new CiliumPlatformEgressOptions("agentweaver", exactHost, [.. additionalRanges ?? []]),
            TimeProvider.System);
}

/// <summary>In-memory Kubernetes store for the one platform object. It round-trips JSON like the API server.</summary>
internal sealed class FakeCiliumPolicyStore : ICiliumPolicyResourceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private int _version = 1;

    public FakeCiliumPolicyStore() => Current = Seed();

    public CiliumNetworkPolicyDocument? Current { get; set; }
    public bool ConflictOnNextReplace { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }

    public Task<CiliumNetworkPolicyDocument?> GetAsync(string @namespace, string name, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Current is not null && Current.Metadata.Namespace == @namespace &&
            Current.Metadata.Name == name
            ? RoundTrip(Current)
            : null);
    }

    public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The platform provider must not create the object.");

    public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
        CiliumNetworkPolicyDocument policy,
        string expectedResourceVersion,
        CancellationToken cancellationToken)
    {
        if (ConflictOnNextReplace || Current?.Metadata.ResourceVersion != expectedResourceVersion)
        {
            ConflictOnNextReplace = false;
            throw new CiliumPolicyException("kubernetes_conflict", "conflict");
        }
        Writes++;
        Current = RoundTrip(policy with
        {
            Metadata = policy.Metadata with { ResourceVersion = (++_version).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        return Task.FromResult(RoundTrip(Current));
    }

    public Task DeleteAsync(string @namespace, string name, string expectedResourceVersion, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The platform provider must not delete the object.");

    // Like the Kubernetes API, drop an empty egress list (Go omitempty).
    private static CiliumNetworkPolicyDocument RoundTrip(CiliumNetworkPolicyDocument policy)
    {
        if (policy.Spec.Egress.IsDefault)
            policy = policy with { Spec = policy.Spec with { Egress = [] } };
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(policy, JsonOptions))!;
        if (node["spec"]?["egress"] is System.Text.Json.Nodes.JsonArray { Count: 0 })
            node["spec"]!.AsObject().Remove("egress");
        return node.Deserialize<CiliumNetworkPolicyDocument>(JsonOptions)!;
    }

    private static CiliumNetworkPolicyDocument Seed() =>
        new(
            "cilium.io/v2",
            "CiliumNetworkPolicy",
            new CiliumPolicyMetadata(
                CiliumPlatformEgressProvider.PolicyName,
                "agentweaver",
                ImmutableDictionary<string, string>.Empty.Add("agentweaver.io/service", "agenthost"),
                ImmutableDictionary<string, string>.Empty,
                "1"),
            new CiliumPolicySpec(
                new CiliumEndpointSelector(
                    ImmutableDictionary<string, string>.Empty.Add("agentweaver.io/workload", "agenthost")),
                [
                    new CiliumEgressRuleDocument(
                        ToEndpoints:
                        [
                            new CiliumEndpointSelector(ImmutableDictionary<string, string>.Empty
                                .Add("k8s:io.kubernetes.pod.namespace", "kube-system")
                                .Add("k8s:k8s-app", "kube-dns")),
                        ],
                        ToPorts: [new CiliumPortRules([new CiliumPort("53", "UDP"), new CiliumPort("53", "TCP")])]),
                ]));
}
