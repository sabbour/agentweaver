using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Endpoints;

public static class PlatformDefaultCopilotBindingEndpoints
{
    public static void MapPlatformDefaultCopilotBindingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/platform-default-copilot/begin", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            CopilotAppRegistrationService registration,
            ILogger<PlatformDefaultCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var service = new PlatformDefaultCopilotBindingService(
                configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger);
            var result = await service.BeginAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, ct).ConfigureAwait(false);
            if (result.Outcome != PlatformDefaultCopilotBindingOutcome.Success)
                return CopilotBindingFailure(result.Outcome);

            PlatformDefaultCopilotBindingService.SetCallbackCookie(httpContext, result.CallbackCookie!);
            return Results.Ok(new
            {
                authorization_url = result.AuthorizationUrl,
                transaction_id = result.TransactionId,
                expires_at = result.ExpiresAt,
            });
        })
            .WithName("BeginPlatformDefaultCopilotAuthorization")
            .WithTags("Platform settings", "GitHub Copilot");

        app.MapGet("/api/admin/platform-default-copilot/status", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            CopilotAppRegistrationService registration,
            ILogger<PlatformDefaultCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var service = new PlatformDefaultCopilotBindingService(
                configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger);
            var result = await service.GetConnectionAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, ct).ConfigureAwait(false);
            return result.Outcome == PlatformDefaultCopilotBindingOutcome.Success
                ? Results.Ok(new
                {
                    connected = result.Connected,
                    github_login = result.GitHubLogin,
                })
                : CopilotBindingFailure(result.Outcome);
        })
            .WithName("GetPlatformDefaultCopilotStatus")
            .WithTags("Platform settings", "GitHub Copilot");

        app.MapPost("/api/admin/platform-default-copilot/disconnect", async (
            HttpContext httpContext,
            IConfiguration configuration,
            GitHubConnectionsPersistenceStore persistence,
            ISecretStore secretStore,
            IGitHubConnectionsCredentialVault credentialVault,
            IHttpClientFactory httpClientFactory,
            CopilotAppRegistrationService registration,
            ILogger<PlatformDefaultCopilotBindingService> logger,
            CancellationToken ct) =>
        {
            var service = new PlatformDefaultCopilotBindingService(
                configuration, persistence, secretStore, credentialVault, httpClientFactory, registration, logger);
            var outcome = await service.DisconnectAsync(
                ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, ct).ConfigureAwait(false);
            return outcome == PlatformDefaultCopilotBindingOutcome.Success
                ? Results.NoContent()
                : CopilotBindingFailure(outcome);
        })
            .WithName("DisconnectPlatformDefaultCopilot")
            .WithTags("Platform settings", "GitHub Copilot");
    }

    private static IResult CopilotBindingFailure(PlatformDefaultCopilotBindingOutcome outcome)
    {
        var statusCode = outcome is PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired or PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status409Conflict;
        return Results.Json(new { error = PlatformDefaultCopilotBindingService.ToStateCode(outcome) }, statusCode: statusCode);
    }
}
