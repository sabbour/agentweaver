using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A single-use OAuth authorization code issued by the Agentweaver Authorization Server, bound to
/// the client_id + redirect_uri + PKCE challenge captured at <c>/oauth/authorize</c>.
///
/// Persisted in <c>MemoryDbContext</c> (Postgres in prod, SQLite in dev) so the code can be redeemed
/// at <c>/oauth/token</c> by ANY replica — not just the one that handled the GitHub callback.
/// Single-use is enforced atomically across replicas by conditionally deleting the row by
/// <see cref="Code"/> and treating zero rows affected as an already-redeemed/invalid code. Very
/// short-lived (60-second TTL, RFC 6749 §4.1.2 recommendation).
/// </summary>
public sealed class McpAuthorizationCode
{
    [Key] public int Id { get; set; }

    /// <summary>The opaque authorization code value. Unique. Redeemed at /oauth/token.</summary>
    public required string Code { get; set; }

    /// <summary>Resolved subject (GitHub login) the eventual access token authenticates.</summary>
    public required string Subject { get; set; }

    public required string GithubLogin { get; set; }

    /// <summary>The PKCE S256 code_challenge captured at /authorize; verified at redemption.</summary>
    public required string CodeChallenge { get; set; }

    /// <summary>The exact redirect URI captured at /authorize; must match at redemption.</summary>
    public required string RedirectUri { get; set; }

    /// <summary>The client_id the code is bound to; must match at redemption.</summary>
    public required string ClientId { get; set; }

    public required string Scope { get; set; }

    /// <summary>When this authorization code expires (60 seconds after issuance).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
