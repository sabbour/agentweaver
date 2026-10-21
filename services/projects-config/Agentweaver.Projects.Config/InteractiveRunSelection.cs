using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Interactive admission uses owner-held runtime defaults rather than exposing admin-only configuration.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InteractiveRunSelectionRequest(long ExpectedProjectConfigRevision);

/// <summary>Builds an immutable interactive selection context from current server configuration.</summary>
public sealed partial class ProjectsConfigService
{
    /// <summary>Collects configured model references and selected provider versions without inventing runtime availability.</summary>
    private RunSelectionContext CreateInteractiveRunContext(ProjectConfiguration project, PlatformRuntimeDefaults defaults)
    {
        var references = new[] { project.ModelSelection?.Reference, defaults.ModelSelection?.Reference }
            .Concat(project.ModelTaskOverrides.Select(item => item.Selection.Reference))
            .Concat(project.ModelRoleOverrides.Select(item => item.Selection.Reference))
            .Concat(project.ModelAgentOverrides.Select(item => item.Selection.Reference))
            .Concat(defaults.ModelTaskDefaults.Select(item => item.Selection.Reference))
            .Concat(defaults.ModelRoleDefaults.Select(item => item.Selection.Reference))
            .Concat(defaults.ModelAgentDefaults.Select(item => item.Selection.Reference))
            .OfType<string>().ToImmutableHashSet(StringComparer.Ordinal);
        var requirements = ImmutableArray.CreateBuilder<ProviderRequirement>();
        foreach (var seam in Enum.GetValues<ProviderSeam>())
        {
            var ids = new List<string>();
            if (providerCatalog.TryGetDefault(seam, out var defaultId) && defaultId is not null)
                ids.Add(project.ProviderOverrides.SingleOrDefault(item => item.Seam == seam)?.ProviderId ?? defaultId);
            if (providerCatalog.TryGetOrdered(seam, out var ordered))
                ids.AddRange(project.OrderedProviderOverrides.SingleOrDefault(item => item.Seam == seam)?.ProviderIds ?? ordered);
            if (seam == ProviderSeam.NetworkPolicy)
                foreach (var layer in Enum.GetValues<NetworkPolicyLayer>())
                    if (providerCatalog.TryGetLayer(layer, out var layerId) && layerId is not null)
                        ids.Add(layerId);
            if (ids.Count == 0)
                continue;
            var registrations = ids.Select(id =>
                providerCatalog.TryGetProvider(id, out var registration) && registration is not null
                    ? registration
                    : throw new ProjectConfigException("provider_not_found",
                        "A selected interactive provider is not registered.", StatusCodes.Status409Conflict)).ToArray();
            var descriptor = registrations[0].Descriptor;
            if (registrations.Any(item => item.Descriptor.AdapterVersion != descriptor.AdapterVersion ||
                item.OptionsSchemaVersion != descriptor.OptionsSchemaVersion))
                throw new ProjectConfigException("interactive_provider_version_conflict",
                    "Selected providers in one area must use compatible contract versions.",
                    StatusCodes.Status409Conflict);
            requirements.Add(new ProviderRequirement
            {
                Seam = seam,
                RequiredAdapterVersion = descriptor.AdapterVersion.ToString(),
                RequiredOptionsSchemaVersion = descriptor.OptionsSchemaVersion
            });
        }
        foreach (var meter in new[] { SdkMeterSources.CopilotNanoAiu, SdkMeterSources.ByokTokens })
            if (providerCatalog.TryGetMeterSource(meter, out var id) && id is not null &&
                providerCatalog.TryGetProvider(id, out var registration) && registration is not null)
                requirements.Add(new ProviderRequirement
                {
                    Seam = ProviderSeam.Cost,
                    MeterSource = meter,
                    RequiredAdapterVersion = registration.Descriptor.AdapterVersion.ToString(),
                    RequiredOptionsSchemaVersion = registration.OptionsSchemaVersion
                });
        return new RunSelectionContext
        {
            Revision = "interactive-v1",
            AvailableModelSelectionReferences = references,
            ProviderRequirements = requirements.ToImmutable()
        };
    }
}
