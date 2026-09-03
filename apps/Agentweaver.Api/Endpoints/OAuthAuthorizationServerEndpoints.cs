using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Api.Endpoints;

public static class OAuthAuthorizationServerEndpoints
{
    public static void MapOAuthAuthorizationServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/oauth/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync)
            .ProtocolManaged();
        app.MapPost("/oauth/register", RegisterAsync)
            .RequireRateLimiting("oauth-registration")
            .ProtocolManaged();
        app.MapGet("/oauth/resume", ResumeAsync).ProtocolManaged();
        app.MapPost("/oauth/token", TokenAsync).ProtocolManaged();
    }

    private static async Task<IResult> TokenAsync(
        HttpContext context,
        MemoryDbContext db,
        OAuthServerConfiguration configuration,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null)
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return OAuthForbid(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");

        var result = await context.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null)
            return OAuthForbid(Errors.InvalidGrant, "The authorization grant is invalid.");

        var principal = result.Principal;
        principal.SetResources(configuration.Resource.AbsoluteUri);
        foreach (var claim in principal.Claims)
            claim.SetDestinations(Destinations.AccessToken);

        var authorizationId = principal.GetAuthorizationId();
        if (!string.IsNullOrWhiteSpace(authorizationId))
        {
            var family = await db.OAuthRefreshTokenFamilies.SingleOrDefaultAsync(
                x => x.AuthorizationId == authorizationId, ct).ConfigureAwait(false);
            if (family?.RevokedAt is not null)
                return OAuthForbid(Errors.InvalidGrant, "The refresh-token family was revoked.");
            if (family is null)
            {
                db.OAuthRefreshTokenFamilies.Add(new OAuthRefreshTokenFamily
                {
                    Id = Guid.NewGuid(),
                    AuthorizationId = authorizationId,
                    Subject = principal.GetClaim(Claims.Subject)!,
                    ClientId = request.ClientId!,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        return Results.SignIn(principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        BrowserEntraSessionService browserSessions,
        MemoryDbContext db,
        OAuthServerConfiguration configuration,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null)
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        var redirectUri = request.RedirectUri!;
        var scope = NormalizeScopes(request.GetScopes());
        if (!scope.Contains(OAuthServerConfiguration.McpScope, StringComparer.Ordinal)
            || request.GetResources().Any(resource =>
                !string.Equals(resource, configuration.Resource.AbsoluteUri, StringComparison.Ordinal)))
        {
            return OAuthForbid(Errors.InvalidTarget, "The request must target the configured MCP resource.");
        }

        var browser = await browserSessions.GetCurrentAsync(context, ct).ConfigureAwait(false);
        if (browser is null)
        {
            var handle = await SaveTransactionAsync(
                db, request, scope, browserSessionId: null, subject: null, ct).ConfigureAwait(false);
            return Results.Redirect($"/auth/entra/authorize?oauth_return_handle={Uri.EscapeDataString(handle)}");
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var handle = form["consent_handle"].ToString();
            var decision = form["decision"].ToString();
            var transaction = await ClaimTransactionAsync(db, handle, browser, request, ct).ConfigureAwait(false);
            if (transaction is null)
                return OAuthForbid(Errors.InvalidRequest, "The consent transaction is invalid or expired.");
            if (decision != "approve")
                return OAuthForbid(Errors.AccessDenied, "The resource owner denied the request.");

            await UpsertConsentAsync(db, browser.EntraObjectId, request.ClientId!, scope, ct).ConfigureAwait(false);
            return SignIn(browser.EntraObjectId, scope, configuration.Resource.AbsoluteUri);
        }

        var consent = await db.OAuthConsents.AsNoTracking().SingleOrDefaultAsync(
            x => x.Subject == browser.EntraObjectId
                && x.ClientId == request.ClientId
                && x.RevokedAt == null, ct).ConfigureAwait(false);
        var approved = consent?.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!request.HasPromptValue("consent") && scope.All(s => approved.Contains(s, StringComparer.Ordinal)))
            return SignIn(browser.EntraObjectId, scope, configuration.Resource.AbsoluteUri);

        var consentHandle = await SaveTransactionAsync(
            db, request, scope, browser.Id, browser.EntraObjectId, ct).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Results.Content(RenderConsent(request, scope, consentHandle), "text/html; charset=utf-8");
    }

    private static async Task<IResult> ResumeAsync(
        HttpContext context,
        string? handle,
        BrowserEntraSessionService browserSessions,
        MemoryDbContext db,
        OAuthServerConfiguration configuration,
        CancellationToken ct)
    {
        var browser = await browserSessions.GetCurrentAsync(context, ct).ConfigureAwait(false);
        if (browser is null || string.IsNullOrWhiteSpace(handle))
            return Results.Unauthorized();

        var hash = OAuthCertificateLoader.HashOpaque(handle);
        var transaction = await db.OAuthAuthorizationTransactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HandleHash == hash, ct).ConfigureAwait(false);
        var claimed = transaction is not null
            && transaction.ExpiresAt > DateTimeOffset.UtcNow
            && await db.OAuthAuthorizationTransactions
                .Where(x => x.HandleHash == hash && x.ConsumedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    x => x.ConsumedAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false) == 1;
        if (!claimed)
            return Results.BadRequest(new { error = "invalid_request" });

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = transaction!.ClientId,
            ["redirect_uri"] = transaction.RedirectUri,
            ["response_type"] = ResponseTypes.Code,
            ["scope"] = transaction.Scope,
            ["state"] = transaction.ClientState,
            ["code_challenge"] = transaction.CodeChallenge,
            ["code_challenge_method"] = CodeChallengeMethods.Sha256,
            ["resource"] = configuration.Resource.AbsoluteUri,
        };
        return Results.Redirect(QueryString.Create(query!).ToUriComponent().Insert(0, "/oauth/authorize"));
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        OAuthDynamicClientRegistrationService service,
        CancellationToken ct)
    {
        const long maximumBodyBytes = 32 * 1024;
        if (context.Request.ContentLength > maximumBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = maximumBodyBytes;

        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                new JsonDocumentOptions { MaxDepth = 8 },
                ct).ConfigureAwait(false);
            var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var response = await service.RegisterAsync(document.RootElement, source, ct).ConfigureAwait(false);
            return Results.Json(response, statusCode: StatusCodes.Status201Created);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "invalid_client_metadata", error_description = "Malformed JSON." },
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (OAuthRegistrationException ex)
        {
            return Results.Json(new { error = ex.Error, error_description = ex.Message },
                statusCode: ex.Error == "temporarily_unavailable"
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status400BadRequest);
        }
    }

    private static IResult SignIn(string subject, string[] scopes, string resource)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, subject);
        identity.SetClaim(Claims.Name, subject);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(resource);
        foreach (var claim in principal.Claims)
            claim.SetDestinations(Destinations.AccessToken);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult OAuthForbid(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static async Task<string> SaveTransactionAsync(
        MemoryDbContext db,
        OpenIddictRequest request,
        string[] scopes,
        string? browserSessionId,
        string? subject,
        CancellationToken ct)
    {
        var handle = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        db.OAuthAuthorizationTransactions.Add(new OAuthAuthorizationTransaction
        {
            HandleHash = OAuthCertificateLoader.HashOpaque(handle),
            ClientId = request.ClientId!,
            RedirectUri = request.RedirectUri!,
            CodeChallenge = request.CodeChallenge!,
            Scope = string.Join(' ', scopes),
            ClientState = request.State,
            BrowserSessionId = browserSessionId,
            Subject = subject,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return handle;
    }

    private static async Task<OAuthAuthorizationTransaction?> ClaimTransactionAsync(
        MemoryDbContext db,
        string handle,
        BrowserEntraSession browser,
        OpenIddictRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return null;
        var hash = OAuthCertificateLoader.HashOpaque(handle);
        var transaction = await db.OAuthAuthorizationTransactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HandleHash == hash, ct).ConfigureAwait(false);
        if (transaction is null
            || transaction.ExpiresAt <= DateTimeOffset.UtcNow
            || transaction.BrowserSessionId != browser.Id
            || transaction.Subject != browser.EntraObjectId
            || transaction.ClientId != request.ClientId
            || transaction.RedirectUri != request.RedirectUri
            || transaction.CodeChallenge != request.CodeChallenge
            || transaction.Scope != string.Join(' ', NormalizeScopes(request.GetScopes())))
            return null;
        var changed = await db.OAuthAuthorizationTransactions
            .Where(x => x.HandleHash == hash && x.ConsumedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.ConsumedAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        return changed == 1 ? transaction : null;
    }

    private static async Task UpsertConsentAsync(
        MemoryDbContext db,
        string subject,
        string clientId,
        string[] scopes,
        CancellationToken ct)
    {
        var consent = await db.OAuthConsents.SingleOrDefaultAsync(
            x => x.Subject == subject && x.ClientId == clientId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            consent = new OAuthConsentRecord
            {
                Id = Guid.NewGuid(),
                Subject = subject,
                ClientId = clientId,
                Scopes = string.Join(' ', scopes),
            };
            db.OAuthConsents.Add(consent);
        }
        consent.Scopes = string.Join(' ', scopes);
        consent.UpdatedAt = DateTimeOffset.UtcNow;
        consent.RevokedAt = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string[] NormalizeScopes(IEnumerable<string> scopes) =>
        scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string RenderConsent(OpenIddictRequest request, string[] scopes, string handle)
    {
        static string Encode(string value) => HtmlEncoder.Default.Encode(value);
        var inputs = new Dictionary<string, string?>
        {
            ["client_id"] = request.ClientId,
            ["redirect_uri"] = request.RedirectUri,
            ["response_type"] = request.ResponseType,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = request.State,
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = request.CodeChallengeMethod,
            ["resource"] = request.GetResources().SingleOrDefault(),
            ["consent_handle"] = handle,
        };
        var hidden = string.Join("", inputs.Where(x => x.Value is not null)
            .Select(x => $"<input type=\"hidden\" name=\"{Encode(x.Key)}\" value=\"{Encode(x.Value!)}\">"));
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Authorize Agentweaver</title></head>" +
            $"<body><main><h1>Authorize MCP access</h1><p>Client <code>{Encode(request.ClientId!)}</code> " +
            $"requests <code>{Encode(string.Join(' ', scopes))}</code>.</p><form method=\"post\" action=\"/oauth/authorize\">" +
            hidden + "<button name=\"decision\" value=\"approve\">Approve</button>" +
            "<button name=\"decision\" value=\"deny\">Deny</button></form></main></body></html>";
    }
}
