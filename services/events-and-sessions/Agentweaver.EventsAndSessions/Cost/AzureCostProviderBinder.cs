using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.EventsAndSessions.Cost;

public sealed class AzureCostProviderBinder(
    AzureCostProvider provider,
    ProviderResolver resolver) : ICostProviderBinder
{
    public ProviderResult<CostBinding> ResolveAndPin(string meterSource, string runId)
    {
        if (!string.Equals(meterSource, AzureCostProvider.MeterSource, StringComparison.Ordinal))
            return Fail(ProviderErrorCode.MeterSourceMismatch,
                "The Azure Cost adapter does not serve the requested meter source.");

        var resolution = resolver.ResolveCost(NewRequest());
        if (!resolution.IsSuccess || resolution.Value is null)
            return Propagate(resolution.Error);

        var negotiated = provider.Negotiate(resolution.Value.Candidate);
        if (!negotiated.IsSuccess || negotiated.Value is null)
            return Propagate(negotiated.Error);

        var pinned = resolver.PinCost(
            runId, resolution.Value, provider.Options.ResourceId, negotiated.Value);
        if (!pinned.IsSuccess || pinned.Value is null)
            return Propagate(pinned.Error);

        try
        {
            return ProviderResult<CostBinding>.Success(provider.CreateBinding(pinned.Value));
        }
        catch (ArgumentException)
        {
            return Fail(ProviderErrorCode.PinnedBindingMismatch,
                "The Azure Cost provider binding changed before pinning.");
        }
    }

    public ProviderResult<CostBinding> VerifyPinned(
        string meterSource, string runId, CostBinding pinnedBinding)
    {
        if (pinnedBinding is null)
            return Fail(ProviderErrorCode.InvalidConfiguration, "A pinned Cost binding is required.");
        if (!string.Equals(meterSource, AzureCostProvider.MeterSource, StringComparison.Ordinal) ||
            !string.Equals(pinnedBinding.MeterSource, meterSource, StringComparison.Ordinal))
            return Fail(ProviderErrorCode.MeterSourceMismatch,
                "The pinned Cost binding belongs to another meter source.");
        if (!Version.TryParse(pinnedBinding.AdapterVersion, out var pinnedVersion) ||
            pinnedVersion is null ||
            !string.Equals(pinnedVersion.ToString(), pinnedBinding.AdapterVersion, StringComparison.Ordinal) ||
            pinnedBinding.NegotiatedCapabilities is null)
            return Fail(ProviderErrorCode.PinnedBindingMismatch, "The pinned Azure Cost identity is invalid.");

        var resolution = resolver.VerifyCost(
            new CostProviderResolutionRequest(
                meterSource,
                pinnedVersion,
                pinnedBinding.OptionsSchemaVersion,
                pinnedBinding.NegotiatedCapabilities),
            pinnedBinding);
        if (!resolution.IsSuccess || resolution.Value is null)
            return Propagate(resolution.Error);

        var negotiated = provider.Negotiate(resolution.Value.Candidate);
        if (!negotiated.IsSuccess || negotiated.Value is null)
            return Propagate(negotiated.Error);

        var currentPin = resolver.PinCost(
            runId, resolution.Value, pinnedBinding.ResourceId, negotiated.Value);
        if (!currentPin.IsSuccess || currentPin.Value is null)
            return Propagate(currentPin.Error);

        CostBinding current;
        try
        {
            current = provider.CreateBinding(currentPin.Value);
        }
        catch (ArgumentException)
        {
            return Fail(ProviderErrorCode.PinnedBindingMismatch,
                "The exact pinned Azure Cost options or resource are no longer available.");
        }

        if (!provider.IsBindingCurrent(pinnedBinding))
            return Fail(ProviderErrorCode.PinnedBindingMismatch,
                "The exact pinned Azure Cost provider, options, resource, or rate card has changed.");

        return ProviderResult<CostBinding>.Success(current);
    }

    private static CostProviderResolutionRequest NewRequest() =>
        new(
            AzureCostProvider.MeterSource,
            AzureCostProvider.AdapterVersion,
            AzureCostProvider.OptionsSchemaVersion,
            AzureCostCapabilities.All);

    private static ProviderResult<CostBinding> Propagate(ProviderError? error)
    {
        var result = error ?? new ProviderError(
            ProviderErrorCode.ProviderNotFound, "The exact Azure Cost provider is unavailable.");
        return ProviderResult<CostBinding>.Failure(result.Code, result.Message);
    }

    private static ProviderResult<CostBinding> Fail(ProviderErrorCode code, string message) =>
        ProviderResult<CostBinding>.Failure(code, message);
}
