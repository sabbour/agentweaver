using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Orchestrator.Core;

public enum SourceControlProviderSelectionFailure
{
    Missing,
    Invalid,
    Unavailable
}

public sealed class SourceControlProviderSelectionException(
    SourceControlProviderSelectionFailure failure,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public SourceControlProviderSelectionFailure Failure { get; } = failure;
}

public sealed record SourceControlProviderSelection(
    EffectiveProviderCandidate EffectiveCandidate,
    ProviderCandidate Candidate);

public static class SourceControlProviderSelectionResolver
{
    private static readonly JsonSerializerOptions SelectionJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };

    public static SourceControlProviderSelection Resolve(
        JsonElement acceptedSelectionSnapshot,
        ProviderCatalog catalog,
        ProviderResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        if (acceptedSelectionSnapshot.ValueKind != JsonValueKind.Object ||
            !acceptedSelectionSnapshot.TryGetProperty("providers", out var providersElement) ||
            providersElement.ValueKind != JsonValueKind.Array)
            throw Missing("The accepted run selection does not contain provider selections.");

        ImmutableArray<EffectiveProviderSelection> selections;
        try
        {
            selections = JsonSerializer.Deserialize<ImmutableArray<EffectiveProviderSelection>>(
                providersElement.GetRawText(), SelectionJsonOptions);
        }
        catch (JsonException exception)
        {
            throw Invalid("The accepted provider selection is malformed.", exception);
        }

        if (selections.IsDefault)
            throw Invalid("The accepted provider selection is uninitialized.");

        var selected = selections
            .Where(item => item?.Seam == ProviderSeam.SourceControl)
            .ToArray();
        if (selected.Length == 0)
            throw Missing("The accepted run selection does not select a SourceControl provider.");
        if (selected.Length != 1)
            throw Invalid("The accepted run selection contains duplicate SourceControl providers.");

        var providerSelection = selected[0];
        if (providerSelection.Cardinality != ProviderCardinality.Exclusive ||
            providerSelection.MeterSource is not null ||
            providerSelection.Candidates.IsDefault ||
            providerSelection.Candidates.Length != 1)
            throw Invalid("The accepted SourceControl selection is not one exclusive provider.");

        var effective = providerSelection.Candidates[0];
        if (effective is null ||
            effective.Seam != ProviderSeam.SourceControl ||
            !IsStableIdentifier(effective.ProviderId) ||
            !Version.TryParse(effective.AdapterVersion, out var adapterVersion) ||
            effective.OptionsSchemaVersion < 1 ||
            !IsStableIdentifier(effective.OptionsRevision) ||
            !Enum.IsDefined(effective.Hosting) ||
            !ValidCapabilities(effective.AdvertisedCapabilities) ||
            !ValidCapabilities(effective.RequiredCapabilities))
            throw Invalid("The accepted SourceControl provider candidate is invalid.");

        catalog.TryGetDefault(ProviderSeam.SourceControl, out var defaultProviderId);
        var projectOverrideId = string.Equals(
            defaultProviderId, effective.ProviderId, StringComparison.Ordinal)
            ? null
            : effective.ProviderId;
        var requiredCapabilities = effective.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            projectOverrideId,
            adapterVersion!,
            effective.OptionsSchemaVersion,
            requiredCapabilities));
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
        {
            var failure = resolution.Error?.Code is
                ProviderErrorCode.ProviderNotFound or
                ProviderErrorCode.ProviderDisabled or
                ProviderErrorCode.CapabilityUnavailable or
                ProviderErrorCode.MissingDefault
                ? SourceControlProviderSelectionFailure.Unavailable
                : SourceControlProviderSelectionFailure.Invalid;
            throw new SourceControlProviderSelectionException(
                failure,
                resolution.Error?.Message ??
                "The accepted SourceControl provider is not available in the current provider catalog.");
        }

        if (!string.Equals(candidate.ProviderId, effective.ProviderId, StringComparison.Ordinal) ||
            candidate.Seam != effective.Seam ||
            candidate.AdapterVersion != adapterVersion ||
            candidate.OptionsSchemaVersion != effective.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, effective.OptionsRevision, StringComparison.Ordinal) ||
            candidate.Hosting != effective.Hosting ||
            !candidate.AdvertisedCapabilities.SetEquals(
                effective.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)) ||
            !candidate.RequiredCapabilities.SetEquals(requiredCapabilities))
            throw Invalid("The accepted SourceControl provider no longer matches its registered catalog entry.");

        return new SourceControlProviderSelection(effective, candidate);
    }

    public static PinnedProviderBinding PinNegotiatedResource(
        ProviderResolver resolver,
        SourceControlProviderSelection selection,
        string runId,
        ResourceNegotiation negotiation)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(negotiation);
        if (string.IsNullOrWhiteSpace(runId) ||
            negotiation.Resource is null ||
            negotiation.Resource.Seam != ProviderSeam.SourceControl ||
            !string.Equals(
                negotiation.Resource.ProviderId,
                selection.Candidate.ProviderId,
                StringComparison.Ordinal) ||
            !IsStableIdentifier(negotiation.Resource.ResourceId) ||
            negotiation.Resource.Generation <= 0)
            throw Invalid("The SourceControl resource negotiation does not match its selected provider.");

        var pinned = resolver.Pin(
            runId,
            selection.Candidate,
            negotiation.Resource.ResourceId,
            negotiation);
        if (!pinned.IsSuccess || pinned.Value is null)
            throw new SourceControlProviderSelectionException(
                pinned.Error?.Code == ProviderErrorCode.CapabilityUnavailable
                    ? SourceControlProviderSelectionFailure.Unavailable
                    : SourceControlProviderSelectionFailure.Invalid,
                pinned.Error?.Message ??
                "The negotiated SourceControl resource could not be pinned.");
        return pinned.Value;
    }

    public static SourceControlRepositoryPin PinNegotiatedRepository(
        ProviderResolver resolver,
        SourceControlProviderSelection selection,
        SourceControlAcceptedRunBinding acceptedRun,
        string pinId,
        SourceControlRepositoryNegotiation negotiation,
        SourceControlCredentialReference apiCredential,
        SourceControlCredentialReference? checkoutCredential,
        SourceControlCredentialReference? webhookCredential,
        DateTimeOffset pinnedAt)
    {
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(negotiation);
        var providerBinding = PinNegotiatedResource(
            resolver,
            selection,
            acceptedRun.RunId,
            negotiation.Resource);
        return new SourceControlRepositoryPin(
            pinId,
            acceptedRun,
            providerBinding,
            negotiation.Repository,
            apiCredential,
            checkoutCredential,
            webhookCredential,
            negotiation.ProviderRepositoryId,
            negotiation.DefaultBranch,
            negotiation.IsPrivate,
            pinnedAt);
    }

    public static SourceControlRepositoryPin PinNegotiatedRepository(
        ProviderResolver resolver,
        SourceControlProviderSelection selection,
        SourceControlAcceptedRunBinding acceptedRun,
        string pinId,
        SourceControlRepositoryNegotiation negotiation,
        SourceControlGitHubAppBinding githubAppBinding,
        SourceControlCredentialReference? webhookCredential,
        DateTimeOffset pinnedAt)
    {
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(negotiation);
        ArgumentNullException.ThrowIfNull(githubAppBinding);
        var providerBinding = PinNegotiatedResource(
            resolver,
            selection,
            acceptedRun.RunId,
            negotiation.Resource);
        return new SourceControlRepositoryPin(
            pinId,
            acceptedRun,
            providerBinding,
            negotiation.Repository,
            apiCredential: null,
            checkoutCredential: null,
            webhookCredential,
            negotiation.ProviderRepositoryId,
            negotiation.DefaultBranch,
            negotiation.IsPrivate,
            pinnedAt,
            githubAppBinding);
    }

    private static bool ValidCapabilities(ImmutableArray<string> capabilities) =>
        !capabilities.IsDefault &&
        capabilities.All(IsStableIdentifier) &&
        capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Length;

    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static SourceControlProviderSelectionException Missing(string message) =>
        new(SourceControlProviderSelectionFailure.Missing, message);

    private static SourceControlProviderSelectionException Invalid(
        string message,
        Exception? innerException = null) =>
        new(SourceControlProviderSelectionFailure.Invalid, message, innerException);
}
