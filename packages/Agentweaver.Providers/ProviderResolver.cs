using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers;

public sealed class ProviderResolver(ProviderCatalog catalog)
{
    public ProviderResult<ProviderResolution> Resolve(ProviderResolutionRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Seam) ||
            request.RequiredAdapterVersion is null || request.RequiredOptionsSchemaVersion < 1 ||
            request.RequiredCapabilities is null ||
            request.RequiredCapabilities.Any(string.IsNullOrWhiteSpace) ||
            (request.ProjectOverrideId is not null && string.IsNullOrWhiteSpace(request.ProjectOverrideId)))
            return Fail<ProviderResolution>(ProviderErrorCode.InvalidConfiguration, "Resolution request is invalid.");

        var cardinality = ProviderSeams.Cardinality(request.Seam);
        if (cardinality is not (ProviderCardinality.Exclusive or ProviderCardinality.PlatformSingleton))
            return Fail<ProviderResolution>(ProviderErrorCode.UnsupportedCardinality,
                $"Resolution of '{request.Seam}' ({cardinality}) is not implemented.");

        if (request.ProjectOverrideId is not null &&
            (cardinality == ProviderCardinality.PlatformSingleton ||
             !catalog.IsOverridePermitted(request.Seam, request.ProjectOverrideId)))
            return Fail<ProviderResolution>(ProviderErrorCode.OverrideNotPermitted,
                $"Project override '{request.ProjectOverrideId}' is not permitted for '{request.Seam}'.");

        catalog.TryGetDefault(request.Seam, out var defaultId);
        var id = request.ProjectOverrideId ?? defaultId;
        if (id is null)
        {
            if (request.Seam == ProviderSeam.Snapshots)
                return request.RequiredCapabilities.Count == 0
                    ? ProviderResult<ProviderResolution>.Success(ProviderResolution.None)
                    : Fail<ProviderResolution>(ProviderErrorCode.CapabilityUnavailable,
                        "Snapshots None cannot satisfy snapshot capabilities.");
            return Fail<ProviderResolution>(ProviderErrorCode.MissingDefault,
                $"No platform default exists for '{request.Seam}'.");
        }
        var candidate = Select(id, request.Seam, request.RequiredAdapterVersion,
            request.RequiredOptionsSchemaVersion, request.RequiredCapabilities);
        return candidate.IsSuccess
            ? ProviderResult<ProviderResolution>.Success(ProviderResolution.Selected(candidate.Value!))
            : Fail<ProviderResolution>(candidate.Error!.Code, candidate.Error.Message);
    }

    public ProviderResult<OrderedProviderResolution> ResolveOrdered(OrderedProviderResolutionRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Seam) ||
            ProviderSeams.Cardinality(request.Seam) != ProviderCardinality.OrderedComposite ||
            !ValidRequirements(request.RequiredAdapterVersion, request.RequiredOptionsSchemaVersion,
                request.RequiredCapabilities) ||
            (request.ProjectProviderIds is { } project &&
             (project.IsDefault || project.Any(string.IsNullOrWhiteSpace))))
            return Fail<OrderedProviderResolution>(ProviderErrorCode.InvalidConfiguration,
                "Ordered resolution request is invalid.");
        if (!catalog.TryGetOrdered(request.Seam, out var platform))
            return Fail<OrderedProviderResolution>(ProviderErrorCode.MissingDefault,
                $"No platform ordered set exists for '{request.Seam}'.");
        var ids = request.ProjectProviderIds ?? platform;
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            return Fail<OrderedProviderResolution>(ProviderErrorCode.DuplicateSelection,
                "Ordered selection contains a duplicate provider.");
        var fixedEntries = platform.Where(id => !catalog.IsOverridePermitted(request.Seam, id));
        if (!fixedEntries.SequenceEqual(ids.Where(id => !catalog.IsOverridePermitted(request.Seam, id)),
                StringComparer.Ordinal))
            return Fail<OrderedProviderResolution>(ProviderErrorCode.OverrideNotPermitted,
                "Project cannot add, remove, or reorder unpermitted platform entries.");

        var candidates = ImmutableArray.CreateBuilder<ProviderCandidate>();
        foreach (var id in ids)
        {
            if (!platform.Contains(id, StringComparer.Ordinal) &&
                !catalog.IsOverridePermitted(request.Seam, id))
                return Fail<OrderedProviderResolution>(ProviderErrorCode.OverrideNotPermitted,
                    $"Project cannot add provider '{id}'.");
            var candidate = Select(id, request.Seam, request.RequiredAdapterVersion,
                request.RequiredOptionsSchemaVersion, request.RequiredCapabilities);
            if (!candidate.IsSuccess)
                return Fail<OrderedProviderResolution>(candidate.Error!.Code, candidate.Error.Message);
            candidates.Add(candidate.Value!);
        }
        if (candidates.Count == 0 && request.RequiredCapabilities.Count > 0)
            return Fail<OrderedProviderResolution>(ProviderErrorCode.CapabilityUnavailable,
                "An empty ordered set cannot satisfy required capabilities.");
        return ProviderResult<OrderedProviderResolution>.Success(
            new OrderedProviderResolution(request.Seam, candidates.ToImmutable()));
    }

    public ProviderResult<NetworkPolicyResolution> ResolveNetworkPolicy(NetworkPolicyResolutionRequest request)
    {
        if (request is null ||
            !ValidRequirements(request.RequiredAdapterVersion, request.RequiredOptionsSchemaVersion,
                request.RequiredL3L4Capabilities) ||
            request.RequiredL7Capabilities is null ||
            request.RequiredL7Capabilities.Any(string.IsNullOrWhiteSpace))
            return Fail<NetworkPolicyResolution>(ProviderErrorCode.InvalidConfiguration,
                "Network policy resolution request is invalid.");
        if (!catalog.TryGetLayer(NetworkPolicyLayer.L3L4, out _))
            return Fail<NetworkPolicyResolution>(ProviderErrorCode.MissingDefault,
                "Network policy requires an L3/L4 provider.");
        var layers = ImmutableArray.CreateBuilder<NetworkPolicyCandidate>();
        foreach (var layer in Enum.GetValues<NetworkPolicyLayer>())
        {
            if (!catalog.TryGetLayer(layer, out var id)) continue;
            var candidate = Select(id!, ProviderSeam.NetworkPolicy, request.RequiredAdapterVersion,
                request.RequiredOptionsSchemaVersion,
                layer == NetworkPolicyLayer.L3L4 ? request.RequiredL3L4Capabilities : request.RequiredL7Capabilities);
            if (!candidate.IsSuccess)
                return Fail<NetworkPolicyResolution>(candidate.Error!.Code, candidate.Error.Message);
            layers.Add(new NetworkPolicyCandidate(layer, candidate.Value!));
        }
        if (layers.Count == 1 && request.RequiredL7Capabilities.Count > 0)
            return Fail<NetworkPolicyResolution>(ProviderErrorCode.CapabilityUnavailable,
                "No L7 provider can enforce the required capabilities.");
        return ProviderResult<NetworkPolicyResolution>.Success(new NetworkPolicyResolution(layers.ToImmutable()));
    }

    private ProviderResult<ProviderCandidate> Select(string id, ProviderSeam seam, Version version,
        int schema, ImmutableHashSet<string> capabilities)
    {
        if (!catalog.TryGetProvider(id, out var registration) || registration is null)
            return Fail<ProviderCandidate>(ProviderErrorCode.ProviderNotFound, $"Provider '{id}' is missing.");
        var descriptor = registration.Descriptor;
        if (descriptor.Seam != seam)
            return Fail<ProviderCandidate>(ProviderErrorCode.ProviderSeamMismatch,
                $"Provider '{id}' does not implement '{seam}'.");
        if (!registration.Enabled)
            return Fail<ProviderCandidate>(ProviderErrorCode.ProviderDisabled, $"Provider '{id}' is disabled.");
        if (descriptor.AdapterVersion != version)
            return Fail<ProviderCandidate>(ProviderErrorCode.AdapterVersionMismatch,
                $"Provider '{id}' adapter version differs from the requested version.");
        if (registration.OptionsSchemaVersion != schema)
            return Fail<ProviderCandidate>(ProviderErrorCode.OptionsSchemaMismatch,
                $"Provider '{id}' options schema differs from the requested schema.");
        var required = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        if (!required.IsSubsetOf(descriptor.AdvertisedCapabilities))
            return Fail<ProviderCandidate>(ProviderErrorCode.CapabilityUnavailable,
                $"Provider '{id}' does not advertise all required capabilities.");

        return ProviderResult<ProviderCandidate>.Success(new ProviderCandidate(registration, required));
    }

    private static bool ValidRequirements(Version version, int schema, ImmutableHashSet<string> capabilities) =>
        version is not null && schema >= 1 && capabilities is not null &&
        !capabilities.Any(string.IsNullOrWhiteSpace);

    public ProviderResult<PinnedOrderedProviderBinding> PinOrdered(
        string runId, OrderedProviderResolution resolution, IReadOnlyList<ProviderPinInput> inputs)
    {
        if (string.IsNullOrWhiteSpace(runId) || resolution is null || inputs is null ||
            inputs.Count != resolution.Candidates.Length)
            return Fail<PinnedOrderedProviderBinding>(ProviderErrorCode.InvalidNegotiation,
                "Ordered pinning requires one resource per selected provider in order.");
        var bindings = ImmutableArray.CreateBuilder<PinnedProviderBinding>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var pinned = Pin(runId, resolution.Candidates[i], input?.ExpectedResourceId!,
                input?.Negotiation!);
            if (!pinned.IsSuccess)
                return Fail<PinnedOrderedProviderBinding>(pinned.Error!.Code, pinned.Error.Message);
            bindings.Add(pinned.Value!);
        }
        return ProviderResult<PinnedOrderedProviderBinding>.Success(
            new PinnedOrderedProviderBinding(runId, resolution.Seam, bindings.ToImmutable()));
    }

    public ProviderResult<PinnedNetworkPolicyBinding> PinNetworkPolicy(
        string runId, NetworkPolicyResolution resolution, IReadOnlyList<ProviderPinInput> inputs,
        long expectedIntentGeneration, long appliedIntentGeneration)
    {
        if (string.IsNullOrWhiteSpace(runId) || resolution is null || inputs is null ||
            inputs.Count != resolution.Layers.Length || expectedIntentGeneration < 1 ||
            appliedIntentGeneration != expectedIntentGeneration)
            return Fail<PinnedNetworkPolicyBinding>(ProviderErrorCode.InvalidNegotiation,
                "Network policy requires exact layers and a confirmed positive intent generation.");
        var bindings = ImmutableArray.CreateBuilder<PinnedNetworkPolicyLayer>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var pinned = Pin(runId, resolution.Layers[i].Candidate, input?.ExpectedResourceId!,
                input?.Negotiation!);
            if (!pinned.IsSuccess)
                return Fail<PinnedNetworkPolicyBinding>(pinned.Error!.Code, pinned.Error.Message);
            bindings.Add(new PinnedNetworkPolicyLayer(resolution.Layers[i].Layer, pinned.Value!));
        }
        return ProviderResult<PinnedNetworkPolicyBinding>.Success(
            new PinnedNetworkPolicyBinding(runId, appliedIntentGeneration, bindings.ToImmutable()));
    }

    public ProviderResult<PinnedProviderBinding> Pin(
        string runId, ProviderCandidate candidate, string expectedResourceId, ResourceNegotiation negotiation)
    {
        if (string.IsNullOrWhiteSpace(runId) || candidate is null ||
            string.IsNullOrWhiteSpace(expectedResourceId) ||
            negotiation?.Resource is not { } resource ||
            negotiation.Capabilities is null ||
            negotiation.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            string.IsNullOrWhiteSpace(resource.ResourceId) || resource.Generation < 1)
            return Fail<PinnedProviderBinding>(ProviderErrorCode.InvalidNegotiation,
                "Run, resource, generation, and negotiated capabilities must be valid.");
        if (resource.Seam != candidate.Seam || resource.ProviderId != candidate.ProviderId ||
            resource.ResourceId != expectedResourceId)
            return Fail<PinnedProviderBinding>(ProviderErrorCode.ResourceMismatch,
                "Negotiated resource does not match the selected seam, provider, and expected resource identity.");

        var negotiated = negotiation.Capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        if (!negotiated.IsSubsetOf(candidate.AdvertisedCapabilities))
            return Fail<PinnedProviderBinding>(ProviderErrorCode.InvalidNegotiation,
                "Resource advertises capabilities absent from the selected provider descriptor.");
        if (!candidate.RequiredCapabilities.IsSubsetOf(negotiated))
            return Fail<PinnedProviderBinding>(ProviderErrorCode.CapabilityUnavailable,
                "Resource lacks a required negotiated capability.");

        return ProviderResult<PinnedProviderBinding>.Success(
            new PinnedProviderBinding(runId, candidate, resource, negotiated));
    }

    public ProviderResult<PinnedSnapshotNoneBinding> PinSnapshotNone(string runId, ProviderResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(runId) || resolution is null)
            return Fail<PinnedSnapshotNoneBinding>(ProviderErrorCode.InvalidConfiguration,
                "A run identity and snapshot resolution are required.");
        if (!resolution.IsNone)
            return Fail<PinnedSnapshotNoneBinding>(ProviderErrorCode.InvalidConfiguration,
                "Only an explicit Snapshots None resolution can be pinned without a resource.");
        return ProviderResult<PinnedSnapshotNoneBinding>.Success(new PinnedSnapshotNoneBinding(runId));
    }

    private static ProviderResult<T> Fail<T>(ProviderErrorCode code, string message) where T : class =>
        ProviderResult<T>.Failure(code, message);
}
