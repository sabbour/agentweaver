using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agentweaver.Providers")]

namespace Agentweaver.Abstractions;

/// <summary>Stable extension points through which services select provider implementations.</summary>
public enum ProviderSeam
{
    Sessions,
    Snapshots,
    Sandbox,
    Storage,
    Memory,
    Policy,
    Guardrails,
    NetworkPolicy,
    Cost,
    ApplicationHosting,
    Secrets,
    SourceControl,
    Telemetry,
    Messaging,
    ObjectStore
}

/// <summary>How many provider registrations may serve a seam in one effective selection.</summary>
public enum ProviderCardinality
{
    Exclusive,
    OrderedComposite,
    PlatformSingleton,
    Layered,
    KeyedByMeterSource,
    PerApplication
}

/// <summary>Where a provider adapter runs relative to the service requesting it.</summary>
public enum ProviderHostingPattern
{
    InProcess,
    Sidecar,
    RemoteService,
    KubernetesController,
    ManagedRuntime
}

/// <summary>Defines the cardinality rules for each provider seam.</summary>
public static class ProviderSeams
{
    /// <summary>Returns the configured cardinality for a seam.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The seam value is not supported.</exception>
    public static ProviderCardinality Cardinality(ProviderSeam seam) => seam switch
    {
        ProviderSeam.Sessions or ProviderSeam.Snapshots or ProviderSeam.Sandbox or
            ProviderSeam.Storage or ProviderSeam.Memory or ProviderSeam.SourceControl
            => ProviderCardinality.Exclusive,
        ProviderSeam.Guardrails or ProviderSeam.Telemetry => ProviderCardinality.OrderedComposite,
        ProviderSeam.Policy or ProviderSeam.Secrets or ProviderSeam.Messaging or
            ProviderSeam.ObjectStore => ProviderCardinality.PlatformSingleton,
        ProviderSeam.NetworkPolicy => ProviderCardinality.Layered,
        ProviderSeam.Cost => ProviderCardinality.KeyedByMeterSource,
        ProviderSeam.ApplicationHosting => ProviderCardinality.PerApplication,
        _ => throw new ArgumentOutOfRangeException(nameof(seam))
    };
}

/// <summary>Canonical capability identifiers shared by snapshot providers and consumers.</summary>
public static class ProviderCapabilities
{
    /// <summary>Provider can capture a snapshot.</summary>
    public const string SnapshotCapture = "snapshot.capture";
    /// <summary>Provider can restore a snapshot.</summary>
    public const string SnapshotRestore = "snapshot.restore";
}

// These records contain only stable identities and references, never option values or credentials.
/// <summary>Provider catalog metadata; it advertises capabilities but contains no options or credentials.</summary>
public sealed record ProviderDescriptor(
    ProviderSeam Seam,
    string Id,
    Version AdapterVersion,
    int OptionsSchemaVersion,
    ProviderHostingPattern Hosting,
    ImmutableHashSet<string> AdvertisedCapabilities);

/// <summary>Catalog-owned enabled state and options revision for a provider descriptor.</summary>
public sealed record ProviderRegistration(
    ProviderDescriptor Descriptor,
    bool Enabled,
    string OptionsRevision,
    int OptionsSchemaVersion);

/// <summary>Selects one provider ID for a provider seam.</summary>
public sealed record ProviderSelection(ProviderSeam Seam, string ProviderId);

/// <summary>Selects a provider for one usage-meter source.</summary>
public sealed record ProviderMeterSourceSelection(string MeterSource, string ProviderId);

/// <summary>Grants permission for a project selection to override a seam's platform provider.</summary>
public sealed record ProviderOverridePermission(ProviderSeam Seam, string ProviderId);

/// <summary>Ordered provider IDs for a composite seam whose execution order is significant.</summary>
public sealed record ProviderOrderedSelection(ProviderSeam Seam, ImmutableArray<string> ProviderIds);

/// <summary>Layer in which a network policy adapter participates.</summary>
public enum NetworkPolicyLayer { L3L4, L7 }

/// <summary>Provider selected for one network policy layer.</summary>
public sealed record ProviderLayerSelection(NetworkPolicyLayer Layer, string ProviderId);

/// <summary>
/// Requirements and optional project selection for resolving an ordered provider chain.
/// A null project list allows platform selection; an explicit list pins candidate order.
/// </summary>
public sealed record OrderedProviderResolutionRequest(
    ProviderSeam Seam,
    ImmutableArray<string>? ProjectProviderIds,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

/// <summary>Capability and version requirements for resolving the required network-policy layers.</summary>
public sealed record NetworkPolicyResolutionRequest(
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredL3L4Capabilities,
    ImmutableHashSet<string> RequiredL7Capabilities);

/// <summary>Requirements and optional project override for resolving one provider seam.</summary>
public sealed record ProviderResolutionRequest(
    ProviderSeam Seam,
    string? ProjectOverrideId,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

/// <summary>Requirements for resolving a provider keyed to one usage-meter source.</summary>
public sealed record CostProviderResolutionRequest(
    string MeterSource,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

/// <summary>
/// Resolver-produced candidate that copies only catalog identity, options revision,
/// hosting, and capability metadata needed to provision and negotiate a provider.
/// </summary>
public sealed class ProviderCandidate
{
    /// <summary>Creates a candidate from one enabled catalog registration and required capability set.</summary>
    internal ProviderCandidate(ProviderRegistration registration, ImmutableHashSet<string> required)
    {
        var descriptor = registration.Descriptor;
        Seam = descriptor.Seam;
        ProviderId = descriptor.Id;
        AdapterVersion = descriptor.AdapterVersion;
        OptionsSchemaVersion = registration.OptionsSchemaVersion;
        OptionsRevision = registration.OptionsRevision;
        Hosting = descriptor.Hosting;
        AdvertisedCapabilities = descriptor.AdvertisedCapabilities;
        RequiredCapabilities = required;
    }

    public ProviderSeam Seam { get; }
    public string ProviderId { get; }
    public Version AdapterVersion { get; }
    public int OptionsSchemaVersion { get; }
    public string OptionsRevision { get; }
    public ProviderHostingPattern Hosting { get; }
    public ImmutableHashSet<string> AdvertisedCapabilities { get; }
    public ImmutableHashSet<string> RequiredCapabilities { get; }
}

/// <summary>Resolved candidate paired with the usage meter it is allowed to price.</summary>
public sealed class CostProviderResolution
{
    /// <summary>Creates a resolution for the given meter and provider candidate.</summary>
    internal CostProviderResolution(string meterSource, ProviderCandidate candidate) =>
        (MeterSource, Candidate) = (meterSource, candidate);

    public string MeterSource { get; }
    public ProviderCandidate Candidate { get; }
}

/// <summary>Either one selected provider candidate or an explicit no-provider result.</summary>
public sealed class ProviderResolution
{
    /// <summary>Selected provider, or null when the resolver explicitly returned no binding.</summary>
    private ProviderResolution(ProviderCandidate? candidate) => Candidate = candidate;
    public ProviderCandidate? Candidate { get; }
    /// <summary>Indicates that no candidate was selected.</summary>
    public bool IsNone => Candidate is null;
    internal static ProviderResolution None { get; } = new(null);
    internal static ProviderResolution Selected(ProviderCandidate candidate) => new(candidate);
}

/// <summary>Candidate provider and layer returned for network-policy resolution.</summary>
public sealed record NetworkPolicyCandidate(NetworkPolicyLayer Layer, ProviderCandidate Candidate);

/// <summary>Resolved ordered candidates for a composite provider seam.</summary>
public sealed class OrderedProviderResolution
{
    /// <summary>Creates a resolved chain for the specified seam.</summary>
    internal OrderedProviderResolution(ProviderSeam seam, ImmutableArray<ProviderCandidate> candidates) =>
        (Seam, Candidates) = (seam, candidates);
    public ProviderSeam Seam { get; }
    public ImmutableArray<ProviderCandidate> Candidates { get; }
}

/// <summary>Resolved provider candidates for the selected network-policy layers.</summary>
public sealed class NetworkPolicyResolution
{
    /// <summary>Creates a network-policy resolution with its resolved layer list.</summary>
    internal NetworkPolicyResolution(ImmutableArray<NetworkPolicyCandidate> layers) => Layers = layers;
    public ImmutableArray<NetworkPolicyCandidate> Layers { get; }
}

// A provisioned resource's identity and generation are opaque to the provider-neutral core.
/// <summary>Opaque provider-owned resource identity and generation returned after provisioning.</summary>
public sealed record ProviderResourceRef(ProviderSeam Seam, string ProviderId, string ResourceId, long Generation);

/// <summary>Provider's negotiated capabilities for a provisioned resource.</summary>
public sealed record ResourceNegotiation(
    ProviderResourceRef Resource,
    ImmutableHashSet<string> Capabilities);

/// <summary>Expected resource identity paired with the provider's negotiation result for pinning.</summary>
public sealed record ProviderPinInput(string ExpectedResourceId, ResourceNegotiation Negotiation);

/// <summary>
/// Immutable provider binding for one run, including exact adapter/options revisions,
/// resource generation, hosting mode, and negotiated capabilities.
/// </summary>
public sealed class PinnedProviderBinding
{
    /// <summary>Creates a run binding from a resolver candidate and validated resource negotiation.</summary>
    internal PinnedProviderBinding(string runId, ProviderCandidate candidate,
        ProviderResourceRef resource, ImmutableHashSet<string> negotiated)
    {
        RunId = runId;
        Seam = candidate.Seam;
        ProviderId = candidate.ProviderId;
        AdapterVersion = candidate.AdapterVersion;
        OptionsSchemaVersion = candidate.OptionsSchemaVersion;
        OptionsRevision = candidate.OptionsRevision;
        Hosting = candidate.Hosting;
        Resource = resource;
        NegotiatedCapabilities = negotiated;
    }

    public string RunId { get; }
    public ProviderSeam Seam { get; }
    public string ProviderId { get; }
    public Version AdapterVersion { get; }
    public int OptionsSchemaVersion { get; }
    public string OptionsRevision { get; }
    public ProviderHostingPattern Hosting { get; }
    public ProviderResourceRef Resource { get; }
    public ImmutableHashSet<string> NegotiatedCapabilities { get; }
}

/// <summary>Run-pinned provider binding scoped to one usage meter.</summary>
public sealed class PinnedCostProviderBinding
{
    /// <summary>Creates a cost binding for the named meter source.</summary>
    internal PinnedCostProviderBinding(string meterSource, PinnedProviderBinding providerBinding) =>
        (MeterSource, ProviderBinding) = (meterSource, providerBinding);

    public string MeterSource { get; }
    public PinnedProviderBinding ProviderBinding { get; }
}

/// <summary>Run-pinned ordered provider chain for a composite seam.</summary>
public sealed class PinnedOrderedProviderBinding
{
    /// <summary>Creates an ordered binding while preserving the resolver's candidate order.</summary>
    internal PinnedOrderedProviderBinding(string runId, ProviderSeam seam,
        ImmutableArray<PinnedProviderBinding> bindings) =>
        (RunId, Seam, Bindings) = (runId, seam, bindings);
    public string RunId { get; }
    public ProviderSeam Seam { get; }
    public ImmutableArray<PinnedProviderBinding> Bindings { get; }
}

/// <summary>One run-pinned provider and its layer within the network-policy stack.</summary>
public sealed record PinnedNetworkPolicyLayer(NetworkPolicyLayer Layer, PinnedProviderBinding Binding);

/// <summary>
/// Network-policy stack pinned to a run and to the applied egress-intent generation;
/// consumers must not treat a provider-only pin as proof of applied policy.
/// </summary>
public sealed class PinnedNetworkPolicyBinding
{
    /// <summary>Creates the stack binding for the exact applied intent generation.</summary>
    internal PinnedNetworkPolicyBinding(string runId, long appliedIntentGeneration,
        ImmutableArray<PinnedNetworkPolicyLayer> layers) =>
        (RunId, AppliedIntentGeneration, Layers) = (runId, appliedIntentGeneration, layers);
    public string RunId { get; }
    public long AppliedIntentGeneration { get; }
    public ImmutableArray<PinnedNetworkPolicyLayer> Layers { get; }
}

/// <summary>
/// Explicit run binding for the no-snapshot case, avoiding an implicit fallback provider
/// while recording that the selected snapshot capability set is empty.
/// </summary>
public sealed class PinnedSnapshotNoneBinding
{
    /// <summary>Creates an explicit no-snapshot binding for a run.</summary>
    internal PinnedSnapshotNoneBinding(string runId) => RunId = runId;
    public string RunId { get; }
    public ProviderSeam Seam => ProviderSeam.Snapshots;
    public ImmutableHashSet<string> NegotiatedCapabilities { get; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);
}
