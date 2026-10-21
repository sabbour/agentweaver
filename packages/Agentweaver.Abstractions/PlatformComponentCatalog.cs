using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>
/// Static catalog of platform component areas. Purpose and cardinality text match the
/// architecture catalog in <c>docs/architecture/design/provider-seams.md</c>; a test keeps them in sync.
/// </summary>
public static class PlatformComponentCatalog
{
    /// <summary>All areas, in the order that reports return them.</summary>
    public static ImmutableArray<PlatformComponentAreaDefinition> Areas { get; } =
    [
        new(PlatformComponentAreaIds.Sessions, "Sessions", "Journal, playback, and optional capture",
            PlatformComponentOwners.EventsAndSessions, PlatformComponentCardinalities.Exclusive, ProviderSeam.Sessions),
        new(PlatformComponentAreaIds.Snapshots, "Snapshots", "Capture and explicitly restore environments",
            PlatformComponentOwners.Environment, PlatformComponentCardinalities.Exclusive, ProviderSeam.Snapshots),
        new(PlatformComponentAreaIds.Sandbox, "Sandbox",
            "Provision, observe, fence, and release AgentHost environments",
            PlatformComponentOwners.Environment, PlatformComponentCardinalities.Exclusive, ProviderSeam.Sandbox),
        new(PlatformComponentAreaIds.Storage, "Storage", "Durable agent workspace volumes and bindings",
            PlatformComponentOwners.Environment, PlatformComponentCardinalities.Exclusive, ProviderSeam.Storage),
        new(PlatformComponentAreaIds.Memory, "Memory", "Authoritative knowledge records and retrieval",
            PlatformComponentOwners.Knowledge, PlatformComponentCardinalities.Exclusive, ProviderSeam.Memory),
        new(PlatformComponentAreaIds.Policy, "Policy", "Decide permitted actions through AGT",
            PlatformComponentOwners.Orchestrator, PlatformComponentCardinalities.PlatformSingleton, ProviderSeam.Policy),
        new(PlatformComponentAreaIds.Guardrails, "Guardrails", "Classify untrusted model inputs/results/output",
            PlatformComponentOwners.Orchestrator, PlatformComponentCardinalities.OrderedComposite, ProviderSeam.Guardrails),
        new(PlatformComponentAreaIds.NetworkPolicy, "Network Policy", "Materialize and verify egress intent",
            PlatformComponentOwners.Environment, PlatformComponentCardinalities.Layered, ProviderSeam.NetworkPolicy),
        new(PlatformComponentAreaIds.Cost, "Cost", "Price durable usage by meter source",
            PlatformComponentOwners.EventsAndSessions, PlatformComponentCardinalities.KeyedByMeterSource, ProviderSeam.Cost),
        new(PlatformComponentAreaIds.ApplicationHosting, "Application Hosting",
            "Serve durable web preview and published revisions",
            PlatformComponentOwners.Environment, PlatformComponentCardinalities.PerApplication,
            ProviderSeam.ApplicationHosting),
        new(PlatformComponentAreaIds.Canvas, "Canvas",
            "Render interactive content and supply a typed host bridge",
            PlatformComponentOwners.None, PlatformComponentCardinalities.PerCanvas, null),
        new(PlatformComponentAreaIds.Secrets, "Secrets", "Resolve credential references for trusted callers",
            PlatformComponentOwners.IdentityBroker, PlatformComponentCardinalities.PlatformSingleton, ProviderSeam.Secrets),
        new(PlatformComponentAreaIds.SourceControl, "Source Control", "Repository access, changes, PRs, and merge",
            PlatformComponentOwners.Orchestrator, PlatformComponentCardinalities.Exclusive, ProviderSeam.SourceControl),
        new(PlatformComponentAreaIds.Telemetry, "Telemetry", "Export traces, metrics, and logs",
            PlatformComponentOwners.EventsAndSessions, PlatformComponentCardinalities.OrderedComposite, ProviderSeam.Telemetry),
        new(PlatformComponentAreaIds.Messaging, "Messaging", "Carry durable cross-service events",
            PlatformComponentOwners.EventsAndSessions, PlatformComponentCardinalities.PlatformSingleton, ProviderSeam.Messaging),
        new(PlatformComponentAreaIds.ObjectStore, "Object Store", "Hold opaque platform blobs and artifacts",
            PlatformComponentOwners.EventsAndSessions, PlatformComponentCardinalities.PlatformSingleton, ProviderSeam.ObjectStore),
    ];

    private static readonly ImmutableDictionary<string, PlatformComponentAreaDefinition> ByArea =
        Areas.ToImmutableDictionary(area => area.Area, StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> DisplayNames =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new KeyValuePair<string, string>[]
        {
            new("postgres.native-sessions", "Native PostgreSQL journal"),
            new("postgres.native-messaging", "PostgreSQL transactional outbox"),
            new("postgres.native-memory", "Native PostgreSQL memory"),
            new("cosmos.memory", "Azure Cosmos DB memory"),
            new("redis.memory", "Redis memory"),
            new("copilot.usage-cost", "Copilot AI-credit pricing"),
            new("azure.byok-usage-cost", "Azure BYOK pricing"),
            new("agent-sandbox", "agent-sandbox on Kubernetes"),
            new("azure-files-csi", "Azure Files CSI"),
            new("cilium", "Cilium"),
            new("agt.dotnet-yaml", "AGT .NET kernel with YAML rules"),
            new("github", "GitHub"),
            new("opentelemetry", "OpenTelemetry"),
            new("azure-blob", "Azure Blob Storage"),
            new("azure-key-vault", "Azure Key Vault"),
        });

    /// <summary>Returns the catalog entry for an area identifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The area isn't in the catalog.</exception>
    public static PlatformComponentAreaDefinition Get(string area) =>
        ByArea.TryGetValue(area, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(area));

    /// <summary>Returns the catalog entry that matches a provider contract area.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No catalog entry uses the provider area.</exception>
    public static PlatformComponentAreaDefinition ForProviderArea(ProviderSeam providerArea) =>
        Areas.FirstOrDefault(area => area.ProviderArea == providerArea)
            ?? throw new ArgumentOutOfRangeException(nameof(providerArea));

    /// <summary>Returns a readable name for an implementation ID, or the ID when no name is known.</summary>
    public static string DisplayName(string implementationId) =>
        DisplayNames.TryGetValue(implementationId, out var name) ? name : implementationId;
}

/// <summary>
/// Builds owner and Projects reports from provider descriptors and readiness results.
/// It copies only identity, version, hosting, and readiness, never option revisions.
/// </summary>
public static class PlatformComponentReportBuilder
{
    /// <summary>Creates an active implementation entry from a provider descriptor.</summary>
    /// <param name="descriptor">Descriptor of the selected implementation.</param>
    /// <param name="readiness">Owner readiness result to apply.</param>
    /// <param name="observedAt">Time that readiness was observed.</param>
    /// <param name="layer">Network policy layer, when the area is layered.</param>
    /// <param name="meterSource">Usage meter source, when the area is keyed by meter source.</param>
    /// <param name="order">One-based position, when the area runs implementations in order.</param>
    public static PlatformComponentActiveImplementation Active(
        ProviderDescriptor descriptor,
        PlatformComponentReadinessResult readiness,
        DateTimeOffset observedAt,
        NetworkPolicyLayer? layer = null,
        string? meterSource = null,
        int? order = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(readiness);
        return new(
            descriptor.Id,
            PlatformComponentCatalog.DisplayName(descriptor.Id),
            descriptor.AdapterVersion.ToString(),
            HostingName(descriptor.Hosting),
            layer switch
            {
                NetworkPolicyLayer.L3L4 => "l3l4",
                NetworkPolicyLayer.L7 => "l7",
                _ => null,
            },
            meterSource,
            order,
            readiness.Readiness,
            readiness.Reason,
            observedAt);
    }

    /// <summary>Creates an available implementation entry from a catalog registration.</summary>
    public static PlatformComponentAvailableImplementation Available(ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new(
            registration.Descriptor.Id,
            PlatformComponentCatalog.DisplayName(registration.Descriptor.Id),
            registration.Descriptor.AdapterVersion.ToString(),
            registration.Enabled);
    }

    /// <summary>
    /// Creates an area report from its active and available implementations. An area with no
    /// active implementation is <c>disabled</c> with reason <c>notSelected</c>.
    /// </summary>
    public static PlatformComponentAreaReport Area(
        string area,
        IEnumerable<PlatformComponentActiveImplementation> active,
        IEnumerable<PlatformComponentAvailableImplementation> available)
    {
        var definition = PlatformComponentCatalog.Get(area);
        var activeList = active.ToImmutableArray();
        var availableList = available.OrderBy(item => item.ImplementationId, StringComparer.Ordinal).ToImmutableArray();
        if (activeList.IsEmpty)
            return Create(definition, PlatformComponentReadiness.Disabled, PlatformComponentReasons.NotSelected, [], availableList);

        var worst = activeList.MaxBy(item => Severity(item.Readiness))!;
        return Create(definition, worst.Readiness, worst.Reason, activeList, availableList);
    }

    /// <summary>Creates an area report with no implementations and a fixed readiness.</summary>
    public static PlatformComponentAreaReport Empty(string area, string readiness, string reason) =>
        Create(PlatformComponentCatalog.Get(area), readiness, reason, [], []);

    /// <summary>
    /// Combines owner reports into the full report. Every catalog area is present. An area whose
    /// owner report is missing, or doesn't contain the area, is <c>unknown</c> with reason
    /// <c>ownerUnavailable</c>. Purpose, owner, and cardinality always come from the static catalog.
    /// </summary>
    /// <param name="ownerReports">Valid owner reports keyed by owner; missing or null means no answer.</param>
    /// <param name="observedAt">Time that Projects finished collecting.</param>
    public static PlatformComponentReport Compose(
        IReadOnlyDictionary<string, PlatformComponentOwnerReport?> ownerReports,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(ownerReports);
        var areas = ImmutableArray.CreateBuilder<PlatformComponentAreaReport>(PlatformComponentCatalog.Areas.Length);
        foreach (var definition in PlatformComponentCatalog.Areas)
        {
            if (definition.Owner == PlatformComponentOwners.None)
            {
                areas.Add(Create(definition, PlatformComponentReadiness.NotAvailable,
                    PlatformComponentReasons.NoBackendOwner, [], []));
                continue;
            }

            var ownerArea = ownerReports.TryGetValue(definition.Owner, out var ownerReport) &&
                ownerReport is not null &&
                string.Equals(ownerReport.Owner, definition.Owner, StringComparison.Ordinal)
                    ? ownerReport.Areas?.FirstOrDefault(area =>
                        area is not null && string.Equals(area.Area, definition.Area, StringComparison.Ordinal))
                    : null;
            areas.Add(ownerArea is not null && IsValid(ownerArea)
                ? Create(definition, ownerArea.Readiness, ownerArea.Reason,
                    ownerArea.Active.ToImmutableArray(), ownerArea.Available.ToImmutableArray())
                : Create(definition, PlatformComponentReadiness.Unknown,
                    PlatformComponentReasons.OwnerUnavailable, [], []));
        }

        return new PlatformComponentReport(observedAt, areas.MoveToImmutable());
    }

    /// <summary>Returns the camelCase name of a hosting pattern.</summary>
    public static string HostingName(ProviderHostingPattern hosting) => hosting switch
    {
        ProviderHostingPattern.InProcess => "inProcess",
        ProviderHostingPattern.Sidecar => "sidecar",
        ProviderHostingPattern.RemoteService => "remoteService",
        ProviderHostingPattern.KubernetesController => "kubernetesController",
        ProviderHostingPattern.ManagedRuntime => "managedRuntime",
        _ => throw new ArgumentOutOfRangeException(nameof(hosting)),
    };

    private static PlatformComponentAreaReport Create(
        PlatformComponentAreaDefinition definition,
        string readiness,
        string? reason,
        ImmutableArray<PlatformComponentActiveImplementation> active,
        ImmutableArray<PlatformComponentAvailableImplementation> available) =>
        new(definition.Area, definition.Purpose, definition.Owner, definition.Cardinality,
            readiness, reason, active, available);

    // Rejects owner answers with unknown readiness or reason codes, or missing implementation fields.
    private static bool IsValid(PlatformComponentAreaReport area) =>
        PlatformComponentReadiness.All.Contains(area.Readiness ?? string.Empty) &&
        (area.Reason is null || PlatformComponentReasons.All.Contains(area.Reason)) &&
        area.Active is not null && area.Available is not null &&
        area.Active.All(item => item is not null &&
            !string.IsNullOrWhiteSpace(item.ImplementationId) &&
            !string.IsNullOrWhiteSpace(item.DisplayName) &&
            !string.IsNullOrWhiteSpace(item.Version) &&
            !string.IsNullOrWhiteSpace(item.Hosting) &&
            PlatformComponentReadiness.All.Contains(item.Readiness ?? string.Empty) &&
            (item.Reason is null || PlatformComponentReasons.All.Contains(item.Reason))) &&
        area.Available.All(item => item is not null &&
            !string.IsNullOrWhiteSpace(item.ImplementationId) &&
            !string.IsNullOrWhiteSpace(item.DisplayName) &&
            !string.IsNullOrWhiteSpace(item.Version));

    // Higher values win when an area combines the readiness of several implementations.
    private static int Severity(string readiness) => readiness switch
    {
        PlatformComponentReadiness.Ready => 0,
        PlatformComponentReadiness.Disabled => 1,
        PlatformComponentReadiness.Degraded => 2,
        PlatformComponentReadiness.Unknown => 3,
        PlatformComponentReadiness.NotReady => 4,
        _ => 3,
    };
}

/// <summary>Runs an owner's existing readiness check with a time limit.</summary>
public static class PlatformComponentReadinessProbe
{
    /// <summary>Default time limit for an owner readiness check. It is below the Projects owner timeout.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the check. A passing check is <c>ready</c>. A failing or throwing check is
    /// <c>notReady</c> with reason <c>readinessCheckFailed</c>. A check that exceeds the
    /// time limit is <c>unknown</c> with reason <c>readinessTimedOut</c>.
    /// </summary>
    /// <param name="check">Readiness check that returns true when the owner is ready.</param>
    /// <param name="timeout">Time limit for the check.</param>
    /// <param name="cancellationToken">Caller cancellation; it is rethrown, not reported.</param>
    public static async Task<PlatformComponentReadinessResult> RunAsync(
        Func<CancellationToken, Task<bool>> check,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await check(timeoutSource.Token).ConfigureAwait(false)
                ? PlatformComponentReadinessResult.Ready
                : new(PlatformComponentReadiness.NotReady, PlatformComponentReasons.ReadinessCheckFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new(PlatformComponentReadiness.Unknown, PlatformComponentReasons.ReadinessTimedOut);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(PlatformComponentReadiness.NotReady, PlatformComponentReasons.ReadinessCheckFailed);
        }
    }
}
