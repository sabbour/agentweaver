using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A pending web sign-in CSRF <c>state</c> token for the GitHub OAuth authorization-code flow,
/// persisted so the flow survives load-balancing across API replicas.
///
/// The browser sign-in (web) leg arms a fresh <see cref="State"/> at <c>/auth/github/authorize</c>;
/// GitHub later redirects the browser back to <c>/auth/github/callback</c>, which the load balancer
/// may route to a DIFFERENT pod than the one that armed it. At <c>replicas:2</c> a purely per-pod
/// in-memory store would then fail the CSRF check (<c>Invalid or expired OAuth state</c>) ~50% of
/// the time, so the state must live in <c>MemoryDbContext</c> (Postgres in prod, SQLite in dev)
/// rather than per-pod memory. Single-use is enforced atomically across replicas by a conditional
/// <c>ExecuteDeleteAsync</c> on <see cref="State"/>: the caller whose delete affected the row wins;
/// a zero-rows result means the state was unknown, already consumed, or expired (replay protection
/// — at-most-once redemption). Short-lived (10-min TTL).
/// </summary>
public sealed class OAuthState
{
    /// <summary>The opaque CSRF state token. Primary key (unique, single-use).</summary>
    [Key] public required string State { get; set; }

    /// <summary>When this state expires (10 minutes after it was armed).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
