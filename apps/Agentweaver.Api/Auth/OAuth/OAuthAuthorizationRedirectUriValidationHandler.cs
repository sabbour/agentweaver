using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthAuthorizationRedirectUriValidationHandler(
    IOpenIddictApplicationManager applications)
    : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ClientId))
            return;

        var application = await applications.FindByClientIdAsync(
            context.ClientId, context.CancellationToken).ConfigureAwait(false);
        if (application is null)
            return;

        if (string.IsNullOrEmpty(context.RedirectUri))
        {
            var registeredRedirectUris = await applications.GetRedirectUrisAsync(
                application, context.CancellationToken).ConfigureAwait(false);
            if (registeredRedirectUris.Length == 1)
            {
                context.SetRedirectUri(registeredRedirectUris[0]);
                return;
            }

            context.Reject(
                Errors.InvalidRequest,
                "The 'redirect_uri' parameter is required for this client application.");
            return;
        }

        if (await applications.ValidateRedirectUriAsync(
                application,
                context.RedirectUri,
                context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var redirects = await applications.GetRedirectUrisAsync(
            application, context.CancellationToken).ConfigureAwait(false);
        if (redirects.Any(registeredRedirectUri =>
                IsRfc8252LoopbackPortMatch(registeredRedirectUri, context.RedirectUri)))
        {
            return;
        }

        context.Reject(
            Errors.InvalidRequest,
            "The specified 'redirect_uri' parameter is not valid for this client application.");
    }

    internal static bool IsRfc8252LoopbackPortMatch(
        string registeredRedirectUri,
        string requestedRedirectUri)
    {
        if (!OAuthRedirectUriValidator.IsValid(
                registeredRedirectUri,
                allowDynamicLoopbackPort: true,
                allowHttps: false)
            || !OAuthRedirectUriValidator.IsValid(
                requestedRedirectUri,
                allowDynamicLoopbackPort: true,
                allowHttps: false)
            || !registeredRedirectUri.StartsWith(
                "http://127.0.0.1/",
                StringComparison.Ordinal)
            || !requestedRedirectUri.StartsWith(
                "http://127.0.0.1:",
                StringComparison.Ordinal)
            || !Uri.TryCreate(registeredRedirectUri, UriKind.Absolute, out var registered)
            || !Uri.TryCreate(requestedRedirectUri, UriKind.Absolute, out var requested)
            || requested.IsDefaultPort)
        {
            return false;
        }

        return string.Equals(
                registered.AbsolutePath,
                requested.AbsolutePath,
                StringComparison.Ordinal)
            && string.Equals(
                registered.Query,
                requested.Query,
                StringComparison.Ordinal);
    }
}
