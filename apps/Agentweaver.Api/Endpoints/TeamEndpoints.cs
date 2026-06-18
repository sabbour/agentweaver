using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;

namespace Agentweaver.Api.Endpoints;

public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this WebApplication app)
    {
// GET /api/projects/{id}/team — get team
app.MapGet("/api/projects/{id}/team", async (
    string id,
    CastingService castingService,
    IProjectStore projectStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return Results.BadRequest(new { error = "Invalid project id." });

        var project = await projectStore.GetAsync(projectId, ct);
        if (project is null) return Results.NotFound();
        if (project.State == ProjectState.Deleting)
            return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });

        var reader = new SquadReader(project.WorkingDirectory);
        var layout = reader.DetectLayout();
        var team = reader.ReadTeam();

        if (team is null) return Results.NotFound();

        var members = team.Members.Select(m =>
        {
            var charterFile = Path.Combine(project.WorkingDirectory, m.CharterPath);
            DateTimeOffset? created = File.Exists(charterFile) ? new DateTimeOffset(File.GetCreationTimeUtc(charterFile), TimeSpan.Zero) : null;
            DateTimeOffset? updated = File.Exists(charterFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(charterFile), TimeSpan.Zero) : null;
            return CastingMappings.ToDto(m, created, updated);
        }).ToList();

        return Results.Ok(new TeamDto
        {
            ProjectName = team.ProjectName,
            Universe = team.Universe,
            Members = members,
            Layout = layout.HasConflict ? "conflict"
                : layout.HasCanonical ? "canonical"
                : layout.HasLegacy ? "legacy"
                : "absent",
            MigrationAvailable = layout.HasLegacy && !layout.HasCanonical,
        });
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to get team for project {ProjectId}", id);
        return Results.Problem("Failed to get team.", statusCode: 500);
    }
});

// GET /api/projects/{id}/team/members/{name}/charter — get charter
app.MapGet("/api/projects/{id}/team/members/{name}/charter", async (
    string id,
    string name,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var content = await castingService.GetCharterAsync(id, name, ct);
        if (content is null) return Results.NotFound();
        return Results.Ok(new CharterDto { MemberName = name, Content = content });
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to get charter for {Name} in project {ProjectId}", name, id);
        return Results.Problem("Failed to get charter.", statusCode: 500);
    }
});

// PUT /api/projects/{id}/team/members/{name}/charter — update charter
app.MapPut("/api/projects/{id}/team/members/{name}/charter", async (
    string id,
    string name,
    UpdateCharterRequest request,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "content is required." });

    if (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Scribe", "Ralph", "Rai" }.Contains(name))
        return Results.BadRequest(new { error = $"'{name}' is a built-in system agent. Its charter cannot be modified." });

    if (request.Content.Length > 50_000)
        return Results.BadRequest(new { error = "Charter content must be 50,000 characters or fewer." });

    try
    {
        await castingService.UpdateCharterAsync(id, name, request.Content, ct);
        return Results.Ok(new CharterDto { MemberName = name, Content = request.Content });
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to update charter for {Name} in project {ProjectId}", name, id);
        return Results.Problem("Failed to update charter.", statusCode: 500);
    }
});

// GET /api/projects/{id}/team/members/{name}/history — get agent history
app.MapGet("/api/projects/{id}/team/members/{name}/history", async (
    string id,
    string name,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var content = await castingService.GetHistoryAsync(id, name, ct);
        // Return empty content when history hasn't been written yet (no 404 — the member exists)
        return Results.Ok(new HistoryDto { MemberName = name, Content = content ?? "" });
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to get history for {Name} in project {ProjectId}", name, id);
        return Results.Problem("Failed to get history.", statusCode: 500);
    }
});

// POST /api/projects/{id}/team/members — add member
app.MapPost("/api/projects/{id}/team/members", async (
    string id,
    AddMemberRequest request,
    CastingService castingService,
    IProjectStore projectStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RoleId))
        return Results.BadRequest(new { error = "role_id is required." });

    try
    {
        var member = await castingService.AddMemberAsync(id, request.RoleId, request.CustomRoleTitle, request.ModelId, ct);
        DateTimeOffset? created = null;
        DateTimeOffset? updated = null;
        if (ProjectId.TryParse(id, out var addProjectId))
        {
            var project = await projectStore.GetAsync(addProjectId, ct);
            if (project is not null)
            {
                var charterFile = Path.Combine(project.WorkingDirectory, member.CharterPath);
                if (File.Exists(charterFile))
                {
                    created = new DateTimeOffset(File.GetCreationTimeUtc(charterFile), TimeSpan.Zero);
                    updated = new DateTimeOffset(File.GetLastWriteTimeUtc(charterFile), TimeSpan.Zero);
                }
            }
        }
        return Results.Ok(CastingMappings.ToDto(member, created, updated));
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to add member to project {ProjectId}", id);
        return Results.Problem("Failed to add member.", statusCode: 500);
    }
});

// DELETE /api/projects/{id}/team/members/{name} — retire member
app.MapDelete("/api/projects/{id}/team/members/{name}", async (
    string id,
    string name,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        await castingService.RetireMemberAsync(id, name, ct);
        return Results.NoContent();
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (MemberNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to retire member {Name} from project {ProjectId}", name, id);
        return Results.Problem("Failed to retire member.", statusCode: 500);
    }
});

// PATCH /api/projects/{id}/team/members/{name} — re-role member
app.MapMethods("/api/projects/{id}/team/members/{name}", ["PATCH"], async (
    string id,
    string name,
    ReroleRequest request,
    CastingService castingService,
    IProjectStore projectStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.NewRoleId))
        return Results.BadRequest(new { error = "new_role_id is required." });

    try
    {
        var member = await castingService.ReroleMemberAsync(id, name, request.NewRoleId, request.CustomRoleTitle, ct);
        DateTimeOffset? created = null;
        DateTimeOffset? updated = null;
        if (ProjectId.TryParse(id, out var reroleProjectId))
        {
            var project = await projectStore.GetAsync(reroleProjectId, ct);
            if (project is not null)
            {
                var charterFile = Path.Combine(project.WorkingDirectory, member.CharterPath);
                if (File.Exists(charterFile))
                {
                    created = new DateTimeOffset(File.GetCreationTimeUtc(charterFile), TimeSpan.Zero);
                    updated = new DateTimeOffset(File.GetLastWriteTimeUtc(charterFile), TimeSpan.Zero);
                }
            }
        }
        return Results.Ok(CastingMappings.ToDto(member, created, updated));
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (MemberNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to re-role member {Name} in project {ProjectId}", name, id);
        return Results.Problem("Failed to re-role member.", statusCode: 500);
    }
});

// GET /api/projects/{projectId}/team/sync
app.MapGet("/api/projects/{projectId}/team/sync", async (
    string projectId,
    CastingService castingService,
    CancellationToken ct) =>
{
    try
    {
        var status = await castingService.GetSyncStatusAsync(projectId, ct);
        return Results.Ok(new SyncStatusResponse
        {
            Changes = status.Changes.Select(c => new SyncChangeDto
            {
                Path = c.RelativePath,
                Kind = c.Kind.ToString().ToLowerInvariant()
            }).ToList(),
            ChangeSetHash = status.ChangeSetHash,
            NothingToSync = status.NothingToSync
        });
    }
    catch (ProjectNotFoundException) { return Results.NotFound(); }
    catch (ProjectUnavailableException) { return Results.Conflict(new { error = "Project unavailable.", code = "project_unavailable" }); }
    catch (Exception ex) when (ex.Message.Contains("not inside a git repository"))
    {
        return Results.BadRequest(new { error = "Project working directory is not a git repository." });
    }
});

// POST /api/projects/{projectId}/team/sync
app.MapPost("/api/projects/{projectId}/team/sync", async (
    string projectId,
    SyncCommitRequest request,
    CastingService castingService,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ExpectedChangeSetHash))
        return Results.BadRequest(new { error = "expected_change_set_hash is required." });

    try
    {
        var commitId = await castingService.CommitSyncAsync(
            projectId, request.ExpectedChangeSetHash, request.Message, ct);
        return Results.Ok(new { commit_id = commitId });
    }
    catch (ProjectNotFoundException) { return Results.NotFound(); }
    catch (ProjectUnavailableException) { return Results.Conflict(new { error = "Project unavailable.", code = "project_unavailable" }); }
    catch (SyncStateChangedException ex) { return Results.Conflict(new { error = ex.Message, code = "sync_state_changed" }); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Nothing to sync"))
    {
        return Results.BadRequest(new { error = "Nothing to sync." });
    }
});
    }
}
