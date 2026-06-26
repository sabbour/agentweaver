using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A revoked access-token identifier (<c>jti</c>) on the short-TTL denylist (T4 / RFC 7009).
///
/// Populated by <c>/oauth/revoke</c> so an access token can be invalidated before its natural
/// expiry. The entry self-purges after <see cref="ExpiresAt"/> (set to the access-token lifetime),
/// keeping the denylist bounded. Token validation rejects any JWT whose <c>jti</c> is present and
/// not yet expired.
/// </summary>
public sealed class McpRevokedJti
{
    [Key] public int Id { get; set; }

    /// <summary>The revoked JWT <c>jti</c> claim. Unique.</summary>
    public required string Jti { get; set; }

    /// <summary>When the denylist entry may be purged (= the revoked token's natural expiry).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset RevokedAt { get; set; }
}
