using System.Security.Claims;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Auth;

public enum HumanEntraSubjectState
{
    Allowed,
    HumanEntraSubjectRequired,
}

/// <summary>Single fail-closed predicate for GitHub credential mutation.</summary>
public static class HumanEntraSubjectAuthorization
{
    public static HumanEntraSubjectState Evaluate(CallerContext caller, ClaimsPrincipal principal) =>
        !string.IsNullOrWhiteSpace(caller.EntraObjectId) &&
        !principal.HasClaim("agentweaver_internal", "true")
            ? HumanEntraSubjectState.Allowed
            : HumanEntraSubjectState.HumanEntraSubjectRequired;
}
