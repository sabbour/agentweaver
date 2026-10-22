using System.Collections.Immutable;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>Controls whether a routed session message is delivered immediately or queued.</summary>
public enum AddressedMessageDeliveryMode
{
    Immediate,
    Enqueue
}

/// <summary>Explains why an addressed session message is being sent.</summary>
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

/// <summary>Classifies the user-facing interaction carried by an addressed message.</summary>
public enum AddressedMessageKind
{
    Text,
    Steering,
    Question,
    Approval,
    Proposal
}

/// <summary>Tracks the owner-managed lifecycle of an addressed session message.</summary>
public enum AddressedMessageStatus
{
    Accepted,
    Claimed,
    Delivered,
    Acknowledged,
    Expired,
    Undeliverable
}

/// <summary>Explains why delivery failed after message admission.</summary>
public enum AddressedMessageFailureReason
{
    StaleFence,
    TargetCancelled,
    TargetCompleted,
    RecipientUnavailable
}

/// <summary>
/// Sender input to the session-message owner. Sender and recipient fences prevent stale
/// sessions from sending or receiving, while the idempotency key makes retries stable.
/// </summary>
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

/// <summary>Identity provenance attached by the trusted message owner, never by the sender.</summary>
public sealed record AddressedMessageIdentityMetadata(
    string Issuer,
    string SubjectHash,
    int ContractVersion);

/// <summary>Provider binding recorded with an admitted message for audit and replay.</summary>
public sealed record AddressedMessageProviderMetadata(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceIdHash,
    long ResourceGeneration,
    ImmutableArray<string> NegotiatedCapabilities);

/// <summary>
/// Owner-issued message record with thread ordering, both session fences, claim fence,
/// lifecycle timestamps, provenance, and the normalized sender payload.
/// </summary>
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

/// <summary>Returns the admitted message and whether this send matched an earlier idempotent request.</summary>
public sealed record AddressedMessageSendResult(AddressedMessageEnvelope Message, bool IsDuplicate);

/// <summary>Lease for one claimed message; the claimant must complete it before expiry.</summary>
public sealed record AddressedMessageClaim(
    AddressedMessageEnvelope Message,
    string Owner,
    DateTimeOffset LeaseExpiresAt);

/// <summary>Represents either a current message claim or no claim available for delivery.</summary>
public sealed record AddressedMessageClaimResult(AddressedMessageClaim? Claim);

/// <summary>Message payload submitted by an owner service before routing and admission.</summary>
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

/// <summary>Asks the session owner to validate an owner-produced outbound message route.</summary>
public sealed record MessageRouteValidationRequest(
    OwnerOutboundMessage Message);

/// <summary>Owner-issued sender, recipient, fence, and claim-owner binding for message admission.</summary>
public sealed record MessageRouteBinding(
    SessionIdentity Sender,
    SessionIdentity Recipient,
    long SenderFence,
    long RecipientFence,
    string ClaimOwner,
    Guid? ReplyToMessageId = null);

/// <summary>Current coordination state used to check whether a session may receive a message.</summary>
public sealed record CoordinationSessionBinding(
    SessionIdentity Identity,
    long ExecutionFence,
    string TurnState,
    long StateVersion,
    bool PendingWake,
    string ClaimOwner);

/// <summary>Supplies the claim fence required to acknowledge or otherwise finish a claimed message.</summary>
public sealed record AddressedMessageClaimFenceRequest(long ClaimFence);

/// <summary>Acknowledges durable message admission and its assigned thread order and current status.</summary>
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

/// <summary>Enforces message size, routing, lifecycle, and correlation invariants at the owner boundary.</summary>
public static class AddressedMessageValidation
{
    /// <summary>Maximum accepted length for an idempotency key.</summary>
    public const int MaximumIdempotencyKeyLength = 128;
    /// <summary>Maximum length for user quote or coordinator instruction text.</summary>
    public const int MaximumTextLength = 16_000;
    /// <summary>Maximum serialized JSON payload length.</summary>
    public const int MaximumPayloadLength = 64_000;
    /// <summary>Maximum lifetime allowed for an admitted message.</summary>
    public const int MaximumLifetimeDays = 7;

    /// <summary>Validates project/session separation and returns a normalized, owned draft.</summary>
    /// <exception cref="ArgumentException">The route, fences, payload, text, or correlation fields are invalid.</exception>
    public static AddressedMessageDraft ValidateAndNormalize(AddressedMessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Sender.ProjectId != draft.Recipient.ProjectId)
            throw new ArgumentException("Addressed messages cannot cross project boundaries.", nameof(draft));
        if (draft.Sender.SessionId == draft.Recipient.SessionId &&
            draft.Sender.RunId == draft.Recipient.RunId && !IsInteractiveFirstMessage(draft))
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
            draft.Payload.GetRawText().Length > (IsInteractiveFirstMessage(draft) ? 200_000 : MaximumPayloadLength) ||
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

    /// <summary>Recognizes owner-proven first input; the Orchestrator additionally checks its persisted request.</summary>
    public static bool IsInteractiveFirstMessage(AddressedMessageDraft draft) =>
        draft.Sender == draft.Recipient &&
        draft.Kind == AddressedMessageKind.Text &&
        draft.Purpose == AddressedMessagePurpose.Steering &&
        draft.DeliveryMode == AddressedMessageDeliveryMode.Immediate &&
        draft.IdempotencyKey == $"{draft.Sender.RunId}:first" &&
        draft.ReplyToId is null && draft.RequestId is null && draft.ReplyCorrelationId is null &&
        draft.UserQuote is null && draft.CoordinatorInstructions is null;

    /// <summary>Checks optional text against the same control-character and length rules as required text.</summary>
    private static bool OptionalText(string? value, int maximumLength) =>
        value is null || IsText(value, maximumLength);

    private static bool IsText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    /// <summary>Trims optional text and represents whitespace-only values as absent.</summary>
    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
