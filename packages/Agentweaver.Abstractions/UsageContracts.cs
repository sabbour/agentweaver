using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public sealed record UsageAttribution(
    string TenantId, string ProjectId, string RunId, string SessionId, string AgentId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }
}

public sealed record UsageModelBinding(
    string ModelReference, string ModelId, string MeterSource, string SelectionRevision);

public sealed record UsageMeasurement(
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    long? RequestCount,
    decimal? ProviderUnits,
    string? ProviderUnit,
    decimal? DurationMilliseconds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWriteTokens { get; init; }
}

public sealed record UsageSubmission(
    Guid EventId,
    DateTimeOffset OccurredAt,
    UsageAttribution Attribution,
    UsageModelBinding ModelBinding,
    UsageMeasurement Measurement)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkSessionFacts? SdkSource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkEventId { get; init; }
}

public sealed record UsageLedgerEntry(
    UsageSubmission Usage, CostBinding? CostBinding, CostPrice Price,
    DateTimeOffset RecordedAt, string CanonicalPayloadHash);

public sealed record UsageAccountingReceipt(
    Guid EventId,
    UsageAttribution Attribution,
    string CanonicalPayloadHash,
    CostDisposition Disposition,
    decimal? Amount,
    string? Unit,
    string? UnpricedReason,
    string? RateCardId,
    string? RateCardVersion,
    DateTimeOffset RecordedAt);

public sealed record UsageIngestionResult(UsageLedgerEntry Entry, bool IsDuplicate)
{
    public UsageAccountingReceipt Receipt => new(
        Entry.Usage.EventId,
        Entry.Usage.Attribution,
        Entry.CanonicalPayloadHash,
        Entry.Price.Disposition,
        Entry.Price.Amount,
        Entry.Price.Unit,
        Entry.Price.UnpricedReason,
        Entry.CostBinding?.RateCard.Id,
        Entry.CostBinding?.RateCard.Version,
        Entry.RecordedAt);
}

public sealed record UsageAmountTotal(
    string MeterSource, string Unit, decimal Amount, long PricedEvents, long UnpricedEvents);

public sealed record UsageAgentTotals(
    string AgentId,
    long Events,
    long? RequestCount,
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    decimal? DurationMilliseconds,
    bool IsFullyPriced,
    ImmutableArray<UsageAmountTotal> Amounts)
{
    public long? CacheWriteTokens { get; init; }
}

public sealed record UsageRunTotals(
    string TenantId,
    string ProjectId,
    string RunId,
    long Events,
    bool IsFullyPriced,
    ImmutableArray<UsageAgentTotals> Agents,
    ImmutableArray<UsageAmountTotal> Amounts);

public interface IUsageLedger
{
    Task<UsageIngestionResult> AppendAsync(
        UsageSubmission submission, CostBinding? binding, CostPrice price,
        CancellationToken cancellationToken = default);

    Task<UsageRunTotals> GetRunTotalsAsync(
        string tenantId, string projectId, string runId,
        CancellationToken cancellationToken = default);
}
