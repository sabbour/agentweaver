using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeSdkSourceRequest(
    RuntimeCredentialHttpRequest Authorization, SdkSessionFacts Source)
{
    public override string ToString() => nameof(RuntimeSdkSourceRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageObservationRequest(
    RuntimeCredentialHttpRequest Authorization, SdkUsageObservation Observation)
{
    public override string ToString() => nameof(RuntimeUsageObservationRequest) + " [REDACTED]";
}

public sealed record RuntimeSdkSourceReceipt(
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid SourceGrantId,
    SdkSessionFacts Source,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnBeginRequest(
    RuntimeCredentialHttpRequest Authorization, RuntimeA2ASendRequest Message, SdkSessionFacts Source)
{
    public override string ToString() => nameof(RuntimeNativeTurnBeginRequest) + " [REDACTED]";
}

public sealed record RuntimeNativeTurnAdmissionReceipt(
    RuntimeRegistration Registration,
    SdkSessionFacts Source,
    Guid MessageId,
    string PromptHash,
    string RequestHash,
    long OwnerRevision);

public sealed record RuntimeNativeAccountingCheckpoint(
    Guid NativeEventId,
    decimal ReportedNanoAiu,
    ImmutableDictionary<string, long> SourceWatermarks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnObservation(
    SdkSessionFacts Source,
    string NativeMessageId,
    string NativeTurnId,
    Guid NativeTurnStartEventId,
    Guid NativeTurnEndEventId,
    Guid NativeCompletionReceiptEventId,
    DateTimeOffset CompletedAt,
    string DurableCursor,
    string OutputHash,
    RuntimeNativeAccountingCheckpoint? AccountingCheckpoint)
{
    public ImmutableArray<Guid> UsageEventIds { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnObservationRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeNativeTurnAdmissionReceipt Admission,
    RuntimeNativeTurnObservation Observation)
{
    public override string ToString() => nameof(RuntimeNativeTurnObservationRequest) + " [REDACTED]";
}

public sealed record RuntimeNativeTurnRecordedReceipt(
    RuntimeNativeTurnAdmissionReceipt Admission,
    RuntimeNativeTurnObservation Observation,
    string CanonicalPayloadHash,
    long OwnerRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnAccountingRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeNativeTurnRecordedReceipt Recorded,
    ImmutableArray<RuntimeUsageCostReceiptReference> RequiredReceipts)
{
    public override string ToString() => nameof(RuntimeNativeTurnAccountingRequest) + " [REDACTED]";
}

public sealed record RuntimeNativeTurnAccountingReceipt(
    RuntimeNativeTurnRecordedReceipt Recorded,
    RuntimeUsageCostSnapshotReceipt Snapshot,
    long OwnerRevision);

public static class RuntimeNativeTurnContract
{
    public static RuntimeUsageCostSnapshotRequest AccountingSnapshotRequest(
        RuntimeNativeTurnRecordedReceipt recorded, ImmutableArray<RuntimeUsageCostReceiptReference> references)
    {
        ValidateRecorded(recorded);
        var admission = recorded.Admission;
        var request = new RuntimeUsageCostSnapshotRequest(1, admission.Registration, admission.Source)
        {
            DispatchId = admission.MessageId, RequiredReceipts = references
        };
        RuntimeUsageCostSnapshotContract.ValidateRequest(request);
        var observed = recorded.Observation.UsageEventIds.Select(id =>
            SdkUsageIdentity.Create(admission.Source.RuntimeInstanceId, admission.Source.SdkSessionId, id.ToString("D")))
            .ToHashSet();
        if (references.Length != observed.Count ||
            references.Any(reference => !observed.Remove(reference.Accounting.EventId)) || observed.Count != 0)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        return request;
    }

    public static void ValidateAccounted(
        RuntimeNativeTurnAccountingReceipt receipt, RuntimeNativeTurnRecordedReceipt recorded,
        ImmutableArray<RuntimeUsageCostReceiptReference> references)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var request = AccountingSnapshotRequest(recorded, references);
        ValidateRecorded(receipt.Recorded);
        if (receipt.Recorded.Admission != recorded.Admission ||
            receipt.Recorded.CanonicalPayloadHash != recorded.CanonicalPayloadHash ||
            receipt.Recorded.OwnerRevision != recorded.OwnerRevision ||
            receipt.OwnerRevision <= recorded.OwnerRevision)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(receipt.Snapshot, request);
    }

    public static string RequestHash(RuntimeA2ASendRequest message) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(message));

    public static void ValidateAdmission(
        RuntimeNativeTurnAdmissionReceipt admission, RuntimeRegistration registration,
        SdkSessionFacts source, RuntimeA2ASendRequest message)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(message);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source);
        var parts = message.Message?.Parts ?? default;
        var text = !parts.IsDefault && parts.Length == 1 ? parts[0]?.Text : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > AddressedMessageValidation.MaximumTextLength ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
            message.Message is not { Kind: "message", Role: "user", Metadata: { } metadata } ||
            message.Message.Parts[0].Kind != "text" ||
            metadata.Runtime?.Registration != registration || !Enum.IsDefined(metadata.DeliveryMode) ||
            metadata.Runtime.ContractVersion != 1 || metadata.Runtime.Purpose != RuntimeCredentialPurpose.Observe ||
            metadata.Runtime.SourceGrantId == Guid.Empty || metadata.Runtime.SourceGrantRevision < 1 ||
            message.Message.ContextId != source.SdkSessionId ||
            admission.Registration != registration || admission.Source != source ||
            admission.MessageId == Guid.Empty || admission.MessageId != message.Message!.MessageId ||
            admission.PromptHash != RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(text)) ||
            admission.RequestHash != RequestHash(message) || admission.OwnerRevision < 2)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_invalid");
    }

    public static void ValidateObservation(
        RuntimeNativeTurnAdmissionReceipt admission, RuntimeNativeTurnObservation observation)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(observation);
        if (admission.MessageId == Guid.Empty || admission.OwnerRevision < 2)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_invalid");
        RuntimeContractValidation.ValidateHash(admission.PromptHash);
        RuntimeContractValidation.ValidateHash(admission.RequestHash);
        RuntimeUsageSourceReceiptContract.ValidateSource(admission.Registration, observation.Source);
        if (observation.Source != admission.Source ||
            !Guid.TryParseExact(observation.NativeMessageId, "D", out var messageId) ||
            messageId == Guid.Empty || messageId.ToString("D") != observation.NativeMessageId ||
            observation.NativeTurnStartEventId == Guid.Empty || observation.NativeTurnEndEventId == Guid.Empty ||
            observation.NativeCompletionReceiptEventId == Guid.Empty ||
            new[] { messageId, observation.NativeTurnStartEventId, observation.NativeTurnEndEventId,
                observation.NativeCompletionReceiptEventId }.Distinct().Count() != 4 ||
            string.IsNullOrWhiteSpace(observation.NativeTurnId) || observation.NativeTurnId.Length > 256 ||
            observation.NativeTurnId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(observation.DurableCursor) || observation.DurableCursor.Length > 4096 ||
            observation.DurableCursor.Any(char.IsControl) || observation.CompletedAt == default ||
            observation.UsageEventIds.IsDefault || observation.UsageEventIds.Length > 512 ||
            observation.UsageEventIds.Any(id => id == Guid.Empty || id == messageId ||
                id == observation.NativeTurnStartEventId || id == observation.NativeTurnEndEventId ||
                id == observation.NativeCompletionReceiptEventId) ||
            observation.UsageEventIds.Distinct().Count() != observation.UsageEventIds.Length)
            throw new RuntimeAuthorizationException("runtime_native_turn_observation_invalid");
        RuntimeContractValidation.ValidateHash(observation.OutputHash);
        if (observation.AccountingCheckpoint is { } checkpoint &&
            (checkpoint.NativeEventId == Guid.Empty || checkpoint.ReportedNanoAiu < 0 ||
             checkpoint.SourceWatermarks is null || checkpoint.SourceWatermarks.Count is < 1 or > 256 ||
             checkpoint.SourceWatermarks.Any(item =>
                 string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 256 ||
                 item.Key.Any(char.IsControl) || item.Value < 0)))
            throw new RuntimeAuthorizationException("runtime_native_accounting_checkpoint_invalid");
    }

    public static string ObservationHash(RuntimeNativeTurnObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("source");
            JsonSerializer.Serialize(writer, observation.Source);
            writer.WriteString("nativeMessageId", observation.NativeMessageId);
            writer.WriteString("nativeTurnId", observation.NativeTurnId);
            writer.WriteString("nativeTurnStartEventId", observation.NativeTurnStartEventId);
            writer.WriteString("nativeTurnEndEventId", observation.NativeTurnEndEventId);
            writer.WriteString("nativeCompletionReceiptEventId", observation.NativeCompletionReceiptEventId);
            writer.WriteString("completedAt", observation.CompletedAt.ToUniversalTime()
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("durableCursor", observation.DurableCursor);
            writer.WriteString("outputHash", observation.OutputHash);
            writer.WritePropertyName("accountingCheckpoint");
            if (observation.AccountingCheckpoint is not { } checkpoint)
                writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("nativeEventId", checkpoint.NativeEventId);
                writer.WriteNumber("reportedNanoAiu", checkpoint.ReportedNanoAiu);
                writer.WritePropertyName("sourceWatermarks");
                writer.WriteStartObject();
                foreach (var (source, sequence) in checkpoint.SourceWatermarks.OrderBy(
                             item => item.Key, StringComparer.Ordinal))
                    writer.WriteNumber(source, sequence);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WritePropertyName("usageEventIds");
            writer.WriteStartArray();
            foreach (var eventId in observation.UsageEventIds)
                writer.WriteStringValue(eventId);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return RuntimeContractValidation.Hash(stream.ToArray());
    }

    public static void ValidateRecorded(RuntimeNativeTurnRecordedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateObservation(receipt.Admission, receipt.Observation);
        if (receipt.OwnerRevision <= receipt.Admission.OwnerRevision ||
            receipt.CanonicalPayloadHash != ObservationHash(receipt.Observation))
            throw new RuntimeAuthorizationException("runtime_native_turn_receipt_invalid");
    }
}
