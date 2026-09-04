using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

public static class UserModelProviderEndpoints
{
    public static void MapUserModelProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/account/ai-access", async (
            HttpContext httpContext,
            UserModelProviderSettingsService settings,
            ByokProviderConfigurationService platformByok,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            CopilotAppRegistrationService registration,
            ILogger<UserCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) != HumanEntraSubjectState.Allowed)
                return Results.Forbid();

            var personal = await settings.GetAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
            var platform = await platformByok.GetAsync(ct).ConfigureAwait(false);
            var copilot = await CreateCopilotService(
                    configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger)
                .GetConnectionAsync(caller, httpContext.User, ct).ConfigureAwait(false);
            var effective = platform is not null
                ? "platform_byok"
                : personal.Preference == UserModelProviderPreference.Byok && personal.ByokProvider is not null
                    ? "user_byok"
                    : copilot.Connected ? "user_github_copilot" : "none";

            return Results.Ok(new
            {
                effective_source = effective,
                platform_byok = platform is null ? null : new
                {
                    name = platform.Name,
                    type = platform.Type,
                    model = platform.Model,
                },
                preference = personal.Preference == UserModelProviderPreference.Byok ? "byok" : "github_copilot",
                personal_byok = personal.ByokProvider is null ? null : ToByokResponse(personal.ByokProvider),
                copilot = new
                {
                    connected = copilot.Connected,
                    github_login = copilot.GitHubLogin,
                    reconnect_required = copilot.Outcome == UserCopilotBindingOutcome.GitHubBindingUnavailable,
                },
            });
        }).AuthenticatedSelf();

        app.MapPut("/api/account/ai-access/byok", async (
            HttpContext httpContext,
            ByokProviderRequest request,
            UserModelProviderSettingsService settings,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) != HumanEntraSubjectState.Allowed)
                return Results.Forbid();
            try
            {
                var provider = await settings.SetByokAsync(
                    caller.EntraObjectId!,
                    new ByokProviderConfiguration(
                        string.Empty, request.Name, request.Type, request.BaseUrl, request.Model,
                        request.ApiKey ?? string.Empty, request.WireApi, request.Headers, request.AzureApiVersion),
                    ct).ConfigureAwait(false);
                return Results.Ok(ToByokResponse(provider));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_byok_provider", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "credential_write_failed", message = ex.Message });
            }
        }).AuthenticatedSelf();

        app.MapDelete("/api/account/ai-access/byok", async (
            HttpContext httpContext,
            UserModelProviderSettingsService settings,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) != HumanEntraSubjectState.Allowed)
                return Results.Forbid();
            await settings.RemoveByokAsync(caller.EntraObjectId!, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).AuthenticatedSelf();

        app.MapPost("/api/account/ai-access/preference/{preference}", async (
            HttpContext httpContext,
            string preference,
            UserModelProviderSettingsService settings,
            CancellationToken ct) =>
        {
            var caller = httpContext.GetCaller();
            if (HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) != HumanEntraSubjectState.Allowed)
                return Results.Forbid();
            var parsed = preference switch
            {
                "github-copilot" => UserModelProviderPreference.GitHubCopilot,
                "byok" => UserModelProviderPreference.Byok,
                _ => (UserModelProviderPreference?)null,
            };
            if (parsed is null)
                return Results.BadRequest(new { error = "invalid_preference" });
            try
            {
                await settings.SetPreferenceAsync(caller.EntraObjectId!, parsed.Value, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "personal_provider_required", message = ex.Message });
            }
        }).AuthenticatedSelf();

        app.MapPost("/api/account/ai-access/copilot/begin", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            BrowserEntraSessionService browserSessions,
            CopilotAppRegistrationService registration,
            ILogger<UserCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var service = CreateCopilotService(
                configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger);
            var browserSession = await browserSessions.GetCurrentAsync(httpContext, ct).ConfigureAwait(false);
            var result = await service.BeginAsync(
                httpContext.GetCaller(), httpContext.User, browserSession?.Id, ct).ConfigureAwait(false);
            if (result.Outcome != UserCopilotBindingOutcome.Success)
                return CopilotFailure(result.Outcome);
            UserCopilotBindingService.SetCallbackCookie(httpContext, result.CallbackCookie!);
            return Results.Ok(new
            {
                authorization_url = result.AuthorizationUrl,
                transaction_id = result.TransactionId,
                expires_at = result.ExpiresAt,
            });
        }).AuthenticatedSelf();

        app.MapPost("/api/account/ai-access/copilot/disconnect", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            CopilotAppRegistrationService registration,
            ILogger<UserCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var outcome = await CreateCopilotService(
                    configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger)
                .DisconnectAsync(httpContext.GetCaller(), httpContext.User, ct).ConfigureAwait(false);
            return outcome == UserCopilotBindingOutcome.Success
                ? Results.NoContent()
                : CopilotFailure(outcome);
        }).AuthenticatedSelf();
    }

    private static UserCopilotBindingService CreateCopilotService(
        IConfiguration configuration,
        GitHubConnectionsPersistenceStore persistence,
        ISecretStore secretStore,
        IGitHubConnectionsCredentialVault credentialVault,
        IHttpClientFactory httpClientFactory,
        CopilotAppRegistrationService registration,
        ILogger<UserCopilotBindingService> logger) =>
        new(configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger);

    private static object ToByokResponse(ByokProviderConfiguration provider) => new
    {
        id = provider.Id,
        name = provider.Name,
        type = provider.Type,
        base_url = provider.BaseUrl,
        model = provider.Model,
        wire_api = provider.WireApi,
        azure_api_version = provider.AzureApiVersion,
        headers = provider.Headers,
        has_api_key = !string.IsNullOrEmpty(provider.ApiKey),
    };

    private static IResult CopilotFailure(UserCopilotBindingOutcome outcome) =>
        Results.Json(
            new { error = UserCopilotBindingService.ToStateCode(outcome) },
            statusCode: outcome == UserCopilotBindingOutcome.HumanEntraSubjectRequired
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status409Conflict);
}
