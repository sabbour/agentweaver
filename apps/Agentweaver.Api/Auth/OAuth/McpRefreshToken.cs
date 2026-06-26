using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A persisted, rotating OAuth refresh token issued by the Agentweaver Authorization Server (T4).
///
/// The opaque token value is NEVER stored; only its SHA-256 hash (<see cref="TokenHash"/>) is kept,
/// so a database disclosure cannot be replayed as a credential. Tokens form a rotation chain
/// (<see cref="ChainId"/>): each successful refresh consumes the presented token
/// (<see cref="ConsumedAt"/>) and issues a fresh token in the same chain. Presenting an already
/// consumed token is treated as reuse/theft and revokes the entire chain (reuse detection).
/// </summary>
public sealed class McpRefreshToken
{
    [Key] public int Id { get; set; }

    /// <summary>SHA-256 (hex) of the opaque refresh-token value. Unique. The plaintext is never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Rotation-chain identifier shared by every token derived from the same original grant.</summary>
    public required string ChainId { get; set; }

    /// <summary>Resolved subject (GitHub login) the token authenticates.</summary>
    public required string Subject { get; set; }

    public required string GithubLogin { get; set; }

    /// <summary>The client_id the token is bound to (DCR or public client).</summary>
    public required string ClientId { get; set; }

    public required string Scope { get; set; }

    /// <summary>Org claim captured at issuance (informational; org is re-checked on refresh).</summary>
    public string? Org { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Sliding expiry (30 days) refreshed on each rotation, never beyond <see cref="AbsoluteExpiresAt"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Hard cap (90 days from the original grant) after which the chain can no longer refresh.</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    /// <summary>Set when this token is rotated (consumed). A consumed token must never be accepted again.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Set when the token (or its chain) is explicitly revoked or invalidated by reuse detection.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
