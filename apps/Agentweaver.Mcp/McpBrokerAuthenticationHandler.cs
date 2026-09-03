using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Mcp;

internal static class McpBrokerAuthenticationDefaults
{
    public const string Scheme = "McpBroker";
    public const string Policy = "McpInvoke";
    public const string ValidatedTokenItem = "mcp.validated_broker_token";
    public const string ChallengeErrorItem = "mcp.challenge_error";
}

internal sealed class McpBrokerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    McpOAuthConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(value))
            return AuthenticateResult.NoResult();

        if (!AuthenticationHeaderValue.TryParse(value, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter)
            || header.Parameter.Contains(',', StringComparison.Ordinal))
        {
            SetChallengeError(OpenIddictConstants.Errors.InvalidToken);
            return AuthenticateResult.Fail("The bearer authorization header is malformed.");
        }

        var token = header.Parameter;
        var result = await Context.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null)
        {
            SetChallengeError(OpenIddictConstants.Errors.InvalidToken);
            return AuthenticateResult.Fail(
                result.Failure ?? new InvalidOperationException("The broker token is invalid."));
        }

        JsonWebToken jwt;
        try
        {
            var handler = new JsonWebTokenHandler();
            if (!handler.CanReadToken(token))
                throw new InvalidOperationException("The broker token is not a JWT.");
            jwt = handler.ReadJsonWebToken(token);
        }
        catch (Exception exception)
        {
            SetChallengeError(OpenIddictConstants.Errors.InvalidToken);
            return AuthenticateResult.Fail(exception);
        }

        var principal = result.Principal;
        var expectedIssuer = configuration.Issuer.AbsoluteUri.TrimEnd('/');
        var audiences = principal.GetAudiences().ToArray();
        if (!string.Equals(jwt.Issuer.TrimEnd('/'), expectedIssuer, StringComparison.Ordinal)
            || audiences.Length != 1
            || !string.Equals(audiences[0], configuration.Resource.AbsoluteUri, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(jwt.Kid)
            || !string.Equals(jwt.Alg, "RS256", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(principal.GetClaim(OpenIddictConstants.Claims.Subject)))
        {
            SetChallengeError(OpenIddictConstants.Errors.InvalidToken);
            return AuthenticateResult.Fail(
                "The broker token does not satisfy the MCP issuer, audience, signing, or subject contract.");
        }

        if (!principal.HasScope(McpOAuthConfiguration.RequiredScope))
        {
            SetChallengeError(OpenIddictConstants.Errors.InsufficientScope);
            return AuthenticateResult.Fail("The broker token does not grant mcp:invoke.");
        }

        Context.Items[McpBrokerAuthenticationDefaults.ValidatedTokenItem] = token;
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items[McpBrokerAuthenticationDefaults.ChallengeErrorItem] as string;
        Response.StatusCode = string.Equals(
            error,
            OpenIddictConstants.Errors.InsufficientScope,
            StringComparison.Ordinal)
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;
        var challenge =
            $"Bearer resource_metadata=\"{configuration.ResourceMetadata.AbsoluteUri}\", " +
            $"scope=\"{McpOAuthConfiguration.RequiredScope}\"";
        if (!string.IsNullOrWhiteSpace(error))
            challenge += $", error=\"{error}\"";
        Response.Headers.WWWAuthenticate = challenge;

        if (!string.IsNullOrWhiteSpace(error))
        {
            Response.ContentType = "application/json";
            await Response.WriteAsync(
                JsonSerializer.Serialize(new { error }),
                Context.RequestAborted).ConfigureAwait(false);
        }
    }

    private void SetChallengeError(string error) =>
        Context.Items[McpBrokerAuthenticationDefaults.ChallengeErrorItem] = error;
}
