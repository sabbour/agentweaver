using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Platform-admin management of the deployment-wide list of configured BYOK ("bring your own
/// key") inference providers. GitHub Copilot itself is not part of this list — it is implicit
/// whenever no configured provider is marked active (<c>active_provider_id: null</c>).
/// </summary>
public static class ByokProviderSettingsEndpoints
{
    public static void MapByokProviderSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/byok-providers", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            var providers = await settings.ListAsync(ct).ConfigureAwait(false);
            var activeProviderId = await settings.GetActiveProviderIdAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                active_provider_id = activeProviderId,
                providers = providers.Select(p => ToResponse(p, activeProviderId)),
            });
        });

        app.MapPost("/api/admin/byok-providers", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderRequest request,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            try
            {
                var created = await settings.AddAsync(ToConfiguration(request, id: string.Empty), ct).ConfigureAwait(false);
                var activeProviderId = await settings.GetActiveProviderIdAsync(ct).ConfigureAwait(false);
                return Results.Ok(ToResponse(created, activeProviderId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_byok_provider", message = ex.Message });
            }
        });

        app.MapPut("/api/admin/byok-providers/{id}", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            string id,
            ByokProviderRequest request,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            try
            {
                var updated = await settings.UpdateAsync(id, ToConfiguration(request, id), ct).ConfigureAwait(false);
                var activeProviderId = await settings.GetActiveProviderIdAsync(ct).ConfigureAwait(false);
                return Results.Ok(ToResponse(updated, activeProviderId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_byok_provider", message = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapDelete("/api/admin/byok-providers/{id}", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            string id,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            await settings.RemoveAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapPost("/api/admin/byok-providers/{id}/activate", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            string id,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            try
            {
                await settings.SetActiveAsync(id, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            return Results.NoContent();
        });

        // Switches the deployment-wide AI source back to GitHub Copilot without discarding any
        // configured (now-inactive) BYOK providers' saved keys.
        app.MapPost("/api/admin/byok-providers/deactivate", async (
            HttpContext httpContext,
            IProjectRoleAuthorizationService projectRoles,
            ByokProviderConfigurationService settings,
            CancellationToken ct) =>
        {
            if (!projectRoles.IsPlatformAdmin(httpContext.GetCaller()))
                return Results.Forbid();

            await settings.SetActiveAsync(null, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static ByokProviderConfiguration ToConfiguration(ByokProviderRequest request, string id) =>
        new(
            id,
            request.Name,
            request.Type,
            request.BaseUrl,
            request.Model,
            request.ApiKey ?? string.Empty,
            request.WireApi,
            request.Headers,
            request.AzureApiVersion);

    private static object ToResponse(ByokProviderConfiguration configuration, string? activeProviderId) => new
    {
        id = configuration.Id,
        name = configuration.Name,
        type = configuration.Type,
        base_url = configuration.BaseUrl,
        model = configuration.Model,
        wire_api = configuration.WireApi,
        azure_api_version = configuration.AzureApiVersion,
        headers = configuration.Headers,
        has_api_key = !string.IsNullOrEmpty(configuration.ApiKey),
        is_active = configuration.Id == activeProviderId,
    };
}

public sealed record ByokProviderRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("api_key")] string? ApiKey,
    [property: JsonPropertyName("wire_api")] string? WireApi = null,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null,
    [property: JsonPropertyName("azure_api_version")] string? AzureApiVersion = null);
