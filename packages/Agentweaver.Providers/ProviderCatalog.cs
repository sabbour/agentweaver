using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers;

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
    InvalidNegotiation
}

public sealed record ProviderError(ProviderErrorCode Code, string Message);

public sealed class ProviderResult<T> where T : class
{
    private ProviderResult(T? value, ProviderError? error) => (Value, Error) = (value, error);

    public T? Value { get; }
    public ProviderError? Error { get; }
    public bool IsSuccess => Error is null;

    public static ProviderResult<T> Success(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);
    public static ProviderResult<T> Failure(ProviderErrorCode code, string message) =>
        new(null, new ProviderError(code, message));
}

public sealed class ProviderCatalog
{
    private readonly ImmutableDictionary<string, ProviderRegistration> _registrations;
    private readonly ImmutableDictionary<ProviderSeam, string> _defaults;
    private readonly ImmutableHashSet<(ProviderSeam Seam, string Id)> _permittedOverrides;
    private readonly ImmutableDictionary<ProviderSeam, ImmutableArray<string>> _ordered;
    private readonly ImmutableDictionary<NetworkPolicyLayer, string> _layers;

    private ProviderCatalog(
        ImmutableDictionary<string, ProviderRegistration> registrations,
        ImmutableDictionary<ProviderSeam, string> defaults,
        ImmutableHashSet<(ProviderSeam Seam, string Id)> permittedOverrides,
        ImmutableDictionary<ProviderSeam, ImmutableArray<string>> ordered,
        ImmutableDictionary<NetworkPolicyLayer, string> layers) =>
        (_registrations, _defaults, _permittedOverrides, _ordered, _layers) =
            (registrations, defaults, permittedOverrides, ordered, layers);

    public static ProviderResult<ProviderCatalog> Create(
        IEnumerable<ProviderRegistration> registrations,
        IEnumerable<ProviderSelection> defaults,
        IEnumerable<ProviderOverridePermission> permittedOverrides,
        IEnumerable<ProviderOrderedSelection>? orderedSelections = null,
        IEnumerable<ProviderLayerSelection>? layerSelections = null)
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

        return ProviderResult<ProviderCatalog>.Success(new ProviderCatalog(
            entries.ToImmutable(), selected.ToImmutable(), permissions.ToImmutable(),
            ordered.ToImmutable(), layers.ToImmutable()));
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

    public bool IsOverridePermitted(ProviderSeam seam, string id) => _permittedOverrides.Contains((seam, id));
    public bool TryGetDefault(ProviderSeam seam, out string? id) => _defaults.TryGetValue(seam, out id);
    public bool TryGetOrdered(ProviderSeam seam, out ImmutableArray<string> ids) => _ordered.TryGetValue(seam, out ids);
    public bool TryGetLayer(NetworkPolicyLayer layer, out string? id) => _layers.TryGetValue(layer, out id);
    public bool TryGetProvider(string id, out ProviderRegistration? registration) =>
        _registrations.TryGetValue(id, out registration);

    private static ProviderResult<ProviderCatalog> Invalid(string message) =>
        ProviderResult<ProviderCatalog>.Failure(ProviderErrorCode.InvalidConfiguration, message);
}
