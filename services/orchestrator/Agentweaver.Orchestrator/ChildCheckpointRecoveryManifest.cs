using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal sealed record ChildRecoverySession(
    string SessionId,
    string? ParentSessionId,
    string RootSessionId,
    bool Detached,
    string WriterIssuer,
    string WriterSubject,
    long ExecutionFence,
    string Lifecycle,
    string TurnState,
    long LogicalTurnOrdinal,
    long StateVersion,
    string? WorkPlanItemId,
    DateTimeOffset? ArchivedAt);

internal sealed record ChildRecoveryStartup(
    RuntimeChildStartBinding Binding,
    Guid IntentId,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    string State,
    string CurrentStep,
    string? EnvironmentId,
    Guid? RuntimeInstanceId,
    long RowVersion,
    bool CleanupObligation,
    string ReceiptLinksHash,
    Guid? RegistrationId,
    long? RegistrationRevision,
    long? CurrentRegistrationRevision,
    string? RegistrationState,
    DateTimeOffset? RegistrationExpiresAt,
    string? RegistrationBindingHash,
    RuntimeRunStartPlacementReceipt? Placement,
    string? BootstrapReceiptHash,
    Guid? BootstrapGrantId,
    string? BootstrapConfigurationHash,
    ImmutableArray<ChildRecoveryPriorStartup> PriorStarts);

internal sealed record ChildRecoveryPriorStartup(
    string Source,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    Guid RegistrationId,
    long RegistrationRevision,
    long CurrentRegistrationRevision,
    string RegistrationBindingHash,
    RuntimeRunStartPlacementReceipt Placement,
    string? RetirementReceiptHash,
    string RecordHash);

internal sealed record ChildRecoveryNativeOrigin(
    Guid DispatchId,
    string CheckpointId,
    long CheckpointRevision,
    long DecisionStateVersion,
    long ExecutionFence,
    string AcceptedSelectionHash,
    string PromptHash,
    string RequestHash,
    long OwnerRevision,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string RuntimeBindingHash,
    string SdkSourceReceiptHash,
    string ModelSelectionReference,
    string SelectedModelId,
    string? CostBindingHash,
    bool IsCostProducing,
    bool HardCapSerialized,
    string State,
    string? TerminalOutcome,
    string SourceReportStatus,
    string? SourceReportHash,
    string? SourceCompletionManifestHash,
    string AcknowledgmentReferencesHash,
    string AccountingStatus,
    long? UsageWitnessWatermark,
    string? UsageWitnessDigest,
    long? RetiredThroughEventPosition);

internal sealed record ChildRecoveryMember(
    string AssociationId,
    ChildRecoverySession Session,
    string WorkflowStepId,
    string AgentId,
    string ModelSelectionReference,
    string? ReasoningEffort,
    string IsolationProviderId,
    string AssociationHash,
    string SpawnCommandHash,
    long? ActivationRevision,
    ChildRecoveryStartup Startup,
    Guid MessageId,
    string PromptHash,
    string? EgressReceiptHash,
    string? MessageStatus,
    string? MessageRequestHash,
    string Progress,
    string Disposition,
    string? Reason,
    ChildRecoveryNativeOrigin? NativeOrigin,
    bool NativeOriginProvenAbsent,
    string? ResultHash,
    string? TurnReceiptHash);

internal sealed record ChildRecoveryCapture(
    string SessionId,
    string CaptureId,
    Guid EventId,
    string PinId,
    string State,
    string WorkspaceId,
    Guid WorkspaceIncarnationId,
    string RepositoryId,
    long ResourceGeneration,
    string BaseSha,
    string OutputTreeSha,
    string ManifestSha256,
    string PatchSha256,
    string PackageSha256,
    string? ObjectKey,
    long? EventPosition);

internal sealed record ChildRecoveryRepositoryPin(
    string PinId,
    string SelectionHash,
    long ExecutionFence,
    string PinHash);

internal sealed record ChildRecoveryClaim(
    string TaskId,
    Guid ClaimId,
    long TaskRevision,
    long ClaimTaskRevision,
    string Phase,
    string ClaimRequestHash,
    long ClaimExecutionFence,
    long ClaimDecisionStateVersion,
    string ContentHash);

internal sealed record ChildRecoveryOtherNativeWork(string Kind, string Id, string State, string EvidenceHash);

internal sealed record ChildRecoveryReservation(
    Guid DispatchId,
    string BucketKey,
    string BudgetId,
    long BudgetRevision,
    string State,
    decimal ReserveAmount,
    long? RetiredCursor,
    string RecordHash);

internal sealed record ChildRecoveryRootStartup(
    Guid IntentId,
    Guid BootstrapOperationId,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    string State,
    string CurrentStep,
    string? EnvironmentId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long RowVersion,
    string ReceiptLinksHash,
    string RegistrationBindingHash,
    RuntimeRunStartPlacementReceipt Placement,
    string BootstrapReceiptHash,
    Guid BootstrapGrantId,
    string BootstrapConfigurationHash,
    ImmutableArray<ChildRecoveryPriorStartup> PriorStarts);

internal sealed record ChildCheckpointRecoveryManifest(
    int Version,
    Guid OperationId,
    SessionIdentity Root,
    string TenantId,
    string ActorIssuer,
    string ActorId,
    string SelectionHash,
    string WorkPlanId,
    string WorkPlanHash,
    string WorkflowHash,
    string SourceCheckpointId,
    long SourceCheckpointRevision,
    string SourceCheckpointHash,
    long SourceExecutionFence,
    long SourceDecisionStateVersion,
    long HoldFence,
    Guid RootRuntimeInstanceId,
    long RootRegistrationRevision,
    ChildRecoveryRootStartup RootStartup,
    ImmutableArray<ChildRecoveryMember> Members,
    ImmutableArray<ChildRecoveryRepositoryPin> RepositoryPins,
    ImmutableArray<ChildRecoveryCapture> OutputCaptures,
    ChildRecoveryClaim? Claim,
    ImmutableArray<ChildRecoveryReservation> Reservations,
    ImmutableArray<ChildRecoveryOtherNativeWork> OtherNativeWork);

internal sealed record EncodedChildRecoveryManifest(string Json, string Sha256);

internal static class ChildCheckpointRecoveryManifestCodec
{
    internal const int Version = 1;
    internal const int MaximumChildren = 512;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumReceiptBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed class ByteBudget
    {
        private long _bytes;

        internal void Include<T>(T value)
        {
            _bytes += JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;
            if (_bytes > MaximumBytes)
                throw new CoordinationException(
                    "checkpoint_recovery_manifest_too_large", StatusCodes.Status409Conflict);
        }
    }

    internal static EncodedChildRecoveryManifest Encode(ChildCheckpointRecoveryManifest manifest)
    {
        if (manifest.Version != Version || manifest.OperationId == Guid.Empty || manifest.RootStartup is null ||
            manifest.RootStartup.Placement is null || manifest.RootStartup.PriorStarts.IsDefault ||
            manifest.SourceExecutionFence < 1 || manifest.SourceExecutionFence == long.MaxValue ||
            manifest.HoldFence != manifest.SourceExecutionFence + 1 ||
            manifest.RootRuntimeInstanceId != manifest.RootStartup.RuntimeInstanceId ||
            manifest.RootRegistrationRevision != manifest.RootStartup.RegistrationRevision ||
            manifest.Members.IsDefaultOrEmpty || manifest.Members.Length > MaximumChildren ||
            manifest.Members.Any(member => member is null || member.Session is null || member.Startup is null ||
                member.Startup.Binding is null || member.Startup.PriorStarts.IsDefault ||
                string.IsNullOrWhiteSpace(member.AssociationId) ||
                string.IsNullOrWhiteSpace(member.Session.SessionId) ||
                member.NativeOriginProvenAbsent != (member.NativeOrigin is null) ||
                member.Disposition is not ("joined" or "completedUnjoined" or "pendingUnsent" or "unresolved")) ||
            manifest.RepositoryPins.IsDefault || manifest.OutputCaptures.IsDefault ||
            manifest.Reservations.IsDefault || manifest.OtherNativeWork.IsDefault ||
            manifest.Members.Select(member => member.AssociationId).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Members.Length ||
            manifest.Members.Select(member => member.Session.SessionId).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Members.Length)
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        var element = JsonSerializer.SerializeToElement(manifest, JsonOptions);
        var bytes = CanonicalBytes(element);
        if (bytes.Length > MaximumBytes)
            throw new CoordinationException(
                "checkpoint_recovery_manifest_too_large", StatusCodes.Status409Conflict);
        return new EncodedChildRecoveryManifest(
            Encoding.UTF8.GetString(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static ChildCheckpointRecoveryManifest Decode(string json, string expectedHash)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is 0 or > MaximumBytes || expectedHash.Length != 64 ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(bytes))),
                Encoding.ASCII.GetBytes(expectedHash)))
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict);
        try
        {
            var manifest = JsonSerializer.Deserialize<ChildCheckpointRecoveryManifest>(bytes, JsonOptions)
                ?? throw new JsonException();
            var encoded = Encode(manifest);
            if (!string.Equals(encoded.Json, json, StringComparison.Ordinal))
                throw new JsonException();
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "checkpoint_recovery_manifest_invalid", StatusCodes.Status409Conflict, exception);
        }
    }

    internal static string Hash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(
            JsonSerializer.SerializeToElement(value, JsonOptions))));

    private static byte[] CanonicalBytes(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, element);
        return buffer.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
