using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

// The trusted Identity authorization boundary around ISecretRedemption. It
// evaluates a server-owned grant for the host-authenticated actor before ever
// calling the credential backend, re-reads the authority fresh on every
// redemption/refresh call (never caches a grant across calls), and re-reads it
// again after the backend's asynchronous acquisition so a grant that was
// revoked, expired, or replaced mid-flight invalidates the acquired credential
// instead of returning it. Denied requests never reach the backend. This
// composes an existing ISecretRedemption (for example the Azure Key Vault
// adapter); it implements no wire authentication, broker, service host, or
// Azure deployment of its own.
public sealed class AuthorizedSecretRedemption : ISecretRedemption
{
    private readonly TrustedActorContext _actor;
    private readonly IGrantAuthority _authority;
    private readonly ISecretRedemption _backend;
    private readonly TimeProvider _timeProvider;

    public AuthorizedSecretRedemption(
        TrustedActorContext actor,
        IGrantAuthority authority,
        ISecretRedemption backend,
        TimeProvider? timeProvider = null)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SecretCredential> RedeemAsync(
        SecretRedemptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Fresh authority read before ever touching the backend. A denial here
        // never calls it.
        var initialGrant = await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var credential = await _backend.RedeemAsync(request, cancellationToken).ConfigureAwait(false);

        // Re-read authority after the asynchronous backend acquisition. A grant
        // revoked, expired, or replaced with a binding that no longer matches
        // this exact request during that await invalidates the credential we
        // just acquired instead of returning it.
        try
        {
            if (credential is null)
                throw new InvalidOperationException("The credential backend returned no credential.");
            cancellationToken.ThrowIfCancellationRequested();
            var grant = await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!SameGrant(initialGrant, grant))
                throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.GrantChanged);
            credential.LimitLifetime(grant.ExpiresAt);
            if (grant.ExpiresAt <= _timeProvider.GetUtcNow())
                throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.Expired);
            cancellationToken.ThrowIfCancellationRequested();
            return credential;
        }
        catch
        {
            credential?.Invalidate();
            throw;
        }
    }

    private async Task<SecretRedemptionGrant> AuthorizeAsync(
        SecretRedemptionRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.RunId, _actor.RunId, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.RunMismatch);

        var grants = await _authority.FindGrantsAsync(_actor, request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (grants is null || grants.Count == 0)
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.NoGrant);
        if (grants.Count > 1)
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.Ambiguous);

        var grant = grants[0] ?? throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.NoGrant);

        // Every binding is checked explicitly here rather than trusted from the
        // authority: a request cannot grant itself access, and a grant that was
        // revised (even reusing the same GrantId) to point somewhere else is
        // re-validated against this exact request and actor on every read.
        if (!string.Equals(grant.ActorId, _actor.ActorId, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.ActorMismatch);
        if (!string.Equals(grant.ProjectId, _actor.ProjectId, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.ProjectMismatch);
        if (!string.Equals(grant.RunId, _actor.RunId, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.RunMismatch);
        if (!string.Equals(grant.Purpose, request.Purpose, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.PurposeMismatch);
        if (!string.Equals(grant.Secret.Id, request.Secret.Id, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.SecretMismatch);
        if (!string.Equals(grant.Secret.Version, request.Secret.Version, StringComparison.Ordinal))
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.VersionMismatch);
        if (grant.State != GrantState.Active)
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.Revoked);
        if (grant.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new SecretAuthorizationDeniedException(SecretAuthorizationDenialReason.Expired);

        return grant;
    }

    private static bool SameGrant(SecretRedemptionGrant initial, SecretRedemptionGrant current) =>
        string.Equals(initial.GrantId, current.GrantId, StringComparison.Ordinal) &&
        string.Equals(initial.Revision, current.Revision, StringComparison.Ordinal) &&
        string.Equals(initial.ActorId, current.ActorId, StringComparison.Ordinal) &&
        string.Equals(initial.ProjectId, current.ProjectId, StringComparison.Ordinal) &&
        string.Equals(initial.RunId, current.RunId, StringComparison.Ordinal) &&
        string.Equals(initial.Purpose, current.Purpose, StringComparison.Ordinal) &&
        string.Equals(initial.Secret.Id, current.Secret.Id, StringComparison.Ordinal) &&
        string.Equals(initial.Secret.Version, current.Secret.Version, StringComparison.Ordinal) &&
        initial.State == current.State && initial.ExpiresAt == current.ExpiresAt;
}
