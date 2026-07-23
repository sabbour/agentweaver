namespace Agentweaver.Api.Security;

using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Centralized project-ownership authorization for project-scoped endpoints (memory, decisions,
/// sessions, decision inbox, and casting). Every one of those routes MUST resolve/authorize the
/// project through this helper so that a "project exists" check (<c>projectStore.GetAsync</c> ≠ null)
/// is never mistaken for a "caller owns this project" check.
///
/// SECURITY (broken access control + stored XPIA): without an ownership check, any authenticated
/// organization member who learns another project's UUID (project ids are not secret) could read or
/// mutate that project's memory, sessions, and decisions, and hijack its agent-team casting. Active
/// decisions are compiled verbatim into future agent system prompts as "non-negotiable" instructions,
/// so a cross-project write is also a persistent cross-prompt-injection (XPIA) vector, not merely a
/// confidentiality leak. This gap was independently confirmed across the API and MCP surfaces (the MCP
/// tools forward the caller's real bearer token to these same API routes, so enforcing here covers both).
///
/// The trusted internal service identity (a run's own agents authenticating with the shared
/// <c>Auth:ApiKey</c>, resolving to <see cref="InternalServiceUser"/> or the configured
/// <c>Auth:User</c>) is exempt, mirroring the existing agent-callback allowance in
/// <c>EndpointHelpers.IsOwnerOrServiceCaller</c>. Narrowing that identity to run-bound capabilities is
/// tracked separately as a larger architectural change.
/// </summary>
public static class ProjectAuthorization
{
    /// <summary>
    /// Principal attributed to callers that authenticate with the shared internal service key
    /// (agent loopback calls). Kept in sync with <c>GitHubTokenAuthMiddleware</c>'s internal-key path.
    /// </summary>
    public const string InternalServiceUser = "agentweaver-internal";

    /// <summary>
    /// True when <paramref name="caller"/> is the trusted internal service identity used by a run's own
    /// agents for loopback memory/decision/casting callbacks.
    /// </summary>
    public static bool IsInternalServiceCaller(CallerContext caller, IConfiguration configuration)
    {
        if (string.Equals(caller.User, InternalServiceUser, StringComparison.Ordinal))
            return true;

        var serviceUser = configuration["Auth:User"];
        return !string.IsNullOrEmpty(serviceUser)
            && string.Equals(caller.User, serviceUser, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="caller"/> may operate on a project owned by <paramref name="ownerUser"/>:
    /// either the caller owns it (principal / signed-in GitHub login match) OR the caller is the trusted
    /// internal service identity.
    /// </summary>
    public static bool CanAccess(CallerContext caller, string? ownerUser, IConfiguration configuration)
        => caller.Owns(ownerUser) || IsInternalServiceCaller(caller, configuration);

    /// <summary>
    /// Authorization guard for a project that the caller has ALREADY loaded. Returns
    /// <see langword="null"/> when access is granted, or a <c>403 Forbidden</c> result to short-circuit
    /// the endpoint when the caller neither owns the project nor is the internal service identity.
    /// </summary>
    public static IResult? RequireOwnership(HttpContext httpContext, Project project, IConfiguration configuration)
    {
        var caller = GitHubTokenAuthMiddleware.GetCaller(httpContext);
        return CanAccess(caller, project.Owner, configuration)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Parses the route project id, loads the project, and authorizes the caller in one step for
    /// endpoints that do not otherwise load the project themselves (e.g. casting routes that delegate to
    /// a service). Returns a non-null <c>Failure</c> to short-circuit the endpoint
    /// (<c>400</c> invalid id, <c>404</c> unknown project, <c>403</c> not owner); on success
    /// <c>Failure</c> is null and <c>Project</c> is the authorized, non-null project.
    /// </summary>
    public static async Task<(IResult? Failure, Project? Project)> ResolveOwnedProjectAsync(
        HttpContext httpContext,
        string rawProjectId,
        IProjectStore projectStore,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!ProjectId.TryParse(rawProjectId, out var projectId))
            return (Results.BadRequest(new { error = "Invalid project id." }), null);

        var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return (Results.NotFound(), null);

        var forbid = RequireOwnership(httpContext, project, configuration);
        return forbid is not null ? (forbid, null) : (null, project);
    }
}
