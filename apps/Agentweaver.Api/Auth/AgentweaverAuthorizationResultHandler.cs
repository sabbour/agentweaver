using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Agentweaver.Api.Auth;

internal sealed class AgentweaverAuthorizationResultHandler(IConfiguration configuration)
    : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        if (authorizeResult.Challenged)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("{\"error\":\"unauthorized\"}").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var classification = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthorizationMetadata>();
        if (classification?.RequiresPlatformAccess == true)
        {
            var rawRoles = context.User.FindAll(AgentweaverClaimTypes.RawPlatformRole)
                .Select(claim => claim.Value)
                .ToArray();
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access denied. A recognized Agentweaver platform role is required.",
                entra_object_id = context.User.FindFirst(AgentweaverClaimTypes.EntraObjectId)?.Value,
                entra_tenant_id = context.User.FindFirst(AgentweaverClaimTypes.EntraTenantId)?.Value
                    ?? configuration["Auth:Entra:TenantId"],
                entra_client_id = configuration["Auth:Entra:ClientId"],
                roles_found_on_token = rawRoles,
                recognized_platform_roles = PlatformRoles.All,
            }).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsync("{\"error\":\"forbidden\"}").ConfigureAwait(false);
    }
}
