using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.EventsAndSessions.Cost;

public sealed class MeterCostProviderBinder : ICostProviderBinder
{
    private readonly ImmutableDictionary<string, ICostProviderBinder> _binders;

    public MeterCostProviderBinder(IReadOnlyDictionary<string, ICostProviderBinder> binders)
    {
        ArgumentNullException.ThrowIfNull(binders);
        if (binders.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("Cost binders require exact meter-source keys and non-null values.",
                nameof(binders));
        _binders = binders.ToImmutableDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public ProviderResult<CostBinding> ResolveAndPin(string meterSource, string runId)
    {
        if (string.IsNullOrWhiteSpace(meterSource))
            return Fail(ProviderErrorCode.InvalidConfiguration, "A Cost meter source is required.");
        return _binders.TryGetValue(meterSource, out var binder)
            ? binder.ResolveAndPin(meterSource, runId)
            : Fail(ProviderErrorCode.ProviderNotFound,
                "No Cost provider is configured for the requested meter source.");
    }

    public ProviderResult<CostBinding> VerifyPinned(
        string meterSource, string runId, CostBinding pinnedBinding)
    {
        if (pinnedBinding is null)
            return Fail(ProviderErrorCode.InvalidConfiguration, "A pinned Cost binding is required.");
        if (string.IsNullOrWhiteSpace(meterSource))
            return Fail(ProviderErrorCode.InvalidConfiguration, "A Cost meter source is required.");
        if (!string.Equals(meterSource, pinnedBinding.MeterSource, StringComparison.Ordinal))
            return Fail(ProviderErrorCode.MeterSourceMismatch,
                "The pinned Cost binding belongs to another meter source.");
        return _binders.TryGetValue(meterSource, out var binder)
            ? binder.VerifyPinned(meterSource, runId, pinnedBinding)
            : Fail(ProviderErrorCode.ProviderNotFound,
                "No Cost provider is configured for the pinned meter source.");
    }

    private static ProviderResult<CostBinding> Fail(ProviderErrorCode code, string message) =>
        ProviderResult<CostBinding>.Failure(code, message);
}
