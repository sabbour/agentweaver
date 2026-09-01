using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Api.Workflows;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
// POST /api/projects/{id}/github/copilot/authorizations — begin a project-pinned Copilot App bind.
app.MapPost("/api/projects/{id}/github/copilot/authorizations", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    if (await projectStore.GetAsync(projectId, ct).ConfigureAwait(false) is null)
        return Results.NotFound();

    var service = new ProjectCopilotBindingService(
        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
        persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var result = await service.BeginAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, projectId, ct).ConfigureAwait(false);
    if (result.Outcome != CopilotBindingOutcome.Success)
        return CopilotBindingFailure(result.Outcome);

    ProjectCopilotBindingService.SetCallbackCookie(httpContext, result.CallbackCookie!);
    return Results.Ok(new
    {
        authorization_url = result.AuthorizationUrl,
        transaction_id = result.TransactionId,
        expires_at = result.ExpiresAt,
    });
})
    .WithName("BeginProjectCopilotAuthorization")
    .WithTags("Projects", "GitHub Copilot")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description = "Begins an Owner-authorized, project-pinned Copilot App binding. The request has no caller-selected redirect URL.";
        return Task.CompletedTask;
    });

// MCP receives only this opaque browser handoff; the OAuth state and callback cookie stay in the API.
app.MapPost("/api/projects/{id}/github/copilot/authorizations/handoff", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    if (await projectStore.GetAsync(projectId, ct).ConfigureAwait(false) is null)
        return Results.NotFound();

    var service = new ProjectCopilotBindingService(
        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
        persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var result = await service.BeginMcpHandoffAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, projectId, ct).ConfigureAwait(false);
    return result.Outcome == CopilotBindingOutcome.Success
        ? Results.Ok(new
        {
            transaction_id = result.TransactionId,
            browser_url = result.BrowserUrl,
            expires_at = result.ExpiresAt,
        })
        : CopilotBindingFailure(result.Outcome);
})
    .WithName("BeginProjectCopilotAuthorizationMcpHandoff")
    .WithTags("Projects", "GitHub Copilot");

app.MapGet("/auth/github/copilot-app/handoff/{transactionId}", async (
    HttpContext httpContext,
    string transactionId,
    IConfiguration configuration,
    BrowserEntraSessionService browserSessions,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    // This browser route has no bearer header, so it validates the authenticated Entra browser
    // session before an opaque MCP transaction can mint a callback cookie.
    var browserSession = await browserSessions.GetCurrentAsync(httpContext, ct).ConfigureAwait(false);
    if (browserSession is null)
        return Results.Unauthorized();

    var service = new ProjectCopilotBindingService(
        configuration, persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var handoff = await service.TakeMcpBrowserHandoffAsync(
        transactionId, browserSession.Id, browserSession.EntraObjectId, ct).ConfigureAwait(false);
    if (handoff is null)
        return Results.NotFound();

    ProjectCopilotBindingService.SetCallbackCookie(httpContext, handoff.Value.CallbackCookie);
    return Results.Redirect(handoff.Value.AuthorizationUrl);
}).AllowAnonymous();

// This callback uses only the one-time cookie issued at the authenticated begin endpoint. It never
// accepts a project id or Entra subject from GitHub; both remain pinned in the transaction.
app.MapGet("/auth/github/copilot-app/callback", async (
    HttpContext httpContext,
    string? code,
    string? state,
    string? error,
    IConfiguration configuration,
    BrowserEntraSessionService browserSessions,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    var service = new ProjectCopilotBindingService(
        configuration, persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var cookie = ProjectCopilotBindingService.ReadCallbackCookie(httpContext);
    ProjectCopilotBindingService.ClearCallbackCookie(httpContext);
    var browserSession = await browserSessions.GetCurrentAsync(httpContext, ct).ConfigureAwait(false);
    var outcome = await service.CompleteBrowserCallbackAsync(
        browserSession?.Id,
        browserSession?.EntraObjectId,
        state, string.IsNullOrWhiteSpace(error) ? code : null, cookie, ct).ConfigureAwait(false);
    return Results.Redirect(await service.GetCallbackRedirectAsync(outcome, state, ct).ConfigureAwait(false));
}).AllowAnonymous();

// GET /api/projects/{id}/github/copilot/authorizations/{transactionId} — initiating-human-only poll.
app.MapGet("/api/projects/{id}/github/copilot/authorizations/{transactionId}", async (
    HttpContext httpContext,
    string id,
    string transactionId,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    var service = new ProjectCopilotBindingService(
        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
        persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var result = await service.PollAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, projectId, transactionId, ct).ConfigureAwait(false);
    return result.Outcome == CopilotBindingOutcome.Success
        ? Results.Ok(new { status = result.Status })
        : CopilotBindingFailure(result.Outcome);
})
    .WithName("PollProjectCopilotAuthorization")
    .WithTags("Projects", "GitHub Copilot");

// GET /api/projects/{id}/github/copilot/connection — Owner-only, redacted binding state.
app.MapGet("/api/projects/{id}/github/copilot/connection", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    if (await projectStore.GetAsync(projectId, ct).ConfigureAwait(false) is null)
        return Results.NotFound();

    var service = new ProjectCopilotBindingService(
        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
        persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var result = await service.GetConnectionAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, projectId, ct).ConfigureAwait(false);
    return result.Outcome == CopilotBindingOutcome.Success
        ? Results.Ok(new
        {
            status = result.Connected ? "connected" : "not_connected",
            github_login = result.GitHubLogin,
        })
        : CopilotBindingFailure(result.Outcome);
})
    .WithName("GetProjectCopilotConnection")
    .WithTags("Projects", "GitHub Copilot")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description = "Returns an Owner-only, redacted project Copilot App connection state and verified GitHub login. It never returns credentials, transactions, grants, or provider permissions.";
        return Task.CompletedTask;
    });

// DELETE /api/projects/{id}/github/copilot/binding — Owner or human platform-admin de-privileging path.
app.MapDelete("/api/projects/{id}/github/copilot/binding", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    GitHubConnectionsPersistenceStore persistence,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IProjectRoleAssignmentStore roleAssignments,
    CopilotAppRegistrationService registration,
    ILogger<ProjectCopilotBindingService> logger,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    if (await projectStore.GetAsync(projectId, ct).ConfigureAwait(false) is null)
        return Results.NotFound();
    var service = new ProjectCopilotBindingService(
        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
        persistence, secretStore, httpClientFactory, roleAssignments, registration, logger);
    var outcome = await service.DisconnectAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext), httpContext.User, projectId, ct).ConfigureAwait(false);
    return outcome == CopilotBindingOutcome.Success
        ? Results.NoContent()
        : CopilotBindingFailure(outcome);
})
    .WithName("DisconnectProjectCopilotBinding")
    .WithTags("Projects", "GitHub Copilot")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description = "Revokes the active project Copilot binding. A human project Owner or human platform administrator may disconnect it.";
        return Task.CompletedTask;
    });

// GET /api/projects/{id}/github/unattended-readiness — project-scoped, redacted status only.
app.MapGet("/api/projects/{id}/github/unattended-readiness", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    MemoryDbContext db,
    CopilotAppRegistrationService registration,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "authorization_transaction_invalid" });
    var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
    if (project is null)
        return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Owner, ct) is { } forbid)
        return forbid;

    var projectKey = projectId.ToString();
    var hasInstallation = await db.GitHubInstallations.AsNoTracking()
        .AnyAsync(x => x.ProjectId == projectKey &&
                       x.AppKind == GitHubAppKind.Repo &&
                       x.RevokedAt == null, ct).ConfigureAwait(false);
    var registrationState = await registration.ValidateAsync(ct).ConfigureAwait(false);
    if (registrationState != CopilotAppRegistrationState.Ready)
        return Results.Ok(CreateUnattendedReadiness(registrationState, hasInstallation));

    var hasBinding = await db.ProjectCopilotBindings.AsNoTracking()
        .AnyAsync(x => x.ProjectId == projectKey && x.Status == GitHubBindingStatus.Active, ct).ConfigureAwait(false);
    var hasRepositoryGrant = await db.GitHubRepositoryGrants.AsNoTracking()
        .AnyAsync(x => x.ProjectId == projectKey && x.RevokedAt == null, ct).ConfigureAwait(false);
    return Results.Ok(CreateUnattendedReadiness(
        hasBinding,
        hasInstallation,
        hasRepositoryGrant));
})
    .WithName("GetProjectUnattendedReadiness")
    .WithTags("Projects", "GitHub")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description = "Returns a redacted, read-only unattended automation readiness status. It never returns GitHub identities, repository details, installation identifiers, permissions, or credentials.";
        return Task.CompletedTask;
    });

// POST /api/projects — create blank or from GitHub
app.MapPost("/api/projects", CreateProjectAsync)
    .WithName("CreateProject")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??=
            "Creates a project from a blank workspace or GitHub repository, optionally applying a blueprint and generated workflow atomically.";
        return Task.CompletedTask;
    });

// GET /api/server/info — public server metadata (no auth required)
app.MapGet("/api/server/info", (IProjectWorkspaceProvider workspaceProvider, IConfiguration configuration) =>
{
    return Results.Ok(new
    {
        data_directory          = AppPaths.DataDirectory,
        workspace_auto_assigned = workspaceProvider.AutoAssignsPath,
        auth_mode               = "entra",
        auth_mode_label         = "Entra ID",
        auth_mode_recommended   = true,
    });
}).AllowAnonymous();

// GET /api/projects — list all projects (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects", ListProjectsAsync)
    .WithName("ListProjects")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Lists the authenticated caller's projects with pagination metadata.";
        return Task.CompletedTask;
    });

// GET /api/projects/{id} — get a single project
app.MapGet("/api/projects/{id}", GetProjectAsync)
    .WithName("GetProject")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Returns one project's current metadata, ownership, and model defaults.";
        return Task.CompletedTask;
    });

// GET /api/projects/{id}/access — current caller/project access snapshot for Entra mode UI.
app.MapGet("/api/projects/{id}/access", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    ProjectRoleAssignmentService roleAssignments,
    IProjectRoleAuthorizationService authorization,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
    if (project is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Viewer, ct) is { } forbid) return forbid;

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var effectiveRole = await authorization.GetEffectiveRoleAsync(caller, projectId, ct).ConfigureAwait(false);
    var assignments = await roleAssignments.ListAsync(projectId, ct).ConfigureAwait(false);
    var canManage = effectiveRole is { } role && role.Satisfies(ProjectRole.Owner);

    return Results.Ok(new
    {
        auth_mode = "entra",
        platform_roles = caller.PlatformRoles,
        platform_roles_source = "entra",
        current_user_project_role = effectiveRole?.ToApiString(),
        can_manage_role_assignments = canManage,
        can_manage_project_github_identity = canManage,
        project_role_assignments = assignments.Select(assignment => new
        {
            assignment_id = assignment.PrincipalId,
            principal_id = assignment.PrincipalId,
            display_name = (string?)null,
            email = (string?)null,
            role = assignment.Role.ToApiString(),
            scope = assignment.Scope,
        }),
        github_identity_override_login = (string?)null,
        effective_github_login = (string?)null,
        effective_github_permission = (string?)null,
        github_identity_permissions = Array.Empty<object>(),
    });
})
    .WithName("GetProjectAccessOverview")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Returns the caller's Entra/project access snapshot used by the project settings and identity UI.";
        return Task.CompletedTask;
    });

// GET /api/projects/{id}/role-assignments — list explicit Tier-2 project members.
app.MapGet("/api/projects/{id}/role-assignments", async (
    HttpContext httpContext,
    string id,
    IProjectStore projectStore,
    ProjectRoleAssignmentService roleAssignments,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Viewer, ct) is { } forbid) return forbid;

    var assignments = await roleAssignments.ListAsync(projectId, ct);
    return Results.Ok(assignments.Select(MapProjectRoleAssignment));
})
    .WithName("ListProjectRoleAssignments")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Lists the explicit Tier-2 project role assignments for an Entra-authorized project.";
        return Task.CompletedTask;
    });

// POST /api/projects/{id}/role-assignments — grant or update a Tier-2 project member role.
app.MapPost("/api/projects/{id}/role-assignments", async (
    HttpContext httpContext,
    string id,
    UpsertProjectRoleAssignmentRequest request,
    IProjectStore projectStore,
    ProjectRoleAssignmentService roleAssignments,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    if (string.IsNullOrWhiteSpace(request.PrincipalId))
        return Results.BadRequest(new { error = "principal_id is required." });
    if (!ProjectRoleExtensions.TryParse(request.Role, out var role))
        return Results.BadRequest(new { error = "role must be Owner, Contributor, or Viewer." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var result = await roleAssignments.UpsertAsync(
        projectId,
        request.PrincipalId.Trim(),
        role,
        caller.EntraObjectId ?? caller.User,
        ct);
    return result.Status switch
    {
        ProjectRoleAssignmentMutationStatus.Ok => Results.Ok(MapProjectRoleAssignment(result.Assignment!)),
        ProjectRoleAssignmentMutationStatus.LastOwnerConflict => Results.Conflict(new { error = result.Error }),
        _ => Results.NotFound(),
    };
})
    .WithName("UpsertProjectRoleAssignment")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Grants or updates a Tier-2 project role assignment. Only project Owners or platform admins may call this.";
        return Task.CompletedTask;
    });

// DELETE /api/projects/{id}/role-assignments/{principalId} — revoke a Tier-2 project member role.
app.MapDelete("/api/projects/{id}/role-assignments/{principalId}", async (
    HttpContext httpContext,
    string id,
    string principalId,
    IProjectStore projectStore,
    ProjectRoleAssignmentService roleAssignments,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    if (string.IsNullOrWhiteSpace(principalId))
        return Results.BadRequest(new { error = "principal_id is required." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    var result = await roleAssignments.RemoveAsync(projectId, principalId.Trim(), ct);
    return result.Status switch
    {
        ProjectRoleAssignmentMutationStatus.Ok => Results.NoContent(),
        ProjectRoleAssignmentMutationStatus.LastOwnerConflict => Results.Conflict(new { error = result.Error }),
        _ => Results.NotFound(),
    };
})
    .WithName("DeleteProjectRoleAssignment")
    .WithTags("Projects")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Revokes one explicit Tier-2 project role assignment. The last explicit Owner cannot be removed until another Owner is granted.";
        return Task.CompletedTask;
    });

// PATCH /api/projects/{id} — rename
app.MapMethods("/api/projects/{id}", ["PATCH"], async (
    HttpContext httpContext,
    string id,
    UpdateProjectNameRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "name is required." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    bool updated;
    try { updated = await projectService.RenameAsync(projectId, request.Name!, ct); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// PUT /api/projects/{id}/provider-settings — update provider defaults
app.MapPut("/api/projects/{id}/provider-settings", async (
    HttpContext httpContext,
    string id,
    UpdateProjectProviderSettingsRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    if (!IsAllowedModelId(request.DefaultModelGitHubCopilot) ||
        !IsAllowedModelId(request.DefaultModelMicrosoftFoundry) ||
        !IsAllowedModelId(request.BlueprintGenerationModel) ||
        !IsAllowedModelId(request.WorkflowGenerationModel) ||
        !IsAllowedModelId(request.OutcomeSpecGenerationModel))
        return Results.BadRequest(new { error = "model_id is not allowed." });

    bool updated;
    try
    {
        updated = await projectService.UpdateProviderSettingsAsync(
            projectId, request.DefaultProvider,
            request.DefaultModelGitHubCopilot,
            request.DefaultModelMicrosoftFoundry,
            request.BlueprintGenerationModel,
            request.WorkflowGenerationModel,
            request.OutcomeSpecGenerationModel,
            ct);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return updated ? Results.NoContent() : Results.NotFound();
});

// PUT /api/projects/{id}/preview-settings — update the project-scoped preview HITL window.
app.MapPut("/api/projects/{id}/preview-settings", async (
    HttpContext httpContext,
    string id,
    UpdateProjectPreviewSettingsRequest request,
    ProjectService projectService,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    if (request.ApprovalTimeoutMinutes is < 1 or > 1440)
        return Results.BadRequest(new { error = "approval_timeout_minutes must be between 1 and 1440." });

    var view = await projectService.GetViewAsync(projectId, ct);
    if (view is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    var updated = await projectService.UpdatePreviewApprovalTimeoutAsync(
        projectId, request.ApprovalTimeoutMinutes, ct);
    return updated
        ? Results.Ok(new ProjectPreviewSettingsResponse
        {
            ApprovalTimeoutMinutes = request.ApprovalTimeoutMinutes,
        })
        : Results.NotFound();
})
    .WithName("UpdateProjectPreviewSettings")
    .WithTags("Projects");

app.MapGet("/api/projects/{id}/github/repository-owners", async (
    HttpContext httpContext,
    string id,
    ProjectService projectService,
    GitHubRepositorySelectionBroker credentials,
    GitHubRepositorySelectionClient repositories,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var view = await projectService.GetViewAsync(projectId, ct).ConfigureAwait(false);
    if (view is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Owner, ct) is { } forbidden)
        return forbidden;

    var owners = await credentials.TryUseCredentialAsync(
        ApiKeyAuthMiddleware.GetCaller(httpContext),
        token => repositories.ListOwnersAsync(token, ct),
        ct).ConfigureAwait(false);
    return owners.Outcome switch
    {
        GitHubRepositorySelectionOutcome.Issued when owners.Value is not null => Results.Ok(
            owners.Value.Select(owner => new { login = owner.Login, type = owner.IsUser ? "user" : "org" })),
        GitHubRepositorySelectionOutcome.GitHubBindingUnavailable =>
            Results.Conflict(new { error = "github_binding_unavailable" }),
        _ => Results.Conflict(new { error = "github_capability_unavailable" }),
    };
})
    .WithName("ListProjectRepositoryOwners")
    .WithTags("Projects", "GitHub");

app.MapPost("/api/projects/{id}/github/repository", async (
    HttpContext httpContext,
    string id,
    CreateProjectRepositoryRequest request,
    ProjectService projectService,
    GitHubRepositorySelectionBroker credentials,
    GitHubRepositorySelectionClient repositories,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    if (string.IsNullOrWhiteSpace(request.Owner))
        return Results.BadRequest(new { error = "owner is required." });
    var view = await projectService.GetViewAsync(projectId, ct).ConfigureAwait(false);
    if (view is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Owner, ct) is { } forbidden)
        return forbidden;
    if (view.Project.Origin.Kind != ProjectOriginKind.Blank)
        return Results.Conflict(new { error = "project_repository_already_connected" });

    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var connected = await credentials.TryUseCredentialAsync(
        caller,
        async token =>
        {
            var repository = await repositories.CreateAsync(
                request.Owner.Trim(),
                string.IsNullOrWhiteSpace(request.Name) ? SlugifyRepositoryName(view.Project.Name) : request.Name.Trim(),
                request.Private ?? true,
                token,
                ct).ConfigureAwait(false);
            return repository is null
                ? null
                : await projectService.ConnectCreatedRepositoryAsync(
                    projectId, repository.FullName, repository.CloneUrl, token, ct).ConfigureAwait(false);
        },
        ct).ConfigureAwait(false);
    return connected.Outcome switch
    {
        GitHubRepositorySelectionOutcome.Issued when connected.Value is not null => Results.Ok(new
        {
            source_repository = connected.Value.Origin.SourceRepository,
            html_url = $"https://github.com/{connected.Value.Origin.SourceRepository}",
        }),
        GitHubRepositorySelectionOutcome.GitHubBindingUnavailable =>
            Results.Conflict(new { error = "github_binding_unavailable" }),
        _ => Results.Conflict(new { error = "github_repository_creation_unavailable" }),
    };
})
    .WithName("CreateProjectRepository")
    .WithTags("Projects", "GitHub");

// DELETE /api/projects/{id}?confirm=true — record-only delete
app.MapDelete("/api/projects/{id}", async (
    HttpContext httpContext,
    string id,
    ProjectService projectService,
    IRunStore runStore,
    RunWorkflowRegistry workflowRegistry,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var confirm = httpContext.Request.Query["confirm"].FirstOrDefault();
    if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "confirm=true query parameter is required for delete." });

    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var deleteView = await projectService.GetViewAsync(projectId, ct);
    if (deleteView is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, deleteView.Project, ProjectRole.Owner, ct) is { } forbid) return forbid;

    bool deleted;
    try
    {
        deleted = await projectService.DeleteAsync(projectId, runStore, workflowRegistry, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to delete project {ProjectId}", id);
        return Results.Problem("Failed to delete the project.", statusCode: 500);
    }
    return deleted ? Results.NoContent() : Results.NotFound();
});

// GET /api/projects/{id}/runs — list runs for a project (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/runs", async (
    HttpContext httpContext,
    string id,
    string? agent,
    bool? terminal_only,
    bool? include_children,
    int? limit,
    int? page,
    int? page_size,
    IProjectStore projectStore,
    IRunStore runStore,
    CoordinatorStatusReader coordinator,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });

    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Viewer, ct) is { } forbid) return forbid;

    var runs = await runStore.GetRunsByProjectAsync(projectId, includeChildren: include_children ?? false, ct: ct);
    if (!string.IsNullOrWhiteSpace(agent))
        runs = runs.Where(r => string.Equals(r.AgentName, agent, StringComparison.Ordinal)).ToList();
    if (terminal_only == true)
        runs = runs.Where(r => IsTerminalHistoryStatus(r.Status)).ToList();
    // Deterministic, newest-first order so pages are stable across requests.
    runs = runs.OrderByDescending(r => r.StartedAt).ToList();

    // Legacy `limit` param (pre-pagination) is honored as a page_size alias for one release so
    // existing callers that only pass `limit` keep getting a bounded, single-page result. New
    // callers should use `page`/`page_size` — see decisions/inbox/niobe-pagination-contract.md.
    var effectivePageSize = page_size ?? (limit is > 0 ? limit : null);

    // For coordinator runs, surface the work-plan orchestration status so the list can render
    // "Dispatching" / "Awaiting assembly" / "Failed: <reason>" instead of the bare run status.
    var coordinatorRunIds = runs
        .Where(r => r.ParentRunId is null && string.Equals(r.AgentName, "Coordinator", StringComparison.Ordinal))
        .Select(r => r.Id.ToString())
        .ToList();
    var coordinatorStatuses = await coordinator.GetCoordinatorStatusesAsync(coordinatorRunIds, ct);

    var summaries = runs.Select(r =>
    {
        var isCoordinator = r.ParentRunId is null && string.Equals(r.AgentName, "Coordinator", StringComparison.Ordinal);
        return new WorkflowRunSummary
        {
            WorkflowRunId = r.WorkflowRunId ?? r.Id.ToString(),
        ExecutionId   = r.Id.ToString(),
        Task          = r.Task,
        Status        = r.Status.ToApiString(),
        AgentName     = r.AgentName,
        ReviewedBy    = r.ReviewedBy,
        StartedAt     = r.StartedAt,
        EndedAt       = r.EndedAt,
        ModelId       = r.ModelId,
        Result        = r.Result,
        CoordinatorStatus = coordinatorStatuses.GetValueOrDefault(r.Id.ToString()),
        CoordinatorStatusReason = isCoordinator ? r.Result : null,
        ArchivedAt = r.ArchivedAt,
        };
    }).ToList();

    return Results.Ok(Paging.Of(summaries, page, effectivePageSize));
});

static bool IsTerminalHistoryStatus(RunStatus status) =>
    status is RunStatus.Completed or RunStatus.Merged or RunStatus.AssembleReady
        or RunStatus.Declined or RunStatus.Failed or RunStatus.MergeFailed;

// POST /api/projects/{id}/runs — deprecated direct run submission route
app.MapPost("/api/projects/{id}/runs", () => Results.Problem(
    title: "Single-run endpoint deprecated",
    detail: "Start work through POST /api/projects/{id}/orchestrations so the Coordinator can decompose, assemble, review, merge, and scribe.",
    statusCode: StatusCodes.Status410Gone));

// -----------------------------------------------------------------------
// Coordinator orchestration (Feature 008 Phase 1) — thin HTTP over CoordinatorRunService.
// The HTTP layer validates input, resolves owner-scoped context, and maps the service result
// to status codes. All orchestration lives behind CoordinatorRunService (Principle III).
// -----------------------------------------------------------------------

// POST /api/projects/{id}/orchestrations — start a coordinator run. Default/defineOutcome drafts a
// confirmable outcome spec and suspends at the confirmation gate; direct plans from the prompt.
// Body: { goal, start_mode?, modelId? }.
app.MapPost("/api/projects/{id}/orchestrations", StartOrchestrationAsync)
    .WithName("StartProjectOrchestration")
    .WithTags("Coordinator")
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Description ??= "Starts a coordinator run for the project using either defineOutcome or direct planning mode.";
        return Task.CompletedTask;
    });
    }

    /// <summary>
    /// Creates a project workspace, optionally cloning a GitHub repository and applying a blueprint before the first run.
    /// </summary>
    /// <param name="request">The project origin, workspace path, model defaults, and optional blueprint materialization payload.</param>
    /// <response code="201">Returns the created project, including any blueprint-derived defaults that were applied.</response>
    /// <response code="400">The request was malformed or the selected blueprint/workflow payload was invalid.</response>
    /// <response code="500">Project creation or rollback failed unexpectedly.</response>
    /// <response code="503">The target workspace root is unavailable.</response>
    /// <remarks>
    /// Persona-style drivers should prefer this route over manual file bootstrapping because it atomically creates the
    /// project, validates blueprint inputs, and rolls back on apply failures.
    /// </remarks>
    internal static async Task<IResult> CreateProjectAsync(
        HttpContext httpContext,
        CreateProjectRequest request,
        ProjectService projectService,
        BlueprintService blueprintService,
        IRunStore runStore,
        RunWorkflowRegistry workflowRegistry,
        IProjectStore projectStore,
        ProjectRoleAssignmentService roleAssignments,
        GitHubRepositorySelectionBroker repositorySelections,
        IConfiguration configuration,
        IProjectWorkspaceProvider workspaceProvider,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "name is required." });

        if (string.IsNullOrWhiteSpace(request.Origin) ||
            (request.Origin != "blank" && request.Origin != "github"))
            return Results.BadRequest(new { error = "origin must be 'blank' or 'github'." });

        if (request.Origin == "github" &&
            (string.IsNullOrWhiteSpace(request.RepositorySelectionCode) ||
             request.AdditionalProperties is { Count: > 0 }))
        {
            return Results.BadRequest(new
            {
                error = "repository_selection_code is required and GitHub repository metadata is not accepted."
            });
        }

        if (request.Origin == "github" &&
            HumanEntraSubjectAuthorization.Evaluate(caller, httpContext.User) != HumanEntraSubjectState.Allowed)
            return Results.Conflict(new { error = "human_entra_subject_required" });

        // working_directory is only mandatory when the active workspace provider cannot auto-assign
        // one (e.g. LocalFilesystemWorkspaceProvider). Providers that report AutoAssignsPath == true
        // (e.g. PersistentVolumeWorkspaceProvider) already derive the path deterministically from the
        // project id in ResolveWorkingDirectoryAsync and ignore any client-supplied value, so requiring
        // it here would force every client to leak server filesystem layout for no benefit (#333).
        if (!workspaceProvider.AutoAssignsPath && string.IsNullOrWhiteSpace(request.WorkingDirectory))
            return Results.BadRequest(new { error = "working_directory is required." });

        if (!string.IsNullOrWhiteSpace(request.BlueprintId) && request.Blueprint is not null)
            return Results.BadRequest(new { error = "Provide either blueprint_id or an inline blueprint, not both." });

        Agentweaver.Squad.Model.Blueprint? blueprintToApply = null;

        if (!string.IsNullOrWhiteSpace(request.BlueprintId))
        {
            blueprintToApply = blueprintService.GetPredefinedById(request.BlueprintId!);
            if (blueprintToApply is null)
                return Results.BadRequest(new { error = $"No predefined blueprint with id '{request.BlueprintId}'." });
        }
        else if (request.Blueprint is not null)
        {
            blueprintToApply = request.Blueprint.ToModel();
        }

        if (blueprintToApply is not null)
        {
            IReadOnlySet<string>? extraKnownWorkflowIds = null;
            if (!string.IsNullOrWhiteSpace(request.GeneratedWorkflowYaml))
            {
                var genWf = WorkflowDefinitionLoader.Load(request.GeneratedWorkflowYaml, "generated");
                if (genWf.IsValid && genWf.Definition is not null)
                    extraKnownWorkflowIds = new HashSet<string>([genWf.Definition.Id], StringComparer.Ordinal);
            }

            var validation = blueprintService.Validate(
                blueprintToApply,
                BlueprintService.ValidationProject(request.WorkingDirectory),
                extraKnownWorkflowIds);
            if (!validation.Valid)
                return Results.BadRequest(new { error = "invalid_blueprint", details = validation.Errors });
        }

        string? blueprintSourceId = null;
        string? blueprintSourceType = null;
        if (!string.IsNullOrWhiteSpace(request.BlueprintId))
        {
            blueprintSourceId = request.BlueprintId;
            blueprintSourceType = "predefined";
        }
        else if (request.Blueprint is not null)
        {
            blueprintSourceId = "inline";
            blueprintSourceType = "inline";
        }

        ResolvedGitHubRepositorySelection? resolvedRepository = null;
        if (request.Origin == "github")
        {
            resolvedRepository = await repositorySelections.TryConsumeAndResolveAsync(
                request.RepositorySelectionCode!, caller, ct).ConfigureAwait(false);
            if (resolvedRepository is null)
                return Results.Conflict(new { error = "github_repository_selection_unavailable" });
        }

        try
        {
            Agentweaver.Domain.Project project;
            // Pass through as-is when supplied; auto-assigning providers ignore this value entirely
            // (ResolveWorkingDirectoryAsync derives the path from the project id instead), and
            // LocalFilesystemWorkspaceProvider treats an empty/relative path as "assign one under the
            // configured workspace root" rather than throwing.
            var requestedWorkingDirectory = request.WorkingDirectory ?? string.Empty;

            if (request.Origin == "blank")
            {
                project = await projectService.CreateBlankAsync(
                    request.Name!, requestedWorkingDirectory,
                    request.DefaultProvider, request.DefaultModelGitHubCopilot,
                    request.DefaultModelMicrosoftFoundry, caller.User, ct);
            }
            else
            {
                project = await projectService.CreateFromGitHubAsync(
                    request.Name!, resolvedRepository!.SourceRepository, requestedWorkingDirectory,
                    request.DefaultProvider, request.DefaultModelGitHubCopilot,
                    request.DefaultModelMicrosoftFoundry, caller.User, resolvedRepository.AccessToken, ct);
            }

            if (!string.IsNullOrWhiteSpace(caller.EntraObjectId))
            {
                try
                {
                    await roleAssignments.SeedOwnerAsync(
                        project.Id,
                        caller.EntraObjectId!,
                        caller.EntraObjectId,
                        ct);
                }
                catch (Exception roleAssignmentEx)
                {
                    logger.LogError(roleAssignmentEx,
                        "Project RBAC bootstrap failed for project {ProjectId}; rolling back project creation",
                        project.Id);
                    await projectService.RollbackCreationAsync(project.Id, runStore, workflowRegistry, ct);
                    throw;
                }
            }

            if (blueprintToApply is not null)
            {
                try
                {
                    var applyResult = await blueprintService.ApplyAsync(
                        project.Id.ToString(), blueprintToApply,
                        request.GeneratedWorkflowYaml,
                        applySkillDefaults: !string.IsNullOrWhiteSpace(request.BlueprintId),
                        ct: ct);
                    if (!applyResult.Valid)
                    {
                        await projectService.RollbackCreationAsync(project.Id, runStore, workflowRegistry, ct);
                        return Results.BadRequest(new { error = "invalid_blueprint", details = applyResult.Errors });
                    }

                    var pid = ProjectId.Parse(project.Id.ToString());
                    await projectStore.UpdateSourceBlueprintAsync(
                        pid, blueprintSourceId, blueprintSourceType, DateTimeOffset.UtcNow, ct);
                }
                catch (Exception blueprintEx)
                {
                    logger.LogError(blueprintEx,
                        "Blueprint application failed for project {ProjectId}; rolling back project creation",
                        project.Id);
                    try
                    {
                        await projectService.RollbackCreationAsync(project.Id, runStore, workflowRegistry, ct);
                    }
                    catch (Exception rollbackEx)
                    {
                        logger.LogError(rollbackEx,
                            "Rollback delete failed for orphaned project {ProjectId}", project.Id);
                    }
                    throw;
                }

                var view = await projectService.GetViewAsync(project.Id, ct);
                if (view is not null)
                    return Results.Created(
                        $"/api/projects/{project.Id}",
                        await MapProjectAsync(httpContext, view.Project, view.Available, ct));
            }

            return Results.Created($"/api/projects/{project.Id}", await MapProjectAsync(httpContext, project, available: true, ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (WorkspaceUnavailableException ex)
        {
            return Results.Json(
                new { error = "workspace_unavailable", message = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create project");
            return Results.Problem(
                $"Failed to create the project. {ex.GetType().Name}: {ex.Message}",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Lists the caller's visible projects with paging metadata so a persona can discover where to work next.
    /// </summary>
    /// <param name="page">Optional one-based page number.</param>
    /// <param name="page_size">Optional page size.</param>
    /// <response code="200">Returns only the projects owned by the authenticated caller.</response>
    public static async Task<IResult> ListProjectsAsync(
        HttpContext httpContext,
        ProjectService projectService,
        IConfiguration configuration,
        IProjectRoleAuthorizationService projectRoles,
        int? page,
        int? page_size,
        CancellationToken ct)
    {
        var views = await projectService.ListViewsAsync(ct);
        List<ProjectResponse> projects;
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            if (projectRoles.IsPlatformAdmin(caller))
            {
                projects = views
                    .Select(v => MapProject(v.Project, v.Available, ProjectRole.Owner))
                    .ToList();
            }
            else
            {
                var visibleRoles = await projectRoles.ListExplicitRolesAsync(caller, ct).ConfigureAwait(false);
                projects = views
                    .Where(v => visibleRoles.ContainsKey(v.Project.Id))
                    .Select(v => MapProject(v.Project, v.Available, visibleRoles[v.Project.Id]))
                    .ToList();
            }
        }
        return Results.Ok(Paging.Of(projects, page, page_size));
    }

    /// <summary>
    /// Returns the current metadata and model defaults for one project.
    /// </summary>
    /// <param name="id">The project identifier returned by project creation or listing endpoints.</param>
    /// <response code="200">Returns the requested project.</response>
    /// <response code="400">The project id was malformed.</response>
    /// <response code="403">The caller does not own the project.</response>
    /// <response code="404">The project does not exist.</response>
    public static async Task<IResult> GetProjectAsync(
        HttpContext httpContext,
        string id,
        ProjectService projectService,
        CancellationToken ct)
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return Results.BadRequest(new { error = "Invalid project id." });

        var view = await projectService.GetViewAsync(projectId, ct);
        if (view is null) return Results.NotFound();
        if (await RequireProjectRoleAsync(httpContext, view.Project, ProjectRole.Viewer, ct) is { } forbid) return forbid;
        return Results.Ok(await MapProjectAsync(httpContext, view.Project, view.Available, ct));
    }

    /// <summary>
    /// Starts a coordinator run for a project so the system can plan, delegate, assemble, and review work.
    /// </summary>
    /// <param name="id">The project identifier that owns the new coordinator run.</param>
    /// <param name="request">The goal, start mode, model override, and autonomy flags for the orchestration.</param>
    /// <response code="201">Returns the new coordinator run id.</response>
    /// <response code="400">The project id, goal, start mode, or model override was invalid.</response>
    /// <response code="403">The caller does not own the project.</response>
    /// <response code="404">The project was not found.</response>
    /// <response code="409">The project is deleting or its workspace is unavailable.</response>
    /// <response code="422">The project has no dispatchable team roster.</response>
    /// <remarks>
    /// This is the main entry point a persona should use to kick off work. In <c>defineOutcome</c> mode the coordinator
    /// first drafts an outcome spec, while <c>direct</c> goes straight to planning from the goal prompt.
    /// </remarks>
    public static async Task<IResult> StartOrchestrationAsync(
        HttpContext httpContext,
        string id,
        StartOrchestrationRequest request,
        IProjectStore projectStore,
        IProjectWorkspaceProvider workspaceProvider,
        CoordinatorRunService coordinator,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return Results.BadRequest(new { error = "Invalid project id." });

        if (string.IsNullOrWhiteSpace(request.Goal))
            return Results.BadRequest(new { error = "goal is required." });

        if (!TryParseCoordinatorStartMode(request.StartMode ?? request.Mode, out var startMode))
            return Results.BadRequest(new { error = "start_mode must be 'defineOutcome' or 'direct'." });

        var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);

        var project = await projectStore.GetAsync(projectId, ct);
        if (project is null) return Results.NotFound();
        if (await RequireProjectRoleAsync(httpContext, project, ProjectRole.Contributor, ct) is { } forbid) return forbid;
        if (!IsAllowedModelId(request.ModelId))
            return Results.BadRequest(new { error = "model_id is not allowed." });

        if (project.State == ProjectState.Deleting)
            return Results.Conflict(new { error = "project_deleting", message = "The project is being deleted and cannot accept new runs." });

        if (!workspaceProvider.IsAvailable(project.WorkingDirectory))
            return Results.Conflict(new { error = "workspace_unavailable", message = "The project workspace is not available." });

        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? project.ProviderSettings.GitHubCopilotModel
            : request.ModelId;

        RunId runId;
        try
        {
            runId = await coordinator.StartCoordinatorRunAsync(
                projectId,
                request.Goal!,
                caller.User,
                project.WorkingDirectory,
                project.DefaultBranch,
                modelId,
                request.AutoApproveTools,
                request.Autopilot,
                ct,
                workflowOverrideId: request.WorkflowOverrideId,
                startMode: startMode,
                submittingUserDisplayName: CoordinatorEndpoints.CallerDisplayName(caller));
        }
        catch (NoTeamException)
        {
            return Results.Conflict(new { error = NoTeamException.ErrorCode, message = NoTeamException.DefaultMessage });
        }
        catch (InvalidTeamException ex)
        {
            logger.LogError(ex, "Failed to read dispatchable team roster for project {ProjectId}", projectId);
            return Results.UnprocessableEntity(new { error = InvalidTeamException.ErrorCode, message = InvalidTeamException.DefaultMessage });
        }
        catch (GitHubCopilotConnectionRequiredException ex)
        {
            return Results.Json(ex.Requirement, statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Created(
            $"/api/runs/{runId}",
            new StartOrchestrationResponse { RunId = runId.ToString() });
    }

    private static bool TryParseCoordinatorStartMode(string? raw, out CoordinatorStartMode mode)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "defineOutcome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "define_outcome", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "outcomeSpec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "outcome_spec", StringComparison.OrdinalIgnoreCase))
        {
            mode = CoordinatorStartMode.DefineOutcome;
            return true;
        }

        if (string.Equals(raw, "direct", StringComparison.OrdinalIgnoreCase))
        {
            mode = CoordinatorStartMode.Direct;
            return true;
        }

        mode = CoordinatorStartMode.DefineOutcome;
        return false;
    }

static ProjectResponse MapProject(Project p, bool available, ProjectRole? effectiveRole = null) => new()
{
    ProjectId = p.Id.ToString(),
    Name = p.Name,
    Origin = p.Origin.ToApiString(),
    SourceRepository = p.Origin.SourceRepository,
    WorkingDirectory = p.WorkingDirectory,
    DefaultBranch = p.DefaultBranch,
    Owner = p.Owner,
    DefaultProvider = p.ProviderSettings.DefaultProvider.ToApiString(),
    DefaultModelGitHubCopilot = p.ProviderSettings.GitHubCopilotModel,
    DefaultModelMicrosoftFoundry = p.ProviderSettings.MicrosoftFoundryModel,
    BlueprintGenerationModel = p.BlueprintGenerationModel,
    WorkflowGenerationModel = p.WorkflowGenerationModel,
    OutcomeSpecGenerationModel = p.OutcomeSpecGenerationModel,
    PreviewApprovalTimeoutMinutes = p.PreviewApprovalTimeoutMinutes,
    Available = available,
    State = p.State == ProjectState.Active ? "active" : "deleting",
    CreatedAt = p.CreatedAt,
    UpdatedAt = p.UpdatedAt,
    SourceBlueprintId = p.SourceBlueprintId,
    SourceBlueprintType = p.SourceBlueprintType,
    AllowedWorkflowIds = p.AllowedWorkflowIds,
    EffectiveRole = effectiveRole?.ToApiString(),
};

private static readonly Regex AgentNameSlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);
private static readonly Regex AllowedModelRegex = new("^(gpt|claude|o)[a-z0-9._-]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static async Task<ProjectResponse> MapProjectAsync(HttpContext httpContext, Project project, bool available, CancellationToken ct)
{
    var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
    var roles = httpContext.RequestServices.GetRequiredService<IProjectRoleAuthorizationService>();
    var effectiveRole = await roles.GetEffectiveRoleAsync(caller, project.Id, ct).ConfigureAwait(false);
    return MapProject(project, available, effectiveRole);
}

private static async Task<IResult?> RequireProjectRoleAsync(
    HttpContext httpContext,
    Project project,
    ProjectRole minimumRole,
    CancellationToken ct)
{
    var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
    return await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, minimumRole, ct).ConfigureAwait(false);
}

private static ProjectRoleAssignmentResponse MapProjectRoleAssignment(ProjectRoleAssignment assignment) => new()
{
    PrincipalId = assignment.PrincipalId,
    Role = assignment.Role.ToApiString(),
    Scope = assignment.Scope,
    GrantedBy = assignment.GrantedBy,
    GrantedAt = assignment.GrantedAt,
};

private static bool IsAllowedModelId(string? modelId) =>
    string.IsNullOrWhiteSpace(modelId) || AllowedModelRegex.IsMatch(modelId.Trim());

private static object CreateUnattendedReadiness(
    CopilotAppRegistrationState registrationState,
    bool repoAppInstallationConnected) => registrationState switch
{
    CopilotAppRegistrationState.ConfigurationUnavailable => new
    {
        status = "not_ready",
        reason_code = "copilot_app_not_configured",
        message = "The Copilot App is not configured.",
        repo_app_installation_connected = repoAppInstallationConnected,
    },
    CopilotAppRegistrationState.RepositoryPermissionsDetected => new
    {
        status = "not_ready",
        reason_code = "copilot_app_repository_permissions_detected",
        message = "The Copilot App has repository permissions and cannot be used for unattended work.",
        repo_app_installation_connected = repoAppInstallationConnected,
    },
    _ => new
    {
        status = "not_ready",
        reason_code = "copilot_app_registration_unavailable",
        message = "The Copilot App registration could not be verified.",
        repo_app_installation_connected = repoAppInstallationConnected,
    },
};

private static object CreateUnattendedReadiness(
    bool hasCopilotBinding,
    bool hasRepoAppInstallation,
    bool hasRepositoryGrant)
{
    if (!hasCopilotBinding)
        return new
        {
            status = "not_ready",
            reason_code = "copilot_binding_required",
            message = "Connect a project Copilot App identity before unattended work can run.",
            repo_app_installation_connected = hasRepoAppInstallation,
        };
    if (!hasRepoAppInstallation)
        return new
        {
            status = "not_ready",
            reason_code = "repo_app_installation_required",
            message = "Install the Repo App for this project before unattended work can run.",
            repo_app_installation_connected = false,
        };
    if (!hasRepositoryGrant)
        return new
        {
            status = "not_ready",
            reason_code = "repo_app_repository_grant_required",
            message = "The Repo App repository grant is unavailable for this project.",
            repo_app_installation_connected = true,
        };
    return new
    {
        status = "ready",
        reason_code = "ready",
        message = "This project is ready for unattended automation when activation consent is granted.",
        repo_app_installation_connected = true,
    };
}

private static IResult CopilotBindingFailure(CopilotBindingOutcome outcome)
{
    var statusCode = outcome is CopilotBindingOutcome.HumanEntraSubjectRequired or CopilotBindingOutcome.ProjectOwnerRequired
        ? StatusCodes.Status403Forbidden
        : StatusCodes.Status409Conflict;
    return Results.Json(new { error = ProjectCopilotBindingService.ToStateCode(outcome) }, statusCode: statusCode);
}

private static string SlugifyRepositoryName(string value)
{
    var slug = string.Concat(value.Trim().ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '-'))
        .Trim('-');
    while (slug.Contains("--", StringComparison.Ordinal))
        slug = slug.Replace("--", "-", StringComparison.Ordinal);
    return string.IsNullOrEmpty(slug) ? "project" : slug;
}
}
