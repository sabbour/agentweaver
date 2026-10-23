using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Projects.Config;

/// <summary>Strict JSON request for creating a project.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectRequest
{
    public required string Name { get; init; }
}

/// <summary>Strict JSON request for updating project metadata at an expected revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectRequest
{
    public required long ExpectedRevision { get; init; }
    public required string Name { get; init; }
    public required ProjectLifecycleState State { get; init; }
}

/// <summary>Strict JSON request for appending a validated project configuration revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectConfigurationRequest
{
    public required long ExpectedRevision { get; init; }
    public required ProjectConfiguration Configuration { get; init; }
}

/// <summary>Strict JSON request for appending platform runtime defaults at an expected revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePlatformRuntimeDefaultsRequest
{
    public required long ExpectedRevision { get; init; }
    public required PlatformRuntimeDefaults Defaults { get; init; }
}

/// <summary>
/// Maps authenticated project, configuration, casting, run-selection, platform-default, and optional private Broker routes.
/// </summary>
public static class ProjectConfigEndpoints
{
    /// <summary>
    /// Registers the Projects &amp; Config HTTP routes; handlers resolve current authority and translate domain errors to problem responses.
    /// </summary>
    /// <param name="app">The application route builder.</param>
    /// <param name="enableBrokerRunBinding">Whether to map the private native-TLS Broker proof endpoint.</param>
    /// <returns>The same route builder for endpoint composition.</returns>
    public static IEndpointRouteBuilder MapProjectConfigEndpoints(
        this IEndpointRouteBuilder app,
        bool enableBrokerRunBinding = false)
    {
        var authorization = app.MapGroup("/api/authorization").RequireAuthorization();
        authorization.MapGet("/context", async (
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                // This scoped write is a settings hint, not an authority to start a model or bypass run-time checks.
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Forbidden();
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(caller.ToEffectiveResponse());
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("GetAuthorizationContext");

        var castingCatalog = app.MapGroup("/api/casting").RequireAuthorization();
        castingCatalog.MapGet("/templates", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ProjectCastingCatalog.Templates);
        }).WithName("ListProjectCastingTemplates");

        var roleCatalog = app.MapGroup("/api/catalog").RequireAuthorization();
        roleCatalog.MapGet("/roles", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ProjectCastingCatalog.Roles);
        }).WithName("ListProjectCastingRoles");

        // Blueprint routes are not project-scoped: any signed-in user can browse and check blueprints.
        // Neither route changes state; applying a blueprint goes through the project casting proposal route.
        var blueprints = app.MapGroup("/api/blueprints").RequireAuthorization();
        blueprints.MapGet("/", (HttpContext context) =>
        {
            // Availability can change between releases, so clients must not cache the list.
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ProjectBlueprintCatalog.List());
        }).WithName("ListBlueprints");

        blueprints.MapPost("/validate", (ValidateBlueprintRequest request, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // A missing blueprint is a bad request; an invalid blueprint is a 200 with Valid = false and its errors.
            return request.Blueprint is null
                ? ToProblem(ProjectConfigException.Invalid("invalid_blueprint_request", "A blueprint is required."))
                : Results.Ok(ProjectBlueprintCatalog.Validate(request.Blueprint));
        }).WithName("ValidateBlueprint");

        var projects = app.MapGroup("/api/projects").RequireAuthorization();

        projects.MapPost("/", async (
            CreateProjectRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await service.CreateProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    request.Name,
                    cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/projects/{created.ProjectId}", created);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/", async (
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListProjectsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}", async (
            string projectId,
            HttpContext context,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "runId") ||
                    context.Request.Query["runId"].Count > 1)
                    throw ProjectConfigException.Forbidden();
                var runId = context.Request.Query["runId"].FirstOrDefault();
                var service = context.RequestServices.GetRequiredService<ProjectsConfigService>();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.GetProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    runId,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("GetProject");

        projects.MapPatch("/{projectId}", async (
            string projectId,
            UpdateProjectRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateProjectAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedRevision,
                    request.Name,
                    request.State,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/role-assignments", async (
            string projectId,
            HttpContext context,
            [FromServices] ProjectRoleAssignmentService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Invalid(
                        "invalid_request", "This operation does not accept query parameters.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.ListAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/role-assignments", async (
            string projectId,
            AssignProjectRoleRequest request,
            HttpContext context,
            [FromServices] ProjectRoleAssignmentService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Invalid(
                        "invalid_request", "This operation does not accept query parameters.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var assignment = await service.AssignAsync(caller, projectId, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{assignment.ProjectId}/role-assignments/{assignment.AssignmentId:D}",
                    assignment);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapDelete("/{projectId}/role-assignments/{assignmentId:guid}", async (
            string projectId,
            Guid assignmentId,
            HttpContext context,
            [FromServices] ProjectRoleAssignmentService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "expectedRevision") ||
                    context.Request.Query["expectedRevision"].Count != 1 ||
                    !long.TryParse(
                        context.Request.Query["expectedRevision"].ToString(),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var expectedRevision))
                    throw ProjectConfigException.Invalid(
                        "invalid_expected_revision", "One expectedRevision query parameter is required.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.RevokeAsync(
                    caller, projectId, assignmentId, expectedRevision, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/configuration", async (
            string projectId,
            long? revision,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetProjectConfigurationAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    revision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/configuration/choices", async (
            string projectId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Invalid(
                        "invalid_query", "This endpoint does not accept query parameters.");
                return Results.Ok(await service.GetProjectConfigurationChoicesAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/configuration/hosted-model-reasoning-catalog", async (
            string projectId,
            HostedModelReasoningCatalogPublicationV1 request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                // This scoped write is a settings hint, not an authority to start a model or bypass run-time checks.
                if (context.Request.Query.Count != 0)
                    throw ProjectConfigException.Invalid(
                        "invalid_query", "This endpoint does not accept query parameters.");
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                caller.RequireResourceBinding(projectId);
                await service.PublishHostedModelReasoningCatalogAsync(
                    caller, projectId, request, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("PublishHostedModelReasoningCatalog");

        projects.MapPut("/{projectId}/configuration", async (
            string projectId,
            UpdateProjectConfigurationRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateProjectConfigurationAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedRevision,
                    request.Configuration,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/casting/universes", async (
            string projectId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var universes = await service.ListCastingUniversesAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    cancellationToken).ConfigureAwait(false);
                // Return only supported names after the service has authorized this project context.
                return Results.Ok(new { universes });
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithName("ListProjectCastingUniverses");

        projects.MapPost("/{projectId}/casting/proposals", async (
            string projectId,
            CreateProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var proposal = await service.CreateCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedConfigurationRevision,
                    request.Draft,
                    cancellationToken).ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{projectId}/casting/proposals/{proposal.ProposalId}",
                    proposal);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/casting/proposals/scenario", async (
            string projectId,
            CreateScenarioProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var proposal = await service.CreateScenarioCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedConfigurationRevision,
                    request.TemplateId,
                    cancellationToken,
                    request.Universe).ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{projectId}/casting/proposals/{proposal.ProposalId}",
                    proposal);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/casting/proposals/manual", async (
            string projectId,
            CreateManualProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var proposal = await service.CreateManualCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedConfigurationRevision,
                    request.RoleIds,
                    cancellationToken,
                    request.Universe).ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{projectId}/casting/proposals/{proposal.ProposalId}",
                    proposal);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/casting/proposals/blueprint", async (
            string projectId,
            CreateBlueprintProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                // The service checks casting write permission and the expected revision; this only creates a
                // pending proposal, and nothing changes until it is confirmed.
                var result = await service.CreateBlueprintCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request.ExpectedConfigurationRevision,
                    request.BlueprintId,
                    request.Blueprint,
                    cancellationToken).ConfigureAwait(false);
                // Point Location at the new proposal; the body also carries the unresolved skill bindings.
                return Results.Created(
                    $"/api/projects/{projectId}/casting/proposals/{result.Proposal.ProposalId}",
                    result);
            }
            catch (ProjectConfigException exception)
            {
                // Map known service failures to their problem responses.
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/casting/proposals", async (
            string projectId,
            int? limit,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "limit") ||
                    context.Request.Query["limit"].Count > 1)
                    throw ProjectConfigException.Forbidden();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.ListCastingProposalsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    limit ?? 50,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/casting/proposals/{proposalId:guid}", async (
            string projectId,
            Guid proposalId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.GetCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    proposalId,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPatch("/{projectId}/casting/proposals/{proposalId:guid}", async (
            string projectId,
            Guid proposalId,
            UpdateProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.UpdateCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    proposalId,
                    request.ExpectedDraftRevision,
                    request.Draft,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/casting/proposals/{proposalId:guid}/confirm", async (
            string projectId,
            Guid proposalId,
            TransitionProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.ConfirmCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    proposalId,
                    request.ExpectedDraftRevision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPost("/{projectId}/casting/proposals/{proposalId:guid}/reject", async (
            string projectId,
            Guid proposalId,
            TransitionProjectCastingProposalRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.RejectCastingProposalAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    proposalId,
                    request.ExpectedDraftRevision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapGet("/{projectId}/casting/export", async (
            string projectId,
            long? revision,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (context.Request.Query.Keys.Any(key => key != "revision") ||
                    context.Request.Query["revision"].Count > 1)
                    throw ProjectConfigException.Forbidden();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.ExportCastingTransferAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    revision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        // Match the public Gateway bound before binding a transfer payload.
        projects.MapPost("/{projectId}/casting/import", async (
            string projectId,
            ImportProjectCastingTransferRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var proposal = await service.ImportCastingTransferAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId,
                    request,
                    cancellationToken).ConfigureAwait(false);
                return Results.Created(
                    $"/api/projects/{projectId}/casting/proposals/{proposal.ProposalId}",
                    proposal);
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(1024 * 1024));

        // Validates an AgentSuite archive and stages its agents as one casting proposal. The 24 MiB request
        // limit fits a 16 MiB archive after base64 encoding (about 21.3 MiB) plus the JSON fields around it.
        projects.MapPost("/{projectId}/agentsuite-imports", async (
            string projectId,
            ImportProjectAgentSuiteRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                // Import results include proposal data, so proxies and browsers must not cache them.
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                // The object store is optional in this service. Without it there is nowhere to keep blobs: 503.
                var store = context.RequestServices.GetService<IObjectStore>()
                    ?? throw AgentSuiteStoreUnavailable();
                var result = await service.ImportAgentSuiteAsync(
                    caller, projectId, request, store, cancellationToken).ConfigureAwait(false);
                // No proposal means every agent already exists unchanged: 200. Otherwise 201 with the proposal URL.
                return result.Proposal is null
                    ? Results.Ok(result)
                    : Results.Created(
                        $"/api/projects/{projectId}/casting/proposals/{result.Proposal.ProposalId}",
                        result);
            }
            catch (AgentSuiteImportRejectedException exception)
            {
                // Invalid archive: 422 with every validation error line, so the author can fix them all at once.
                return Results.Problem(
                    title: "agentsuite_invalid",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = exception.Code,
                        ["errors"] = exception.Errors,
                    });
            }
            catch (ProjectConfigException exception)
            {
                // Authorization, stale revision, conflict and storage errors keep their own status code.
                return ToProblem(exception);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(24 * 1024 * 1024));

        // Read-only: returns the stored import record. The service checks the caller's read role.
        projects.MapGet("/{projectId}/agentsuite-imports/{sourceIdentity}", async (
            string projectId,
            string sourceIdentity,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var store = context.RequestServices.GetService<IObjectStore>()
                    ?? throw AgentSuiteStoreUnavailable();
                return Results.Ok(await service.GetAgentSuiteImportAsync(
                    caller, projectId, sourceIdentity, store, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        // Read-only: returns the policy bound to one imported agent. Reading it never enforces it.
        projects.MapGet("/{projectId}/agentsuite-agents/{effectiveAgentId}/policy", async (
            string projectId,
            string effectiveAgentId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                var store = context.RequestServices.GetService<IObjectStore>()
                    ?? throw AgentSuiteStoreUnavailable();
                return Results.Ok(await service.GetAgentSuiteAgentPolicyAsync(
                    caller, projectId, effectiveAgentId, store, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        // This owner-only helper applies the existing Orchestrator role gate without exposing platform defaults.
        projects.MapPut("/{projectId}/runs/{runId}/interactive-selection", async (
            string projectId,
            string runId,
            InteractiveRunSelectionRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.AcceptInteractiveRunSelectionAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    projectId, runId, request, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        projects.MapPut("/{projectId}/runs/{runId}/selection", async (
            string projectId,
            string runId,
            AcceptRunSelectionRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.AcceptRunSelectionAsync(
                    caller, projectId, runId, request, cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        // Cancellation reads use fresh project-access roles without granting Orchestrator admission.
        foreach (var forCancellation in new[] { false, true })
            projects.MapGet(forCancellation
                ? "/{projectId}/runs/{runId}/cancellation-selection"
                : "/{projectId}/runs/{runId}/selection", async (
                string projectId,
                string runId,
                HttpContext context,
                ProjectsConfigService service,
                ProjectAuthorizationOwner authorizationOwner,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    context.Response.Headers.CacheControl = "no-store";
                    var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(await service.GetRunSelectionAsync(
                        caller, projectId, runId, cancellationToken, forCancellation).ConfigureAwait(false));
                }
                catch (ProjectConfigException exception)
                {
                    return ToProblem(exception);
                }
            });

        // Returns the current project-scope spend budgets for one accepted run. Orchestrator calls this before
        // each new dispatch.
        projects.MapGet("/{projectId}/runs/{runId}/spend-budgets", async (
            string projectId,
            string runId,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                // Budgets can change at any time, so no cache may keep an older answer.
                context.Response.Headers.CacheControl = "no-store";
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(await service.GetCurrentProjectSpendBudgetsAsync(
                    caller, projectId, runId, cancellationToken).ConfigureAwait(false));
            }
            // Map known configuration errors, such as 403, 404, and 409, to problem responses.
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        var platform = app.MapGroup("/api/platform/runtime-defaults").RequireAuthorization();
        platform.MapGet("/", async (
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetPlatformRuntimeDefaultsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });
        platform.MapPut("/", async (
            UpdatePlatformRuntimeDefaultsRequest request,
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdatePlatformRuntimeDefaultsAsync(
                    await ResolveCallerAsync(context, authorizationOwner, cancellationToken).ConfigureAwait(false),
                    request.ExpectedRevision,
                    request.Defaults,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        });

        // Reports active platform components for TenantAdmin or PlatformAdmin callers.
        app.MapGet("/api/platform/components", async (
            HttpContext context,
            ProjectsConfigService service,
            ProjectAuthorizationOwner authorizationOwner,
            [Microsoft.AspNetCore.Mvc.FromServices] PlatformComponentCollector collector,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var caller = await ResolveCallerAsync(context, authorizationOwner, cancellationToken)
                    .ConfigureAwait(false);
                await service.EnsurePlatformComponentReaderAsync(caller, cancellationToken).ConfigureAwait(false);
                return Results.Ok(await collector.CollectAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (ProjectConfigException exception)
            {
                return ToProblem(exception);
            }
        }).RequireAuthorization();

        if (enableBrokerRunBinding)
        {
            app.MapPost(
                "/internal/projects/{projectId}/runs/{runId}/broker-run-binding",
                async (
                    string projectId,
                    string runId,
                    HttpContext context,
                    ProjectAuthorizationOwner authorizationOwner,
                    ProjectsConfigService service,
                    CancellationToken cancellationToken) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    try
                    {
                        if (!BrokerRunBindingTlsIdentity.IsNativeHttps(context) ||
                            context.Request.Query.Count != 0 ||
                            context.Request.ContentLength is > 0 ||
                            context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                            context.Request.ContentType is not null)
                            throw ProjectConfigException.Forbidden();
                        var caller = await authorizationOwner.ResolveBrokerRunBindingAsync(
                            context.User, projectId, runId, cancellationToken).ConfigureAwait(false);
                        return Results.Ok(await service.GetBrokerRunBindingProofAsync(
                            caller, projectId, runId, cancellationToken).ConfigureAwait(false));
                    }
                    catch (ProjectConfigException exception)
                    {
                        return ToProblem(exception);
                    }
                })
                .RequireAuthorization(ProjectAuthorizationOwner.BrokerRunBindingAuthorizationPolicy)
                .WithName("GetBrokerRunBindingProof");
        }

        return app;
    }

    /// <summary>Converts an owner error to a problem response while preserving its stable code and HTTP status.</summary>
    private static IResult ToProblem(ProjectConfigException exception) =>
        Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });

    // Retained AgentSuite blobs need the optional object store (ProjectsConfig:SkillContent:ContainerUri).
    // 503 tells the caller this is a deployment setting, not a problem with the request.
    private static ProjectConfigException AgentSuiteStoreUnavailable() =>
        new("agentsuite_store_unavailable",
            "AgentSuite import needs object storage. Configure ProjectsConfig:SkillContent:ContainerUri.",
            StatusCodes.Status503ServiceUnavailable);

    /// <summary>Resolves the bearer principal and optional tenant selector through the Projects authority owner.</summary>
    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        CancellationToken cancellationToken) =>
        authorizationOwner.ResolveAsync(
            context.User,
            context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader].ToArray(),
            cancellationToken);
}
