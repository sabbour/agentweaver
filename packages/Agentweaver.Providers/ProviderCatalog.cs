using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers;

/// <summary>Stable error categories used when validating or resolving provider selections.</summary>
public enum ProviderErrorCode
{
    InvalidConfiguration,
    DuplicateProviderId,
    DuplicateDefault,
    DuplicateSelection,
    MissingDefault,
    ProviderNotFound,
    ProviderDisabled,
    ProviderSeamMismatch,
    OverrideNotPermitted,
    UnsupportedCardinality,
    AdapterVersionMismatch,
    OptionsSchemaMismatch,
    CapabilityUnavailable,
    ResourceMismatch,
    InvalidNegotiation,
    MeterSourceMismatch,
    PinnedBindingMismatch
}

/// <summary>A machine-readable catalog or resolution failure with a safe diagnostic.</summary>
public sealed record ProviderError(ProviderErrorCode Code, string Message);

/// <summary>
/// Holds either a successful provider contract or a structured failure. It avoids throwing for
/// expected invalid selections while reserving exceptions for programmer and configuration faults.
/// </summary>
public sealed class ProviderResult<T> where T : class
{
    private ProviderResult(T? value, ProviderError? error) => (Value, Error) = (value, error);

    /// <summary>The successful value, or null when the result is a failure.</summary>
    public T? Value { get; }

    /// <summary>The failure code and message, or null when the result succeeded.</summary>
    public ProviderError? Error { get; }

    /// <summary>True when <see cref="Value"/> contains a result.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Creates a successful result and rejects a null value.</summary>
    public static ProviderResult<T> Success(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);

    /// <summary>Creates a failed result with its stable error code and diagnostic.</summary>
    public static ProviderResult<T> Failure(ProviderErrorCode code, string message) =>
        new(null, new ProviderError(code, message));
}

/// <summary>
/// Immutable validated snapshot of provider registrations and per-seam selection policy.
/// The resolver uses this catalog as its sole source of defaults, overrides, ordered sets,
/// network layers, and cost meter mappings.
/// </summary>
public sealed class ProviderCatalog
{
    private readonly ImmutableDictionary<string, ProviderRegistration> _registrations;
    private readonly ImmutableDictionary<ProviderSeam, string> _defaults;
    private readonly ImmutableHashSet<(ProviderSeam Seam, string Id)> _permittedOverrides;
    private readonly ImmutableDictionary<ProviderSeam, ImmutableArray<string>> _ordered;
    private readonly ImmutableDictionary<NetworkPolicyLayer, string> _layers;
    private readonly ImmutableDictionary<string, string> _meterSources;

    private ProviderCatalog(
        ImmutableDictionary<string, ProviderRegistration> registrations,
        ImmutableDictionary<ProviderSeam, string> defaults,
        ImmutableHashSet<(ProviderSeam Seam, string Id)> permittedOverrides,
        ImmutableDictionary<ProviderSeam, ImmutableArray<string>> ordered,
        ImmutableDictionary<NetworkPolicyLayer, string> layers,
        ImmutableDictionary<string, string> meterSources) =>
        (_registrations, _defaults, _permittedOverrides, _ordered, _layers, _meterSources) =
            (registrations, defaults, permittedOverrides, ordered, layers, meterSources);

    /// <summary>
    /// Validates and freezes the provider snapshot. Invalid references, duplicate selections,
    /// incompatible seam cardinalities, and missing required network layers are returned as errors.
    /// </summary>
    /// <param name="registrations">Descriptors and options metadata for available adapters.</param>
    /// <param name="defaults">Selections for exclusive and platform-singleton seams.</param>
    /// <param name="permittedOverrides">Project override permissions for eligible seams.</param>
    /// <param name="orderedSelections">Optional ordered provider sets for composite seams.</param>
    /// <param name="layerSelections">Optional network policy providers by layer.</param>
    /// <param name="meterSourceSelections">Optional cost providers keyed by meter source.</param>
    /// <returns>A frozen catalog or a structured validation failure.</returns>
    public static ProviderResult<ProviderCatalog> Create(
        IEnumerable<ProviderRegistration> registrations,
        IEnumerable<ProviderSelection> defaults,
        IEnumerable<ProviderOverridePermission> permittedOverrides,
        IEnumerable<ProviderOrderedSelection>? orderedSelections = null,
        IEnumerable<ProviderLayerSelection>? layerSelections = null,
        IEnumerable<ProviderMeterSourceSelection>? meterSourceSelections = null)
    {
        if (registrations is null || defaults is null || permittedOverrides is null)
            return Invalid("Catalog collections cannot be null.");

        var entries = ImmutableDictionary.CreateBuilder<string, ProviderRegistration>(StringComparer.Ordinal);
        foreach (var entry in registrations)
        {
            if (entry?.Descriptor is not { } descriptor ||
                !Enum.IsDefined(descriptor.Seam) || !Enum.IsDefined(descriptor.Hosting) ||
                string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.AdapterVersion is null ||
                descriptor.OptionsSchemaVersion < 1 || entry.OptionsSchemaVersion < 1 ||
                string.IsNullOrWhiteSpace(entry.OptionsRevision) ||
                descriptor.AdvertisedCapabilities is null ||
                descriptor.AdvertisedCapabilities.Any(string.IsNullOrWhiteSpace))
                return Invalid("A provider registration has invalid descriptor, capabilities, or options revision.");
            if (entry.OptionsSchemaVersion != descriptor.OptionsSchemaVersion)
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.OptionsSchemaMismatch, $"Provider '{descriptor.Id}' options schema does not match its descriptor.");
            if (entries.ContainsKey(descriptor.Id))
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.DuplicateProviderId, $"Provider ID '{descriptor.Id}' is registered more than once.");
            var immutableDescriptor = descriptor with
            {
                AdvertisedCapabilities = descriptor.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
            };
            entries.Add(descriptor.Id, entry with { Descriptor = immutableDescriptor });
        }

        var selected = ImmutableDictionary.CreateBuilder<ProviderSeam, string>();
        foreach (var selection in defaults)
        {
            if (selection is null || !Enum.IsDefined(selection.Seam) || string.IsNullOrWhiteSpace(selection.ProviderId))
                return Invalid("A platform default is invalid.");
            if (ProviderSeams.Cardinality(selection.Seam) is not
                (ProviderCardinality.Exclusive or ProviderCardinality.PlatformSingleton))
                return Invalid($"Seam '{selection.Seam}' requires its cardinality-specific selection.");
            if (selected.ContainsKey(selection.Seam))
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.DuplicateDefault, $"Seam '{selection.Seam}' has multiple defaults.");
            if (!entries.TryGetValue(selection.ProviderId, out var registration))
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.ProviderNotFound, $"Default provider '{selection.ProviderId}' is not registered.");
            if (registration.Descriptor.Seam != selection.Seam)
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.ProviderSeamMismatch, $"Default provider '{selection.ProviderId}' belongs to another seam.");
            selected.Add(selection.Seam, selection.ProviderId);
        }

        var permissions = ImmutableHashSet.CreateBuilder<(ProviderSeam Seam, string Id)>();
        foreach (var permission in permittedOverrides)
        {
            if (permission is null || !Enum.IsDefined(permission.Seam) || string.IsNullOrWhiteSpace(permission.ProviderId))
                return Invalid("An override permission is invalid.");
            if (ProviderSeams.Cardinality(permission.Seam) is not
                (ProviderCardinality.Exclusive or ProviderCardinality.OrderedComposite))
                return Invalid($"Seam '{permission.Seam}' cannot have project provider overrides.");
            if (!entries.TryGetValue(permission.ProviderId, out var registration))
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.ProviderNotFound, $"Override provider '{permission.ProviderId}' is not registered.");
            if (registration.Descriptor.Seam != permission.Seam)
                return ProviderResult<ProviderCatalog>.Failure(
                    ProviderErrorCode.ProviderSeamMismatch, $"Override provider '{permission.ProviderId}' belongs to another seam.");
            permissions.Add((permission.Seam, permission.ProviderId));
        }

        var ordered = ImmutableDictionary.CreateBuilder<ProviderSeam, ImmutableArray<string>>();
        foreach (var selection in orderedSelections ?? [])
        {
            if (selection is null || !Enum.IsDefined(selection.Seam) ||
                ProviderSeams.Cardinality(selection.Seam) != ProviderCardinality.OrderedComposite ||
                selection.ProviderIds.IsDefault || selection.ProviderIds.Any(string.IsNullOrWhiteSpace))
                return Invalid("An ordered selection is invalid.");
            if (ordered.ContainsKey(selection.Seam) || selected.ContainsKey(selection.Seam))
                return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.DuplicateSelection,
                    $"Seam '{selection.Seam}' has multiple selections.");
            if (selection.ProviderIds.Distinct(StringComparer.Ordinal).Count() != selection.ProviderIds.Length)
                return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.DuplicateSelection,
                    $"Seam '{selection.Seam}' contains a duplicate provider.");
            foreach (var id in selection.ProviderIds)
            {
                var error = CheckSelection(entries, selection.Seam, id);
                if (error is not null) return error;
            }
            ordered.Add(selection.Seam, selection.ProviderIds.ToImmutableArray());
        }

        var layers = ImmutableDictionary.CreateBuilder<NetworkPolicyLayer, string>();
        foreach (var selection in layerSelections ?? [])
        {
            if (selection is null || !Enum.IsDefined(selection.Layer) ||
                string.IsNullOrWhiteSpace(selection.ProviderId))
                return Invalid("A network policy layer selection is invalid.");
            if (layers.ContainsKey(selection.Layer))
                return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.DuplicateSelection,
                    $"Network policy layer '{selection.Layer}' has multiple selections.");
            var error = CheckSelection(entries, ProviderSeam.NetworkPolicy, selection.ProviderId);
            if (error is not null) return error;
            layers.Add(selection.Layer, selection.ProviderId);
        }
        if (layers.Count > 0 && !layers.ContainsKey(NetworkPolicyLayer.L3L4))
            return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.MissingDefault,
                "Network policy requires an L3/L4 provider.");

        var meterSources = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var selection in meterSourceSelections ?? [])
        {
            if (selection is null || string.IsNullOrWhiteSpace(selection.MeterSource) ||
                string.IsNullOrWhiteSpace(selection.ProviderId))
                return Invalid("A cost meter-source selection is invalid.");
            if (meterSources.ContainsKey(selection.MeterSource))
                return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.DuplicateSelection,
                    $"Cost meter source '{selection.MeterSource}' has multiple providers.");
            var error = CheckSelection(entries, ProviderSeam.Cost, selection.ProviderId);
            if (error is not null) return error;
            meterSources.Add(selection.MeterSource, selection.ProviderId);
        }

        return ProviderResult<ProviderCatalog>.Success(new ProviderCatalog(
            entries.ToImmutable(), selected.ToImmutable(), permissions.ToImmutable(),
            ordered.ToImmutable(), layers.ToImmutable(), meterSources.ToImmutable()));
    }

    private static ProviderResult<ProviderCatalog>? CheckSelection(
        ImmutableDictionary<string, ProviderRegistration>.Builder entries, ProviderSeam seam, string id)
    {
        if (!entries.TryGetValue(id, out var entry))
            return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.ProviderNotFound,
                $"Provider '{id}' is not registered.");
        if (entry.Descriptor.Seam != seam)
            return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.ProviderSeamMismatch,
                $"Provider '{id}' belongs to another seam.");
        if (!entry.Enabled)
            return ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.ProviderDisabled,
                $"Provider '{id}' is disabled.");
        return null;
    }

    /// <summary>Checks whether the exact seam/provider pair may be selected by project policy.</summary>
    public bool IsOverridePermitted(ProviderSeam seam, string id) => _permittedOverrides.Contains((seam, id));

    /// <summary>Looks up a platform default provider for a seam.</summary>
    public bool TryGetDefault(ProviderSeam seam, out string? id) => _defaults.TryGetValue(seam, out id);

    /// <summary>Looks up the frozen provider order for an ordered-composite seam.</summary>
    public bool TryGetOrdered(ProviderSeam seam, out ImmutableArray<string> ids) => _ordered.TryGetValue(seam, out ids);

    /// <summary>Looks up the configured provider for a network policy layer.</summary>
    public bool TryGetLayer(NetworkPolicyLayer layer, out string? id) => _layers.TryGetValue(layer, out id);

    /// <summary>Looks up the provider assigned to a cost meter source.</summary>
    public bool TryGetMeterSource(string meterSource, out string? id) =>
        _meterSources.TryGetValue(meterSource, out id);

    /// <summary>Looks up a registered provider by its exact ID.</summary>
    public bool TryGetProvider(string id, out ProviderRegistration? registration) =>
        _registrations.TryGetValue(id, out registration);

    /// <summary>Lists every registered provider, ordered by ID.</summary>
    public IEnumerable<ProviderRegistration> Registrations =>
        _registrations.Values.OrderBy(registration => registration.Descriptor.Id, StringComparer.Ordinal);

    /// <summary>Lists every cost meter-source assignment, ordered by meter source.</summary>
    public IEnumerable<ProviderMeterSourceSelection> MeterSourceSelections =>
        _meterSources.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new ProviderMeterSourceSelection(entry.Key, entry.Value));

    private static ProviderResult<ProviderCatalog> Invalid(string message) =>
        ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.InvalidConfiguration, message);
}
