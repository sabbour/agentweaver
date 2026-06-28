using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A short-lived, single-use one-time code issued by the GitHub OAuth callback to carry the
/// session token back to the browser without exposing it in the redirect URL (security finding F5).
///
/// The callback redirects with only the opaque <see cref="Code"/>; the browser then exchanges it
/// for the actual GitHub access token + login via a server-side POST
/// (<c>/api/auth/session/exchange</c>).
///
/// Persisted in <c>MemoryDbContext</c> (Postgres in prod, SQLite in dev) so the exchange endpoint
/// works on ANY replica — with <c>replicas:2</c> and no session affinity the POST can land on a
/// DIFFERENT pod than the one that issued the code. At-most-once redemption is enforced atomically
/// across replicas via a conditional <c>ExecuteDeleteAsync</c> on <see cref="Code"/>: exactly one
/// caller's delete succeeds; a zero-rows result means unknown, already consumed, or expired.
/// Short-lived (60s TTL).
/// </summary>
public sealed class WebSessionExchangeCode
{
    /// <summary>The opaque one-time code. Primary key (unique, single-use).</summary>
    [Key] public required string Code { get; set; }

    /// <summary>The GitHub access token bound to this code.</summary>
    public required string AccessToken { get; set; }

    /// <summary>The GitHub login bound to this code.</summary>
    public required string Login { get; set; }

    /// <summary>When this code expires (60 seconds after it was issued).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
