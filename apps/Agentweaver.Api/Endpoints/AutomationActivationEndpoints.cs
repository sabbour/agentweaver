using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Owner-only control surface for a project's background schedule/event automation. Before this
/// existed, <see cref="AutomationActivationSnapshotService.ActivateAsync"/> had no caller anywhere
/// in the product, so no project could ever satisfy the active
/// <c>AutomationActivationRecord</c> that <c>WorkflowScheduleTriggerService</c> and
/// <c>WorkflowEventTriggerService</c> require to fire — these endpoints are that missing entry
/// point.
/// </summary>
public static class AutomationActivationEndpoints
{
    public static void MapAutomationActivationEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/automation/status — Owner-only, redacted activation status.
        app.MapGet("/api/projects/{id}/automation/status", async (
            HttpContext httpContext,
            string id,
            IProjectStore projectStore,
            AutomationActivationSnapshotService activationService,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveOwnedProjectAsync(
                httpContext, id, projectStore, configuration, ct).ConfigureAwait(false);
            if (failure is not null) return failure;

            var status = await activationService.GetStatusAsync(project!.Id, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                is_active = status.IsActive,
                model_provider_source = status.ModelProviderSource,
                activated_at = status.ActivatedAt,
            });
        })
            .WithName("GetProjectAutomationStatus")
            .WithTags("Projects", "Automation");

        // POST /api/projects/{id}/automation/activate — Owner-only. Resolves and fences the
        // project's exact live repository grant + model-provider authority (GitHub Copilot binding
        // or, when configured, the deployment-wide BYOK provider) so schedule/event triggers can fire.
        app.MapPost("/api/projects/{id}/automation/activate", async (
            HttpContext httpContext,
            string id,
            IProjectStore projectStore,
            AutomationActivationSnapshotService activationService,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveOwnedProjectAsync(
                httpContext, id, projectStore, configuration, ct).ConfigureAwait(false);
            if (failure is not null) return failure;

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var (outcome, activation) = await activationService.ActivateAsync(
                caller, httpContext.User, project!.Id, ct).ConfigureAwait(false);
            return outcome switch
            {
                AutomationActivationOutcome.Activated => Results.Ok(new
                {
                    is_active = true,
                    model_provider_source = activation!.ModelProviderSource == AutomationModelProviderSource.Byok
                        ? "byok"
                        : "github_copilot",
                }),
                AutomationActivationOutcome.HumanEntraSubjectRequired =>
                    Results.Json(new { error = "human_entra_subject_required" }, statusCode: StatusCodes.Status403Forbidden),
                AutomationActivationOutcome.ProjectOwnerRequired =>
                    Results.Json(new { error = "project_owner_required" }, statusCode: StatusCodes.Status403Forbidden),
                AutomationActivationOutcome.RepositoryGrantUnavailable => Results.Conflict(new
                {
                    error = "repository_grant_unavailable",
                    message = "No live repository authorization is available for this project's background work yet.",
                }),
                AutomationActivationOutcome.RepositoryGrantAmbiguous => Results.Conflict(new
                {
                    error = "repository_grant_ambiguous",
                    message = "More than one live repository authorization is available for this project; automation activation requires exactly one.",
                }),
                AutomationActivationOutcome.CopilotBindingUnavailable => Results.Conflict(new
                {
                    error = "copilot_binding_unavailable",
                    message = "No GitHub Copilot account (project, platform-default, or BYOK) is available for this project's background AI yet.",
                }),
                AutomationActivationOutcome.CopilotBindingAmbiguous => Results.Conflict(new
                {
                    error = "copilot_binding_ambiguous",
                    message = "More than one GitHub Copilot account is available for this project; automation activation requires exactly one.",
                }),
                _ => Results.Conflict(new
                {
                    error = "activation_conflict",
                    message = "Automation is already active for this project.",
                }),
            };
        })
            .WithName("ActivateProjectAutomation")
            .WithTags("Projects", "Automation");

        // POST /api/projects/{id}/automation/deactivate — Owner-only. Turns off schedule/event
        // triggers for this project without discarding any underlying repository grant or Copilot
        // binding, so a later re-activation only needs a fresh fence, not a whole new authorization.
        app.MapPost("/api/projects/{id}/automation/deactivate", async (
            HttpContext httpContext,
            string id,
            IProjectStore projectStore,
            AutomationActivationSnapshotService activationService,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveOwnedProjectAsync(
                httpContext, id, projectStore, configuration, ct).ConfigureAwait(false);
            if (failure is not null) return failure;

            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var outcome = await activationService.DeactivateAsync(
                caller, httpContext.User, project!.Id, ct).ConfigureAwait(false);
            return outcome switch
            {
                AutomationDeactivationOutcome.Deactivated => Results.Ok(new { is_active = false }),
                AutomationDeactivationOutcome.HumanEntraSubjectRequired =>
                    Results.Json(new { error = "human_entra_subject_required" }, statusCode: StatusCodes.Status403Forbidden),
                AutomationDeactivationOutcome.ProjectOwnerRequired =>
                    Results.Json(new { error = "project_owner_required" }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Conflict(new
                {
                    error = "not_active",
                    message = "Automation is not currently active for this project.",
                }),
            };
        })
            .WithName("DeactivateProjectAutomation")
            .WithTags("Projects", "Automation");
    }
}
