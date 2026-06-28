using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A pending MCP OAuth authorization, persisted while the brokered GitHub login leg is in flight.
///
/// Created at <c>/oauth/authorize</c> and correlated to the brokered GitHub callback by the GitHub
/// CSRF <see cref="State"/>. Holds the client's PKCE request so the callback can mint a matching
/// authorization code. Replica-safe: stored in <c>MemoryDbContext</c> (Postgres in prod, SQLite in
/// dev) so any replica can complete the flow regardless of which one served <c>/oauth/authorize</c>.
/// Single-use: the row is removed when the GitHub callback is handled. Short-lived (10-min TTL).
/// </summary>
public sealed class McpPendingAuthorization
{
    [Key] public int Id { get; set; }

    /// <summary>The GitHub CSRF state correlating this pending authorization to its callback. Unique.</summary>
    public required string State { get; set; }

    /// <summary>The requesting public client's client_id.</summary>
    public required string ClientId { get; set; }

    /// <summary>The exact redirect URI captured at /authorize, re-checked at token redemption.</summary>
    public required string RedirectUri { get; set; }

    /// <summary>The PKCE S256 code_challenge supplied by the client.</summary>
    public required string CodeChallenge { get; set; }

    /// <summary>The client's opaque state echoed back on the final redirect (optional).</summary>
    public string? ClientState { get; set; }

    public required string Scope { get; set; }

    /// <summary>The MCP resource (RFC 8707) the issued token is bound to (optional).</summary>
    public string? Resource { get; set; }

    /// <summary>When this pending authorization expires (10 minutes after /authorize).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
