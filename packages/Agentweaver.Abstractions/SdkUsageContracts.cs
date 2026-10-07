namespace Agentweaver.Abstractions;

public static class SdkMeterSources
{
    public const string CopilotNanoAiu = "copilot.nano_aiu";
}

public sealed record SdkSessionFacts(
    Guid RuntimeInstanceId,
    string SdkSessionId,
    string SdkVersion,
    string RuntimeVersion,
    string ModelSelectionReference,
    string ModelId,
    string CatalogHash,
    decimal? ModelMultiplier,
    string SourceMode,
    string MeterSource,
    string AcceptedSelectionHash,
    long RegistrationRevision);

public sealed record SdkUsageObservation(
    Guid EventId,
    string SdkEventId,
    string SdkSessionId,
    DateTimeOffset OccurredAt,
    string ModelId,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens,
    decimal? TotalNanoAiu,
    decimal? DurationMilliseconds);
