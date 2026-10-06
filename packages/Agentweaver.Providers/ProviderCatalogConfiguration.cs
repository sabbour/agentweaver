using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Providers;

public static class ProviderCatalogConfiguration
{
    public static ProviderCatalog Load(
        IConfiguration configuration,
        string sectionPath = "ProjectsConfig:ProviderCatalog")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        var section = configuration.GetSection(sectionPath);
        if (!section.Exists())
            throw new InvalidOperationException(
                $"The provider catalog snapshot supplied by the catalog owner is missing at '{sectionPath}'.");

        var settings = section.Get<ProviderCatalogSettings>()
            ?? throw new InvalidOperationException($"Provider catalog settings at '{sectionPath}' are invalid.");
        var registrations = settings.Registrations.Select(item =>
        {
            var seam = ParseEnum<ProviderSeam>(item.Seam, "provider seam");
            var hosting = ParseEnum<ProviderHostingPattern>(item.Hosting, "provider hosting pattern");
            if (!Version.TryParse(item.AdapterVersion, out var adapterVersion))
                throw new InvalidOperationException($"Provider '{item.Id}' has an invalid adapter version.");
            return new ProviderRegistration(
                new ProviderDescriptor(
                    seam,
                    item.Id,
                    adapterVersion,
                    item.OptionsSchemaVersion,
                    hosting,
                    item.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)),
                item.Enabled,
                item.OptionsRevision,
                item.OptionsSchemaVersion);
        }).ToArray();

        var defaults = settings.Defaults.Select(item =>
            new ProviderSelection(ParseEnum<ProviderSeam>(item.Seam, "provider seam"), item.ProviderId));
        var overrides = settings.PermittedOverrides.Select(item =>
            new ProviderOverridePermission(ParseEnum<ProviderSeam>(item.Seam, "provider seam"), item.ProviderId));
        var ordered = settings.OrderedSelections.Select(item =>
            new ProviderOrderedSelection(
                ParseEnum<ProviderSeam>(item.Seam, "provider seam"),
                item.ProviderIds.ToImmutableArray()));
        var layers = settings.LayerSelections.Select(item =>
            new ProviderLayerSelection(ParseEnum<NetworkPolicyLayer>(item.Layer, "network policy layer"), item.ProviderId));
        var meterSources = settings.MeterSourceSelections.Select(item =>
            new ProviderMeterSourceSelection(item.MeterSource, item.ProviderId));

        var result = ProviderCatalog.Create(
            registrations, defaults, overrides, ordered, layers, meterSources);
        return result.Value
            ?? throw new InvalidOperationException(
                $"The provider catalog owner supplied an invalid snapshot at '{sectionPath}': {result.Error?.Code}: {result.Error?.Message}");
    }

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Provider catalog {name} '{value}' is invalid.");

    private sealed class ProviderCatalogSettings
    {
        public List<ProviderRegistrationSettings> Registrations { get; init; } = [];
        public List<ProviderSelectionSettings> Defaults { get; init; } = [];
        public List<ProviderOverrideSettings> PermittedOverrides { get; init; } = [];
        public List<ProviderOrderedSelectionSettings> OrderedSelections { get; init; } = [];
        public List<ProviderLayerSelectionSettings> LayerSelections { get; init; } = [];
        public List<ProviderMeterSourceSelectionSettings> MeterSourceSelections { get; init; } = [];
    }

    private sealed class ProviderRegistrationSettings
    {
        public string Seam { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string AdapterVersion { get; init; } = string.Empty;
        public int OptionsSchemaVersion { get; init; }
        public string Hosting { get; init; } = string.Empty;
        public string[] AdvertisedCapabilities { get; init; } = [];
        public bool Enabled { get; init; }
        public string OptionsRevision { get; init; } = string.Empty;
    }

    private sealed class ProviderSelectionSettings
    {
        public string Seam { get; init; } = string.Empty;
        public string ProviderId { get; init; } = string.Empty;
    }

    private sealed class ProviderOverrideSettings
    {
        public string Seam { get; init; } = string.Empty;
        public string ProviderId { get; init; } = string.Empty;
    }

    private sealed class ProviderOrderedSelectionSettings
    {
        public string Seam { get; init; } = string.Empty;
        public string[] ProviderIds { get; init; } = [];
    }

    private sealed class ProviderLayerSelectionSettings
    {
        public string Layer { get; init; } = string.Empty;
        public string ProviderId { get; init; } = string.Empty;
    }

    private sealed class ProviderMeterSourceSelectionSettings
    {
        public string MeterSource { get; init; } = string.Empty;
        public string ProviderId { get; init; } = string.Empty;
    }
}
