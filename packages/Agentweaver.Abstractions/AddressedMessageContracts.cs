using System.Collections.Immutable;
using System.Text.Json;

namespace Agentweaver.Abstractions;

public enum AddressedMessageDeliveryMode
{
    Immediate,
    Enqueue
}

public enum AddressedMessagePurpose
{
    Progress,
    Handoff,
    NeedsInput,
    Error,
    Steering,
    Question,
    ApprovalRequest,
    Proposal
}

public enum AddressedMessageKind
{
    Text,
    Steering,
    Question,
    Approval,
    Proposal
}

public enum AddressedMessageStatus
{
    Accepted,
    Claimed,
    Delivered,
    Acknowledged,
    Expired,
    Undeliverable
}

public enum AddressedMessageFailureReason
{
    StaleFence,
    TargetCancelled,
    TargetCompleted,
    RecipientUnavailable
}

public sealed record AddressedMessageDraft(
    SessionIdentity Sender,
    SessionIdentity Recipient,
    string IdempotencyKey,
    AddressedMessageDeliveryMode DeliveryMode,
    AddressedMessagePurpose Purpose,
    AddressedMessageKind Kind,
    JsonElement Payload,
    long SenderFence,
    long RecipientFence,
    Guid? ThreadId = null,
    Guid? ReplyToId = null,
    string? RequestId = null,
    string? ReplyCorrelationId = null,
    string? UserQuote = null,
    string? CoordinatorInstructions = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record AddressedMessageIdentityMetadata(
    string Issuer,
    string SubjectHash,
    int ContractVersion);

public sealed record AddressedMessageProviderMetadata(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceIdHash,
    long ResourceGeneration,
    ImmutableArray<string> NegotiatedCapabilities);

public sealed record AddressedMessageEnvelope(
    Guid MessageId,
    SessionIdentity Sender,
    SessionIdentity Recipient,
    Guid ThreadId,
    Guid? ReplyToId,
    string IdempotencyKey,
    long ThreadSequence,
    long SenderFence,
    long RecipientFence,
    long ClaimFence,
    AddressedMessageDeliveryMode DeliveryMode,
    AddressedMessagePurpose Purpose,
    AddressedMessageKind Kind,
    string? RequestId,
    string? ReplyCorrelationId,
    string? UserQuote,
    string? CoordinatorInstructions,
    JsonElement Payload,
    AddressedMessageStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PresentedAt,
    DateTimeOffset? AcknowledgedAt,
    AddressedMessageFailureReason? FailureReason,
    AddressedMessageIdentityMetadata Identity,
    AddressedMessageProviderMetadata Provider);

public sealed record AddressedMessageSendResult(AddressedMessageEnvelope Message, bool IsDuplicate);

public sealed record AddressedMessageClaim(
    AddressedMessageEnvelope Message,
    string Owner,
    DateTimeOffset LeaseExpiresAt);

public sealed record AddressedMessageClaimResult(AddressedMessageClaim? Claim);

public sealed record OwnerOutboundMessage(
    Guid OwnerMessageId,
    SessionIdentity Sender,
    SessionIdentity Recipient,
    string IdempotencyKey,
    AddressedMessageDeliveryMode DeliveryMode,
    AddressedMessagePurpose Purpose,
    AddressedMessageKind Kind,
    JsonElement Payload,
    long SenderFence,
    long RecipientFence,
    Guid? ThreadId,
    Guid? ReplyToId,
    string? RequestId,
    string? ReplyCorrelationId,
    string? UserQuote,
    string? CoordinatorInstructions);

public sealed record MessageRouteValidationRequest(
    OwnerOutboundMessage Message);

public sealed record MessageRouteBinding(
    SessionIdentity Sender,
    SessionIdentity Recipient,
    long SenderFence,
    long RecipientFence,
    string ClaimOwner,
    Guid? ReplyToMessageId = null);

public sealed record CoordinationSessionBinding(
    SessionIdentity Identity,
    long ExecutionFence,
    string TurnState,
    long StateVersion,
    bool PendingWake,
    string ClaimOwner);

public sealed record AddressedMessageClaimFenceRequest(long ClaimFence);

public sealed record MessageAdmissionReceipt(
    Guid OwnerMessageId,
    Guid MessageId,
    Guid ThreadId,
    long ThreadSequence,
    AddressedMessageStatus Status,
    string? RequestId,
    AddressedMessagePurpose Purpose,
    SessionIdentity Sender,
    SessionIdentity Recipient,
    long SenderFence,
    long RecipientFence,
    AddressedMessageClaim? Claim = null);

public static class AddressedMessageValidation
{
    public const int MaximumIdempotencyKeyLength = 128;
    public const int MaximumTextLength = 16_000;
    public const int MaximumPayloadLength = 64_000;
    public const int MaximumLifetimeDays = 7;

    public static AddressedMessageDraft ValidateAndNormalize(AddressedMessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Sender.ProjectId != draft.Recipient.ProjectId)
            throw new ArgumentException("Addressed messages cannot cross project boundaries.", nameof(draft));
        if (draft.Sender.SessionId == draft.Recipient.SessionId &&
            draft.Sender.RunId == draft.Recipient.RunId)
            throw new ArgumentException("A message must target a different session.", nameof(draft));
        if (!Enum.IsDefined(draft.DeliveryMode) ||
            !Enum.IsDefined(draft.Purpose) ||
            !Enum.IsDefined(draft.Kind) ||
            draft.SenderFence < 1 ||
            draft.RecipientFence < 1)
            throw new ArgumentException("Addressed message routing metadata is invalid.", nameof(draft));

        var idempotencyKey = draft.IdempotencyKey?.Trim();
        if (!IsText(idempotencyKey, MaximumIdempotencyKeyLength) ||
            draft.Payload.ValueKind != JsonValueKind.Object ||
            draft.Payload.GetRawText().Length > MaximumPayloadLength ||
            !OptionalText(draft.RequestId, 128) ||
            !OptionalText(draft.ReplyCorrelationId, 128) ||
            !OptionalText(draft.UserQuote, MaximumTextLength) ||
            !OptionalText(draft.CoordinatorInstructions, MaximumTextLength) ||
            (draft.Kind is AddressedMessageKind.Question or AddressedMessageKind.Approval or
                AddressedMessageKind.Proposal && string.IsNullOrWhiteSpace(draft.RequestId)) ||
            (draft.ReplyToId is null && draft.ReplyCorrelationId is not null) ||
            (draft.ThreadId == Guid.Empty) ||
            (draft.ReplyToId == Guid.Empty))
            throw new ArgumentException("Addressed message content or correlation metadata is invalid.", nameof(draft));

        return draft with
        {
            IdempotencyKey = idempotencyKey!,
            Payload = draft.Payload.Clone(),
            UserQuote = NormalizeOptionalText(draft.UserQuote),
            CoordinatorInstructions = NormalizeOptionalText(draft.CoordinatorInstructions),
            RequestId = NormalizeOptionalText(draft.RequestId),
            ReplyCorrelationId = NormalizeOptionalText(draft.ReplyCorrelationId)
        };
    }

    private static bool OptionalText(string? value, int maximumLength) =>
        value is null || IsText(value, maximumLength);

    private static bool IsText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
