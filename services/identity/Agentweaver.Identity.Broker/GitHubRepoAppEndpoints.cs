using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

internal static class GitHubRepoAppEndpoints
{
    internal static void MapGitHubRepoAppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/github/repo-app/connect", BeginUserAuthorizationAsync);
        app.MapPost("/auth/github/repo-app/install", BeginInstallationSetupAsync);
        app.MapGet("/auth/github/repo-app/callback", CompleteCallbackAsync);
        app.MapGet("/auth/github/repo-app/csrf", GetAntiforgeryTokenAsync);
        app.MapGet("/auth/github/repo-app/status", GetStatusAsync);
        app.MapPost("/auth/github/repo-app/disconnect", DisconnectAsync);
        app.MapGet("/auth/github/repo-app/repositories", ListRepositoriesAsync);
        app.MapPost("/auth/github/repo-app/selection", CreateRepositorySelectionAsync);
    }

    private static async Task<IResult> BeginUserAuthorizationAsync(
        HttpContext context,
        GitHubRepoAppConnectionService connectionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false))
            return Results.BadRequest(new { error = "invalid_request" });

        try
        {
            var start = await connectionService.BeginUserAuthorizationAsync(ownerId, cancellationToken)
                .ConfigureAwait(false);
            SetCallbackCookie(context, start.CallbackCookie);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(start.RedirectUri.AbsoluteUri);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<IResult> BeginInstallationSetupAsync(
        HttpContext context,
        GitHubRepoAppConnectionService connectionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false))
            return Results.BadRequest(new { error = "invalid_request" });

        try
        {
            var start = await connectionService.BeginInstallationSetupAsync(ownerId, cancellationToken)
                .ConfigureAwait(false);
            SetCallbackCookie(context, start.CallbackCookie);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(start.RedirectUri.AbsoluteUri);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<IResult> CompleteCallbackAsync(
        HttpContext context,
        GitHubRepoAppConnectionService connectionService,
        CancellationToken cancellationToken)
    {
        var ownerId = await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false);
        var state = SingleQueryValue(context, "state");
        var code = SingleQueryValue(context, "code");
        var setupAction = SingleQueryValue(context, "setup_action");
        var installationValue = SingleQueryValue(context, "installation_id");
        long? installationId = null;
        if (installationValue is not null)
        {
            if (!long.TryParse(installationValue, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                parsed <= 0)
            {
                DeleteCallbackCookie(context);
                return Results.BadRequest(new { error = "invalid_callback" });
            }
            installationId = parsed;
        }

        var callbackCookie = context.Request.Cookies[GitHubRepoAppConnectionService.CallbackCookie];
        DeleteCallbackCookie(context);
        context.Response.Headers.CacheControl = "no-store";
        if (ownerId is null)
            return Results.Unauthorized();

        try
        {
            await connectionService.CompleteCallbackAsync(
                ownerId.Value,
                state,
                callbackCookie,
                code,
                installationId,
                setupAction,
                cancellationToken).ConfigureAwait(false);
            return Results.Redirect("/settings/source-control?repoApp=connected");
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<IResult> GetAntiforgeryTokenAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is null)
            return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { csrf_token = antiforgery.GetAndStoreTokens(context).RequestToken });
    }

    private static async Task<IResult> ListRepositoriesAsync(
        HttpContext context,
        GitHubRepoAppConnectionService connectionService,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();

        try
        {
            var browser = await connectionService.ListRepositoriesAsync(ownerId, cancellationToken)
                .ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(browser);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        GitHubRepoAppConnectionService connectionService,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();

        context.Response.Headers.CacheControl = "no-store";
        var status = await connectionService.GetStatusAsync(ownerId, cancellationToken).ConfigureAwait(false);
        return Results.Json(status);
    }

    private static async Task<IResult> DisconnectAsync(
        HttpContext context,
        GitHubRepoAppDisconnectInput input,
        GitHubRepoAppConnectionService connectionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        if (string.IsNullOrWhiteSpace(input.ConnectionId) || input.ExpectedConnectionRevision < 1)
            return Results.BadRequest(new { error = "invalid_request" });

        try
        {
            var status = await connectionService.DisconnectAsync(
                ownerId,
                input.ConnectionId,
                input.ExpectedConnectionRevision,
                cancellationToken).ConfigureAwait(false);
            return Results.Json(status);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<IResult> CreateRepositorySelectionAsync(
        HttpContext context,
        GitHubRepoAppRepositorySelectionInput input,
        GitHubRepoAppConnectionService connectionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (await TryGetOwnerIdAsync(context, cancellationToken).ConfigureAwait(false) is not { } ownerId)
            return Results.Unauthorized();
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        try
        {
            var selection = await connectionService.CreateRepositorySelectionAsync(
                ownerId, input.InstallationId, input.RepositoryId, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(selection);
        }
        catch (GitHubRepoAppConnectionException error)
        {
            return ConnectionError(context, error);
        }
    }

    private static async Task<Guid?> TryGetOwnerIdAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var local = await context.AuthenticateAsync(IdentityBrokerEndpoints.LocalCookieScheme)
            .ConfigureAwait(false);
        var claims = local.Principal?.FindAll(Claims.Subject).Take(2).ToArray();
        return local.Succeeded && claims is { Length: 1 } &&
            Guid.TryParseExact(claims[0].Value, "D", out var ownerId) && ownerId != Guid.Empty
                ? ownerId
                : null;
    }

    private static async Task<bool> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static string? SingleQueryValue(HttpContext context, string key)
    {
        var values = context.Request.Query[key];
        return values.Count == 1 && values[0]!.Length <= 2048
            ? values[0]
            : null;
    }

    private static void SetCallbackCookie(HttpContext context, string value) =>
        context.Response.Cookies.Append(
            GitHubRepoAppConnectionService.CallbackCookie,
            value,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromMinutes(10),
                IsEssential = true
            });

    private static void DeleteCallbackCookie(HttpContext context) =>
        context.Response.Cookies.Delete(
            GitHubRepoAppConnectionService.CallbackCookie,
            new CookieOptions { Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });

    private static IResult ConnectionError(
        HttpContext context,
        GitHubRepoAppConnectionException error)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (status, code) = error.Failure switch
        {
            GitHubRepoAppConnectionFailure.NotConnected => (StatusCodes.Status409Conflict, "not_connected"),
            GitHubRepoAppConnectionFailure.AuthorizationInvalid => (StatusCodes.Status400BadRequest, "invalid_callback"),
            GitHubRepoAppConnectionFailure.Revoked => (StatusCodes.Status409Conflict, "connection_revoked"),
            GitHubRepoAppConnectionFailure.RefreshInProgress => (StatusCodes.Status503ServiceUnavailable, "refresh_in_progress"),
            GitHubRepoAppConnectionFailure.RotationUncertain => (StatusCodes.Status409Conflict, "rotation_uncertain"),
            GitHubRepoAppConnectionFailure.ConnectionRevisionConflict =>
                (StatusCodes.Status409Conflict, "connection_revision_conflict"),
            GitHubRepoAppConnectionFailure.ProviderUnavailable => (StatusCodes.Status502BadGateway, "provider_unavailable"),
            _ => (StatusCodes.Status404NotFound, "repository_unavailable")
        };
        return Results.Json(new { error = code }, statusCode: status);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GitHubRepoAppRepositorySelectionInput(long InstallationId, long RepositoryId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GitHubRepoAppDisconnectInput(string ConnectionId, long ExpectedConnectionRevision);
