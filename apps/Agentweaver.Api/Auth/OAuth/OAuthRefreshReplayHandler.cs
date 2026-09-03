using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthExactResourceTokenRequestHandler(
    OAuthServerConfiguration configuration,
    IHttpContextAccessor accessor) : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (!context.Request.IsAuthorizationCodeGrantType()
            && !context.Request.IsRefreshTokenGrantType())
            return;

        var httpContext = accessor.HttpContext;
        if (httpContext is null)
        {
            context.Reject(Errors.InvalidTarget, "The request must target exactly the configured MCP resource.");
            return;
        }

        var values = httpContext.Request.Query["resource"].ToArray();
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(context.CancellationToken).ConfigureAwait(false);
            values = [.. values, .. form["resource"]];
        }
        var resources = context.Request.GetResources().ToArray();
        if (values.Length != 1
            || resources.Length != 1
            || !string.Equals(values[0], configuration.Resource.AbsoluteUri, StringComparison.Ordinal)
            || !string.Equals(resources[0], configuration.Resource.AbsoluteUri, StringComparison.Ordinal))
        {
            context.Reject(Errors.InvalidTarget, "The request must target exactly the configured MCP resource.");
        }
    }
}

public sealed class OAuthRefreshReplayHandler(
    IOpenIddictTokenManager tokens,
    OAuthRefreshTokenFamilyRevoker familyRevoker) : IOpenIddictServerHandler<ValidateTokenRequestContext>
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
            await familyRevoker.RevokeAsync(authorizationId, context.CancellationToken).ConfigureAwait(false);

        context.Reject(Errors.InvalidGrant, "The refresh token was already used.");
    }
}

public sealed class OAuthAtomicRefreshTokenRedemptionHandler(
    IOpenIddictTokenManager tokens,
    OAuthRefreshTokenFamilyRevoker familyRevoker)
    : IOpenIddictServerHandler<ProcessSignInContext>
{
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.EndpointType is not OpenIddictServerEndpointType.Token
            || !context.Request.IsRefreshTokenGrantType())
            return;

        var identifier = context.Principal?.GetTokenId();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            context.Reject(Errors.InvalidGrant, "The refresh token is invalid.");
            return;
        }

        var token = await tokens.FindByIdAsync(identifier, context.CancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            context.Reject(Errors.InvalidGrant, "The refresh token is invalid.");
            return;
        }

        // OpenIddict 7.6 deliberately ignores a failed refresh-token TryRedeemAsync in its built-in
        // ProcessSignIn handler. Claim first so optimistic concurrency produces exactly one winner.
        if (await tokens.TryRedeemAsync(token, context.CancellationToken).ConfigureAwait(false))
            return;

        var authorizationId = await tokens.GetAuthorizationIdAsync(
            token, context.CancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(authorizationId))
            await familyRevoker.RevokeAsync(authorizationId, context.CancellationToken).ConfigureAwait(false);

        context.Reject(Errors.InvalidGrant, "The refresh token was already used.");
    }
}

public sealed class OAuthRefreshTokenFamilyRevoker(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations,
    MemoryDbContext db)
{
    public async Task RevokeAsync(string authorizationId, CancellationToken ct)
    {
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, ct).ConfigureAwait(false);

        var authorization = await authorizations.FindByIdAsync(authorizationId, ct).ConfigureAwait(false);
        if (authorization is not null)
            await authorizations.TryRevokeAsync(authorization, ct).ConfigureAwait(false);

        await db.OAuthRefreshTokenFamilies
            .Where(x => x.AuthorizationId == authorizationId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.RevocationReason, "refresh_token_replay"),
                ct).ConfigureAwait(false);
    }
}
