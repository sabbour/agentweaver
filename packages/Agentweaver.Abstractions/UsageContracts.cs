using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public sealed record UsageAttribution(
    string TenantId, string ProjectId, string RunId, string SessionId, string AgentId);

public sealed record UsageModelBinding(
    string ModelReference, string ModelId, string MeterSource, string SelectionRevision);

public sealed record UsageMeasurement(
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    long RequestCount,
    decimal? ProviderUnits,
    string? ProviderUnit,
    decimal? DurationMilliseconds);

public sealed record UsageSubmission(
    Guid EventId,
    DateTimeOffset OccurredAt,
    UsageAttribution Attribution,
    UsageModelBinding ModelBinding,
    UsageMeasurement Measurement);

public sealed record UsageLedgerEntry(
    UsageSubmission Usage, CostBinding? CostBinding, CostPrice Price, DateTimeOffset RecordedAt);

public sealed record UsageIngestionResult(UsageLedgerEntry Entry, bool IsDuplicate);

public sealed record UsageAmountTotal(
    string MeterSource, string Unit, decimal Amount, long PricedEvents, long UnpricedEvents);

public sealed record UsageAgentTotals(
    string AgentId,
    long Events,
    long RequestCount,
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ReasoningTokens,
    decimal? DurationMilliseconds,
    bool IsFullyPriced,
    ImmutableArray<UsageAmountTotal> Amounts);

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
