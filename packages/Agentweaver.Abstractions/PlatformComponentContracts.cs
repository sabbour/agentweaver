using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>Stable area identifiers returned by the platform components report.</summary>
public static class PlatformComponentAreaIds
{
    public const string Sessions = "sessions";
    public const string Snapshots = "snapshots";
    public const string Sandbox = "sandbox";
    public const string Storage = "storage";
    public const string Memory = "memory";
    public const string Policy = "policy";
    public const string Guardrails = "guardrails";
    public const string NetworkPolicy = "networkPolicy";
    public const string Cost = "cost";
    public const string ApplicationHosting = "applicationHosting";
    public const string Canvas = "canvas";
    public const string Secrets = "secrets";
    public const string SourceControl = "sourceControl";
    public const string Telemetry = "telemetry";
    public const string Messaging = "messaging";
    public const string ObjectStore = "objectStore";
}

/// <summary>Services that report the active implementations for one or more platform component areas.</summary>
public static class PlatformComponentOwners
{
    public const string EventsAndSessions = "eventsAndSessions";
    public const string Environment = "environment";
    public const string Knowledge = "knowledge";
    public const string Orchestrator = "orchestrator";
    public const string IdentityBroker = "identityBroker";
    /// <summary>The area has no backend service that reports it.</summary>
    public const string None = "none";

    /// <summary>Backend owners that Projects asks for a report, in a stable order.</summary>
    public static ImmutableArray<string> Reporting { get; } =
        [EventsAndSessions, Environment, Knowledge, Orchestrator, IdentityBroker];
}

/// <summary>How many implementations can serve an area at the same time.</summary>
public static class PlatformComponentCardinalities
{
    public const string Exclusive = "exclusive";
    public const string OrderedComposite = "orderedComposite";
    public const string PlatformSingleton = "platformSingleton";
    public const string Layered = "layered";
    public const string KeyedByMeterSource = "keyedByMeterSource";
    public const string PerApplication = "perApplication";
    public const string PerCanvas = "perCanvas";
}

/// <summary>Readiness values for an area or one active implementation.</summary>
public static class PlatformComponentReadiness
{
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string NotReady = "notReady";
    public const string Disabled = "disabled";
    public const string NotAvailable = "notAvailable";
    public const string Unknown = "unknown";

    /// <summary>All readiness values that a report may contain.</summary>
    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal, Ready, Degraded, NotReady, Disabled, NotAvailable, Unknown);
}

/// <summary>Stable reason codes that explain a readiness value other than <c>ready</c>.</summary>
public static class PlatformComponentReasons
{
    /// <summary>Projects didn't receive a valid report from the owner service in time.</summary>
    public const string OwnerUnavailable = "ownerUnavailable";
    /// <summary>The area has no backend owner service in this release.</summary>
    public const string NoBackendOwner = "noBackendOwner";
    /// <summary>The owner has no implementation selected for the area.</summary>
    public const string NotSelected = "notSelected";
    /// <summary>The area has no implementation in this release.</summary>
    public const string NotImplemented = "notImplemented";
    /// <summary>The owner's readiness check failed.</summary>
    public const string ReadinessCheckFailed = "readinessCheckFailed";
    /// <summary>The owner's readiness check didn't finish in time.</summary>
    public const string ReadinessTimedOut = "readinessTimedOut";

    /// <summary>All reason codes that a report may contain.</summary>
    public static ImmutableHashSet<string> All { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal, OwnerUnavailable, NoBackendOwner, NotSelected, NotImplemented,
        ReadinessCheckFailed, ReadinessTimedOut);
}

/// <summary>
/// Report of the active implementation for every platform component area. It contains
/// only stable identities, versions, and readiness. It never contains option values,
/// option revisions, endpoints, credentials, or resource identifiers.
/// </summary>
/// <param name="ObservedAt">Time that Projects finished collecting owner reports.</param>
/// <param name="Areas">One entry for each known area, in catalog order.</param>
public sealed record PlatformComponentReport(
    DateTimeOffset ObservedAt,
    IReadOnlyList<PlatformComponentAreaReport> Areas);

/// <summary>Active and available implementations for one platform component area.</summary>
/// <param name="Area">Stable area identifier from <see cref="PlatformComponentAreaIds"/>.</param>
/// <param name="Purpose">Short description of what the area does.</param>
/// <param name="Owner">Owner identifier from <see cref="PlatformComponentOwners"/>.</param>
/// <param name="Cardinality">Cardinality identifier from <see cref="PlatformComponentCardinalities"/>.</param>
/// <param name="Readiness">Combined readiness for the area.</param>
/// <param name="Reason">Reason code when readiness isn't <c>ready</c>; otherwise null.</param>
/// <param name="Active">Implementations that currently serve the area.</param>
/// <param name="Available">Registered implementations that the owner could select.</param>
public sealed record PlatformComponentAreaReport(
    string Area,
    string Purpose,
    string Owner,
    string Cardinality,
    string Readiness,
    string? Reason,
    IReadOnlyList<PlatformComponentActiveImplementation> Active,
    IReadOnlyList<PlatformComponentAvailableImplementation> Available);

/// <summary>One implementation that currently serves an area.</summary>
/// <param name="ImplementationId">Stable implementation identifier.</param>
/// <param name="DisplayName">Readable implementation name.</param>
/// <param name="Version">Adapter version.</param>
/// <param name="Hosting">Hosting pattern, such as <c>inProcess</c> or <c>kubernetesController</c>.</param>
/// <param name="Layer">Network policy layer, such as <c>l3l4</c> or <c>l7</c>, when the area is layered.</param>
/// <param name="MeterSource">Usage meter source when the area is keyed by meter source.</param>
/// <param name="Order">One-based position when the area runs implementations in order.</param>
/// <param name="Readiness">Readiness of this implementation as seen by its owner.</param>
/// <param name="Reason">Reason code when readiness isn't <c>ready</c>; otherwise null.</param>
/// <param name="ObservedAt">Time that the owner observed readiness.</param>
public sealed record PlatformComponentActiveImplementation(
    string ImplementationId,
    string DisplayName,
    string Version,
    string Hosting,
    string? Layer,
    string? MeterSource,
    int? Order,
    string Readiness,
    string? Reason,
    DateTimeOffset ObservedAt);

/// <summary>One registered implementation that an owner could select for an area.</summary>
/// <param name="ImplementationId">Stable implementation identifier.</param>
/// <param name="DisplayName">Readable implementation name.</param>
/// <param name="Version">Adapter version.</param>
/// <param name="Enabled">Whether the owner's registration allows selection.</param>
public sealed record PlatformComponentAvailableImplementation(
    string ImplementationId,
    string DisplayName,
    string Version,
    bool Enabled);

/// <summary>Report that one owner service returns for the areas it owns.</summary>
/// <param name="Owner">Owner identifier from <see cref="PlatformComponentOwners"/>.</param>
/// <param name="ObservedAt">Time that the owner built the report.</param>
/// <param name="Areas">Reports for the areas this owner serves.</param>
public sealed record PlatformComponentOwnerReport(
    string Owner,
    DateTimeOffset ObservedAt,
    IReadOnlyList<PlatformComponentAreaReport> Areas);

/// <summary>Static catalog entry for one platform component area.</summary>
/// <param name="Area">Stable area identifier.</param>
/// <param name="Name">Readable area name, as used in the architecture catalog.</param>
/// <param name="Purpose">Short description of what the area does.</param>
/// <param name="Owner">Owner service that reports the area.</param>
/// <param name="Cardinality">How many implementations can serve the area.</param>
/// <param name="ProviderArea">Matching provider contract area, or null when there's none.</param>
public sealed record PlatformComponentAreaDefinition(
    string Area,
    string Name,
    string Purpose,
    string Owner,
    string Cardinality,
    ProviderSeam? ProviderArea);

/// <summary>Owner readiness result applied to the owner's active implementations.</summary>
/// <param name="Readiness">Readiness value from <see cref="PlatformComponentReadiness"/>.</param>
/// <param name="Reason">Reason code, or null when the check passed.</param>
public sealed record PlatformComponentReadinessResult(string Readiness, string? Reason)
{
    /// <summary>The readiness check passed.</summary>
    public static PlatformComponentReadinessResult Ready { get; } = new(PlatformComponentReadiness.Ready, null);
}
