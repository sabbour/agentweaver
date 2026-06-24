using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.ReviewPolicies;

/// <summary>
/// Project-scoped review-policy endpoints (Feature 010, FR-025/027/033). Lists the project's discovered
/// policies with their validation status, returns a single policy by name, sets the project's active
/// policy by name, and re-reads <c>.agentweaver/review-policies/</c> on an explicit Sync. All discovery,
/// validation, and resolution is server-side (Principles III, IV); clients only render the results.
/// Owner-scoped like the other project endpoints: 404 when the project is missing, 403 when the caller
/// is not the project owner.
/// </summary>
public static class ReviewPolicyEndpoints
{
    public static void MapReviewPolicyEndpoints(this WebApplication app)
    {
        // GET /api/projects/{projectId}/review-policies — list discovered policies + validation status.
        app.MapGet("/api/projects/{projectId}/review-policies", async (
            HttpContext httpContext,
            string projectId,
            IProjectStore projectStore,
            ReviewPolicyRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var set = registry.GetOrLoad(project!);
            return Results.Ok(BuildListResponse(project!, set));
        });

        // POST /api/projects/{projectId}/review-policies/sync — re-read from disk, refresh the set.
        app.MapPost("/api/projects/{projectId}/review-policies/sync", async (
            HttpContext httpContext,
            string projectId,
            IProjectStore projectStore,
            ReviewPolicyRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var set = registry.Sync(project!);
            return Results.Ok(BuildListResponse(project!, set));
        });

        // GET /api/projects/{projectId}/review-policies/{policyName} — single policy definition.
        app.MapGet("/api/projects/{projectId}/review-policies/{policyName}", async (
            HttpContext httpContext,
            string projectId,
            string policyName,
            IProjectStore projectStore,
            ReviewPolicyRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var result = registry.Get(project!, policyName);
            if (result?.Policy is null) return Results.NotFound();

            return Results.Ok(ReviewPolicyDtoMapper.ToDetail(result, ActiveName(project!)));
        });

        // PUT /api/projects/{projectId}/review-policies/active — select the project's active policy by
        // name (FR-027/033). A null/empty name clears the selection and reverts to the built-in default.
        app.MapPut("/api/projects/{projectId}/review-policies/active", async (
            HttpContext httpContext,
            string projectId,
            SetActiveReviewPolicyRequest request,
            IProjectStore projectStore,
            ReviewPolicyRegistry registry,
            CancellationToken ct) =>
        {
            var (project, error) = await ResolveOwnedProjectAsync(httpContext, projectId, projectStore, ct);
            if (error is not null) return error;

            var requested = string.IsNullOrWhiteSpace(request?.Name) ? null : request!.Name!.Trim();

            // A non-null selection must resolve to a known, valid policy (bind-by-name, FR-033).
            var set = registry.GetOrLoad(project!);
            if (requested is not null && set.FindByName(requested) is null)
                return Results.NotFound(new { error = $"No review policy named '{requested}' is available for this project." });

            await projectStore.UpdateActiveReviewPolicyAsync(project!.Id, requested, DateTimeOffset.UtcNow, ct);

            var updated = project! with { ActiveReviewPolicyName = requested };
            return Results.Ok(BuildListResponse(updated, set));
        });
    }

    private static ReviewPolicyListResponse BuildListResponse(Project project, ProjectReviewPolicySet set)
    {
        var active = ActiveName(project);
        return new ReviewPolicyListResponse
        {
            ActivePolicyName = active,
            Policies = set.Results.Select(r => ReviewPolicyDtoMapper.ToSummary(r, active)).ToList(),
        };
    }

    /// <summary>The project's effective active policy name: its configured selection (FR-027) or the
    /// built-in default when none is set.</summary>
    private static string ActiveName(Project project) =>
        string.IsNullOrWhiteSpace(project.ActiveReviewPolicyName)
            ? BuiltInReviewPolicies.DefaultPolicyName
            : project.ActiveReviewPolicyName!;

    /// <summary>Resolves the route project and enforces owner authorization. Returns the project on
    /// success, or an IResult (400/404/403) describing the failure.</summary>
    private static async Task<(Project? Project, IResult? Error)> ResolveOwnedProjectAsync(
        HttpContext httpContext, string projectId, IProjectStore projectStore, CancellationToken ct)
    {
        if (!ProjectId.TryParse(projectId, out var pid))
            return (null, Results.BadRequest(new { error = "Invalid project id." }));

        var project = await projectStore.GetAsync(pid, ct);
        if (project is null) return (null, Results.NotFound());

        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
        if (!caller.Owns(project.Owner))
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));

        return (project, null);
    }
}
