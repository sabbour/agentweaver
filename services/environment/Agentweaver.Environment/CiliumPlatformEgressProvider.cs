using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Settings for the one platform egress object that the Cilium provider owns.</summary>
/// <param name="Namespace">Namespace of the AgentHost workloads and of the policy object.</param>
/// <param name="ExactHostVerifiedForKata">
/// True only after the #2633 spike and the install probe prove exact-host rules for Kata pods.
/// While false, the provider does not advertise <see cref="PlatformEgressCapabilities.ExactHost"/>.
/// </param>
/// <param name="AdditionalExcludedRanges">Cluster pod and service ranges in canonical CIDR form.</param>
public sealed record CiliumPlatformEgressOptions(
    string Namespace,
    bool ExactHostVerifiedForKata,
    ImmutableArray<string> AdditionalExcludedRanges);

/// <summary>
/// Applies the platform egress policy to <c>CiliumNetworkPolicy/agenthost-platform-egress</c>.
/// The deploy creates the object; this provider only reads and replaces it.
/// </summary>
public sealed class CiliumPlatformEgressProvider : IPlatformEgressProvider
{
    public const string PolicyName = "agenthost-platform-egress";
    public const string GenerationAnnotation = "agentweaver.io/egress-generation";
    public const string IntentHashAnnotation = "agentweaver.io/egress-intent-hash";
    public const string SourceHashAnnotation = "agentweaver.io/egress-source-hash";

    private static readonly ImmutableDictionary<string, string> WorkloadLabels =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
            [new KeyValuePair<string, string>("agentweaver.io/workload", "agenthost")]);

    private static readonly ImmutableDictionary<string, string> KubeDnsLabels =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("k8s:io.kubernetes.pod.namespace", "kube-system"),
            new KeyValuePair<string, string>("k8s:k8s-app", "kube-dns"),
        ]);

    private readonly ICiliumPolicyResourceStore _store;
    private readonly CiliumPlatformEgressOptions _options;
    private readonly TimeProvider _time;
    private readonly ImmutableArray<string> _excludedV4;
    private readonly ImmutableArray<string> _excludedV6;

    public CiliumPlatformEgressProvider(
        ICiliumPolicyResourceStore store,
        CiliumEgressProviderOptions providerOptions,
        CiliumPlatformEgressOptions options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        _time = time;
        var ranges = PlatformEgressExcludedRanges.Create(
            options.AdditionalExcludedRanges.IsDefault ? [] : options.AdditionalExcludedRanges);
        _excludedV4 = ranges.Where(range => range.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
            .Select(range => range.ToString()).ToImmutableArray();
        _excludedV6 = ranges.Where(range => range.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(range => range.ToString()).ToImmutableArray();

        var registration = providerOptions.CreateRegistration().Descriptor;
        var capabilities = registration.AdvertisedCapabilities.Union(
        [
            PlatformEgressCapabilities.PrivateRangeExclusion,
            PlatformEgressCapabilities.Status,
            PlatformEgressCapabilities.PublicHttps,
        ]);
        if (options.ExactHostVerifiedForKata)
            capabilities = capabilities.Add(PlatformEgressCapabilities.ExactHost);
        Descriptor = registration with { AdvertisedCapabilities = capabilities };
    }

    public ProviderDescriptor Descriptor { get; }

    public async Task<PlatformEgressStatus> ApplyAsync(
        PlatformEgressPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!PlatformEgressCapabilities.CanApply(Descriptor.AdvertisedCapabilities, policy.Mode))
            return Failed("capability_unavailable");
        var current = Normalize(await _store.GetAsync(_options.Namespace, PolicyName, cancellationToken)
            .ConfigureAwait(false));
        if (current is null)
            return Failed("policy_object_missing");
        var currentGeneration = ReadGeneration(current);
        if (currentGeneration is null)
            return Failed("invalid_applied_generation");
        var desired = Render(current, policy);
        if (currentGeneration == policy.Generation)
            return CiliumEgressPolicyAdapter.SameSpec(current.Spec, desired.Spec) &&
                Annotations(current).GetValueOrDefault(IntentHashAnnotation) == policy.IntentHash &&
                Annotations(current).GetValueOrDefault(SourceHashAnnotation) == policy.SourceHash
                ? Observe(current)
                : Failed("generation_conflict");
        if (currentGeneration > policy.Generation)
            return Failed("stale_generation");
        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            return Failed("invalid_resource_version");

        try
        {
            var replaced = await _store.ReplaceAsync(desired, current.Metadata.ResourceVersion, cancellationToken)
                .ConfigureAwait(false);
            return Observe(Normalize(replaced)!);
        }
        catch (CiliumPolicyException exception)
        {
            return Failed(exception.Code);
        }
    }

    public async Task<PlatformEgressStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = Normalize(await _store.GetAsync(_options.Namespace, PolicyName, cancellationToken)
                .ConfigureAwait(false));
            return current is null ? Failed("policy_object_missing") : Observe(current);
        }
        catch (CiliumPolicyException exception)
        {
            return Failed(exception.Code);
        }
    }

    /// <summary>Resets the object to the DNS-only seed that the deploy creates. It does not delete the object.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var current = Normalize(await _store.GetAsync(_options.Namespace, PolicyName, cancellationToken)
            .ConfigureAwait(false));
        if (current is null)
            return;
        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            throw new CiliumPolicyException("invalid_resource_version", "Kubernetes returned a policy without a resource version.");
        var seed = current with
        {
            Metadata = current.Metadata with
            {
                Annotations = Annotations(current).RemoveRange(
                    [GenerationAnnotation, IntentHashAnnotation, SourceHashAnnotation]),
            },
            Spec = new CiliumPolicySpec(new CiliumEndpointSelector(WorkloadLabels), [DnsRule(null)]),
        };
        await _store.ReplaceAsync(seed, current.Metadata.ResourceVersion, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<EgressFlowObservation> ReadFlowsAsync(
        string? cursor,
        bool includeAllowed,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The Cilium provider does not advertise egress.blocked-attempts.");

    public Task<EgressTrustBundle?> GetTrustBundleAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EgressTrustBundle?>(null);

    /// <summary>
    /// Reads the mode and hosts back from the spec, renders them again, and requires the same spec and
    /// the same intent hash as the annotations. Any difference is <c>readback_mismatch</c>.
    /// </summary>
    private PlatformEgressStatus Observe(CiliumNetworkPolicyDocument actual)
    {
        if (actual.Spec is { Egress.IsDefault: true })
            actual = actual with { Spec = actual.Spec with { Egress = [] } };
        var generation = ReadGeneration(actual);
        if (generation is null)
            return Failed("invalid_applied_generation");
        if (generation == 0)
            return Failed("policy_not_applied");
        var intentHash = Annotations(actual).GetValueOrDefault(IntentHashAnnotation);
        var sourceHash = Annotations(actual).GetValueOrDefault(SourceHashAnnotation);
        var readback = ReadPolicy(actual, generation.Value, intentHash, sourceHash);
        if (readback is null ||
            actual.Metadata.Namespace != _options.Namespace || actual.Metadata.Name != PolicyName ||
            !CiliumEgressPolicyAdapter.SameSpec(actual.Spec, Render(actual, readback).Spec))
            return new(generation.Value, intentHash, sourceHash, PlatformEgressApplyState.Failed,
                _time.GetUtcNow(), "readback_mismatch");
        return new(generation.Value, intentHash, sourceHash, PlatformEgressApplyState.Accepted,
            _time.GetUtcNow(), null);
    }

    /// <summary>
    /// Rebuilds the policy from the spec. In Allowlist mode the hosts are in the spec, so the intent hash
    /// must match. PublicHttps has no hosts, so its intent hash must equal the fixed PublicHttps hash.
    /// </summary>
    private static PlatformEgressPolicy? ReadPolicy(
        CiliumNetworkPolicyDocument actual,
        long generation,
        string? intentHash,
        string? sourceHash)
    {
        if (intentHash is null || sourceHash is null || actual.Spec?.Egress is not { IsDefault: false } egress)
            return null;
        var https = egress.Skip(1).ToArray();
        if (https.Any(rule => rule.ToCidrSet is not null))
            return intentHash == PlatformEgressPolicy.ComputeIntentHash(PlatformEgressMode.PublicHttps, [])
                ? new(generation, PlatformEgressMode.PublicHttps, [], intentHash, sourceHash)
                : null;
        try
        {
            var hosts = https.SelectMany(rule => rule.ToFqDns ?? [])
                .Select(selector => new PlatformEgressDestination(
                    selector.MatchName ?? string.Empty, PlatformEgressHostValidator.HttpsPort));
            var policy = PlatformEgressPolicy.Create(generation, PlatformEgressMode.Allowlist, hosts, sourceHash);
            return policy.IntentHash == intentHash ? policy : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private CiliumNetworkPolicyDocument Render(CiliumNetworkPolicyDocument current, PlatformEgressPolicy policy)
    {
        var https = ImmutableArray.Create(new CiliumPortRules([new CiliumPort("443", "TCP")]));
        var hosts = policy.Destinations.Select(destination => destination.Host).ToImmutableArray();
        var egress = ImmutableArray.CreateBuilder<CiliumEgressRuleDocument>();
        if (policy.Mode == PlatformEgressMode.PublicHttps)
        {
            egress.Add(DnsRule(null));
            egress.Add(new CiliumEgressRuleDocument(
                ToCidrSet: [new CiliumCidrSelector("0.0.0.0/0", _excludedV4), new CiliumCidrSelector("::/0", _excludedV6)],
                ToPorts: https));
        }
        else if (!hosts.IsEmpty)
        {
            // An empty Allowlist renders no allow rule, not an unrestricted DNS rule.
            egress.Add(DnsRule(hosts));
            egress.Add(new CiliumEgressRuleDocument(
                ToFqDns: hosts.Select(host => new CiliumFqdnSelector(MatchName: host)).ToImmutableArray(),
                ToPorts: https));
        }

        var deny = new CiliumEgressRuleDocument(
            ToCidrSet: _excludedV4.Concat(_excludedV6).Select(range => new CiliumCidrSelector(range)).ToImmutableArray());
        return current with
        {
            Metadata = current.Metadata with
            {
                Annotations = Annotations(current).SetItems(
                [
                    new KeyValuePair<string, string>(
                        GenerationAnnotation, policy.Generation.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>(IntentHashAnnotation, policy.IntentHash),
                    new KeyValuePair<string, string>(SourceHashAnnotation, policy.SourceHash),
                ]),
            },
            Spec = new CiliumPolicySpec(new CiliumEndpointSelector(WorkloadLabels), egress.ToImmutable(), [deny]),
        };
    }

    private static CiliumEgressRuleDocument DnsRule(ImmutableArray<string>? hosts) =>
        new(
            ToEndpoints: [new CiliumEndpointSelector(KubeDnsLabels)],
            ToPorts:
            [
                new CiliumPortRules(
                    [new CiliumPort("53", "UDP"), new CiliumPort("53", "TCP")],
                    hosts is { } names
                        ? new CiliumDnsRuleCollection(names.Select(name => new CiliumDnsRule(MatchName: name)).ToImmutableArray())
                        : null),
            ]);

    /// <summary>Returns 0 when the object has no generation annotation, and null when the annotation is invalid.</summary>
    private static long? ReadGeneration(CiliumNetworkPolicyDocument policy)
    {
        if (!Annotations(policy).TryGetValue(GenerationAnnotation, out var value))
            return 0;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) &&
            generation > 0
            ? generation
            : null;
    }

    private static ImmutableDictionary<string, string> Annotations(CiliumNetworkPolicyDocument policy) =>
        policy.Metadata.Annotations ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    // Kubernetes omits an empty egress list, which deserializes as a default array.
    private static CiliumNetworkPolicyDocument? Normalize(CiliumNetworkPolicyDocument? policy) =>
        policy?.Spec is { Egress.IsDefault: true } spec ? policy with { Spec = spec with { Egress = [] } } : policy;

    private PlatformEgressStatus Failed(string code) =>
        new(0, null, null, PlatformEgressApplyState.Failed, _time.GetUtcNow(), code);
}
