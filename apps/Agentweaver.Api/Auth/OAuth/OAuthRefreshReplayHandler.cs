using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthRefreshReplayHandler(
    IOpenIddictTokenManager tokens,
    MemoryDbContext db) : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (!context.Request.IsRefreshTokenGrantType()
            || string.IsNullOrWhiteSpace(context.Request.RefreshToken))
            return;

        var token = await tokens.FindByReferenceIdAsync(
            context.Request.RefreshToken, context.CancellationToken).ConfigureAwait(false);
        if (token is null
            || (!await tokens.HasStatusAsync(token, Statuses.Redeemed, context.CancellationToken).ConfigureAwait(false)
                && !await tokens.HasStatusAsync(token, Statuses.Revoked, context.CancellationToken).ConfigureAwait(false)))
            return;

        var authorizationId = await tokens.GetAuthorizationIdAsync(
            token, context.CancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(authorizationId))
        {
            await foreach (var familyToken in tokens.FindByAuthorizationIdAsync(
                authorizationId, context.CancellationToken))
            {
                await tokens.TryRevokeAsync(familyToken, context.CancellationToken).ConfigureAwait(false);
            }

            await db.OAuthRefreshTokenFamilies
                .Where(x => x.AuthorizationId == authorizationId && x.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.RevocationReason, "refresh_token_replay"),
                    context.CancellationToken).ConfigureAwait(false);
        }

        context.Reject(Errors.InvalidGrant, "The refresh token was already used.");
    }
}
