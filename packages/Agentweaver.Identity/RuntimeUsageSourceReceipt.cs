using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record RuntimeUsageSourceReceipt(
    int ContractVersion,
    Guid ReceiptId,
    RuntimeRegistration Registration,
    UsageSubmission Usage,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageReceiptReferenceRequest(Guid ReceiptId);

public sealed record RuntimeUsageAccountingAcknowledgment(
    Guid SourceReceiptId, UsageAccountingReceipt Accounting, bool IsDuplicate);

public static class RuntimeUsageSourceReceiptContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Validate(RuntimeUsageSourceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ContractVersion != 1 || receipt.ReceiptId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_invalid");
        ValidateUsage(receipt.Registration, receipt.Usage);
        RuntimeContractValidation.ValidateHash(receipt.CanonicalPayloadHash);
        if (Hash(receipt.Registration, receipt.Usage) != receipt.CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_hash_invalid");
    }

    public static void ValidateUsage(RuntimeRegistration registration, UsageSubmission usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var binding = registration.Binding;
        var source = usage.SdkSource;
        if (source is null)
            throw new RuntimeAuthorizationException("runtime_usage_source_invalid");
        ValidateSource(registration, source);
        if (
            usage.Attribution is not { } attribution || usage.ModelBinding is not { } model ||
            usage.Measurement is not { } measurement ||
            attribution.TenantId != binding.TenantId || attribution.ProjectId != binding.ProjectId ||
            attribution.RunId != binding.RunId || attribution.SessionId != binding.SessionId ||
            attribution.AgentId != binding.AgentId || attribution.TurnId != binding.TurnId ||
            model.ModelReference != source.ModelSelectionReference || model.ModelId != source.ModelId ||
            model.MeterSource != source.MeterSource || model.SelectionRevision != binding.ContextRevision ||
            measurement.RequestCount is not null || measurement.ProviderUnit != "nano_aiu" ||
            measurement.InputTokens < 0 || measurement.OutputTokens < 0 ||
            measurement.CachedTokens < 0 || measurement.CacheWriteTokens < 0 ||
            measurement.ReasoningTokens < 0 || measurement.ProviderUnits < 0 ||
            measurement.DurationMilliseconds < 0)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        if (usage.SdkEventId is not { } sdkEventId ||
            !Guid.TryParseExact(sdkEventId, "D", out var nativeEvent) || nativeEvent == Guid.Empty ||
            nativeEvent.ToString("D") != sdkEventId ||
            usage.EventId != SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, sdkEventId))
            throw new RuntimeAuthorizationException("runtime_usage_event_invalid");
    }

    public static void ValidateSource(RuntimeRegistration registration, SdkSessionFacts source)
    {
        RuntimeContractValidation.Validate(registration);
        ArgumentNullException.ThrowIfNull(source);
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.PlacementProviderId is null || binding.EnvironmentLifecycleGeneration < 1 ||
            binding.EnvironmentLeaseRevision < 1 ||
            source.RuntimeInstanceId != registration.RuntimeInstanceId ||
            source.RegistrationRevision != registration.Revision ||
            source.SdkSessionId != $"agentweaver-runtime-{registration.RuntimeInstanceId:D}" ||
            source.ModelSelectionReference != binding.ModelSelectionReference ||
            source.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            source.SourceMode != "hosted-copilot" || source.MeterSource != SdkMeterSources.CopilotNanoAiu ||
            source.ModelMultiplier < 0)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        RuntimeContractValidation.ValidateHash(source.CatalogHash);
        RequireText(source.SdkVersion);
        RequireText(source.RuntimeVersion);
        RequireText(source.ModelId);
    }

    public static string Hash(RuntimeRegistration registration, UsageSubmission usage) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalSourceUsage(registration, usage), JsonOptions));

    public static string HashSource(RuntimeRegistration registration, SdkSessionFacts source) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new { Registration = registration, Source = source }, JsonOptions));

    public static UsageSubmission CreateUsage(
        RuntimeRegistration registration, SdkSessionFacts source, SdkUsageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(observation);
        var binding = registration.Binding;
        if (observation.SdkSessionId != source.SdkSessionId || observation.ModelId != source.ModelId)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        var usage = new UsageSubmission(
            observation.EventId, observation.OccurredAt,
            new(binding.TenantId, binding.ProjectId, binding.RunId, binding.SessionId, binding.AgentId)
            {
                TurnId = binding.TurnId
            },
            new(source.ModelSelectionReference, source.ModelId, source.MeterSource, binding.ContextRevision),
            new(observation.InputTokens, observation.OutputTokens, observation.CacheReadTokens,
                observation.ReasoningTokens, null, observation.TotalNanoAiu, "nano_aiu",
                observation.DurationMilliseconds)
            {
                CacheWriteTokens = observation.CacheWriteTokens
            })
        {
            SdkSource = source,
            SdkEventId = observation.SdkEventId
        };
        ValidateUsage(registration, usage);
        return usage;
    }

    private static void RequireText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 ||
            text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new RuntimeAuthorizationException("runtime_usage_source_invalid");
    }

    private sealed record CanonicalSourceUsage(RuntimeRegistration Registration, UsageSubmission Usage);
}
