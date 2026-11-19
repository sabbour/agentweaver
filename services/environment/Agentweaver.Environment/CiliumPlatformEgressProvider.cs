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
    public const string SourceEpochAnnotation = "agentweaver.io/egress-source-epoch";
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
    private readonly IPlatformEgressApplyReservationReader? _reservationReader;
    private readonly ImmutableArray<string> _excludedV4;
    private readonly ImmutableArray<string> _excludedV6;

    public CiliumPlatformEgressProvider(
        ICiliumPolicyResourceStore store,
        CiliumEgressProviderOptions providerOptions,
        CiliumPlatformEgressOptions options,
        TimeProvider time,
        IPlatformEgressApplyReservationReader? reservationReader = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        _time = time;
        _reservationReader = reservationReader;
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

    internal string GetProofTargetId() => _store.GetTargetId(_options.Namespace, PolicyName);
    internal string ProofNamespace => _options.Namespace;

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
        if (string.IsNullOrWhiteSpace(current.Metadata.Uid))
            return Failed("invalid_object_uid");
        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            return Failed("invalid_resource_version");

        try
        {
            desired = desired with
            {
                Metadata = desired.Metadata with
                {
                    Uid = current.Metadata.Uid,
                    ResourceVersion = current.Metadata.ResourceVersion,
                },
            };
            var replaced = await _store.ReplaceAsync(
                    desired, current.Metadata.Uid, current.Metadata.ResourceVersion, cancellationToken)
                .ConfigureAwait(false);
            return Observe(Normalize(replaced)!);
        }
        catch (CiliumPolicyException exception)
        {
            return Failed(exception.Code);
        }
    }

    /// <summary>Applies only the immutable request and Kubernetes object fence saved in a reservation.</summary>
    public async Task<PlatformEgressStatus> ApplyReservedAsync(
        PlatformEgressApplyReservation reservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var policy = reservation.Policy;
        if (policy is null || reservation.SourceEpoch <= 0 ||
            reservation.Generation != policy.Generation ||
            !IsValidPolicy(policy) ||
            !PlatformEgressCapabilities.CanApply(Descriptor.AdvertisedCapabilities, policy.Mode))
            return Failed("invalid_apply_reservation");

        string targetId;
        try
        {
            targetId = _store.GetTargetId(_options.Namespace, PolicyName);
        }
        catch (CiliumPolicyException exception)
        {
            return Failed(exception.Code);
        }
        if (reservation.TargetId != targetId)
            return Failed("provider_target_mismatch");
        if (_reservationReader is null)
            return Failed("apply_reservation_store_unavailable");

        try
        {
            var saved = await _reservationReader.FindAsync(
                reservation.TargetId, reservation.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (saved is null ||
                saved.ReservationId != reservation.ReservationId ||
                saved.RequestFingerprint != reservation.RequestFingerprint)
                return Failed("unknown_apply_reservation");
            if (saved.SourceEpoch != reservation.SourceEpoch ||
                saved.Generation != reservation.Generation ||
                saved.SourceRevision != reservation.SourceRevision ||
                saved.SourceId != reservation.SourceId ||
                saved.ObjectUid != reservation.ObjectUid ||
                saved.ResourceVersion != reservation.ResourceVersion ||
                !SamePolicy(policy, saved.Policy))
                return Failed("invalid_apply_reservation");
            reservation = saved;
            policy = reservation.Policy;

            var latest = await _reservationReader.FindLatestAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (latest?.ReservationId != reservation.ReservationId)
                return Failed("stale_apply_reservation");

            var current = Normalize(await _store.GetAsync(_options.Namespace, PolicyName, cancellationToken)
                .ConfigureAwait(false));
            if (current is null)
                return Failed("policy_object_missing");
            if (string.IsNullOrWhiteSpace(current.Metadata.Uid) ||
                string.IsNullOrWhiteSpace(reservation.ObjectUid))
                return Failed("invalid_object_uid");
            if (current.Metadata.Uid != reservation.ObjectUid)
                return Failed("stale_object_uid");
            if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
                return Failed("invalid_resource_version");

            var currentEpoch = ReadSourceEpoch(current);
            var currentGeneration = ReadGeneration(current);
            if (currentEpoch is null)
                return Failed("invalid_source_epoch");
            if (currentGeneration is null)
                return Failed("invalid_applied_generation");
            if (currentEpoch > 0 && currentGeneration == 0)
                return Failed("invalid_source_epoch");

            var desired = Render(current, policy, reservation.SourceEpoch);
            if (currentEpoch == reservation.SourceEpoch &&
                currentGeneration == reservation.Generation)
            {
                latest = await _reservationReader.FindLatestAsync(targetId, cancellationToken).ConfigureAwait(false);
                if (latest?.ReservationId != reservation.ReservationId)
                    return Failed("stale_apply_reservation");
                return CiliumEgressPolicyAdapter.SameSpec(current.Spec, desired.Spec) &&
                    Annotations(current).GetValueOrDefault(IntentHashAnnotation) == policy.IntentHash &&
                    Annotations(current).GetValueOrDefault(SourceHashAnnotation) == policy.SourceHash
                    ? Observe(current)
                    : Failed("generation_conflict");
            }

            if (currentEpoch > reservation.SourceEpoch)
                return Failed("stale_source_epoch");
            if (currentEpoch == reservation.SourceEpoch &&
                currentGeneration > 0 &&
                (Annotations(current).GetValueOrDefault(IntentHashAnnotation) != policy.IntentHash ||
                 Annotations(current).GetValueOrDefault(SourceHashAnnotation) != policy.SourceHash))
                return Failed("source_epoch_conflict");
            if (currentGeneration >= reservation.Generation)
                return Failed("stale_generation");
            if (current.Metadata.ResourceVersion != reservation.ResourceVersion)
                return Failed("stale_resource_version");

            latest = await _reservationReader.FindLatestAsync(targetId, cancellationToken).ConfigureAwait(false);
            if (latest?.ReservationId != reservation.ReservationId)
                return Failed("stale_apply_reservation");

            desired = desired with
            {
                Metadata = desired.Metadata with
                {
                    Uid = reservation.ObjectUid,
                    ResourceVersion = reservation.ResourceVersion,
                },
            };
            var replaced = Normalize(await _store.ReplaceAsync(
                desired,
                reservation.ObjectUid,
                reservation.ResourceVersion,
                cancellationToken).ConfigureAwait(false))!;
            var status = Observe(replaced);
            return status.Matches(policy.IntentHash, policy.SourceHash) &&
                ReadSourceEpoch(replaced) == reservation.SourceEpoch &&
                replaced.Metadata.Uid == reservation.ObjectUid
                ? status
                : Failed("readback_mismatch");
        }
        catch (PlatformEgressApplyReservationException exception)
        {
            return Failed(exception.Code);
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
        if (string.IsNullOrWhiteSpace(current.Metadata.Uid))
            throw new CiliumPolicyException("invalid_object_uid", "Kubernetes returned a policy without an object UID.");
        if (string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            throw new CiliumPolicyException("invalid_resource_version", "Kubernetes returned a policy without a resource version.");
        var seed = current with
        {
            Metadata = current.Metadata with
            {
                Annotations = Annotations(current).RemoveRange(
                    [GenerationAnnotation, SourceEpochAnnotation, IntentHashAnnotation, SourceHashAnnotation]),
            },
            Spec = new CiliumPolicySpec(new CiliumEndpointSelector(WorkloadLabels), [DnsRule(null)]),
        };
        seed = seed with
        {
            Metadata = seed.Metadata with
            {
                Uid = current.Metadata.Uid,
                ResourceVersion = current.Metadata.ResourceVersion,
            },
        };
        await _store.ReplaceAsync(
            seed, current.Metadata.Uid, current.Metadata.ResourceVersion, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<EgressFlowObservation> ReadFlowsAsync(
        string? cursor,
        bool includeAllowed,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The Cilium provider does not advertise egress.blocked-attempts.");

    public Task<EgressTrustBundle?> GetTrustBundleAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EgressTrustBundle?>(null);

    internal CiliumNetworkPolicyDocument RenderForProof(
        CiliumNetworkPolicyDocument current,
        PlatformEgressPolicy policy,
        long sourceEpoch)
    {
        if (!IsValidPolicy(policy))
            throw new CiliumPolicyException(
                "invalid_apply_reservation", "The proof candidate policy is invalid.");
        var rendered = Render(current, policy, sourceEpoch);
        if (string.IsNullOrWhiteSpace(current.Metadata.Uid) ||
            string.IsNullOrWhiteSpace(current.Metadata.ResourceVersion))
            throw new CiliumPolicyException(
                "invalid_object_identity", "The proof target policy identity is incomplete.");
        return rendered with
        {
            Metadata = rendered.Metadata with
            {
                Uid = current.Metadata.Uid,
                ResourceVersion = current.Metadata.ResourceVersion,
            },
        };
    }

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

    private CiliumNetworkPolicyDocument Render(
        CiliumNetworkPolicyDocument current,
        PlatformEgressPolicy policy,
        long? sourceEpoch = null)
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
        var annotations = Annotations(current).SetItems(
        [
            new KeyValuePair<string, string>(
                GenerationAnnotation, policy.Generation.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(IntentHashAnnotation, policy.IntentHash),
            new KeyValuePair<string, string>(SourceHashAnnotation, policy.SourceHash),
        ]);
        if (sourceEpoch is { } epoch)
            annotations = annotations.SetItem(SourceEpochAnnotation, epoch.ToString(CultureInfo.InvariantCulture));
        return current with
        {
            Metadata = current.Metadata with
            {
                Annotations = annotations,
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

    private static long? ReadSourceEpoch(CiliumNetworkPolicyDocument policy)
    {
        if (!Annotations(policy).TryGetValue(SourceEpochAnnotation, out var value))
            return 0;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch > 0
            ? epoch
            : null;
    }

    private static bool IsValidPolicy(PlatformEgressPolicy policy)
    {
        if (policy.Generation <= 0 || !Enum.IsDefined(policy.Mode) ||
            policy.Destinations.IsDefault ||
            policy.SourceHash is not { Length: 64 } ||
            policy.SourceHash.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
            return false;
        try
        {
            if (!policy.Destinations.All(destination => destination is not null &&
                    PlatformEgressHostValidator.Validate(destination.Host, destination.Port) is null))
                return false;
            var canonical = PlatformEgressPolicy.Create(
                policy.Generation, policy.Mode, policy.Destinations, policy.SourceHash);
            return policy.Destinations.SequenceEqual(canonical.Destinations) &&
                policy.IntentHash == canonical.IntentHash &&
                (policy.Mode != PlatformEgressMode.PublicHttps || policy.Destinations.IsEmpty);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SamePolicy(PlatformEgressPolicy left, PlatformEgressPolicy right) =>
        left.Generation == right.Generation &&
        left.Mode == right.Mode &&
        left.IntentHash == right.IntentHash &&
        left.SourceHash == right.SourceHash &&
        left.Destinations.SequenceEqual(right.Destinations);

    // Kubernetes responses can omit an empty annotation map.
    private static ImmutableDictionary<string, string> Annotations(CiliumNetworkPolicyDocument policy) =>
        policy.Metadata.Annotations ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    // Kubernetes omits an empty egress list, which deserializes as a default array.
    private static CiliumNetworkPolicyDocument? Normalize(CiliumNetworkPolicyDocument? policy) =>
        policy?.Spec is { Egress.IsDefault: true } spec ? policy with { Spec = spec with { Egress = [] } } : policy;

    private PlatformEgressStatus Failed(string code) =>
        new(0, null, null, PlatformEgressApplyState.Failed, _time.GetUtcNow(), code);
}
