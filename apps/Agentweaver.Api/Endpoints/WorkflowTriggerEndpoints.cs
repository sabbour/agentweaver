using System.Text.Json.Serialization;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Entry point for an inbound event to fire event-triggered workflows (issue #53, first pass). NO
/// concrete event source (a GitHub webhook receiver, an issue/PR/comment listener, etc.) calls this
/// endpoint yet — it IS the trigger interface itself, callable directly today (e.g. for manual testing
/// or a bespoke integration) until a real event source is wired to call it automatically in a
/// follow-up. See <see cref="WorkflowEventTriggerService"/> for the firing logic.
/// </summary>
public static class WorkflowTriggerEndpoints
{
    public static void MapWorkflowTriggerEndpoints(this WebApplication app)
    {
        // POST /api/projects/{projectId}/workflow-events — fire an event trigger (issue #53).
        app.MapPost("/api/projects/{projectId}/workflow-events", async (
            HttpContext httpContext,
            string projectId,
            FireWorkflowEventRequest request,
            IProjectStore projectStore,
            WorkflowEventTriggerService triggerService,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(projectId, out var pid))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (string.IsNullOrWhiteSpace(request.EventName))
                return Results.BadRequest(new { error = "event_name is required." });

            var project = await projectStore.GetAsync(pid, ct);
            if (project is null) return Results.NotFound();

            var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid)
                return forbid;

            var fired = await triggerService.FireEventAsync(
                project, request.EventName!.Trim(), request.DedupeKey, ct);

            return Results.Ok(new FireWorkflowEventResponse { FiredWorkflowIds = fired });
        });
    }
}

/// <summary>Request body to fire an event trigger (issue #53).</summary>
public sealed record FireWorkflowEventRequest
{
    [JsonPropertyName("event_name")] public string? EventName { get; init; }

    /// <summary>Optional caller-supplied dedupe key (e.g. a webhook delivery id) so a retried
    /// delivery never double-fires. Omit when the caller has no natural dedupe key.</summary>
    [JsonPropertyName("dedupe_key")] public string? DedupeKey { get; init; }
}

/// <summary>Response body after firing an event trigger (issue #53): the ids of the workflows that
/// matched the event name and fired a Ready backlog task.</summary>
public sealed record FireWorkflowEventResponse
{
    [JsonPropertyName("fired_workflow_ids")] public required IReadOnlyList<string> FiredWorkflowIds { get; init; }
}
