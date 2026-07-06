using System.IO.Compression;
using System.Text;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Project-scoped skill catalog endpoints (issues #51/#56): list/get/delete catalog skills, acquire
/// via connected-repo sync / repo import / file upload, and manage skill→agent assignments. The
/// service layer is shared with the MCP surface so both expose identical behavior (constitution IV).
/// </summary>
public static class SkillEndpoints
{
    public static void MapSkillEndpoints(this WebApplication app)
    {
        // GET /api/projects/{id}/skills — list catalog skills with assignments.
        app.MapGet("/api/projects/{id}/skills", async (
            HttpContext http, string id, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var (outcome, value) = await svc.ListAsync(projectId, caller, ct);
            return outcome == SkillOutcome.Ok ? Results.Ok(value) : Results.NotFound();
        });

        // GET /api/projects/{id}/skills/{skillId} — full skill (including instructions + resources).
        app.MapGet("/api/projects/{id}/skills/{skillId}", async (
            HttpContext http, string id, string skillId, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId) || !SkillId.TryParse(skillId, out var sid))
                return Results.BadRequest(new { error = "Invalid id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var (outcome, skill) = await svc.GetAsync(projectId, sid, caller, ct);
            return outcome == SkillOutcome.Ok ? Results.Ok(ToDetail(skill!)) : Results.NotFound();
        });

        // DELETE /api/projects/{id}/skills/{skillId} — remove a skill + its assignments.
        app.MapDelete("/api/projects/{id}/skills/{skillId}", async (
            HttpContext http, string id, string skillId, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId) || !SkillId.TryParse(skillId, out var sid))
                return Results.BadRequest(new { error = "Invalid id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var outcome = await svc.DeleteAsync(projectId, sid, caller, ct);
            return outcome == SkillOutcome.Ok ? Results.NoContent() : Results.NotFound();
        });

        // POST /api/projects/{id}/skills/sync — discover/sync skills from the connected repository.
        app.MapPost("/api/projects/{id}/skills/sync", async (
            HttpContext http, string id, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var result = await svc.SyncConnectedRepoAsync(projectId, caller, ct);
            return MapAcquisition(result);
        });

        // POST /api/projects/{id}/skills/import/preview — list candidate skills in a repo.
        app.MapPost("/api/projects/{id}/skills/import/preview", async (
            HttpContext http, string id, ImportPreviewRequest body, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var (outcome, error, candidates) = await svc.PreviewRepoCandidatesAsync(projectId, body.RepoUrl ?? "", caller, ct);
            return outcome switch
            {
                SkillOutcome.Ok => Results.Ok(new { candidates }),
                SkillOutcome.NotFound => Results.NotFound(),
                SkillOutcome.Invalid => Results.BadRequest(new { error }),
                _ => Results.UnprocessableEntity(new { error }),
            };
        });

        // POST /api/projects/{id}/skills/import — import selected skills from a repo.
        app.MapPost("/api/projects/{id}/skills/import", async (
            HttpContext http, string id, ImportRequest body, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var result = await svc.ImportFromRepoAsync(projectId, body.RepoUrl ?? "", body.Locations, caller, ct);
            return MapAcquisition(result);
        });

        // POST /api/projects/{id}/skills/upload — multipart upload of a skill file/folder/archive.
        app.MapPost("/api/projects/{id}/skills/upload", async (
            HttpContext http, string id, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data upload." });

            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var form = await http.Request.ReadFormAsync(ct);
            var files = new List<UploadedSkillFile>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                if (file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    ms.Position = 0;
                    ExpandZip(ms, files);
                }
                else
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = await reader.ReadToEndAsync(ct);
                    // Prefer an explicit relative path (folder upload) via a paired form field; else the file name.
                    var rel = form[$"path:{file.Name}"].FirstOrDefault()
                              ?? (string.IsNullOrEmpty(file.FileName) ? file.Name : file.FileName);
                    files.Add(new UploadedSkillFile(rel, content));
                }
            }

            var result = await svc.UploadFilesAsync(projectId, files, caller, ct);
            return MapAcquisition(result);
        }).DisableAntiforgery();

        // GET /api/projects/{id}/skills/assignments — all assignments in the project.
        app.MapGet("/api/projects/{id}/skills/assignments", async (
            HttpContext http, string id, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var (outcome, value) = await svc.ListAsync(projectId, caller, ct);
            if (outcome != SkillOutcome.Ok) return Results.NotFound();
            var assignments = value!
                .SelectMany(s => s.AssignedAgents.Select(a => new { skill_id = s.Id, skill_name = s.Name, agent_name = a }))
                .ToList();
            return Results.Ok(assignments);
        });

        // PUT /api/projects/{id}/skills/{skillId}/assignments/{agentName} — assign skill to agent.
        app.MapPut("/api/projects/{id}/skills/{skillId}/assignments/{agentName}", async (
            HttpContext http, string id, string skillId, string agentName, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId) || !SkillId.TryParse(skillId, out var sid))
                return Results.BadRequest(new { error = "Invalid id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var outcome = await svc.AssignAsync(projectId, sid, agentName, caller, ct);
            return outcome switch
            {
                SkillOutcome.Ok => Results.NoContent(),
                SkillOutcome.Invalid => Results.BadRequest(new { error = "Agent name is required." }),
                _ => Results.NotFound(),
            };
        });

        // DELETE /api/projects/{id}/skills/{skillId}/assignments/{agentName} — unassign.
        app.MapDelete("/api/projects/{id}/skills/{skillId}/assignments/{agentName}", async (
            HttpContext http, string id, string skillId, string agentName, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId) || !SkillId.TryParse(skillId, out var sid))
                return Results.BadRequest(new { error = "Invalid id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var outcome = await svc.UnassignAsync(projectId, sid, agentName, caller, ct);
            return outcome == SkillOutcome.Ok ? Results.NoContent() : Results.NotFound();
        });
    }

    private static IResult MapAcquisition(SkillAcquisitionResult result) => result.Outcome switch
    {
        SkillOutcome.Ok => Results.Ok(new { results = result.Results, marked_missing = result.MarkedMissing }),
        SkillOutcome.NotFound => Results.NotFound(),
        SkillOutcome.Invalid => Results.BadRequest(new { error = result.Error, results = result.Results }),
        SkillOutcome.SourceUnavailable => Results.UnprocessableEntity(new { error = result.Error }),
        _ => Results.StatusCode(500),
    };

    private static void ExpandZip(Stream zipStream, List<UploadedSkillFile> files)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            using var es = entry.Open();
            using var reader = new StreamReader(es, Encoding.UTF8);
            files.Add(new UploadedSkillFile(entry.FullName, reader.ReadToEnd()));
        }
    }

    private static object ToDetail(Skill s) => new
    {
        id = s.Id.ToString(),
        name = s.Name,
        description = s.Description,
        instructions = s.Instructions,
        resources = s.Resources.Select(r => new { relative_path = r.RelativePath, content = r.Content }),
        provenance = s.Provenance.ToApiString(),
        source_repository = s.SourceRepository,
        source_location = s.SourceLocation,
        status = s.Status.ToApiString(),
        content_hash = s.ContentHash,
        created_at = s.CreatedAt,
        updated_at = s.UpdatedAt,
    };

    public sealed record ImportPreviewRequest(string? RepoUrl);
    public sealed record ImportRequest(string? RepoUrl, IReadOnlyList<string>? Locations);
}
