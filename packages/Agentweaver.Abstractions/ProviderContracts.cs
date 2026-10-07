using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agentweaver.Providers")]

namespace Agentweaver.Abstractions;

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

public enum ProviderCardinality
{
    Exclusive,
    OrderedComposite,
    PlatformSingleton,
    Layered,
    KeyedByMeterSource,
    PerApplication
}

public enum ProviderHostingPattern
{
    InProcess,
    Sidecar,
    RemoteService,
    KubernetesController,
    ManagedRuntime
}

public static class ProviderSeams
{
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

public static class ProviderCapabilities
{
    public const string SnapshotCapture = "snapshot.capture";
    public const string SnapshotRestore = "snapshot.restore";
}

// These records contain only stable identities and references, never option values or credentials.
public sealed record ProviderDescriptor(
    ProviderSeam Seam,
    string Id,
    Version AdapterVersion,
    int OptionsSchemaVersion,
    ProviderHostingPattern Hosting,
    ImmutableHashSet<string> AdvertisedCapabilities);

public sealed record ProviderRegistration(
    ProviderDescriptor Descriptor,
    bool Enabled,
    string OptionsRevision,
    int OptionsSchemaVersion);

public sealed record ProviderSelection(ProviderSeam Seam, string ProviderId);

public sealed record ProviderMeterSourceSelection(string MeterSource, string ProviderId);

public sealed record ProviderOverridePermission(ProviderSeam Seam, string ProviderId);

public sealed record ProviderOrderedSelection(ProviderSeam Seam, ImmutableArray<string> ProviderIds);

public enum NetworkPolicyLayer { L3L4, L7 }

public sealed record ProviderLayerSelection(NetworkPolicyLayer Layer, string ProviderId);

public sealed record OrderedProviderResolutionRequest(
    ProviderSeam Seam,
    ImmutableArray<string>? ProjectProviderIds,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

public sealed record NetworkPolicyResolutionRequest(
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredL3L4Capabilities,
    ImmutableHashSet<string> RequiredL7Capabilities);

public sealed record ProviderResolutionRequest(
    ProviderSeam Seam,
    string? ProjectOverrideId,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

public sealed record CostProviderResolutionRequest(
    string MeterSource,
    Version RequiredAdapterVersion,
    int RequiredOptionsSchemaVersion,
    ImmutableHashSet<string> RequiredCapabilities);

public sealed class ProviderCandidate
{
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

public sealed class CostProviderResolution
{
    internal CostProviderResolution(string meterSource, ProviderCandidate candidate) =>
        (MeterSource, Candidate) = (meterSource, candidate);

    public string MeterSource { get; }
    public ProviderCandidate Candidate { get; }
}

public sealed class ProviderResolution
{
    private ProviderResolution(ProviderCandidate? candidate) => Candidate = candidate;
    public ProviderCandidate? Candidate { get; }
    public bool IsNone => Candidate is null;
    internal static ProviderResolution None { get; } = new(null);
    internal static ProviderResolution Selected(ProviderCandidate candidate) => new(candidate);
}

public sealed record NetworkPolicyCandidate(NetworkPolicyLayer Layer, ProviderCandidate Candidate);

public sealed class OrderedProviderResolution
{
    internal OrderedProviderResolution(ProviderSeam seam, ImmutableArray<ProviderCandidate> candidates) =>
        (Seam, Candidates) = (seam, candidates);
    public ProviderSeam Seam { get; }
    public ImmutableArray<ProviderCandidate> Candidates { get; }
}

public sealed class NetworkPolicyResolution
{
    internal NetworkPolicyResolution(ImmutableArray<NetworkPolicyCandidate> layers) => Layers = layers;
    public ImmutableArray<NetworkPolicyCandidate> Layers { get; }
}

// A provisioned resource's identity and generation are opaque to the provider-neutral core.
public sealed record ProviderResourceRef(ProviderSeam Seam, string ProviderId, string ResourceId, long Generation);

public sealed record ResourceNegotiation(
    ProviderResourceRef Resource,
    ImmutableHashSet<string> Capabilities);

public sealed record ProviderPinInput(string ExpectedResourceId, ResourceNegotiation Negotiation);

public sealed class PinnedProviderBinding
{
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

public sealed class PinnedCostProviderBinding
{
    internal PinnedCostProviderBinding(string meterSource, PinnedProviderBinding providerBinding) =>
        (MeterSource, ProviderBinding) = (meterSource, providerBinding);

    public string MeterSource { get; }
    public PinnedProviderBinding ProviderBinding { get; }
}

public sealed class PinnedOrderedProviderBinding
{
    internal PinnedOrderedProviderBinding(string runId, ProviderSeam seam,
        ImmutableArray<PinnedProviderBinding> bindings) =>
        (RunId, Seam, Bindings) = (runId, seam, bindings);
    public string RunId { get; }
    public ProviderSeam Seam { get; }
    public ImmutableArray<PinnedProviderBinding> Bindings { get; }
}

public sealed record PinnedNetworkPolicyLayer(NetworkPolicyLayer Layer, PinnedProviderBinding Binding);

public sealed class PinnedNetworkPolicyBinding
{
    internal PinnedNetworkPolicyBinding(string runId, long appliedIntentGeneration,
        ImmutableArray<PinnedNetworkPolicyLayer> layers) =>
        (RunId, AppliedIntentGeneration, Layers) = (runId, appliedIntentGeneration, layers);
    public string RunId { get; }
    public long AppliedIntentGeneration { get; }
    public ImmutableArray<PinnedNetworkPolicyLayer> Layers { get; }
}

public sealed class PinnedSnapshotNoneBinding
{
    internal PinnedSnapshotNoneBinding(string runId) => RunId = runId;
    public string RunId { get; }
    public ProviderSeam Seam => ProviderSeam.Snapshots;
    public ImmutableHashSet<string> NegotiatedCapabilities { get; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);
}
