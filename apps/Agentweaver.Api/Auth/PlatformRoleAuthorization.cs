using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Agentweaver.Api.Auth;

public sealed class PlatformRoleRequirement : IAuthorizationRequirement;

public sealed class PlatformRoleAuthorizationHandler : AuthorizationHandler<PlatformRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformRoleRequirement requirement)
    {
        var isInternal = string.Equals(
            context.User.FindFirst("agentweaver_internal")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (isInternal || context.User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && PlatformRoles.IsRecognized(claim.Value)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
