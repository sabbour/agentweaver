namespace Agentweaver.Api.Memory;

public static class AddressedMessageStates
{
    public const string Accepted = "accepted";
    public const string Claimed = "claimed";
    public const string Delivered = "delivered";
    public const string Acknowledged = "acknowledged";
    public const string Expired = "expired";
    public const string Undeliverable = "undeliverable";
}

public sealed class AddressedMessage
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Sender { get; set; } = "";
    public string SenderIdentity { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string? SourceRunId { get; set; }
    public string TargetRunId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string? ReplyToId { get; set; }
    public string? ReferenceKind { get; set; }
    public string? ReferenceId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Content { get; set; } = "";
    public string Status { get; set; } = AddressedMessageStates.Accepted;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public string? ClaimOwner { get; set; }
    public long Fence { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? FailureReason { get; set; }
}
