using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Revokes every token and the underlying authorization for a given authorization ID. Used
/// when a redeemed or revoked refresh token is presented again: the whole family is burned,
/// not merely the already-redeemed token, since a replayed refresh token indicates the
/// family may have leaked.
/// </summary>
public sealed class RefreshTokenFamilyRevoker(
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
{
    public async Task RevokeAsync(string authorizationId, CancellationToken cancellationToken)
    {
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);

        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is not null)
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
    }
}

/// <summary>
/// Rejects a refresh token that has already been redeemed or revoked and, because presenting
/// it again signals the family may be compromised, revokes every other token descended from
/// the same authorization.
/// </summary>
public sealed class RefreshReplayHandler(IOpenIddictTokenManager tokens, RefreshTokenFamilyRevoker familyRevoker)
    : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (!context.Request.IsRefreshTokenGrantType() || string.IsNullOrWhiteSpace(context.Request.RefreshToken))
            return;

        var token = await tokens.FindByReferenceIdAsync(context.Request.RefreshToken, context.CancellationToken);
        if (token is null ||
            (!await tokens.HasStatusAsync(token, Statuses.Redeemed, context.CancellationToken) &&
             !await tokens.HasStatusAsync(token, Statuses.Revoked, context.CancellationToken)))
        {
            return;
        }

        var authorizationId = await tokens.GetAuthorizationIdAsync(token, context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(authorizationId))
            await familyRevoker.RevokeAsync(authorizationId, context.CancellationToken);

        context.Reject(Errors.InvalidGrant, "The refresh token was already used.");
    }
}

/// <summary>
/// Atomically claims (redeems) the refresh token being exchanged. OpenIddict's own built-in
/// sign-in handler does not fail the request when redemption loses a concurrency race, so this
/// handler claims first and revokes the whole family on a lost race, closing a reuse window
/// that two near-simultaneous refresh requests could otherwise exploit.
/// </summary>
public sealed class AtomicRefreshTokenRedemptionHandler(
    IOpenIddictTokenManager tokens, RefreshTokenFamilyRevoker familyRevoker)
    : IOpenIddictServerHandler<ProcessSignInContext>
{
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.EndpointType is not OpenIddictServerEndpointType.Token || !context.Request.IsRefreshTokenGrantType())
            return;

        var identifier = context.Principal?.GetTokenId();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            context.Reject(Errors.InvalidGrant, "The refresh token is invalid.");
            return;
        }

        var token = await tokens.FindByIdAsync(identifier, context.CancellationToken);
        if (token is null)
        {
            context.Reject(Errors.InvalidGrant, "The refresh token is invalid.");
            return;
        }

        if (await tokens.TryRedeemAsync(token, context.CancellationToken))
            return;

        var authorizationId = await tokens.GetAuthorizationIdAsync(token, context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(authorizationId))
            await familyRevoker.RevokeAsync(authorizationId, context.CancellationToken);

        context.Reject(Errors.InvalidGrant, "The refresh token was already used.");
    }
}
