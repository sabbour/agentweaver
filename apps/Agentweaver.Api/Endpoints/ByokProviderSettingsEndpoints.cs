using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Endpoints;

public static class ByokProviderSettingsEndpoints
{
    public static void MapByokProviderSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/byok-provider", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(ApiKeyAuthMiddleware.GetCaller(httpContext)))
                return Results.Forbid();

            var configuration = await settings.GetAsync(ct).ConfigureAwait(false);
            return Results.Ok(configuration is null ? null : new
            {
                type = configuration.Type,
                base_url = configuration.BaseUrl,
                model = configuration.Model,
                configured = true,
            });
        });

        app.MapPut("/api/admin/byok-provider", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderConfigurationRequest request,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(ApiKeyAuthMiddleware.GetCaller(httpContext)))
                return Results.Forbid();

            try
            {
                await settings.SetAsync(
                    new ByokProviderConfiguration(
                        request.Type,
                        request.BaseUrl,
                        request.Model,
                        request.ApiKey),
                    ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_byok_provider", message = ex.Message });
            }

            return Results.NoContent();
        });

        app.MapDelete("/api/admin/byok-provider", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(ApiKeyAuthMiddleware.GetCaller(httpContext)))
                return Results.Forbid();

            await settings.ClearAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }
}

public sealed record ByokProviderConfigurationRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("api_key")] string ApiKey);
