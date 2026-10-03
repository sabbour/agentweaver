namespace Agentweaver.Identity.Broker;

/// <summary>
/// A short-lived, server-persisted record of an in-flight authorization request that is
/// waiting on either external login or explicit consent. The opaque handle returned to the
/// caller is never stored directly: only its SHA-256 hash is persisted, so a leaked log line
/// or database row cannot be replayed as the handle itself. Protocol parameters and
/// project/run selectors live here across redirects; selectors remain untrusted until the
/// grant authority confirms the authenticated subject's active binding.
/// </summary>
public sealed class PendingAuthorization
{
    public Guid Id { get; set; }

    public required string HandleHash { get; set; }

    public required string ClientId { get; set; }

    public required string RedirectUri { get; set; }

    public required string Scope { get; set; }

    public string? State { get; set; }

    public string? CodeChallenge { get; set; }

    public string? CodeChallengeMethod { get; set; }

    public string? Nonce { get; set; }

    /// <summary>Untrusted authorization selectors, later checked against active Identity grants.</summary>
    public string? ProjectId { get; set; }

    public string? RunId { get; set; }

    /// <summary>Set once the caller has authenticated with the external provider.</summary>
    public Guid? SubjectUserId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}
