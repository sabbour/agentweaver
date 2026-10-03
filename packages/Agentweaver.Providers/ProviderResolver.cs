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
        if (!catalog.TryGetProvider(id, out var registration) || registration is null)
            return Fail<ProviderResolution>(ProviderErrorCode.ProviderNotFound, $"Provider '{id}' is missing.");
        var descriptor = registration.Descriptor;
        if (descriptor.Seam != request.Seam)
            return Fail<ProviderResolution>(ProviderErrorCode.ProviderSeamMismatch,
                $"Provider '{id}' does not implement '{request.Seam}'.");
        if (!registration.Enabled)
            return Fail<ProviderResolution>(ProviderErrorCode.ProviderDisabled, $"Provider '{id}' is disabled.");
        if (descriptor.AdapterVersion != request.RequiredAdapterVersion)
            return Fail<ProviderResolution>(ProviderErrorCode.AdapterVersionMismatch,
                $"Provider '{id}' adapter version differs from the requested version.");
        if (registration.OptionsSchemaVersion != request.RequiredOptionsSchemaVersion)
            return Fail<ProviderResolution>(ProviderErrorCode.OptionsSchemaMismatch,
                $"Provider '{id}' options schema differs from the requested schema.");
        var required = request.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        if (!required.IsSubsetOf(descriptor.AdvertisedCapabilities))
            return Fail<ProviderResolution>(ProviderErrorCode.CapabilityUnavailable,
                $"Provider '{id}' does not advertise all required capabilities.");

        return ProviderResult<ProviderResolution>.Success(
            ProviderResolution.Selected(new ProviderCandidate(registration, required)));
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
