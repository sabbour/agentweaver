using Microsoft.AspNetCore.Authorization;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Enforces platform-role presence on every API request.
/// </summary>
public sealed class PlatformRoleAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthorizationService _authorizationService;
    private readonly IConfiguration _configuration;

    public PlatformRoleAuthorizationMiddleware(
        RequestDelegate next,
        IAuthorizationService authorizationService,
        IConfiguration configuration)
    {
        _next = next;
        _authorizationService = authorizationService;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpointAuthorization = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthorizationMetadata>();
        if (endpointAuthorization is { RequiresPlatformAccess: false }
            || (endpointAuthorization is null && !context.Request.Path.StartsWithSegments("/api")))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var result = await _authorizationService
            .AuthorizeAsync(context.User, null, "PlatformAccess")
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            var rawRoles = context.User.FindAll("raw_role").Select(c => c.Value).ToArray();
            var clientId = _configuration["Auth:Entra:ClientId"];
            var tenantId = context.User.FindFirst("tid")?.Value ?? _configuration["Auth:Entra:TenantId"];
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access denied. A recognized Agentweaver platform role is required.",
                entra_object_id = context.User.FindFirst("oid")?.Value,
                entra_tenant_id = tenantId,
                entra_client_id = clientId,
                // What Microsoft Entra actually put on the token's `roles` claim (may be empty if
                // no App Role assignment exists at all, or non-empty-but-unrecognized if the wrong
                // role name was assigned). Empty means: grant an App Role assignment on this app
                // registration/service principal for this user or one of their groups.
                roles_found_on_token = rawRoles,
                recognized_platform_roles = PlatformRoles.All,
            }).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
