using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/config", (IConfiguration configuration) =>
        {
            var tenantId = configuration["Auth:Entra:TenantId"];
            var authority = configuration["Auth:Entra:Authority"];
            if (string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(tenantId))
                authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

            return Results.Ok(new
            {
                mode = "Entra",
                entra = new
                {
                    client_id = configuration["Auth:Entra:ClientId"],
                    tenant_id = tenantId,
                    authority,
                },
            });
        }).AllowAnonymous();

        app.MapGet("/api/auth/context", (HttpContext httpContext) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            return Results.Ok(new
            {
                mode = "Entra",
                user_id = caller.User,
                github_login = caller.GitHubLogin,
                entra_object_id = caller.EntraObjectId,
                entra_tenant_id = caller.EntraTenantId,
                platform_roles = caller.PlatformRoles,
                primary_platform_role = caller.PrimaryPlatformRole,
            });
        });

        app.MapGet("/api/auth/session", async (
            HttpContext httpContext,
            ByokProviderConfigurationService byokSettings,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var platformBinding = await persistence.GetActivePlatformDefaultCopilotBindingAsync(ct).ConfigureAwait(false);
            var aiConfigured =
                await HasByokConfigurationAsync(byokSettings, ct).ConfigureAwait(false) ||
                await HasUsablePlatformDefaultCopilotBindingAsync(platformBinding, secretStore, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                authenticated = true,
                auth_mode = "entra",
                display_name = caller.DisplayName,
                email = caller.Email,
                login = caller.GitHubLogin,
                avatar_url = (string?)null,
                entra_object_id = caller.EntraObjectId,
                platform_roles = caller.PlatformRoles,
                ai_configured = aiConfigured,
            });
        });

        app.MapPost("/api/auth/github/repo-app/authorizations", async (
            HttpContext httpContext,
            RepoAppAuthorizationBeginRequest? request,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var result = await service.BeginAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, request?.ReturnRouteKey, ct).ConfigureAwait(false);
            if (result.Outcome != RepoAppAuthorizationOutcome.Success)
                return Results.Conflict(new { error = RepoAppUserAuthorizationService.ToStateCode(result.Outcome) });

            RepoAppUserAuthorizationService.SetCallbackCookie(httpContext, result.CallbackCookie!);
            return Results.Ok(new
            {
                authorization_url = result.AuthorizationUrl,
                transaction_id = result.TransactionId,
                expires_at = result.ExpiresAt,
            });
        });

        app.MapPost("/api/auth/github/repo-app/authorizations/handoff", async (
            HttpContext httpContext,
            RepoAppAuthorizationBeginRequest? request,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var result = await service.BeginMcpHandoffAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, request?.ReturnRouteKey, ct).ConfigureAwait(false);
            return result.Outcome == RepoAppAuthorizationOutcome.Success
                ? Results.Ok(new
                {
                    transaction_id = result.TransactionId,
                    browser_url = result.BrowserUrl,
                    expires_at = result.ExpiresAt,
                })
                : Results.Conflict(new { error = RepoAppUserAuthorizationService.ToStateCode(result.Outcome) });
        });

        app.MapGet("/auth/github/repo-app/handoff/{transactionId}", async (
            HttpContext httpContext,
            string transactionId,
            IConfiguration configuration,
            BrowserEntraSessionService browserSessions,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var browserSession = await browserSessions.GetCurrentAsync(httpContext, ct).ConfigureAwait(false);
            if (browserSession is null)
                return Results.Unauthorized();

            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var handoff = await service.TakeMcpBrowserHandoffAsync(
                transactionId, browserSession.Id, browserSession.EntraObjectId, ct).ConfigureAwait(false);
            if (handoff is null)
                return Results.NotFound();

            RepoAppUserAuthorizationService.SetCallbackCookie(httpContext, handoff.Value.CallbackCookie);
            return Results.Redirect(handoff.Value.AuthorizationUrl);
        }).AllowAnonymous();

        app.MapGet("/auth/github/repo-app/callback", async (
            HttpContext httpContext,
            string? code,
            string? state,
            string? error,
            IConfiguration configuration,
            BrowserEntraSessionService browserSessions,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var callbackCookie = RepoAppUserAuthorizationService.ReadCallbackCookie(httpContext);
            RepoAppUserAuthorizationService.ClearCallbackCookie(httpContext);
            var browserSession = await browserSessions.GetCurrentAsync(httpContext, ct).ConfigureAwait(false);
            var result = await service.CompleteBrowserCallbackAsync(
                browserSession?.Id, browserSession?.EntraObjectId, state,
                string.IsNullOrWhiteSpace(error) ? code : null, callbackCookie, ct).ConfigureAwait(false);
            return Results.Redirect(service.GetCallbackRedirect(result.ReturnRouteKey, result.Outcome));
        }).AllowAnonymous();

        app.MapGet("/api/auth/github/repo-app/authorizations/{transactionId}", async (
            HttpContext httpContext,
            string transactionId,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var result = await service.PollAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, transactionId, ct).ConfigureAwait(false);
            return result.Outcome == RepoAppAuthorizationOutcome.Success
                ? Results.Ok(new { status = result.Status })
                : Results.Conflict(new { error = RepoAppUserAuthorizationService.ToStateCode(result.Outcome) });
        });

        app.MapPost("/api/auth/github/repo-app/authorization/refresh", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var outcome = await service.RefreshAsync(ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, ct).ConfigureAwait(false);
            return outcome == RepoAppAuthorizationOutcome.Success
                ? Results.NoContent()
                : Results.Conflict(new { error = RepoAppUserAuthorizationService.ToStateCode(outcome) });
        });

        app.MapDelete("/api/auth/github/repo-app/authorization", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var service = new RepoAppUserAuthorizationService(configuration, persistence, secretStore, httpClientFactory);
            var outcome = await service.RevokeAsync(ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, ct).ConfigureAwait(false);
            return outcome == RepoAppAuthorizationOutcome.Success
                ? Results.NoContent()
                : Results.Conflict(new { error = RepoAppUserAuthorizationService.ToStateCode(outcome) });
        });

        app.MapGet("/auth/entra/authorize", async (
            HttpContext httpContext,
            EntraOAuthRedirectService entraOauthService,
            CancellationToken ct) =>
        {
            try
            {
                var url = await entraOauthService.BeginAuthorizationAsync(ct).ConfigureAwait(false);
                var state = EntraOAuthStateCookie.ExtractState(url);
                if (state is not null)
                    EntraOAuthStateCookie.Set(httpContext, state);
                return Results.Redirect(url);
            }
            catch (EntraNotConfiguredException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        app.MapGet("/auth/entra/callback", async (
            HttpContext httpContext,
            string? code,
            string? state,
            string? error,
            EntraOAuthRedirectService entraOauthService,
            WebSessionExchangeService webSessionExchange,
            CancellationToken ct) =>
        {
            EntraAuthorizationFlowConfiguration authorizationConfiguration;
            try
            {
                authorizationConfiguration = entraOauthService.GetAuthorizationFlowConfiguration();
            }
            catch (EntraNotConfiguredException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var frontendUrl = authorizationConfiguration.FrontendUrl;

            if (string.IsNullOrWhiteSpace(state))
                return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

            var boundState = EntraOAuthStateCookie.Read(httpContext);
            EntraOAuthStateCookie.Clear(httpContext);
            if (string.IsNullOrEmpty(boundState) || !EntraOAuthStateCookie.ConstantTimeEquals(boundState, state))
                return Results.Redirect($"{frontendUrl}/?auth=error&reason=state_mismatch");
            if (!string.IsNullOrWhiteSpace(error))
                return Results.Redirect($"{frontendUrl}/?auth=error&reason={Uri.EscapeDataString(error)}");
            if (string.IsNullOrWhiteSpace(code))
                return Results.Redirect($"{frontendUrl}/?auth=error&reason=missing_params");

            try
            {
                var (claims, accessToken) = await entraOauthService.ExchangeCodeAsync(code, state, ct).ConfigureAwait(false);
                var oneTimeCode = await webSessionExchange.IssueAsync(accessToken, claims.DisplayName, ct).ConfigureAwait(false);
                return Results.Redirect($"{frontendUrl}/?auth=success&code={Uri.EscapeDataString(oneTimeCode)}");
            }
            catch (Exception)
            {
                return Results.Redirect($"{frontendUrl}/?auth=error&reason=sign_in_failed");
            }
        }).AllowAnonymous();

        app.MapPost("/api/auth/session/exchange", async (
            HttpContext httpContext,
            SessionExchangeRequest request,
            WebSessionExchangeService webSessionExchange,
            BrowserEntraSessionService browserSessions,
            EntraAccessTokenValidator entraTokenValidator,
            CancellationToken ct) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "invalid_code" });
            var (success, accessToken, login) = await webSessionExchange.TryRedeemAsync(request.Code, ct).ConfigureAwait(false);
            if (!success)
                return Results.BadRequest(new { error = "invalid_code" });

            var claims = await entraTokenValidator.ValidateAsync(accessToken, ct).ConfigureAwait(false);
            if (claims is null)
                return Results.BadRequest(new { error = "invalid_code" });
            await browserSessions.IssueAsync(httpContext, claims, ct).ConfigureAwait(false);
            return Results.Ok(new SessionExchangeResponse(accessToken, login));
        }).AllowAnonymous();
    }

    private static async Task<bool> HasUsablePlatformDefaultCopilotBindingAsync(
        RepoAppCredentialReference? binding,
        ISecretStore secretStore,
        CancellationToken ct)
    {
        if (binding is null)
            return false;

        var secret = await secretStore.GetSecretAsync(binding.CredentialReference, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return false;

        try
        {
            using var document = JsonDocument.Parse(secret.Value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var status = GetJsonString(document.RootElement, "status");
            var accessToken = GetJsonString(document.RootElement, "accessToken");
            return string.Equals(status, "signed-in", StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(accessToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> HasByokConfigurationAsync(
        ByokProviderConfigurationService byokSettings,
        CancellationToken ct)
    {
        try
        {
            return await byokSettings.GetAsync(ct).ConfigureAwait(false) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var propertyName in propertyNames)
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return null;
    }
}

internal static class EntraOAuthStateCookie
{
    internal const string Name = "aw_entra_oauth_state";
    private const string Path = "/auth/entra";

    public static string? ExtractState(string authorizeUrl)
    {
        if (!Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var uri))
            return null;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(pair => pair.StartsWith("state=", StringComparison.Ordinal))
            ?.Substring("state=".Length) is { } encoded
            ? Uri.UnescapeDataString(encoded)
            : null;
    }

    public static void Set(HttpContext context, string state) =>
        context.Response.Cookies.Append(Name, state, Options(context));

    public static string? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(Name, out var value) ? value : null;

    public static void Clear(HttpContext context) =>
        context.Response.Cookies.Append(Name, string.Empty, Options(context, DateTimeOffset.UnixEpoch));

    public static bool ConstantTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static CookieOptions Options(HttpContext context, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = Path,
        MaxAge = expires is null ? TimeSpan.FromMinutes(10) : null,
        Expires = expires,
    };
}

file sealed record SessionExchangeRequest([property: JsonPropertyName("code")] string? Code);
file sealed record SessionExchangeResponse(
    [property: JsonPropertyName("session_token")] string SessionToken,
    [property: JsonPropertyName("login")] string Login);
file sealed record RepoAppAuthorizationBeginRequest(
    [property: JsonPropertyName("return_route_key")] string? ReturnRouteKey);
