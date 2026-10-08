using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

public static class SdkMeterSources
{
    public const string CopilotNanoAiu = "copilot.nano_aiu";
    public const string ByokTokens = "byok.tokens";
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

public static class SdkUsageIdentity
{
    public static Guid Create(Guid runtimeInstanceId, string sdkSessionId, string sdkEventId)
    {
        if (runtimeInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(sdkSessionId) ||
            !Guid.TryParseExact(sdkEventId, "D", out var eventId) || eventId == Guid.Empty)
            throw new ArgumentException("A native SDK usage identity is required.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"agentweaver.sdk.usage.v1\0{runtimeInstanceId:D}\0{sdkSessionId}\0{eventId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
