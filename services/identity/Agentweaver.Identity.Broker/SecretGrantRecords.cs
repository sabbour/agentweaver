using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// The mutable pointer to a grant's latest immutable revision. The database trigger
/// permits only a one-step advance; the revision rows themselves are append-only.
/// </summary>
public sealed class SecretGrantHead
{
    public required string GrantId { get; set; }

    public long CurrentRevision { get; set; }
}

/// <summary>
/// An immutable server-owned grant snapshot. Secret references are identifiers only;
/// credential values never enter Identity persistence.
/// </summary>
public sealed class SecretGrantRevision
{
    public required string GrantId { get; set; }

    public long Revision { get; set; }

    public required string ActorId { get; set; }

    public required string ProjectId { get; set; }

    public required string RunId { get; set; }

    public required string Purpose { get; set; }

    public required string SecretId { get; set; }

    public required string SecretVersion { get; set; }

    public GrantState State { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Durable idempotency receipt for a grant mutation. The request hash contains only
/// grant metadata, never credential bytes.
/// </summary>
public sealed class SecretGrantOperation
{
    public required string IdempotencyKey { get; set; }

    public required string RequestHash { get; set; }

    public required string GrantId { get; set; }

    public long Revision { get; set; }
}
