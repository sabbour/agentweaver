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
        if (!await HasExactResourceAsync(context, request, configuration.Resource.AbsoluteUri, ct).ConfigureAwait(false))
            return OAuthForbid(Errors.InvalidTarget, "The request must target exactly the configured MCP resource.");

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
        IOpenIddictApplicationManager applications,
        OAuthServerConfiguration configuration,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest();
        if (request is null)
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        var scope = NormalizeScopes(request.GetScopes());
        if (!scope.Contains(OAuthServerConfiguration.McpScope, StringComparer.Ordinal)
            || !await HasExactResourceAsync(
                context, request, configuration.Resource.AbsoluteUri, ct).ConfigureAwait(false))
        {
            return OAuthForbid(Errors.InvalidTarget, "The request must target the configured MCP resource.");
        }

        var consentApplication = await ResolveConsentApplicationAsync(
            applications, request, ct).ConfigureAwait(false);
        if (consentApplication is null)
        {
            return Results.BadRequest(new
            {
                error = Errors.InvalidRequest,
                error_description = "The OAuth client or validated redirect URI could not be resolved.",
            });
        }

        var browser = await browserSessions.GetCurrentAsync(context, ct).ConfigureAwait(false);
        if (browser is null)
        {
            var handle = await SaveTransactionAsync(
                db,
                request,
                scope,
                browserSessionId: null,
                subject: null,
                continuationDecision: HttpMethods.IsPost(context.Request.Method) ? "reauthenticate" : null,
                ct).ConfigureAwait(false);
            var continuationPath =
                $"/auth/entra/authorize?oauth_return_handle={Uri.EscapeDataString(handle)}";
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                var unsignedStyleNonce =
                    Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy =
                    OAuthConsentContentSecurityPolicy.CreateNoForm(unsignedStyleNonce);
                return Results.Content(
                    RenderUnsignedAuthorization(
                        consentApplication.ClientName,
                        continuationPath,
                        unsignedStyleNonce),
                    "text/html; charset=utf-8");
            }

            var reauthenticationStyleNonce =
                Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy =
                OAuthConsentContentSecurityPolicy.CreateNoForm(reauthenticationStyleNonce);
            return Results.Content(
                RenderReauthentication(
                    consentApplication.ClientName,
                    continuationPath,
                    reauthenticationStyleNonce),
                "text/html; charset=utf-8");
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            var handle = form["consent_handle"].ToString();
            var decision = form["decision"].ToString();
            var transaction = await ClaimTransactionAsync(db, handle, browser, request, ct).ConfigureAwait(false);
            if (transaction is null)
                return OAuthForbid(Errors.InvalidRequest, "The consent transaction is invalid or expired.");
            var consentApproved = decision == "approve";
            if (!consentApproved)
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
            db, request, scope, browser.Id, browser.EntraObjectId, continuationDecision: null, ct)
            .ConfigureAwait(false);
        var styleNonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy =
            OAuthConsentContentSecurityPolicy.Create(
                styleNonce,
                consentApplication.CallbackSource);
        return Results.Content(
            RenderConsent(
                request,
                scope,
                consentHandle,
                consentApplication.ClientName,
                browser.DisplayName!,
                browser.Email ?? browser.EntraObjectId,
                styleNonce),
            "text/html; charset=utf-8");
    }

    private static async Task<ConsentApplication?> ResolveConsentApplicationAsync(
        IOpenIddictApplicationManager applications,
        OpenIddictRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return null;
        }

        var application = await applications.FindByClientIdAsync(
            request.ClientId, ct).ConfigureAwait(false);
        if (application is null)
            return null;

        // OpenIddict has already matched this redirect against the application, including
        // RFC 8252 loopback port substitution. Serialize that validated request value.
        if (!OAuthConsentContentSecurityPolicy.TrySerializeCallbackSource(
                request.RedirectUri, out var callbackSource))
        {
            return null;
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application, ct).ConfigureAwait(false);
        return new ConsentApplication(
            descriptor.DisplayName ?? request.ClientId,
            callbackSource);
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
            && transaction.ContinuationDecision is null or "reauthenticate"
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
            ["prompt"] = transaction.ContinuationDecision == "reauthenticate" ? "consent" : null,
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
        string? continuationDecision,
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
            ContinuationDecision = continuationDecision,
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
            || transaction.ContinuationDecision is not null
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

    private static async Task<bool> HasExactResourceAsync(
        HttpContext context,
        OpenIddictRequest request,
        string expected,
        CancellationToken ct)
    {
        var values = context.Request.Query["resource"].ToArray();
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            values = [.. values, .. form["resource"]];
        }

        var parsed = request.GetResources().ToArray();
        return values.Length == 1
            && parsed.Length == 1
            && string.Equals(values[0], expected, StringComparison.Ordinal)
            && string.Equals(parsed[0], expected, StringComparison.Ordinal);
    }

    private static string RenderConsent(
        OpenIddictRequest request,
        string[] scopes,
        string handle,
        string clientName,
        string signedInName,
        string signedInIdentifier,
        string styleNonce)
    {
        static string Encode(string value) => HtmlEncoder.Default.Encode(value);
        static (string Title, string Description) DescribeScope(string scope) => scope switch
        {
            OAuthServerConfiguration.McpScope => (
                "Use Agentweaver MCP tools",
                "Read project context and perform actions through the Agentweaver MCP server."),
            Scopes.OfflineAccess => (
                "Stay connected",
                "Refresh this connection without asking you to sign in again."),
            _ => (scope, "Use this permission when connecting to Agentweaver."),
        };

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
        var permissions = string.Join("", scopes.Select(scope =>
        {
            var (title, description) = DescribeScope(scope);
            return $"""
                <li class="permission">
                  <span class="permission-icon" aria-hidden="true">✓</span>
                  <span><strong>{Encode(title)}</strong><small>{Encode(description)}</small><code>{Encode(scope)}</code></span>
                </li>
                """;
        }));

        var content = $"""
            <div class="client">
              <span class="label">Requesting application</span>
              <span class="client-name">{Encode(clientName)}</span>
              <span class="client-id">Client ID: {Encode(request.ClientId!)}</span>
            </div>
            <h2>This application will be able to:</h2>
            <ul class="permissions">{permissions}</ul>
            <div class="identity"><span class="label">Signed in to Agentweaver as</span><strong>{Encode(signedInName)}</strong><span>{Encode(signedInIdentifier)}</span></div>
            """;
        var actions = $"""
            <form method="post" action="/oauth/authorize">
              {hidden}
              {AuthDialogPage.SubmitButton("Deny", "decision", "deny")}
              {AuthDialogPage.SubmitButton("Allow", "decision", "approve", primary: true)}
            </form>
            """;
        return AuthDialogPage.Render(new(
            $"Authorize {clientName}",
            "Allow access to Agentweaver?",
            "An MCP client wants to connect to your Agentweaver account.",
            AuthDialogTone.Info,
            content,
            actions,
            "Only approve applications you recognize.",
            styleNonce));
    }

    private static string RenderReauthentication(
        string clientName,
        string continuationPath,
        string styleNonce)
    {
        return AuthDialogPage.Render(new(
            "Sign in again",
            "Sign in again to continue",
            $"Your Agentweaver session expired before you finished authorizing {clientName}.",
            AuthDialogTone.Warning,
            ActionsHtml: AuthDialogPage.Link("Sign in again", continuationPath, primary: true),
            Footer: "You will return here to review the request.",
            StyleNonce: styleNonce));
    }

    private static string RenderUnsignedAuthorization(
        string clientName,
        string continuationPath,
        string styleNonce)
    {
        return AuthDialogPage.Render(new(
            "Sign in to authorize",
            "Sign in to review this request",
            $"{clientName} wants to use Agentweaver MCP tools.",
            AuthDialogTone.Info,
            ActionsHtml: AuthDialogPage.Link("Sign in to Agentweaver", continuationPath, primary: true),
            Footer: "Sign in with Microsoft Entra ID before you approve or deny access.",
            StyleNonce: styleNonce));
    }

    private sealed record ConsentApplication(string ClientName, string CallbackSource);
}
