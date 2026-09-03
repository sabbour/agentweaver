using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>
/// A pending Microsoft Entra web sign-in CSRF <c>state</c> token for the Microsoft identity platform
/// v2.0 authorization-code + PKCE flow, persisted so the flow survives load-balancing across API
/// replicas.
///
/// Unlike the GitHub web leg (<see cref="OAuthState"/>, state-only) the Entra flow uses PKCE, so the
/// server must remember the <see cref="CodeVerifier"/> it generated at <c>/auth/entra/authorize</c>
/// and present it again when redeeming the authorization code at Microsoft's token endpoint in
/// <c>/auth/entra/callback</c>. The verifier is the confidential half of the PKCE pair — it is never
/// sent to the browser — so it is bound to the opaque <see cref="State"/> here rather than round-tripped
/// through the user agent.
///
/// The browser sign-in arms a fresh <see cref="State"/>/<see cref="CodeVerifier"/> pair at
/// <c>/auth/entra/authorize</c>; Microsoft later redirects the browser back to
/// <c>/auth/entra/callback</c>, which the load balancer may route to a DIFFERENT pod than the one that
/// armed it. At <c>replicas:2</c> a purely per-pod in-memory store would then fail the CSRF/PKCE check
/// ~50% of the time, so the state must live in <c>MemoryDbContext</c> (Postgres in prod, SQLite in dev)
/// rather than per-pod memory. Single-use is enforced atomically across replicas by a conditional
/// <c>ExecuteDeleteAsync</c> on <see cref="State"/>: the caller whose delete affected the row wins; a
/// zero-rows result means the state was unknown, already consumed, or expired (replay protection —
/// at-most-once redemption). Short-lived (10-min TTL).
/// </summary>
public sealed class EntraOAuthState
{
    /// <summary>The opaque CSRF state token. Primary key (unique, single-use).</summary>
    [Key] public required string State { get; set; }

    /// <summary>The PKCE code_verifier bound to this state; replayed at the token endpoint (S256).</summary>
    public required string CodeVerifier { get; set; }

    /// <summary>Optional local OAuth transaction handle; never an arbitrary URL.</summary>
    public string? ReturnHandle { get; set; }

    /// <summary>OIDC nonce used only by an Agentweaver authorization-server broker transaction.</summary>
    public string? Nonce { get; set; }

    /// <summary>When this state expires (10 minutes after it was armed).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
