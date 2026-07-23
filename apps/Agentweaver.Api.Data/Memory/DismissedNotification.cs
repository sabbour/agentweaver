namespace Agentweaver.Api.Memory;

public sealed class DismissedNotification
{
    public required string User { get; init; }
    public required string NotificationId { get; init; }
    public DateTimeOffset DismissedAt { get; init; }
}
