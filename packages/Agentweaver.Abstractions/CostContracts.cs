using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public enum CostDisposition { Estimate, Reconciled, Unpriced }
public enum CostQuoteBasis { ProviderWeightedNanoAiu, UnweightedAiCredits }

public sealed record CostRateCard(
    string Id,
    string Version,
    string MeterSource,
    string Unit,
    decimal NanoUnitsPerUnit,
    ImmutableDictionary<string, decimal> ModelMultipliers);

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

public sealed record CostPrice(
    decimal? Amount, string? Unit, CostDisposition Disposition,
    CostRateCard? RateCard, string? UnpricedReason);

public sealed record CostQuoteRequest(
    string ModelId, decimal? Units, CostQuoteBasis Basis);

public interface ICostProvider
{
    CostPrice Price(UsageMeasurement usage, UsageModelBinding model, CostBinding binding);
    CostPrice Quote(CostQuoteRequest plannedWork, CostBinding binding);
}
