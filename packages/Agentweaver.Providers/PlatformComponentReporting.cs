using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Providers;

/// <summary>Builds platform component area reports from an owner's frozen provider catalog.</summary>
public static class PlatformComponentCatalogReporter
{
    /// <summary>
    /// Describes one area from the catalog selection that matches the area's cardinality. Available
    /// entries list every registration for the area. Option values and revisions are never copied.
    /// </summary>
    /// <param name="catalog">The owner's frozen provider catalog.</param>
    /// <param name="providerArea">Provider contract area to describe.</param>
    /// <param name="readiness">Owner readiness result applied to each active implementation.</param>
    /// <param name="observedAt">Time that readiness was observed.</param>
    public static PlatformComponentAreaReport DescribeArea(
        ProviderCatalog catalog,
        ProviderSeam providerArea,
        PlatformComponentReadinessResult readiness,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(readiness);
        var definition = PlatformComponentCatalog.ForProviderArea(providerArea);
        var active = ImmutableArray.CreateBuilder<PlatformComponentActiveImplementation>();
        void AddActive(string? id, NetworkPolicyLayer? layer = null, string? meterSource = null, int? order = null)
        {
            if (id is not null && catalog.TryGetProvider(id, out var registration) && registration is not null)
                active.Add(PlatformComponentReportBuilder.Active(
                    registration.Descriptor, readiness, observedAt, layer, meterSource, order));
        }

        switch (ProviderSeams.Cardinality(providerArea))
        {
            case ProviderCardinality.Exclusive or ProviderCardinality.PlatformSingleton:
                if (catalog.TryGetDefault(providerArea, out var id))
                    AddActive(id);
                break;
            case ProviderCardinality.OrderedComposite:
                if (catalog.TryGetOrdered(providerArea, out var ids))
                {
                    for (var index = 0; index < ids.Length; index++)
                        AddActive(ids[index], order: index + 1);
                }
                break;
            case ProviderCardinality.Layered:
                foreach (var layer in new[] { NetworkPolicyLayer.L3L4, NetworkPolicyLayer.L7 })
                {
                    if (catalog.TryGetLayer(layer, out var layerId))
                        AddActive(layerId, layer: layer);
                }
                break;
            case ProviderCardinality.KeyedByMeterSource:
                foreach (var selection in catalog.MeterSourceSelections)
                    AddActive(selection.ProviderId, meterSource: selection.MeterSource);
                break;
        }

        var available = catalog.Registrations
            .Where(registration => registration.Descriptor.Seam == providerArea)
            .Select(PlatformComponentReportBuilder.Available);
        return PlatformComponentReportBuilder.Area(definition.Area, active.ToImmutable(), available);
    }

    /// <summary>
    /// Describes an area that an owner serves with one built-in implementation outside its provider
    /// catalog. A null descriptor means nothing is selected, and the area is <c>disabled</c>.
    /// </summary>
    /// <param name="area">Area identifier from <see cref="PlatformComponentAreaIds"/>.</param>
    /// <param name="selected">Descriptor of the implementation in use, or null.</param>
    /// <param name="readiness">Owner readiness result applied to the implementation.</param>
    /// <param name="observedAt">Time that readiness was observed.</param>
    public static PlatformComponentAreaReport DescribeSelected(
        string area,
        ProviderDescriptor? selected,
        PlatformComponentReadinessResult readiness,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (selected is null)
            return PlatformComponentReportBuilder.Area(area, [], []);

        return PlatformComponentReportBuilder.Area(
            area,
            [PlatformComponentReportBuilder.Active(selected, readiness, observedAt)],
            [new PlatformComponentAvailableImplementation(
                selected.Id,
                PlatformComponentCatalog.DisplayName(selected.Id),
                selected.AdapterVersion.ToString(),
                true)]);
    }

    /// <summary>
    /// Creates a descriptor for a built-in implementation that has no provider catalog registration.
    /// The adapter version is the version of the assembly that contains the implementation.
    /// </summary>
    /// <param name="providerArea">Provider contract area the implementation serves.</param>
    /// <param name="id">Stable implementation identifier.</param>
    /// <param name="implementationType">A type from the implementation assembly.</param>
    /// <param name="hosting">Where the implementation runs.</param>
    public static ProviderDescriptor BuiltInDescriptor(
        ProviderSeam providerArea,
        string id,
        Type implementationType,
        ProviderHostingPattern hosting = ProviderHostingPattern.InProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(implementationType);
        return new ProviderDescriptor(
            providerArea,
            id,
            implementationType.Assembly.GetName().Version ?? new Version(1, 0),
            1,
            hosting,
            ImmutableHashSet<string>.Empty);
    }

    /// <summary>
    /// Runs the owner's readiness check, then builds the owner report from the area callback.
    /// </summary>
    /// <param name="owner">Owner identifier from <see cref="PlatformComponentOwners"/>.</param>
    /// <param name="readinessCheck">The owner's existing readiness check.</param>
    /// <param name="describe">Builds the owner's area reports from the readiness result and observation time.</param>
    /// <param name="timeProvider">Clock for the observation time.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    public static async Task<PlatformComponentOwnerReport> CreateOwnerReportAsync(
        string owner,
        Func<CancellationToken, Task<bool>> readinessCheck,
        Func<PlatformComponentReadinessResult, DateTimeOffset, IEnumerable<PlatformComponentAreaReport>> describe,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var readiness = await PlatformComponentReadinessProbe.RunAsync(
            readinessCheck, PlatformComponentReadinessProbe.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var observedAt = timeProvider.GetUtcNow();
        return new PlatformComponentOwnerReport(owner, observedAt, describe(readiness, observedAt).ToImmutableArray());
    }
}

/// <summary>
/// Validated settings for the private service token that Projects presents to an owner's
/// <c>GET /internal/platform/components</c> route. Owners don't map the route when the section is absent.
/// </summary>
/// <param name="Authority">HTTPS token issuer whose OpenID Connect discovery document lists the signing keys.</param>
/// <param name="Audience">Private absolute HTTPS audience, distinct from the owner's public audience.</param>
/// <param name="CallerSubject">Exact <c>sub</c> claim of the Projects workload.</param>
public sealed record PlatformComponentServiceAuthenticationOptions(
    Uri Authority,
    Uri Audience,
    string CallerSubject)
{
    /// <summary>Configuration section that holds the settings.</summary>
    public const string SectionName = "PlatformComponents:ServiceAuthentication";
    /// <summary>Authentication scheme name for the private service token.</summary>
    public const string SchemeName = "PlatformComponentsService";
    /// <summary>Authorization policy name for the owner route.</summary>
    public const string PolicyName = "PlatformComponentsService";
    /// <summary>Owner route that returns <see cref="PlatformComponentOwnerReport"/>.</summary>
    public const string OwnerRoute = "/internal/platform/components";

    /// <summary>
    /// Reads the optional settings. Returns null when the section is absent and throws when it is
    /// partial or invalid, so a misconfigured owner fails at startup instead of reporting wrongly.
    /// </summary>
    /// <param name="configuration">Service configuration.</param>
    /// <param name="publicAudiences">Public audiences that the private audience must not equal.</param>
    public static PlatformComponentServiceAuthenticationOptions? ReadOptional(
        IConfiguration configuration,
        params string?[] publicAudiences)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var authority = section["Authority"];
        var audience = section["Audience"];
        var callerSubject = section["CallerSubject"];
        if (authority is null && audience is null && callerSubject is null)
            return null;

        if (!TryHttps(authority, out var authorityUri) ||
            !TryHttps(audience, out var audienceUri) ||
            string.IsNullOrWhiteSpace(callerSubject) ||
            publicAudiences.Any(publicAudience => publicAudience is not null &&
                (string.Equals(publicAudience, audience, StringComparison.Ordinal) ||
                 Uri.TryCreate(publicAudience, UriKind.Absolute, out var publicUri) &&
                 string.Equals(publicUri.AbsoluteUri, audienceUri.AbsoluteUri, StringComparison.Ordinal))))
            throw new InvalidOperationException(
                $"'{SectionName}' requires an HTTPS Authority, a distinct absolute HTTPS Audience, and a CallerSubject.");

        return new PlatformComponentServiceAuthenticationOptions(authorityUri, audienceUri, callerSubject.Trim());
    }

    private static bool TryHttps(string? value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrEmpty(parsed.Host) &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment) &&
            (uri = parsed) is not null;
    }
}
