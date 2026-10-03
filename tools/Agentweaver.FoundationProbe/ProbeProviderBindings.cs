using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.FoundationProbe;

internal static class ProbeProviderBindings
{
    private static readonly Version AdapterVersion = new(0, 1, 0);
    private static readonly ImmutableHashSet<string> NoCapabilities =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private const string OptionsRevision = "foundation-probe-v1";

    public static IReadOnlyList<ProviderPinEvidence> ResolveAndPin(ProbeTarget target, string nonce)
    {
        var registrations = new[]
        {
            Registration(ProviderSeam.Secrets, "azure-key-vault"),
            Registration(ProviderSeam.ObjectStore, "azure-blob"),
            Registration(ProviderSeam.Telemetry, "azure-monitor"),
        };
        var catalogResult = ProviderCatalog.Create(
            registrations,
            [
                new ProviderSelection(ProviderSeam.Secrets, "azure-key-vault"),
                new ProviderSelection(ProviderSeam.ObjectStore, "azure-blob"),
            ],
            [],
            [new ProviderOrderedSelection(ProviderSeam.Telemetry, ImmutableArray.Create("azure-monitor"))]);
        var catalog = catalogResult.Value ?? throw new ProbeException("provider_catalog_invalid");
        var resolver = new ProviderResolver(catalog);

        var secrets = Resolve(resolver, ProviderSeam.Secrets, nonce,
            target.FoundationResources.KeyVaultId);
        var objectStore = Resolve(resolver, ProviderSeam.ObjectStore, nonce,
            target.FoundationResources.BlobContainerId);

        var telemetryResolution = resolver.ResolveOrdered(new OrderedProviderResolutionRequest(
            ProviderSeam.Telemetry, null, AdapterVersion, 1, NoCapabilities));
        if (telemetryResolution.Value is not { } telemetry)
            throw new ProbeException("provider_resolution_failed");
        var telemetryPin = resolver.PinOrdered(nonce, telemetry,
            [
                new ProviderPinInput(target.FoundationResources.AppInsightsResourceId,
                    Negotiation(ProviderSeam.Telemetry, "azure-monitor",
                        target.FoundationResources.AppInsightsResourceId)),
            ]);
        if (telemetryPin.Value is not { Bindings.Length: 1 } pinnedTelemetry)
            throw new ProbeException("provider_pin_failed");

        return [secrets, objectStore, Evidence(pinnedTelemetry.Bindings[0])];
    }

    private static ProviderRegistration Registration(ProviderSeam seam, string id) =>
        new(new ProviderDescriptor(seam, id, AdapterVersion, 1, ProviderHostingPattern.ManagedRuntime, NoCapabilities),
            true, OptionsRevision, 1);

    private static ProviderPinEvidence Resolve(ProviderResolver resolver, ProviderSeam seam, string nonce, string resourceId)
    {
        var providerId = seam == ProviderSeam.Secrets ? "azure-key-vault" : "azure-blob";
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            seam, null, AdapterVersion, 1, NoCapabilities));
        var candidate = resolution.Value?.Candidate ?? throw new ProbeException("provider_resolution_failed");
        var result = resolver.Pin(nonce, candidate, resourceId,
            Negotiation(seam, providerId, resourceId));
        return Evidence(result.Value ?? throw new ProbeException("provider_pin_failed"));
    }

    private static ResourceNegotiation Negotiation(ProviderSeam seam, string providerId, string resourceId) =>
        new(new ProviderResourceRef(seam, providerId, resourceId, 1), NoCapabilities);

    private static ProviderPinEvidence Evidence(PinnedProviderBinding binding) =>
        new(binding.Seam.ToString(), binding.ProviderId, binding.AdapterVersion.ToString(),
            binding.OptionsSchemaVersion, binding.OptionsRevision, binding.Resource.ResourceId,
            binding.Resource.Generation, binding.NegotiatedCapabilities.Order(StringComparer.Ordinal).ToArray());
}
