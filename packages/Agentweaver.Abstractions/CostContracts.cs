using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

/// <summary>States whether a price is provisional, reconciled to usage, or unavailable.</summary>
public enum CostDisposition { Estimate, Reconciled, Unpriced }

/// <summary>Defines whether a quote uses the provider's model weighting or raw AI-credit units.</summary>
public enum CostQuoteBasis { ProviderWeightedNanoAiu, UnweightedAiCredits }

/// <summary>Versioned pricing inputs owned by a cost provider and pinned into a run binding.</summary>
public sealed record CostRateCard(
    string Id,
    string Version,
    string MeterSource,
    string Unit,
    decimal NanoUnitsPerUnit,
    ImmutableDictionary<string, decimal> ModelMultipliers);

/// <summary>
/// Pins the selected meter provider, its resource generation and capabilities, and the
/// rate card used to price a run.
/// </summary>
public sealed record CostBinding(
    string MeterSource,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceId,
    long ResourceGeneration,
    ImmutableHashSet<string> NegotiatedCapabilities,
    CostRateCard RateCard);

/// <summary>Price returned by a cost provider; unpriced results carry a reason instead of an amount.</summary>
public sealed record CostPrice(
    decimal? Amount, string? Unit, CostDisposition Disposition,
    CostRateCard? RateCard, string? UnpricedReason);

/// <summary>Work estimate requested from a cost provider before usage has been measured.</summary>
public sealed record CostQuoteRequest(
    string ModelId, decimal? Units, CostQuoteBasis Basis);

/// <summary>Prices measured usage and planned work for the selected run-scoped cost binding.</summary>
public interface ICostProvider
{
    /// <summary>Reconciles a usage observation against its resolved model and pinned rate card.</summary>
    CostPrice Price(UsageMeasurement usage, UsageModelBinding model, CostBinding binding);

    /// <summary>Quotes planned units using the requested pricing basis and pinned rate card.</summary>
    CostPrice Quote(CostQuoteRequest plannedWork, CostBinding binding);
}
