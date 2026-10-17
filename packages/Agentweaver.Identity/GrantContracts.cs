using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>
/// Authenticated actor, project, and run identity supplied by a trusted host to the
/// secret-grant authorization boundary; callers must not construct it from request claims.
/// </summary>
// Established only by the trusted host after it authenticates the calling actor
// (for example AgentHost validating a run's identity). Nothing in this type is
// derived from caller-supplied request data, so a request cannot forge or widen
// the actor/project/run binding it is evaluated against.
public sealed class TrustedActorContext
{
    /// <summary>Creates an actor context after validating all three opaque identities.</summary>
    /// <exception cref="ArgumentException">An identity is empty, too long, or contains unsupported characters.</exception>
    public TrustedActorContext(string actorId, string projectId, string runId)
    {
        ActorId = GrantIdentifier.Validate(actorId, nameof(actorId));
        ProjectId = GrantIdentifier.Validate(projectId, nameof(projectId));
        RunId = GrantIdentifier.Validate(runId, nameof(runId));
    }

    /// <summary>The authenticated actor identity.</summary>
    public string ActorId { get; }

    /// <summary>The project bound to the authenticated actor.</summary>
    public string ProjectId { get; }

    /// <summary>The run bound to the authenticated actor.</summary>
    public string RunId { get; }
}

/// <summary>Lifecycle state of an immutable secret redemption grant.</summary>
public enum GrantState
{
    Active,
    Revoked,
}

/// <summary>
/// Server-owned immutable permission for one actor, project, run, purpose, and exact secret
/// version. Changes to authorization require a new stored revision rather than mutating this object.
/// </summary>
// A server-owned, immutable authorization record. It binds exactly one actor,
// project, run, purpose and SecretRef (identifier and exact version); there is
// no wildcard or admin-scope form. A grant never mutates in place: revoking or
// otherwise changing what it authorizes must produce a different grant (a new
// GrantId or revision in the authority's store), never a setter on
// this instance.
public sealed class SecretRedemptionGrant
{
    /// <summary>Creates a future-expiring grant with exact actor, run, purpose, and secret bindings.</summary>
    /// <exception cref="ArgumentException">An opaque identifier is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown or the grant is already expired.</exception>
    public SecretRedemptionGrant(
        string grantId,
        string actorId,
        string projectId,
        string runId,
        string purpose,
        SecretRef secret,
        GrantState state,
        DateTimeOffset expiresAt,
        TimeProvider? timeProvider = null,
        string revision = "1")
    {
        var time = timeProvider ?? TimeProvider.System;
        GrantId = GrantIdentifier.Validate(grantId, nameof(grantId));
        Revision = GrantIdentifier.Validate(revision, nameof(revision));
        ActorId = GrantIdentifier.Validate(actorId, nameof(actorId));
        ProjectId = GrantIdentifier.Validate(projectId, nameof(projectId));
        RunId = GrantIdentifier.Validate(runId, nameof(runId));
        Purpose = GrantIdentifier.Validate(purpose, nameof(purpose));
        Secret = secret ?? throw new ArgumentNullException(nameof(secret));
        if (state is not (GrantState.Active or GrantState.Revoked))
            throw new ArgumentOutOfRangeException(nameof(state), "Unrecognized grant state.");
        State = state;
        if (expiresAt <= time.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A grant must have a future expiry.");
        ExpiresAt = expiresAt;
    }

    public string GrantId { get; }
    public string Revision { get; }
    public string ActorId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string Purpose { get; }
    public SecretRef Secret { get; }
    public GrantState State { get; }
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// Lookup boundary for the trusted grant store. Callers re-read on every redemption and deny
/// ambiguous results; implementations should advance a revision for every authorization change.
/// </summary>
// The trusted authority (store) that an authorization layer re-reads on every
// redemption and refresh. Implementations own revocation and expiry state;
// this contract only shapes the lookup so callers never cache a grant across
// calls. Returning more than one candidate for a lookup is treated as
// ambiguous and denied; implementations should not rely on the caller to
// de-duplicate. Advance Revision on every change, including revoke/reactivate.
public interface IGrantAuthority
{
    /// <summary>Returns all grants matching the trusted actor and exact secret request.</summary>
    Task<IReadOnlyList<SecretRedemptionGrant>> FindGrantsAsync(
        TrustedActorContext actor,
        SecretRedemptionRequest request,
        CancellationToken cancellationToken);
}

internal static class GrantIdentifier
{
    // Mirrors Agentweaver.Abstractions.SecretContractIdentifier's rules so actor,
    // project, run, purpose and grant identifiers share one opaque-identifier
    // shape with SecretRef across the authorization boundary.
    /// <summary>Applies the shared opaque-identifier character and length rules.</summary>
    internal static string Validate(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier (at most 256 ASCII characters).", paramName);
        return value;
    }
}
