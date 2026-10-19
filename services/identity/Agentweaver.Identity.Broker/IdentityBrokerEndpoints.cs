using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
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
    private const string SilentAuthorizationProperty = "agentweaver.prompt.none";

    /// <summary>Maps OAuth endpoints, the discovery root redirect, and broker health and identity diagnostics routes.</summary>
    public static void MapIdentityBrokerEndpoints(this IEndpointRouteBuilder app)
    {
        var webEndpoints = app.MapGroup("/connect")
            .RequireCors(IdentityBrokerOptions.WebCorsPolicyName);
        webEndpoints.MapMethods("/authorize", [HttpMethods.Get], AuthorizeAsync);
        webEndpoints.MapGet("/authorize/resume", ResumeAsync);
        webEndpoints.MapPost("/consent", ConsentAsync);
        webEndpoints.MapPost("/token", TokenAsync);

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

        return Results.Json(new
        {
            consent_required = true,
            consent_handle = consentHandle,
            client_id = request.ClientId,
            requested_scopes = requestedScopes,
            csrf_token = antiforgery.GetAndStoreTokens(context).RequestToken,
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
        if (context.Request.Query.ContainsKey("error"))
        {
            if (context.Request.Query["error"].Count != 1 ||
                !TryMapInteractionError(error, out var mappedError) ||
                string.IsNullOrWhiteSpace(handle))
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
                mappedError, InteractionErrorDescription(mappedError));
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
        var upstreamError = await ReadExternalInteractionErrorAsync(
            context.Request, context.HttpContext.RequestAborted);
        if (properties?.Items.TryGetValue(SilentAuthorizationProperty, out var silent) == true &&
            silent == "true" &&
            TryMapInteractionError(upstreamError, out var mappedError) &&
            properties.RedirectUri is { } redirectUri)
        {
            context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, "error", mappedError));
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
    }

    private static async Task<string?> ReadExternalInteractionErrorAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        var queryErrors = request.Query["error"];
        if (!request.HasFormContentType)
            return queryErrors.Count == 1 ? queryErrors[0] : null;

        var formErrors = (await request.ReadFormAsync(cancellationToken).ConfigureAwait(false))["error"];
        if (queryErrors.Count != 0 || formErrors.Count != 1)
            return null;
        return formErrors[0];
    }

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
