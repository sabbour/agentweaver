using Agentweaver.Api.Security;
using Agentweaver.Api.Auth;
using Agentweaver.Domain.BlueprintPackages;

namespace Agentweaver.Api.Blueprints;

/// <summary>Resolves the package-library owner from the authenticated request.</summary>
public sealed class HttpContextAuthenticatedOwnerContext(IHttpContextAccessor accessor)
    : IAuthenticatedOwnerContext
{
    public string OwnerId
    {
        get
        {
            var context = accessor.HttpContext
                ?? throw new InvalidOperationException(
                    "An authenticated HTTP request is required for owner-scoped package operations.");

            CallerContext caller;
            try
            {
                caller = context.GetCaller();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "An authenticated caller is required for owner-scoped package operations.");
            }

            return caller.User;
        }
    }
}
