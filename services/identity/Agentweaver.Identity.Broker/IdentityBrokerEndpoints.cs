using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Hosts the broker's browser authorization, consent and token endpoints. It persists
/// short-lived protocol state, validates run-bound grants when authorizing and again
/// before issuing or refreshing tokens, and lets OpenIddict enforce OAuth client rules.
/// </summary>
public static class IdentityBrokerEndpoints
{
    public const string LocalCookieScheme = "Identity.Local";
    public const string ExternalScheme = "Identity.External";
    private const string NativeCliClientId = "agentweaver-cli";
    private const string SilentAuthorizationProperty = "agentweaver.prompt.none";

    /// <summary>Maps OAuth endpoints, the discovery root redirect, and broker health and identity diagnostics routes.</summary>
    public static void MapIdentityBrokerEndpoints(this IEndpointRouteBuilder app)
    {
        var webEndpoints = app.MapGroup("/connect")
            .RequireCors(IdentityBrokerOptions.WebCorsPolicyName);
        webEndpoints.MapMethods("/authorize", [HttpMethods.Get], AuthorizeAsync);
        webEndpoints.MapGet("/authorize/resume", ResumeAsync);
        webEndpoints.MapPost("/consent", ConsentAsync);
        webEndpoints.MapPost("/consent/native", NativeConsentAsync);
        webEndpoints.MapPost("/token", TokenAsync);

        app.MapGet("/agentweaver.png", () =>
            Results.File(
                typeof(IdentityBrokerEndpoints).Assembly.GetManifestResourceStream(
                    "Agentweaver.Identity.Broker.agentweaver.png")
                    ?? throw new InvalidOperationException("The embedded Agentweaver logo is missing."),
                "image/png"));
        app.MapGet("/", () => Results.Redirect("/.well-known/openid-configuration"));
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (IdentityBrokerDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

        app.MapGet("/diagnostics/whoami", (HttpContext context) => Results.Ok(new
        {
            subject = context.User.FindFirst(Claims.Subject)?.Value,
            scopes = context.User.FindAll(Claims.Private.Scope).Select(c => c.Value),
        })).RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
    }

    /// <summary>
    /// Validates requested project/run selectors and current authority before reusing an
    /// existing authorization, completing silent authorization, or creating a one-use
    /// consent transaction. An unauthenticated request stores only a hash of its resume handle.
    /// </summary>
    private static async Task<IResult> AuthorizeAsync(
        HttpContext context, IdentityBrokerDbContext db, IOpenIddictScopeManager scopes,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications,
        IdentityGrantAuthority grantAuthority, BrokerRunBindingAuthority runBindingAuthority,
        TimeProvider timeProvider, IAntiforgery antiforgery,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        if (!TryGetRequestedRunBinding(context.Request.Query, out var runBinding))
            return Forbid(Errors.InvalidRequest, "The project and run selectors are invalid.");
        var requestedScopes = request.GetScopes().ToImmutableArray();
        var usesProjectsAuthority = runBinding is not null &&
            BrokerRunBindingAuthority.UsesProjectsAuthority(requestedScopes);
        if (usesProjectsAuthority && !HasSupportedProjectsScopes(requestedScopes))
            return Forbid(Errors.AccessDenied, "The requested project and run scopes are not supported.");
        var silent = request.HasPromptValue("none");

        var local = await context.AuthenticateAsync(LocalCookieScheme);
        if (!local.Succeeded || local.Principal?.FindFirst(Claims.Subject) is not { } subjectClaim)
        {
            var handle = OpaqueHandle.NewHandle();
            db.PendingAuthorizations.Add(new PendingAuthorization
            {
                Id = Guid.NewGuid(),
                HandleHash = OpaqueHandle.Hash(handle),
                ClientId = request.ClientId!,
                RedirectUri = request.RedirectUri!,
                Scope = request.Scope ?? string.Empty,
                State = request.State,
                CodeChallenge = request.CodeChallenge,
                CodeChallengeMethod = request.CodeChallengeMethod,
                Nonce = request.Nonce,
                PromptNone = silent,
                ProjectId = runBinding?.ProjectId,
                RunId = runBinding?.RunId,
                CreatedAt = timeProvider.GetUtcNow(),
                ExpiresAt = timeProvider.GetUtcNow().AddMinutes(10),
            });
            await db.SaveChangesAsync(ct);

            var resumeUri = $"/connect/authorize/resume?handle={Uri.EscapeDataString(handle)}";
            var properties = new AuthenticationProperties { RedirectUri = resumeUri };
            if (silent)
            {
                properties.Items[SilentAuthorizationProperty] = "true";
                properties.Parameters["prompt"] = "none";
            }
            return Results.Challenge(
                properties, [ExternalScheme]);
        }

        if (!Guid.TryParseExact(subjectClaim.Value, "D", out var userId) || userId == Guid.Empty)
            return Forbid(Errors.AccessDenied, "The local account is invalid.");
        var authorityKind = runBinding is null
            ? null
            : usesProjectsAuthority
                ? BrokerRunBindingAuthority.ProjectsAuthorityKind
                : BrokerRunBindingAuthority.SecretGrantAuthorityKind;
        BrokerRunBindingReceipt? bindingReceipt = null;
        if (runBinding is not null && usesProjectsAuthority)
        {
            var user = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == userId, ct);
            if (user is null || user.Disabled)
                return Forbid(Errors.AccessDenied, "The account is disabled.");

            var check = await runBindingAuthority.CheckAsync(
                userId, runBinding.ProjectId, runBinding.RunId, expectedReceipt: null, ct);
            if (check.Status != BrokerRunBindingStatus.Authorized)
                return RunBindingFailure(check, Errors.AccessDenied);
            bindingReceipt = check.Receipt;
        }
        else if (runBinding is not null && !await grantAuthority.HasActiveRunBindingAsync(
            subjectClaim.Value, runBinding.ProjectId, runBinding.RunId, ct))
        {
            return Forbid(Errors.AccessDenied, "No active grant authorizes the requested project and run.");
        }

        var application = await applications.FindByClientIdAsync(request.ClientId!, ct)
            ?? throw new InvalidOperationException("The client was removed after its request was validated.");
        var applicationId = await applications.GetIdAsync(application, ct);

        var existing = await authorizations.FindAsync(
            subject: subjectClaim.Value, client: applicationId!, status: Statuses.Valid,
            type: AuthorizationTypes.Permanent, scopes: requestedScopes).ToListAsync(ct);
        if (existing.Count > 0)
            return await SignInAsync(db, scopes, authorizations, existing[0], userId, requestedScopes,
                runBinding, authorityKind, bindingReceipt, grantAuthority, runBindingAuthority, ct);
        if (silent)
            return RedirectAuthorizationError(
                request.RedirectUri!, request.State, "consent_required",
                "Consent is required for the requested permissions.");

        var consentHandle = OpaqueHandle.NewHandle();
        db.PendingAuthorizations.Add(new PendingAuthorization
        {
            Id = Guid.NewGuid(),
            HandleHash = OpaqueHandle.Hash(consentHandle),
            ClientId = request.ClientId!,
            RedirectUri = request.RedirectUri!,
            Scope = request.Scope ?? string.Empty,
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            Nonce = request.Nonce,
            ProjectId = runBinding?.ProjectId,
            RunId = runBinding?.RunId,
            SubjectUserId = userId,
            CreatedAt = timeProvider.GetUtcNow(),
            ExpiresAt = timeProvider.GetUtcNow().AddMinutes(10),
        });
        await db.SaveChangesAsync(ct);

        var antiforgeryToken = antiforgery.GetAndStoreTokens(context).RequestToken;
        if (string.Equals(request.ClientId, NativeCliClientId, StringComparison.Ordinal))
        {
            var styleNonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; img-src 'self'; style-src 'nonce-{styleNonce}'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(BuildNativeConsentPage(consentHandle, antiforgeryToken, styleNonce, requestedScopes),
                "text/html; charset=utf-8");
        }

        return Results.Json(new
        {
            consent_required = true,
            consent_handle = consentHandle,
            client_id = request.ClientId,
            requested_scopes = requestedScopes,
            csrf_token = antiforgeryToken,
        });
    }

    /// <summary>
    /// Atomically claims an unexpired login continuation or silent error for the saved OAuth
    /// request. A missing, expired or replayed handle is rejected.
    /// </summary>
    private static async Task<IResult> ResumeAsync(
        HttpContext context, string? handle, string? error, IdentityBrokerDbContext db,
        TimeProvider timeProvider, CancellationToken ct)
    {
        if (context.Request.Query.ContainsKey("error") ||
            context.Request.Query.ContainsKey("external_error"))
        {
            var query = context.Request.Query;
            string mappedError;
            string description;
            if (query.ContainsKey("external_error"))
            {
                var externalErrors = query["external_error"];
                var aadstsValues = query["aadsts"];
                var aadstsCode = aadstsValues.Count == 1 ? aadstsValues[0]?.ToUpperInvariant() : null;
                if (query.ContainsKey("error") || externalErrors.Count != 1 ||
                    !TryMapExternalAuthenticationError(externalErrors[0], out mappedError) ||
                    aadstsValues.Count > 1 ||
                    aadstsValues.Count == 1 && !IsAadstsCode(aadstsCode))
                    return Results.BadRequest(new { error = Errors.InvalidRequest });

                // Rebuild fixed guidance from safe codes; never reflect provider-supplied text.
                description = ExternalAuthenticationFailureDescriptionForCode(mappedError, aadstsCode);
            }
            else if (query["error"].Count != 1 ||
                !TryMapInteractionError(error, out mappedError))
            {
                return Results.BadRequest(new { error = Errors.InvalidRequest });
            }
            else
            {
                description = InteractionErrorDescription(mappedError);
            }
            if (string.IsNullOrWhiteSpace(handle))
                return Results.BadRequest(new { error = Errors.InvalidRequest });

            var errorHash = OpaqueHandle.Hash(handle);
            var errorNow = timeProvider.GetUtcNow();
            var claimedError = await db.PendingAuthorizations
                .Where(p => p.HandleHash == errorHash && p.ConsumedAt == null &&
                    p.ExpiresAt > errorNow && p.SubjectUserId == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ConsumedAt, errorNow), ct);
            if (claimedError != 1)
                return Results.BadRequest(new { error = Errors.InvalidRequest });

            var pendingError = await db.PendingAuthorizations.AsNoTracking()
                .SingleAsync(p => p.HandleHash == errorHash, ct);
            return RedirectAuthorizationError(pendingError.RedirectUri, pendingError.State,
                mappedError, description);
        }

        var local = await context.AuthenticateAsync(LocalCookieScheme);
        if (!local.Succeeded || local.Principal?.FindFirst(Claims.Subject) is not { } subjectClaim ||
            string.IsNullOrWhiteSpace(handle))
        {
            return Results.Unauthorized();
        }

        var hash = OpaqueHandle.Hash(handle);
        var now = timeProvider.GetUtcNow();
        var claimed = await db.PendingAuthorizations
            .Where(p => p.HandleHash == hash && p.ConsumedAt == null && p.ExpiresAt > now && p.SubjectUserId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ConsumedAt, now)
                .SetProperty(p => p.SubjectUserId, Guid.Parse(subjectClaim.Value)), ct);
        if (claimed != 1)
            return Results.BadRequest(new { error = Errors.InvalidRequest });

        var pending = await db.PendingAuthorizations.AsNoTracking()
            .SingleAsync(p => p.HandleHash == hash, ct);
        return Results.Redirect(BuildAuthorizeQuery(pending));
    }

    /// <summary>
    /// Validates antiforgery protection and atomically consumes the pending consent handle.
    /// Approved scopes must be a subset of the original request; denial redirects with an
    /// OAuth error and does not create a permanent authorization.
    /// </summary>
    private static async Task<IResult> ConsentAsync(
        HttpContext context, ConsentDecision decision, IdentityBrokerDbContext db,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications,
        TimeProvider timeProvider, IAntiforgery antiforgery, CancellationToken ct)
    {
        var local = await context.AuthenticateAsync(LocalCookieScheme);
        if (!local.Succeeded || !Guid.TryParse(local.Principal?.FindFirst(Claims.Subject)?.Value, out var userId))
            return Results.Unauthorized();
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        }
        ct.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = OpaqueHandle.Hash(decision.ConsentHandle ?? string.Empty);
        var now = timeProvider.GetUtcNow();
        var claimed = await db.PendingAuthorizations
            .Where(p => p.HandleHash == hash && p.ConsumedAt == null && p.ExpiresAt > now && p.SubjectUserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ConsumedAt, now), ct);
        if (claimed != 1)
            return Results.BadRequest(new { error = Errors.InvalidRequest, error_description = "The consent transaction is invalid or expired." });

        var pending = await db.PendingAuthorizations.AsNoTracking().SingleAsync(p => p.HandleHash == hash, ct);
        if (context.Request.Path.Equals("/connect/consent/native", StringComparison.Ordinal) &&
            !string.Equals(pending.ClientId, NativeCliClientId, StringComparison.Ordinal))
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        var requestedScopes = (pending.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (!decision.Approve)
        {
            await transaction.CommitAsync(ct);
            return Results.Redirect(QueryHelpers.AddQueryString(pending.RedirectUri,
                new Dictionary<string, string?> { ["error"] = Errors.AccessDenied, ["state"] = pending.State }));
        }

        var approvedScopes = (decision.Scopes ?? requestedScopes).ToImmutableArray();
        if (approvedScopes.Except(requestedScopes).Any())
        {
            return Results.BadRequest(new
            {
                error = Errors.InvalidScope,
                error_description = "Approved scopes must be a subset of the originally requested scopes.",
            });
        }
        if (pending.ProjectId is not null && pending.RunId is not null &&
            BrokerRunBindingAuthority.UsesProjectsAuthority(requestedScopes) &&
            !HasSupportedProjectsScopes(approvedScopes))
        {
            await transaction.CommitAsync(ct);
            return Results.Redirect(QueryHelpers.AddQueryString(pending.RedirectUri,
                new Dictionary<string, string?> { ["error"] = Errors.AccessDenied, ["state"] = pending.State }));
        }

        var application = await applications.FindByClientIdAsync(pending.ClientId, ct)
            ?? throw new InvalidOperationException("The client was removed while awaiting consent.");
        var applicationId = await applications.GetIdAsync(application, ct);

        await authorizations.CreateAsync(
            principal: BuildMinimalPrincipal(pending.SubjectUserId!.Value, approvedScopes),
            subject: pending.SubjectUserId.Value.ToString(),
            client: applicationId!,
            type: AuthorizationTypes.Permanent,
            scopes: approvedScopes,
            cancellationToken: ct);
        await transaction.CommitAsync(ct);
        return Results.Redirect(BuildAuthorizeQuery(pending, scopeOverride: string.Join(' ', approvedScopes)));
    }

    private static async Task<IResult> NativeConsentAsync(
        HttpContext context, IdentityBrokerDbContext db,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications,
        TimeProvider timeProvider, IAntiforgery antiforgery, CancellationToken ct)
    {
        const int maxFormBytes = 16 * 1024;
        var maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySize is { IsReadOnly: false })
            maxBodySize.MaxRequestBodySize = maxFormBytes;
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(),
                "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is > maxFormBytes)
            return Results.BadRequest(new { error = Errors.InvalidRequest });

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new { error = Errors.InvalidRequest });
        }
        if (form.Keys.Any(key => key is not ("consent_handle" or "approve" or "__RequestVerificationToken")) ||
            form["consent_handle"].Count != 1 || form["approve"].Count != 1 ||
            form["__RequestVerificationToken"].Count != 1 ||
            form.Any(field => field.Value.Any(value => value is null || value.Length > maxFormBytes)) ||
            form.Sum(field => Encoding.UTF8.GetByteCount(field.Key) +
                field.Value.Sum(value => Encoding.UTF8.GetByteCount(value ?? string.Empty))) > maxFormBytes ||
            !bool.TryParse(form["approve"], out var approve))
            return Results.BadRequest(new { error = Errors.InvalidRequest });

        return await ConsentAsync(context,
            new ConsentDecision(form["consent_handle"][0], approve, null),
            db, authorizations, applications, timeProvider, antiforgery, ct);
    }

    private static string BuildNativeConsentPage(
        string consentHandle, string? antiforgeryToken, string styleNonce, IEnumerable<string> requestedScopes)
    {
        var scopes = string.Join(string.Empty, requestedScopes.Select(scope =>
            $"<li><code>{WebUtility.HtmlEncode(scope)}</code></li>"));
        return $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <meta name="color-scheme" content="light">
            <style nonce="{WebUtility.HtmlEncode(styleNonce)}">{NativeConsentStyles}</style>
            <title>Authorize Agentweaver CLI</title></head>
            <body>
            <main class="v1-auth-page">
            <section class="v1-auth-card" aria-labelledby="consent-title">
            <div class="v1-brand"><img src="/agentweaver.png" alt="">Agentweaver</div>
            <div>
            <h1 id="consent-title">Review access</h1>
            <p>Agentweaver's sign-in service asks to use these permissions for this app.</p>
            </div>
            <dl class="v1-detail-list">
            <div><dt>App</dt><dd>Agentweaver CLI</dd></div>
            <div><dt>Requested permissions</dt><dd><ul class="v1-consent-scopes">{scopes}</ul></dd></div>
            </dl>
            <form method="post" action="/connect/consent/native">
            <input type="hidden" name="consent_handle" value="{WebUtility.HtmlEncode(consentHandle)}">
            <input type="hidden" name="__RequestVerificationToken" value="{WebUtility.HtmlEncode(antiforgeryToken ?? string.Empty)}">
            <div class="v1-actions">
            <button type="submit" class="native-button native-button--primary" name="approve" value="true">Allow</button>
            <button type="submit" class="native-button native-button--secondary" name="approve" value="false">Deny</button>
            </div>
            </form>
            </section>
            </main>
            </body>
            </html>
            """;
    }

    private const string NativeConsentStyles = """
        *,
        *::before,
        *::after {
          box-sizing: border-box;
        }

        body {
          margin: 0;
          color: #272320;
          background: #efeae7;
          font-family: "Segoe UI", ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif;
          font-size: 15px;
          line-height: 1.5;
          -webkit-font-smoothing: antialiased;
        }

        .v1-auth-page {
          display: grid;
          min-height: 100vh;
          place-items: center;
          padding: 24px;
        }

        .v1-auth-card {
          display: flex;
          width: min(100%, 520px);
          flex-direction: column;
          gap: 16px;
          padding: 24px 32px;
          border: 1px solid #ece7e3;
          border-radius: 16px;
          background: #fdfbf8;
          box-shadow: 0 8px 16px rgba(0, 0, 0, 0.14), 0 0 2px rgba(0, 0, 0, 0.12);
        }

        .v1-auth-card h1 {
          margin: 0;
          font-size: 24px;
          line-height: 32px;
          font-weight: 600;
          letter-spacing: -0.02em;
        }

        .v1-auth-card p {
          margin: 0;
          color: #3f3935;
        }

        .v1-brand {
          display: flex;
          align-items: center;
          gap: 10px;
          font-weight: 600;
        }

        .v1-brand img {
          width: 28px;
          height: 28px;
          border-radius: 8px;
        }

        .v1-detail-list {
          display: grid;
          gap: 12px;
          margin: 0;
        }

        .v1-detail-list > div {
          display: grid;
          grid-template-columns: minmax(120px, 0.4fr) minmax(0, 1fr);
          gap: 16px;
          padding-bottom: 4px;
          border-bottom: 1px solid #ece7e3;
        }

        .v1-detail-list dt {
          color: #635c57;
        }

        .v1-detail-list dd {
          min-width: 0;
          margin: 0;
        }

        .v1-consent-scopes {
          display: grid;
          margin: 0;
          padding: 0;
          list-style: none;
        }

        .v1-consent-scopes li {
          padding: 8px 0;
          border-top: 1px solid #ece7e3;
        }

        .v1-consent-scopes code {
          overflow-wrap: anywhere;
          font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
          font-size: 14px;
          font-weight: 600;
        }

        .v1-actions {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;
        }

        .native-button {
          min-height: 32px;
          padding: 5px 12px;
          border: 1px solid transparent;
          border-radius: 8px;
          font: inherit;
          font-size: 14px;
          font-weight: 600;
          cursor: pointer;
        }

        .native-button:focus-visible {
          outline: 2px solid #8c837c;
          outline-offset: 2px;
        }

        .native-button--primary {
          color: #faf6f2;
          background: #272320;
        }

        .native-button--primary:hover {
          background: #3f3935;
        }

        .native-button--secondary {
          color: #272320;
          background: #fdfbf8;
          border-color: #e2ddd9;
        }

        .native-button--secondary:hover {
          background: #efeae7;
        }

        @media (max-width: 600px) {
          .v1-auth-card {
            padding: 24px;
          }

          .v1-detail-list > div {
            grid-template-columns: 1fr;
            gap: 2px;
          }
        }
        """;

    /// <summary>
    /// Accepts only authorization-code and refresh-token exchanges. Before signing in the
    /// validated principal, it rechecks the local user and the exact run authority, scope
    /// and Projects receipt that were bound to the authorization.
    /// </summary>
    private static async Task<IResult> TokenAsync(
        HttpContext context, IdentityBrokerDbContext db, IdentityGrantAuthority grantAuthority,
        BrokerRunBindingAuthority runBindingAuthority, IdentityBrokerOptions brokerOptions,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return Forbid(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Forbid(Errors.InvalidGrant, "The authorization grant is invalid.");

        var subjectClaims = result.Principal.FindAll(Claims.Subject).Take(2).ToArray();
        if (subjectClaims.Length != 1 ||
            !Guid.TryParseExact(subjectClaims[0].Value, "D", out var userId) ||
            userId == Guid.Empty)
            return Forbid(Errors.InvalidGrant, "The authorization grant is invalid.");

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Disabled)
            return Forbid(Errors.InvalidGrant, "The account is disabled.");

        if (!TryGetPrincipalRunBinding(result.Principal, out var runBinding) ||
            !BrokerRunBindingAuthority.TryReadTicket(
                result.Principal, runBinding, out var authorityKind, out var receipt))
            return Forbid(Errors.InvalidGrant, "The run authorization is no longer active.");

        if (runBinding is null)
        {
            if (authorityKind is not null || receipt is not null)
                return Forbid(Errors.InvalidGrant, "The run authorization is no longer active.");
        }
        else
        {
            var tokenScopes = result.Principal.GetScopes().ToImmutableArray();
            if (!BrokerRunBindingAuthority.IsAuthorityKindConsistentWithScopes(
                    authorityKind, tokenScopes) ||
                authorityKind == BrokerRunBindingAuthority.ProjectsAuthorityKind &&
                !HasSupportedProjectsScopes(tokenScopes))
                return Forbid(Errors.InvalidGrant, "The run authorization is no longer active.");

            if (!await RequestRunBindingMatchesAsync(context.Request, runBinding, ct))
                return Forbid(Errors.InvalidGrant, "The authorization grant is bound to another project and run.");

            if (authorityKind == BrokerRunBindingAuthority.ProjectsAuthorityKind)
            {
                if (receipt is null ||
                    receipt.ActorIssuer != new Uri(brokerOptions.Issuer).AbsoluteUri ||
                    receipt.ActorSubject != userId.ToString("D") ||
                    receipt.ProjectId != runBinding.ProjectId ||
                    receipt.RunId != runBinding.RunId)
                    return Forbid(Errors.InvalidGrant, "The run authorization is no longer active.");
                var check = await runBindingAuthority.CheckAsync(
                    userId, runBinding.ProjectId, runBinding.RunId, receipt, ct);
                if (check.Status != BrokerRunBindingStatus.Authorized)
                    return RunBindingFailure(check, Errors.InvalidGrant);
            }
            else if (receipt is not null ||
                !await grantAuthority.HasActiveRunBindingAsync(
                    userId.ToString("D"), runBinding.ProjectId, runBinding.RunId, ct))
            {
                return Forbid(Errors.InvalidGrant, "The run authorization is no longer active.");
            }
        }

        return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Builds a minimal token principal from the authenticated local user and approved
    /// scopes. Run-bound issuance requires either a fresh Projects receipt or a live secret
    /// grant; inconsistent or absent authority evidence is denied.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        IdentityBrokerDbContext db, IOpenIddictScopeManager scopes,
        IOpenIddictAuthorizationManager authorizations, object authorization,
        Guid userId, ImmutableArray<string> requestedScopes, RequestedRunBinding? runBinding,
        string? authorityKind, BrokerRunBindingReceipt? bindingReceipt,
        IdentityGrantAuthority grantAuthority, BrokerRunBindingAuthority runBindingAuthority,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (user.Disabled)
            return Forbid(Errors.AccessDenied, "The account is disabled.");

        if (runBinding is not null)
        {
            if (authorityKind == BrokerRunBindingAuthority.ProjectsAuthorityKind)
            {
                if (bindingReceipt is null ||
                    bindingReceipt.ProjectId != runBinding.ProjectId ||
                    bindingReceipt.RunId != runBinding.RunId)
                    return Forbid(Errors.AccessDenied, "The project and run authorization is invalid.");
                var check = await runBindingAuthority.CheckAsync(
                    userId, runBinding.ProjectId, runBinding.RunId, bindingReceipt, ct);
                if (check.Status != BrokerRunBindingStatus.Authorized)
                    return RunBindingFailure(check, Errors.AccessDenied);
            }
            else if (authorityKind != BrokerRunBindingAuthority.SecretGrantAuthorityKind ||
                bindingReceipt is not null ||
                !await grantAuthority.HasActiveRunBindingAsync(
                    userId.ToString("D"), runBinding.ProjectId, runBinding.RunId, ct))
            {
                return Forbid(Errors.AccessDenied, "No active grant authorizes the requested project and run.");
            }
        }
        else if (authorityKind is not null || bindingReceipt is not null)
        {
            return Forbid(Errors.AccessDenied, "The project and run authorization is invalid.");
        }

        var principal = BuildMinimalPrincipal(
            userId, requestedScopes, runBinding, authorityKind, bindingReceipt);
        principal.SetResources(await scopes.ListResourcesAsync(requestedScopes, ct).ToListAsync(ct));
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization, ct));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Creates the minimal OpenIddict principal and assigns claims to access or identity
    /// tokens explicitly. Throws if run selectors and authority evidence do not form one
    /// consistent binding.
    /// </summary>
    private static ClaimsPrincipal BuildMinimalPrincipal(
        Guid userId,
        IEnumerable<string> scopes,
        RequestedRunBinding? runBinding = null,
        string? authorityKind = null,
        BrokerRunBindingReceipt? bindingReceipt = null)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, userId.ToString()).SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        if (runBinding is not null)
        {
            identity.AddClaim(new Claim(SecretRedemptionEndpoints.ProjectIdClaim, runBinding.ProjectId)
                .SetDestinations(Destinations.AccessToken));
            identity.AddClaim(new Claim(SecretRedemptionEndpoints.RunIdClaim, runBinding.RunId)
                .SetDestinations(Destinations.AccessToken));
            if (authorityKind == BrokerRunBindingAuthority.ProjectsAuthorityKind && bindingReceipt is not null)
                BrokerRunBindingAuthority.AddReceiptClaims(identity, bindingReceipt);
            else if (authorityKind == BrokerRunBindingAuthority.SecretGrantAuthorityKind && bindingReceipt is null)
                BrokerRunBindingAuthority.AddAuthorityClaim(identity, authorityKind);
            else
                throw new InvalidOperationException("A run-bound authorization requires one valid authority receipt.");
        }
        else if (authorityKind is not null || bindingReceipt is not null)
            throw new InvalidOperationException("An unbound authorization cannot contain run authority.");
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes.ToImmutableArray());
        return principal;
    }

    private static bool HasSupportedProjectsScopes(IEnumerable<string> scopes)
    {
        var requested = scopes.ToHashSet(StringComparer.Ordinal);
        return requested.Contains("api.read") &&
            requested.Contains("projects.orchestrator") &&
            requested.All(scope => scope is "api.read" or "projects.orchestrator" or
                "openid" or "profile" or "email" or "offline_access");
    }

    private static IResult RunBindingFailure(BrokerRunBindingCheck check, string deniedError) =>
        check.Status == BrokerRunBindingStatus.Unavailable
            ? Results.Json(
                new { error = "temporarily_unavailable", error_description = "The project authorization owner is unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Forbid(deniedError, "The project and run authorization is no longer active.");

    /// <summary>
    /// Returns silent upstream interaction failures through the original, one-use
    /// authorization continuation. Other external authentication failures remain denied.
    /// </summary>
    public static async Task HandleExternalAuthenticationFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        var properties = context.Properties;
        var failure = await ReadExternalAuthenticationFailureAsync(
            context.Request, context.HttpContext.RequestAborted);
        if (properties?.Items.TryGetValue(SilentAuthorizationProperty, out var silent) == true &&
            silent == "true" &&
            TryMapInteractionError(failure.Error, out var mappedError) &&
            properties.RedirectUri is { } redirectUri)
        {
            context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, "error", mappedError));
        }
        else if (IsAuthorizationResumeUri(properties?.RedirectUri))
        {
            // Resume through the one-use transaction so Web receives safe, actionable callback errors.
            var aadstsCode = ExtractAadstsCode(failure.Description);
            context.Response.Redirect(QueryHelpers.AddQueryString(
                properties!.RedirectUri!,
                new Dictionary<string, string?>
                {
                    ["external_error"] = MapExternalAuthenticationError(failure.Error),
                    ["aadsts"] = aadstsCode,
                }));
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = MapExternalAuthenticationError(failure.Error),
                error_description = ExternalAuthenticationFailureDescription(failure.Error, failure.Description),
            }, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed record ExternalAuthenticationFailure(string? Error, string? Description);

    private static async Task<ExternalAuthenticationFailure> ReadExternalAuthenticationFailureAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        var queryErrors = request.Query["error"];
        if (!request.HasFormContentType)
        {
            var queryDescriptions = request.Query["error_description"];
            return new ExternalAuthenticationFailure(
                queryErrors.Count == 1 ? queryErrors[0] : null,
                queryDescriptions.Count == 1 ? queryDescriptions[0] : null);
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var formErrors = form["error"];
        var formDescriptions = form["error_description"];
        if (queryErrors.Count != 0 || formErrors.Count != 1)
            return new ExternalAuthenticationFailure(null, null);
        return new ExternalAuthenticationFailure(
            formErrors[0],
            formDescriptions.Count == 1 ? formDescriptions[0] : null);
    }

    private static string MapExternalAuthenticationError(string? upstreamError) => upstreamError switch
    {
        "access_denied" => "access_denied",
        "invalid_client" => "invalid_client",
        "unauthorized_client" => "unauthorized_client",
        "invalid_request" => "invalid_request",
        "temporarily_unavailable" => "temporarily_unavailable",
        "server_error" => "server_error",
        "login_required" => "login_required",
        "consent_required" => "consent_required",
        "interaction_required" or "account_selection_required" or "session_selection_required" =>
            "interaction_required",
        _ => "external_sign_in_failed",
    };

    private static string ExternalAuthenticationFailureDescription(string? upstreamError, string? providerDescription) =>
        ExternalAuthenticationFailureDescriptionForCode(upstreamError, ExtractAadstsCode(providerDescription));

    private static string ExternalAuthenticationFailureDescriptionForCode(
        string? upstreamError,
        string? aadstsCode)
    {
        var description = aadstsCode switch
        {
            "AADSTS650057" => "Microsoft Entra rejected the requested resource. The Broker requests only openid, profile, and email; no Agentweaver API permission is required.",
            "AADSTS50011" => "Microsoft Entra rejected the callback URL. Register the exact route-derived Broker /signin-oidc URL.",
            "AADSTS700016" => "Microsoft Entra could not find this client in the selected tenant. Check the exact tenant and client ID.",
            "AADSTS7000218" => "Microsoft Entra requires the public-client flow for this application. Enable the public-client setting.",
            "AADSTS50105" => "This user is not assigned to the Microsoft Entra application. Check its enterprise application assignments.",
            "AADSTS50020" => "This account is not available in the selected tenant. Check the tenant and user membership.",
            _ => upstreamError switch
            {
                "access_denied" => "Microsoft Entra sign-in was canceled or access was denied.",
                "invalid_client" => "Microsoft Entra rejected the client application. Check the exact tenant, client ID, and public-client setting.",
                "unauthorized_client" => "Microsoft Entra does not allow this client to use the requested sign-in flow.",
                "invalid_request" => "Microsoft Entra rejected the request. Check the exact route-derived callback and OpenID Connect scopes.",
                "temporarily_unavailable" or "server_error" => "Microsoft Entra could not complete sign-in right now. Try again later.",
                "login_required" => "An active sign-in is required.",
                "consent_required" => "Consent is required for the requested permissions.",
                "interaction_required" => "More sign-in interaction is required.",
                _ => "Microsoft Entra could not complete sign-in. Check the exact tenant, client ID, and route-derived callback.",
            },
        };
        return aadstsCode is null ? description : $"{description} ({aadstsCode}).";
    }

    private static string? ExtractAadstsCode(string? providerDescription)
    {
        var safeDescription = providerDescription is { Length: > 4096 }
            ? providerDescription[..4096]
            : providerDescription ?? string.Empty;
        // Provider text may contain account data; return only the numeric AADSTS identifier.
        var match = Regex.Match(safeDescription, @"\bAADSTS\d{5,10}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static bool IsAadstsCode(string? value) =>
        value is not null && Regex.IsMatch(value, @"^AADSTS\d{5,10}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsAuthorizationResumeUri(string? value) =>
        value is not null &&
        value.StartsWith("/connect/authorize/resume?", StringComparison.Ordinal) &&
        Uri.TryCreate(value, UriKind.Relative, out _);

    private static bool TryMapInteractionError(string? upstreamError, out string mappedError)
    {
        mappedError = upstreamError switch
        {
            "login_required" => "login_required",
            "consent_required" => "consent_required",
            "interaction_required" or "account_selection_required" or "session_selection_required" =>
                "interaction_required",
            _ => string.Empty,
        };
        return mappedError.Length > 0;
    }

    private static bool TryMapExternalAuthenticationError(string? upstreamError, out string mappedError)
    {
        mappedError = upstreamError switch
        {
            "access_denied" => "access_denied",
            "invalid_client" => "invalid_client",
            "unauthorized_client" => "unauthorized_client",
            "invalid_request" => "invalid_request",
            "temporarily_unavailable" => "temporarily_unavailable",
            "server_error" => "server_error",
            "login_required" => "login_required",
            "consent_required" => "consent_required",
            "interaction_required" => "interaction_required",
            "external_sign_in_failed" => "external_sign_in_failed",
            _ => string.Empty,
        };
        return mappedError.Length > 0;
    }

    private static string InteractionErrorDescription(string error) => error switch
    {
        "login_required" => "An active sign-in is required.",
        "consent_required" => "Consent is required for the requested permissions.",
        _ => "More sign-in interaction is required.",
    };

    private static IResult RedirectAuthorizationError(
        string redirectUri, string? state, string error, string description) =>
        Results.Redirect(QueryHelpers.AddQueryString(redirectUri,
            new Dictionary<string, string?>
            {
                ["error"] = error,
                ["error_description"] = description,
                ["state"] = state,
            }));

    private static async Task<bool> RequestRunBindingMatchesAsync(
        HttpRequest request,
        RequestedRunBinding binding,
        CancellationToken cancellationToken)
    {
        var projectValues = request.Query[SecretRedemptionEndpoints.ProjectIdClaim]
            .Select(value => (string?)value).ToList();
        var runValues = request.Query[SecretRedemptionEndpoints.RunIdClaim]
            .Select(value => (string?)value).ToList();
        if (request.HasFormContentType)
        {
            try
            {
                var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                projectValues.AddRange(form[SecretRedemptionEndpoints.ProjectIdClaim]
                    .Select(value => (string?)value));
                runValues.AddRange(form[SecretRedemptionEndpoints.RunIdClaim]
                    .Select(value => (string?)value));
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (BadHttpRequestException)
            {
                return false;
            }
        }

        if (projectValues.Count == 0 && runValues.Count == 0)
            return true;
        return projectValues.Count == 1 && runValues.Count == 1 &&
            projectValues[0] is { } projectId &&
            runValues[0] is { } runId &&
            IsValidBindingIdentifier(projectId) &&
            IsValidBindingIdentifier(runId) &&
            string.Equals(projectId, binding.ProjectId, StringComparison.Ordinal) &&
            string.Equals(runId, binding.RunId, StringComparison.Ordinal);
    }

    private static string BuildAuthorizeQuery(PendingAuthorization pending, string? scopeOverride = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = pending.ClientId,
            ["redirect_uri"] = pending.RedirectUri,
            ["response_type"] = ResponseTypes.Code,
            ["scope"] = scopeOverride ?? pending.Scope,
            ["state"] = pending.State,
            ["code_challenge"] = pending.CodeChallenge,
            ["code_challenge_method"] = pending.CodeChallengeMethod,
            ["nonce"] = pending.Nonce,
            ["prompt"] = pending.PromptNone ? "none" : null,
            [SecretRedemptionEndpoints.ProjectIdClaim] = pending.ProjectId,
            [SecretRedemptionEndpoints.RunIdClaim] = pending.RunId,
        };
        var builder = new StringBuilder("/connect/authorize?");
        var first = true;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!first) builder.Append('&');
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return builder.ToString();
    }

    private static bool TryGetRequestedRunBinding(
        IQueryCollection query,
        out RequestedRunBinding? binding)
    {
        binding = null;
        var projectValues = query[SecretRedemptionEndpoints.ProjectIdClaim];
        var runValues = query[SecretRedemptionEndpoints.RunIdClaim];
        if (projectValues.Count == 0 && runValues.Count == 0)
            return true;
        if (projectValues.Count != 1 || runValues.Count != 1)
            return false;

        var projectId = projectValues[0];
        var runId = runValues[0];
        if (projectId is null || runId is null ||
            !IsValidBindingIdentifier(projectId) || !IsValidBindingIdentifier(runId))
            return false;

        binding = new RequestedRunBinding(projectId, runId);
        return true;
    }

    private static bool TryGetPrincipalRunBinding(
        ClaimsPrincipal principal,
        out RequestedRunBinding? binding)
    {
        binding = null;
        var projectClaims = principal.FindAll(SecretRedemptionEndpoints.ProjectIdClaim).Take(2).ToArray();
        var runClaims = principal.FindAll(SecretRedemptionEndpoints.RunIdClaim).Take(2).ToArray();
        if (projectClaims.Length == 0 && runClaims.Length == 0)
            return true;
        if (projectClaims.Length != 1 || runClaims.Length != 1 ||
            !IsValidBindingIdentifier(projectClaims[0].Value) ||
            !IsValidBindingIdentifier(runClaims[0].Value))
            return false;

        binding = new RequestedRunBinding(projectClaims[0].Value, runClaims[0].Value);
        return true;
    }

    private static bool IsValidBindingIdentifier(string value) =>
        value.Length is > 0 and <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static IResult Forbid(string error, string description) => Results.Json(
        new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Untrusted project and run selectors that must be checked by an authority.</summary>
internal sealed record RequestedRunBinding(string ProjectId, string RunId);

/// <summary>Browser consent decision; approved scopes are restricted to the pending request.</summary>
public sealed record ConsentDecision(
    [property: JsonPropertyName("consent_handle")] string? ConsentHandle,
    [property: JsonPropertyName("approve")] bool Approve,
    [property: JsonPropertyName("scopes")] string[]? Scopes);
