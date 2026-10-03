using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

// Established only by the trusted host after it authenticates the calling actor
// (for example AgentHost validating a run's identity). Nothing in this type is
// derived from caller-supplied request data, so a request cannot forge or widen
// the actor/project/run binding it is evaluated against.
public sealed class TrustedActorContext
{
    public TrustedActorContext(string actorId, string projectId, string runId)
    {
        ActorId = GrantIdentifier.Validate(actorId, nameof(actorId));
        ProjectId = GrantIdentifier.Validate(projectId, nameof(projectId));
        RunId = GrantIdentifier.Validate(runId, nameof(runId));
    }

    public string ActorId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
}

public enum GrantState
{
    Active,
    Revoked,
}

// A server-owned, immutable authorization record. It binds exactly one actor,
// project, run, purpose and SecretRef (identifier and exact version); there is
// no wildcard or admin-scope form. A grant never mutates in place: revoking or
// otherwise changing what it authorizes must produce a different grant (a new
// GrantId or a replacement entry in the authority's store), never a setter on
// this instance.
public sealed class SecretRedemptionGrant
{
    public SecretRedemptionGrant(
        string grantId,
        string actorId,
        string projectId,
        string runId,
        string purpose,
        SecretRef secret,
        GrantState state,
        DateTimeOffset expiresAt,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        GrantId = GrantIdentifier.Validate(grantId, nameof(grantId));
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
    public string ActorId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string Purpose { get; }
    public SecretRef Secret { get; }
    public GrantState State { get; }
    public DateTimeOffset ExpiresAt { get; }
}

// The trusted authority (store) that an authorization layer re-reads on every
// redemption and refresh. Implementations own revocation and expiry state;
// this contract only shapes the lookup so callers never cache a grant across
// calls. Returning more than one candidate for a lookup is treated as
// ambiguous and denied; implementations should not rely on the caller to
// de-duplicate.
public interface IGrantAuthority
{
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
    internal static string Validate(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier (at most 256 ASCII characters).", paramName);
        return value;
    }
}
