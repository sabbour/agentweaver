namespace Agentweaver.Identity;

/// <summary>Stable, non-sensitive reasons for denying a secret redemption.</summary>
public enum SecretAuthorizationDenialReason
{
    // The trusted actor's bound run does not match the request's run. Neither
    // side is caller-supplied request data alone: the actor is host-established,
    // but a mismatch here means the request cannot be evaluated against it.
    RunMismatch,

    // No grant authority has no candidate for this actor/request shape.
    NoGrant,

    // More than one candidate grant matched; an ambiguous match fails closed
    // rather than guessing which authorization applies.
    Ambiguous,

    ActorMismatch,
    ProjectMismatch,
    PurposeMismatch,
    SecretMismatch,
    VersionMismatch,
    Revoked,
    Expired,
    GrantChanged,
}

/// <summary>A redacted authorization denial that does not reveal secret or grant data.</summary>
// Thrown whenever a redemption is denied. The message carries only the
// denial reason, never a secret value, identifier payload, or grant detail
// that could help an attacker enumerate valid bindings.
public sealed class SecretAuthorizationDeniedException : Exception
{
    /// <summary>Creates a denial carrying only the safe reason category.</summary>
    internal SecretAuthorizationDeniedException(SecretAuthorizationDenialReason reason)
        : base($"Secret redemption was denied ({reason}).")
    {
        Reason = reason;
    }

    /// <summary>The non-secret authorization failure reason.</summary>
    public SecretAuthorizationDenialReason Reason { get; }
}
