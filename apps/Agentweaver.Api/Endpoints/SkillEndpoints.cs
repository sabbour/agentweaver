using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        app.MapPost("/api/projects/{id}/skill-defaults/preview", async (
            HttpContext http, string id, SkillDefaultsPreviewRequest body,
            BlueprintService blueprints, SkillDefaultsService defaults,
            IProjectStore projects, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (string.IsNullOrWhiteSpace(body.BlueprintId))
                return Results.BadRequest(new { error = "blueprint_id is required." });

            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var project = await projects.GetAsync(projectId, ct);
            if (project is null) return Results.NotFound();
            if (await RequireProjectRoleAsync(http, project, ProjectRole.Viewer, ct) is { } forbid) return forbid;
            var blueprint = blueprints.GetPredefinedById(body.BlueprintId);
            if (blueprint is null) return Results.BadRequest(new { error = "Unknown predefined blueprint." });

            var preview = await defaults.PreviewAsync(projectId, blueprint, ct);
            return preview.CanApply
                ? Results.Ok(preview)
                : Results.UnprocessableEntity(preview);
        })
        .WithName("PreviewSkillDefaults")
        .WithTags("Skills")
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Description = "Previews explicit bundled skill defaults for a confirmed project team without writing catalog state.";
            return Task.CompletedTask;
        });

        app.MapPost("/api/projects/{id}/skill-defaults/apply", async (
            HttpContext http, string id, SkillDefaultsApplyRequest body,
            BlueprintService blueprints, SkillDefaultsService defaults,
            IProjectStore projects, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            if (string.IsNullOrWhiteSpace(body.BlueprintId) || string.IsNullOrWhiteSpace(body.Digest))
                return Results.BadRequest(new { error = "blueprint_id and digest are required." });

            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var project = await projects.GetAsync(projectId, ct);
            if (project is null) return Results.NotFound();
            if (await RequireProjectRoleAsync(http, project, ProjectRole.Contributor, ct) is { } forbid) return forbid;
            var blueprint = blueprints.GetPredefinedById(body.BlueprintId);
            if (blueprint is null) return Results.BadRequest(new { error = "Unknown predefined blueprint." });

            var result = await defaults.ApplyAsync(projectId, blueprint, body.Digest, ct);
            return result.Outcome switch
            {
                "applied" => Results.Ok(result),
                "stale" => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
                _ => Results.UnprocessableEntity(result),
            };
        })
        .WithName("ApplySkillDefaults")
        .WithTags("Skills")
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Description = "Atomically applies a matching skill-defaults preview. Stale or incomplete previews are rejected.";
            return Task.CompletedTask;
        });

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

        // POST /api/projects/{id}/skills — create or update a manual skill from form fields.
        app.MapPost("/api/projects/{id}/skills", async (
            HttpContext http, string id, CreateSkillRequest body, SkillCatalogService svc, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var result = await svc.CreateManualSkillAsync(
                projectId,
                new CreateSkillRequestDto(body.Name ?? "", body.DisplayName, body.Description, body.Instructions ?? ""),
                caller,
                ct);
            return MapAcquisition(result);
        });

        // POST /api/projects/{id}/skills/generate — generate an unsaved SKILL.md draft server-side.
        app.MapPost("/api/projects/{id}/skills/generate", async (
            HttpContext http, string id, GenerateSkillRequest body, SkillCatalogService svc, ISkillGenerator generator, CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var (outcome, _) = await svc.ListAsync(projectId, caller, ct);
            if (outcome == SkillOutcome.NotFound)
                return Results.NotFound();
            if (body is null || string.IsNullOrWhiteSpace(body.Description ?? body.Prompt))
                return Results.BadRequest(new { error = "description is required." });
            try
            {
                var draft = await generator.GenerateAsync(
                    (body.Description ?? body.Prompt)!,
                    caller.User,
                    ct,
                    projectId: projectId.ToString());
                return Results.Ok(draft);
            }
            catch (SkillGenerationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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
        app.MapGet("/api/skill-marketplaces", (SkillMarketplaceRegistry marketplaces) =>
            Results.Ok(marketplaces.ListEnabled().Select(m => new { name = m.Name, repository = m.Repository, subpath = m.Subpath, layout_note = m.LayoutNote })))
            .WithName("ListSkillMarketplaces").WithTags("Skills");

        // GET /api/projects/{id}/skill-marketplaces — config definitions + this project's URL sources.
        app.MapGet("/api/projects/{id}/skill-marketplaces", async (
            HttpContext http, string id, IProjectStore projects, IConfiguration configuration, MarketplaceSourceService sources, CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveProjectAsync(http, id, projects, configuration, ProjectRole.Viewer, ct);
            if (failure is not null) return failure;
            var list = await sources.ListForProjectAsync(project!.Id, ct);
            return Results.Ok(list.Select(m => new
            {
                name = m.Name,
                repository = $"{m.Owner}/{m.Repo}",
                branch = m.Branch,
                subpath = m.Subpath,
                auto_detect = m.IsAuto,
                parse_strategy = m.ParseStrategy,
                project_source = m.IsProjectSource,
            }));
        }).WithName("ListProjectSkillMarketplaces").WithTags("Skills");

        // POST /api/projects/{id}/skill-marketplaces/sources — add a marketplace source by repo URL.
        app.MapPost("/api/projects/{id}/skill-marketplaces/sources", async (
            HttpContext http, string id, AddMarketplaceSourceRequest body, IProjectStore projects, IConfiguration configuration, MarketplaceSourceService sources, CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveProjectAsync(http, id, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            if (body is null || string.IsNullOrWhiteSpace(body.Repository))
                return Results.BadRequest(new { error = "A GitHub repository URL or owner/repo is required." });
            var result = await sources.AddSourceAsync(
                project!.Id, body.Repository, body.Name, body.Branch, body.Subpath, body.ParseStrategy, ct);
            return result.Outcome switch
            {
                AddSourceOutcome.Ok => Results.Created(
                    $"/api/projects/{id}/skill-marketplaces/{Uri.EscapeDataString(result.Source!.Name)}",
                    new
                    {
                        name = result.Source!.Name,
                        repository = $"{result.Source.Owner}/{result.Source.Repo}",
                        branch = result.Source.Branch,
                        subpath = result.Source.Subpath,
                        auto_detect = result.Source.IsAuto,
                        parse_strategy = result.Source.ParseStrategy,
                        project_source = true,
                    }),
                AddSourceOutcome.NotFound => Results.NotFound(),
                AddSourceOutcome.Conflict => Results.Conflict(new { error = result.Error }),
                AddSourceOutcome.NotPublic => Results.UnprocessableEntity(new { error = result.Error }),
                AddSourceOutcome.Unavailable => Results.UnprocessableEntity(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error }),
            };
        }).WithName("AddProjectSkillMarketplaceSource").WithTags("Skills");

        // DELETE /api/projects/{id}/skill-marketplaces/sources/{name} — remove a project source.
        app.MapDelete("/api/projects/{id}/skill-marketplaces/sources/{name}", async (
            HttpContext http, string id, string name, IProjectStore projects, IConfiguration configuration, MarketplaceSourceService sources, CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveProjectAsync(http, id, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var outcome = await sources.RemoveSourceAsync(project!.Id, name, ct);
            return outcome == AddSourceOutcome.Ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RemoveProjectSkillMarketplaceSource").WithTags("Skills");

        app.MapPost("/api/projects/{id}/skill-marketplaces/{marketplace}/browse", async (
            HttpContext http, string id, string marketplace, MarketplaceBrowseRequest body, IProjectStore projects, IConfiguration configuration, MarketplaceSourceService sources, SkillCatalogService svc, CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveProjectAsync(http, id, projects, configuration, ProjectRole.Viewer, ct);
            if (failure is not null) return failure;
            var projectId = project!.Id;
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var source = await sources.ResolveAsync(projectId, marketplace, ct);
            if (source is null) return Results.NotFound();
            var page = body?.Page ?? 1;
            var pageSize = body?.PageSize ?? SkillCatalogService.DefaultMarketplacePageSize;

            // A URL source with no configured subpath auto-detects its layout (heuristic + LLM fallback);
            // config definitions keep the existing hardcoded-subpath browse path unchanged.
            var (outcome, error, result) = source.IsAuto
                ? await svc.BrowseMarketplaceAutoAsync(projectId, source.Owner, source.Repo, source.Branch, body?.Query, page, pageSize, caller, ct, source.ParseStrategy)
                : await svc.BrowseMarketplaceAsync(projectId, source.Owner, source.Repo, source.Branch, source.Subpath!, body?.Query, page, pageSize, caller, ct);
            if (outcome != SkillOutcome.Ok) return outcome == SkillOutcome.NotFound ? Results.NotFound() : Results.UnprocessableEntity(new { error });
            return Results.Ok(new
            {
                marketplace = source.Name,
                candidates = result!.Candidates,
                total = result.Total,
                page = result.Page,
                page_size = result.PageSize,
                has_more = result.HasMore,
            });
        }).WithName("BrowseSkillMarketplace").WithTags("Skills");

        app.MapPost("/api/projects/{id}/skill-marketplaces/{marketplace}/import", async (
            HttpContext http, string id, string marketplace, MarketplaceImportRequest body, IProjectStore projects, IConfiguration configuration, MarketplaceSourceService sources, SkillCatalogService svc, CancellationToken ct) =>
        {
            var (failure, project) = await ProjectAuthorization.ResolveProjectAsync(http, id, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var projectId = project!.Id;
            var caller = ApiKeyAuthMiddleware.GetCaller(http);
            var source = await sources.ResolveAsync(projectId, marketplace, ct);
            if (source is null) return Results.NotFound();

            // For an auto-detected source the selected candidate location IS the import subpath: fetch
            // just that directory (locations=null → the single skill under it is imported). Config
            // sources keep passing their hardcoded subpath + the selected candidate locations.
            SkillAcquisitionResult result;
            if (source.IsAuto)
            {
                var selected = body?.Locations is { Count: > 0 } ? body.Locations[0] : null;
                if (string.IsNullOrWhiteSpace(selected))
                    return Results.BadRequest(new { error = "Select a skill location to import." });
                result = await svc.ImportMarketplaceAsync(
                    projectId, source.Owner, source.Repo, source.Branch, selected, locations: null, caller, source.Name, ct);
            }
            else
            {
                result = await svc.ImportMarketplaceAsync(
                    projectId, source.Owner, source.Repo, source.Branch, source.Subpath!, body?.Locations, caller, source.Name, ct);
            }
            return MapAcquisition(result);
        }).WithName("ImportSkillMarketplace").WithTags("Skills");

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
            try
            {
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
                        var rawRel = form[$"path:{file.Name}"].FirstOrDefault()
                                  ?? (string.IsNullOrEmpty(file.FileName) ? file.Name : file.FileName);
                        var rel = SkillPaths.NormalizeRelative(rawRel);
                        if (rel is null)
                            return Results.BadRequest(new { error = $"Uploaded file '{rawRel}' has an unsafe path." });
                        files.Add(new UploadedSkillFile(rel, content));
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
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

    private static (string Owner, string Repo) SplitRepository(string repository)
    {
        var trimmed = (repository ?? string.Empty).Trim().Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    private static IResult MapAcquisition(SkillAcquisitionResult result) => result.Outcome switch
    {
        SkillOutcome.Ok => Results.Ok(new { results = result.Results, marked_missing = result.MarkedMissing }),
        SkillOutcome.NotFound => Results.NotFound(),
        SkillOutcome.Invalid => Results.BadRequest(new { error = result.Error, results = result.Results }),
        SkillOutcome.SourceUnavailable => Results.UnprocessableEntity(new { error = result.Error }),
        _ => Results.StatusCode(500),
    };

    internal static void ExpandZip(Stream zipStream, List<UploadedSkillFile> files)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        long total = 0;
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            if (++count > MaxArchiveEntries)
                throw new InvalidDataException($"Archive contains more than {MaxArchiveEntries} entries.");

            // Zip-slip: reject rooted / drive-qualified / traversal entry names before touching them.
            var rel = SkillPaths.NormalizeRelative(entry.FullName);
            if (rel is null)
                throw new InvalidDataException($"Archive entry '{entry.FullName}' has an unsafe path.");

            using var es = entry.Open();
            var (text, bytes) = ReadCapped(es, MaxArchiveEntryBytes);
            total += bytes;
            if (total > MaxArchiveTotalBytes)
                throw new InvalidDataException($"Archive decompresses to more than {MaxArchiveTotalBytes / (1024 * 1024)} MB.");
            if (text is null) continue; // oversized or binary — skipped (mirrors filesystem SafeReadText)
            files.Add(new UploadedSkillFile(rel, text));
        }
    }

    // Decompression guards (mirror SkillParser caps + the filesystem SafeReadText 2*MaxResourceBytes
    // bail): applied DURING extraction so a zip bomb can never be fully decompressed into memory.
    private const int MaxArchiveEntries = 1024;
    private const int MaxArchiveEntryBytes = SkillParser.MaxResourceBytes * 2;        // 512 KB per entry
    private const long MaxArchiveTotalBytes = 16L * 1024 * 1024;                       // 16 MB total decompressed

    /// <summary>
    /// Reads a stream into text up to <paramref name="cap"/> bytes. Returns (null, bytesRead) when the
    /// entry exceeds the cap or contains a NUL byte (binary), so the caller can enforce a running total
    /// while skipping content that validation would reject anyway.
    /// </summary>
    private static (string? Text, long Bytes) ReadCapped(Stream stream, int cap)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > cap)
            {
                // Drain no further; report an over-cap size so the running total still advances.
                return (null, ms.Length);
            }
        }
        var data = ms.GetBuffer();
        var len = (int)ms.Length;
        for (var i = 0; i < len; i++)
            if (data[i] == 0) return (null, len); // binary
        return (Encoding.UTF8.GetString(data, 0, len), len);
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
        marketplace_name = s.MarketplaceName,
        status = s.Status.ToApiString(),
        content_hash = s.ContentHash,
        created_at = s.CreatedAt,
        updated_at = s.UpdatedAt,
    };

    public sealed record MarketplaceBrowseRequest(string? Query, int? Page, int? PageSize);
    public sealed record MarketplaceImportRequest(IReadOnlyList<string>? Locations);
    public sealed record AddMarketplaceSourceRequest(string Repository, string? Name, string? Branch, string? Subpath, string? ParseStrategy);
    public sealed record ImportPreviewRequest(string? RepoUrl);
    public sealed record ImportRequest(string? RepoUrl, IReadOnlyList<string>? Locations);
    public sealed record CreateSkillRequest(string? Name, string? DisplayName, string? Description, string? Instructions);
    public sealed record GenerateSkillRequest(string? Description, string? Prompt);
    public sealed record SkillDefaultsPreviewRequest(
        [property: JsonPropertyName("blueprint_id")] string? BlueprintId);
    public sealed record SkillDefaultsApplyRequest(
        [property: JsonPropertyName("blueprint_id")] string? BlueprintId,
        [property: JsonPropertyName("digest")] string? Digest);

    private static async Task<IResult?> RequireProjectRoleAsync(
        HttpContext httpContext,
        Project project,
        ProjectRole minimumRole,
        CancellationToken ct)
    {
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        return await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, minimumRole, ct).ConfigureAwait(false);
    }
}
