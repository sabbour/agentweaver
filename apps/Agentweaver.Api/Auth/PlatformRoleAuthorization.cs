using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Agentweaver.Api.Auth;

public sealed class PlatformRoleRequirement : IAuthorizationRequirement;
public sealed class PlatformOrBrokerRequirement : IAuthorizationRequirement;
public sealed class InternalServiceRequirement : IAuthorizationRequirement;
public sealed class PlatformOrRunCapabilityRequirement : IAuthorizationRequirement;

public sealed class PlatformRoleAuthorizationHandler : AuthorizationHandler<PlatformRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformRoleRequirement requirement)
    {
        var scheme = context.User.FindFirst(AgentweaverClaimTypes.AuthenticationScheme)?.Value;
        var isInternal = scheme is AgentweaverAuthenticationSchemes.InternalServiceKey
            || context.User.HasClaim(AgentweaverClaimTypes.InternalService, "true");

        if (isInternal || context.User.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && PlatformRoles.IsRecognized(claim.Value)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public sealed class EndpointSchemeAuthorizationHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements.ToArray())
        {
            switch (requirement)
            {
                case PlatformOrBrokerRequirement when
                    HasScheme(context.User, AgentweaverAuthenticationSchemes.BrokerBearer)
                    || HasPlatformAccess(context.User):
                    context.Succeed(requirement);
                    break;
                case InternalServiceRequirement when
                    HasScheme(context.User, AgentweaverAuthenticationSchemes.InternalServiceKey)
                    || context.User.HasClaim(AgentweaverClaimTypes.InternalService, "true"):
                    context.Succeed(requirement);
                    break;
                case PlatformOrRunCapabilityRequirement when
                    HasScheme(context.User, AgentweaverAuthenticationSchemes.RunCapability)
                    || HasPlatformAccess(context.User):
                    context.Succeed(requirement);
                    break;
            }
        }
        return Task.CompletedTask;
    }

    private static bool HasPlatformAccess(ClaimsPrincipal principal) =>
        HasScheme(principal, AgentweaverAuthenticationSchemes.InternalServiceKey)
        || principal.HasClaim(AgentweaverClaimTypes.InternalService, "true")
        || principal.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role && PlatformRoles.IsRecognized(claim.Value));

    private static bool HasScheme(ClaimsPrincipal principal, string scheme) =>
        principal.HasClaim(AgentweaverClaimTypes.AuthenticationScheme, scheme);
}
