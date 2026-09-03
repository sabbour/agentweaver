using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Api.Auth;

internal static class AgentweaverAuthentication
{
    private const string BearerPrefix = "Bearer ";

    public static string SelectScheme(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        var authorization = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthorizationMetadata>();
        var header = context.Request.Headers.Authorization.ToString();
        var token = TryReadBearer(header, out var bearer) ? bearer : null;

        if (environment.IsDevelopment()
            && configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth"))
            return AgentweaverAuthenticationSchemes.TestBypass;

        if (authorization?.Kind == EndpointAuthorizationKind.InternalService)
            return AgentweaverAuthenticationSchemes.InternalServiceKey;

        if (authorization?.Kind == EndpointAuthorizationKind.RunCapability
            && token is not null
            && FixedTimeEquals(token, context.Request.Headers[RunAuthorshipHeaders.RunToken].ToString()))
            return AgentweaverAuthenticationSchemes.RunCapability;

        var internalKey = configuration["Auth:ApiKey"];
        if (token is not null
            && !string.IsNullOrEmpty(internalKey)
            && FixedTimeEquals(token, internalKey))
            return AgentweaverAuthenticationSchemes.InternalServiceKey;

        if (authorization?.Kind == EndpointAuthorizationKind.PlatformOrMcp
            && token is not null
            && IsBrokerToken(token, context.RequestServices.GetRequiredService<OAuthServerConfiguration>()))
            return AgentweaverAuthenticationSchemes.BrokerBearer;

        if (authorization?.Kind == EndpointAuthorizationKind.ProtocolManaged
            && string.IsNullOrEmpty(header)
            && context.Request.Cookies.ContainsKey(BrowserEntraSessionService.CookieName))
            return AgentweaverAuthenticationSchemes.BrowserSession;

        return AgentweaverAuthenticationSchemes.Entra;
    }

    public static bool TryReadBearer(string header, out string token)
    {
        token = string.Empty;
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        token = header[BearerPrefix.Length..].Trim();
        return token.Length > 0;
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsBrokerToken(string token, OAuthServerConfiguration configuration)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            if (!handler.CanReadToken(token))
                return false;
            return string.Equals(
                handler.ReadJsonWebToken(token).Issuer.TrimEnd('/'),
                configuration.PublicOrigin.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

internal abstract class AgentweaverAuthenticationHandler<TOptions>(
    IOptionsMonitor<TOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TOptions>(options, logger, encoder)
    where TOptions : AuthenticationSchemeOptions, new()
{
    protected bool TryGetBearer(out string token, out AuthenticateResult? absentOrInvalid)
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            token = string.Empty;
            absentOrInvalid = AuthenticateResult.NoResult();
            return false;
        }

        if (!AgentweaverAuthentication.TryReadBearer(header, out token))
        {
            absentOrInvalid = AuthenticateResult.Fail("A valid Bearer credential is required.");
            return false;
        }

        absentOrInvalid = null;
        return true;
    }

    protected AuthenticateResult Success(CallerContext caller, bool isInternalService = false)
    {
        var principal = CallerContextClaimsAdapter.ToPrincipal(caller, Scheme.Name, isInternalService);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

internal sealed class EntraAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    EntraAccessTokenValidator validator)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetBearer(out var token, out var result))
            return result!;

        var claims = await validator.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (claims is null)
            return AuthenticateResult.Fail("The Entra bearer token is invalid.");

        return Success(new CallerContext
        {
            User = claims.ObjectId,
            EntraObjectId = claims.ObjectId,
            EntraTenantId = claims.TenantId,
            PlatformRoles = claims.RecognizedRoles,
            RawPlatformRoles = claims.RawRoles,
            PrimaryPlatformRole = claims.PrimaryRole,
            DisplayName = claims.DisplayName,
            Email = claims.Email,
        });
    }
}

internal sealed class BrowserSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.ContainsKey(BrowserEntraSessionService.CookieName))
            return AuthenticateResult.NoResult();

        var session = await Context.RequestServices.GetRequiredService<BrowserEntraSessionService>()
            .GetCurrentAsync(Context, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
            return AuthenticateResult.Fail("The browser session is invalid or expired.");

        var caller = new CallerContext
        {
            User = session.EntraObjectId,
            EntraObjectId = session.EntraObjectId,
        };
        var principal = CallerContextClaimsAdapter.ToPrincipal(caller, Scheme.Name);
        ((ClaimsIdentity)principal.Identity!).AddClaim(
            new Claim(AgentweaverClaimTypes.BrowserSessionId, session.Id));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

internal sealed class BrokerBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OAuthServerConfiguration configuration)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetBearer(out var token, out var result))
            return result!;

        var brokerResult = await Context.AuthenticateAsync(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!brokerResult.Succeeded || brokerResult.Principal is null)
            return AuthenticateResult.Fail(
                brokerResult.Failure ?? new InvalidOperationException("The broker bearer token is invalid."));

        var source = brokerResult.Principal;
        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        }
        catch (Exception exception)
        {
            return AuthenticateResult.Fail(exception);
        }

        var audiences = source.GetAudiences().ToArray();
        if (!source.HasScope(OAuthServerConfiguration.McpScope)
            || audiences.Length != 1
            || !string.Equals(audiences[0], configuration.Resource.AbsoluteUri, StringComparison.Ordinal)
            || !string.Equals(
                jwt.Issuer.TrimEnd('/'),
                configuration.PublicOrigin.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(jwt.Kid)
            || !string.Equals(jwt.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            return AuthenticateResult.Fail("The broker bearer token has an invalid trust boundary.");

        var subject = source.GetClaim(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(subject))
            return AuthenticateResult.Fail("The broker bearer token has no subject.");

        return Success(new CallerContext
        {
            User = subject,
            EntraObjectId = subject,
            DisplayName = source.GetClaim(OpenIddictConstants.Claims.Name),
        });
    }
}

internal sealed class InternalServiceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetBearer(out var token, out var result))
            return Task.FromResult(result!);

        var configuredKey = configuration["Auth:ApiKey"];
        if (string.IsNullOrEmpty(configuredKey)
            || !AgentweaverAuthentication.FixedTimeEquals(token, configuredKey))
            return Task.FromResult(AuthenticateResult.Fail("The internal service credential is invalid."));

        return Task.FromResult(Success(new CallerContext
        {
            User = ProjectAuthorization.InternalServiceUser,
            GitHubLogin = ProjectAuthorization.InternalServiceUser,
            PlatformRoles = [PlatformRoles.PlatformAdmin],
            PrimaryPlatformRole = PlatformRoles.PlatformAdmin,
        }, isInternalService: true));
    }
}

internal sealed class RunCapabilityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetBearer(out var token, out var result))
            return result!;

        var metadata = Context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthorizationMetadata>();
        var runId = Request.RouteValues["id"]?.ToString();
        var headerRunId = Request.Headers[RunAuthorshipHeaders.RunId].ToString();
        var headerToken = Request.Headers[RunAuthorshipHeaders.RunToken].ToString();
        if (metadata?.Kind != EndpointAuthorizationKind.RunCapability
            || !HttpMethods.IsGet(Request.Method)
            || string.IsNullOrWhiteSpace(runId)
            || !string.Equals(runId, headerRunId, StringComparison.Ordinal)
            || !AgentweaverAuthentication.FixedTimeEquals(token, headerToken)
            || !await Context.RequestServices.GetRequiredService<IRunAuthorshipCapabilityStore>()
                .ValidateAsync(runId, token, Context.RequestAborted).ConfigureAwait(false))
            return AuthenticateResult.Fail("The run capability credential is invalid.");

        return Success(new CallerContext
        {
            User = ProjectAuthorization.InternalServiceUser,
        }, isInternalService: true);
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    IHostEnvironment environment)
    : AgentweaverAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment()
            || !configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth"))
            return Task.FromResult(AuthenticateResult.Fail(
                "The test authentication scheme is disabled."));

        if (!TryGetBearer(out var token, out var result))
            return Task.FromResult(result!);

        var configured = ReadConfiguredCaller(token);
        var caller = new CallerContext
        {
            User = configured.User,
            EntraObjectId = configured.User,
            PlatformRoles = configured.Roles,
            PrimaryPlatformRole = configured.Roles.FirstOrDefault(),
        };
        return Task.FromResult(Success(caller, configured.IsInternalService));
    }

    private (string User, IReadOnlyList<string> Roles, bool IsInternalService) ReadConfiguredCaller(string token)
    {
        if (AgentweaverAuthentication.FixedTimeEquals(token, configuration["Auth:ApiKey"] ?? string.Empty)
            && !string.IsNullOrWhiteSpace(configuration["Auth:User"]))
            return (configuration["Auth:User"]!, ReadRoles(
                configuration["Auth:PlatformRoles"], [PlatformRoles.PlatformAdmin]), true);

        foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
        {
            if (AgentweaverAuthentication.FixedTimeEquals(token, entry["Token"] ?? string.Empty)
                && !string.IsNullOrWhiteSpace(entry["User"]))
                return (entry["User"]!, ReadRoles(
                    entry["PlatformRoles"], [PlatformRoles.PlatformAdmin]), false);
        }

        return (token, [], false);
    }

    private static IReadOnlyList<string> ReadRoles(string? value, IReadOnlyList<string> defaults) =>
        string.IsNullOrWhiteSpace(value)
            ? defaults
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
