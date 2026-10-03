using System.Collections.Immutable;
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

public static class IdentityBrokerEndpoints
{
    public const string LocalCookieScheme = "Identity.Local";
    public const string ExternalScheme = "Identity.External";

    public static void MapIdentityBrokerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", [HttpMethods.Get], AuthorizeAsync);
        app.MapGet("/connect/authorize/resume", ResumeAsync);
        app.MapPost("/connect/consent", ConsentAsync);
        app.MapPost("/connect/token", TokenAsync);

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

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context, IdentityBrokerDbContext db, IOpenIddictScopeManager scopes,
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications,
        TimeProvider timeProvider, IAntiforgery antiforgery, CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

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
                CreatedAt = timeProvider.GetUtcNow(),
                ExpiresAt = timeProvider.GetUtcNow().AddMinutes(10),
            });
            await db.SaveChangesAsync(ct);

            var resumeUri = $"/connect/authorize/resume?handle={Uri.EscapeDataString(handle)}";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = resumeUri }, [ExternalScheme]);
        }

        var userId = Guid.Parse(subjectClaim.Value);
        var requestedScopes = request.GetScopes();

        var application = await applications.FindByClientIdAsync(request.ClientId!, ct)
            ?? throw new InvalidOperationException("The client was removed after its request was validated.");
        var applicationId = await applications.GetIdAsync(application, ct);

        var existing = await authorizations.FindAsync(
            subject: subjectClaim.Value, client: applicationId!, status: Statuses.Valid,
            type: AuthorizationTypes.Permanent, scopes: requestedScopes).ToListAsync(ct);
        if (existing.Count > 0)
            return await SignInAsync(db, scopes, authorizations, existing[0], userId, requestedScopes, ct);

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

    private static async Task<IResult> ResumeAsync(
        HttpContext context, string? handle, IdentityBrokerDbContext db,
        TimeProvider timeProvider, CancellationToken ct)
    {
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

    private static async Task<IResult> TokenAsync(
        HttpContext context, IdentityBrokerDbContext db, CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return Forbid(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Forbid(Errors.InvalidGrant, "The authorization grant is invalid.");

        var subjectClaim = result.Principal.FindFirst(Claims.Subject);
        if (subjectClaim is null || !Guid.TryParse(subjectClaim.Value, out var userId))
            return Forbid(Errors.InvalidGrant, "The authorization grant is invalid.");

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Disabled)
            return Forbid(Errors.InvalidGrant, "The account is disabled.");

        return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> SignInAsync(
        IdentityBrokerDbContext db, IOpenIddictScopeManager scopes,
        IOpenIddictAuthorizationManager authorizations, object authorization,
        Guid userId, ImmutableArray<string> requestedScopes, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (user.Disabled)
            return Forbid(Errors.AccessDenied, "The account is disabled.");

        var principal = BuildMinimalPrincipal(userId, requestedScopes);
        principal.SetResources(await scopes.ListResourcesAsync(requestedScopes, ct).ToListAsync(ct));
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization, ct));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static ClaimsPrincipal BuildMinimalPrincipal(Guid userId, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, userId.ToString()).SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes.ToImmutableArray());
        return principal;
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

    private static IResult Forbid(string error, string description) => Results.Json(
        new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}

public sealed record ConsentDecision(
    [property: JsonPropertyName("consent_handle")] string? ConsentHandle,
    [property: JsonPropertyName("approve")] bool Approve,
    [property: JsonPropertyName("scopes")] string[]? Scopes);
