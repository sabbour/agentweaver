namespace Agentweaver.Api.Memory;

/// <summary>
/// Durable, short-lived proof that an AgentHost owns a specific run.
/// Only a SHA-256 digest is persisted; the bearer capability is returned to the host once.
/// </summary>
public sealed class RunAuthorshipCapability
{
    public required string RunId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
