using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Canonical meter identifiers emitted by native SDK usage accounting.</summary>
public static class SdkMeterSources
{
    /// <summary>Meter for hosted Copilot nano-AIU observations.</summary>
    public const string CopilotNanoAiu = "copilot.nano_aiu";
    /// <summary>Meter for token counts from bring-your-own-key providers.</summary>
    public const string ByokTokens = "byok.tokens";
}

/// <summary>
/// Facts captured when the runtime creates an SDK session; usage ingestion uses them
/// to bind observations to the accepted model selection and accounting source.
/// </summary>
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
    long RegistrationRevision)
{
    /// <summary>Prompt limit active when the SDK session was created, if one was configured.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }
    /// <summary>Reasoning effort requested and verified for the active model session.</summary>
    public string ReasoningEffort { get; init; } = "high";
    /// <summary>Accepted immutable model binding used to create or resume the session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }
    /// <summary>Current server-owned model binding resolved for this session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ResolvedModelBindingPin { get; init; }
    /// <summary>Provider-specific configuration identity for a BYOK session; contains no credential.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkByokProviderFacts? ByokProvider { get; init; }
}

/// <summary>Non-secret provider identity pinned for a BYOK SDK session.</summary>
public sealed record SdkByokProviderFacts(string Type, string DeploymentId, string ConfigurationHash);

/// <summary>One native SDK usage event with token, cost, duration, and optional accounting facts.</summary>
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
    decimal? DurationMilliseconds)
{
    /// <summary>Correlates this SDK observation with the addressed message that caused it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? A2AMessageId { get; init; }
    /// <summary>Provider-reported completeness and stable identity for hosted AI-credit accounting.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkUsageAccountingObservation? Accounting { get; init; }
}

/// <summary>Completeness state reported for hosted Copilot AI-credit accounting.</summary>
public enum SdkAiCreditsStatus
{
    Complete,
    Partial,
    Unavailable
}

/// <summary>Stable source identity and ordering for one SDK accounting event.</summary>
public sealed record SdkUsageAccountingIdentity(string SourceSessionId, long Sequence, string UsageId);

/// <summary>
/// Accounting evidence as reported by the SDK. The separate reported flag preserves the
/// distinction between an explicit unavailable status and a missing field in older events.
/// </summary>
public sealed record SdkUsageAccountingObservation(
    SdkUsageAccountingIdentity? Identity,
    SdkAiCreditsStatus AiCreditsStatus,
    bool AiCreditsStatusReported);

/// <summary>Builds deterministic usage-event identities from the native SDK session and event IDs.</summary>
public static class SdkUsageIdentity
{
    /// <summary>Returns a stable UUID derived from the runtime instance and SDK usage event.</summary>
    /// <exception cref="ArgumentException">A runtime, session, or canonical event identity is invalid.</exception>
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
