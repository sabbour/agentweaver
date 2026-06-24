using Agentweaver.Api.Projects;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Project-level, read-only workspace browsing endpoints. They let the web Workspace page browse a
/// project's git repository at its base branch and switch to any active run's worktree branch, with
/// syntax-highlightable file content. These complement (and do not change) the per-run workspace
/// endpoints; the listing/content shapes are shared so the web client renders both the same way.
/// </summary>
public static class ProjectWorkspaceEndpoints
{
    public static void MapProjectWorkspaceEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/workspace/refs — list the browsable refs (base branch + active worktrees).
        app.MapGet("/api/projects/{id}/workspace/refs", async (
            HttpContext httpContext,
            string id,
            ProjectWorkspaceService service,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await service.ListRefsAsync(projectId, caller, ct);
            return result.Outcome switch
            {
                WorkspaceOutcome.Ok => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        // GET /api/projects/{id}/workspace?ref={branch} — flat file listing for a ref (default: base branch).
        app.MapGet("/api/projects/{id}/workspace", async (
            HttpContext httpContext,
            string id,
            ProjectWorkspaceService service,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var @ref = httpContext.Request.Query["ref"].FirstOrDefault();
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await service.ListWorkspaceAsync(projectId, caller, @ref, ct);
            return result.Outcome switch
            {
                WorkspaceOutcome.Ok => Results.Json(result.Nodes),
                _ => Results.NotFound(),
            };
        });

        // GET /api/projects/{id}/workspace/files/{**path}/content?ref={branch} — file content for a ref.
        app.MapGet("/api/projects/{id}/workspace/files/{**path}", async (
            HttpContext httpContext,
            string id,
            string path,
            ProjectWorkspaceService service,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            // Only the content sub-resource is served here; the listing lives at /workspace.
            const string contentSuffix = "/content";
            if (!path.EndsWith(contentSuffix, StringComparison.Ordinal))
                return Results.NotFound();
            var filePath = path[..^contentSuffix.Length];

            var @ref = httpContext.Request.Query["ref"].FirstOrDefault();
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await service.GetFileContentAsync(projectId, caller, filePath, @ref, ct);
            return result.Outcome switch
            {
                WorkspaceOutcome.Ok => Results.Json(result.Value),
                WorkspaceOutcome.InvalidPath => Results.BadRequest(new { error = "Invalid file path." }),
                _ => Results.NotFound(),
            };
        });
    }
}
