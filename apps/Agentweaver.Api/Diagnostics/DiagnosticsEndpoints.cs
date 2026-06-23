using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Maps the read-only diagnostics and health endpoints (FR-013, FR-016, FR-017).
/// Register with <c>app.MapDiagnosticsEndpoints()</c> in Program.cs after the other
/// endpoint registrations.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        // FR-013: Lightweight API-reachability probe. This path does NOT start with /api so
        // ApiKeyAuthMiddleware passes it through unauthenticated — the web status dot works
        // before the user has signed in and without exposing any sensitive data.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // /api/health: same reachability probe under the /api prefix for callers that already
        // hold an API key (e.g. post-auth web requests, integration tests). Requires API key
        // like all /api routes (ApiKeyAuthMiddleware).
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

        // FR-016: Global system diagnostics — real executed checks, no mocks.
        // Reachable from the MCP server at parity (FR-016a).
        app.MapGet("/api/diagnostics", async (
            DiagnosticsService service,
            CancellationToken ct) =>
        {
            var dto = await service.GetSystemDiagnosticsAsync(ct).ConfigureAwait(false);
            return Results.Ok(dto);
        });

        // FR-017: Coordinator heartbeat status with ring-buffer activity and automations catalog.
        // Reachable from the MCP server at parity (FR-017a).
        app.MapGet("/api/diagnostics/heartbeat", (DiagnosticsService service) =>
            Results.Ok(service.GetHeartbeatStatus()));

        // FR-016 (project scope): workspace, scaffold directories, and active workflow/policy checks
        // for a single project. Owner-authorized (same pattern as other project endpoints).
        app.MapGet("/api/projects/{id}/diagnostics", async (
            HttpContext httpContext,
            string id,
            DiagnosticsService service,
            IProjectStore projectStore,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound();

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (!caller.Owns(project.Owner)) return Results.Forbid();

            var dto = await service.GetProjectDiagnosticsAsync(project, ct).ConfigureAwait(false);
            return Results.Ok(dto);
        });
    }
}
