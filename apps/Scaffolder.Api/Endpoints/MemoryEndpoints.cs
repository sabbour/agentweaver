using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Scaffolder.AgentRuntime;
using Scaffolder.Api.Memory;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Auth;
using Scaffolder.Api.Casting;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Coordinator;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Projects;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Security;
using Scaffolder.Domain;
using Scaffolder.Squad.Catalog;
using Scaffolder.Squad.Model;
using Scaffolder.Squad.Squad;
using Scaffolder.Squad.Analysis;
using Scaffolder.Squad.Sync;

namespace Scaffolder.Api.Endpoints;

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
// GET /api/projects/{id}/memory — cross-agent search across all memories for a project
app.MapGet("/api/projects/{id}/memory", async (
    string id,
    string? type,
    string? tags,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    IQueryable<AgentMemory> query = memoryDb.AgentMemory.Where(m => m.ProjectId == id);

    if (!string.IsNullOrWhiteSpace(type))
        query = query.Where(m => m.Type == type);

    if (!string.IsNullOrWhiteSpace(tags))
    {
        var requestedTags = tags.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        foreach (var tag in requestedTags)
            query = query.Where(m => m.Tags != null && EF.Functions.Like(m.Tags, $"%,{tag},%"));
    }

    var memories = (await query.ToListAsync(ct))
        .OrderByDescending(m => m.CreatedAt)
        .ToList();
    return Results.Ok(memories.Select(m => new
    {
        m.Id, m.AgentName, m.Type, m.Importance, m.Content, m.Tags,
        created_at = m.CreatedAt, updated_at = m.UpdatedAt,
    }));
});

// GET /api/projects/{id}/agents/{name}/memory
app.MapGet("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var memories = (await memoryDb.AgentMemory
        .Where(m => m.ProjectId == id && m.AgentName == name)
        .ToListAsync(ct))
        .OrderByDescending(m => m.CreatedAt)
        .ToList();
    return Results.Ok(memories.Select(m => new
    {
        m.Id, m.AgentName, m.Type, m.Importance, m.Content, m.Tags,
        created_at = m.CreatedAt, updated_at = m.UpdatedAt,
    }));
});

// POST /api/projects/{id}/agents/{name}/memory
app.MapPost("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    RecordMemoryRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "type and content are required." });

    var now = DateTimeOffset.UtcNow;
    var tags = request.Tags;
    var normalizedTags = !string.IsNullOrWhiteSpace(tags)
        ? "," + string.Join(",", tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)) + ","
        : null;
    var memory = new AgentMemory
    {
        ProjectId = id,
        AgentName = name,
        Type = request.Type!,
        Importance = request.Importance ?? "medium",
        Content = request.Content!,
        Tags = normalizedTags,
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.AgentMemory.Add(memory);
    await memoryDb.SaveChangesAsync(ct);
    return Results.Created($"/api/projects/{id}/agents/{name}/memory/{memory.Id}", new
    {
        memory.Id, memory.AgentName, memory.Type, memory.Importance, memory.Content, memory.Tags,
        created_at = memory.CreatedAt,
    });
});

// GET /api/projects/{id}/agents/{name}/memory/{memId}
app.MapGet("/api/projects/{id}/agents/{name}/memory/{memId}", async (
    string id,
    string name,
    int memId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var memory = await memoryDb.AgentMemory.FindAsync(new object[] { memId }, ct);
    if (memory is null || memory.ProjectId != id || memory.AgentName != name) return Results.NotFound();
    return Results.Ok(new
    {
        memory.Id, memory.AgentName, memory.Type, memory.Importance, memory.Content, memory.Tags,
        created_at = memory.CreatedAt, updated_at = memory.UpdatedAt,
    });
});

// GET /api/projects/{id}/sessions/current
app.MapGet("/api/projects/{id}/sessions/current", async (
    string id,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var session = (await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id && s.EndedAt == null)
        .ToListAsync(ct))
        .OrderByDescending(s => s.StartedAt)
        .FirstOrDefault();
    if (session is null) return Results.NotFound();
    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        started_at = session.StartedAt, ended_at = session.EndedAt,
    });
});

// POST /api/projects/{id}/sessions
app.MapPost("/api/projects/{id}/sessions", async (
    string id,
    StartSessionRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.FocusArea))
        return Results.BadRequest(new { error = "focus_area is required." });

    var newSessionId = request.SessionId ?? Guid.NewGuid().ToString("N");

    await using var tx = await memoryDb.Database.BeginTransactionAsync(ct);

    // Check for duplicate SessionId
    var duplicate = await memoryDb.SessionContexts
        .AnyAsync(s => s.ProjectId == id && s.SessionId == newSessionId, ct);
    if (duplicate)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { error = "A session with this session_id already exists." });
    }

    // Close any open sessions
    var openSessions = await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id && s.EndedAt == null)
        .ToListAsync(ct);
    foreach (var s in openSessions)
        s.EndedAt = DateTimeOffset.UtcNow;

    var now = DateTimeOffset.UtcNow;
    var session = new SessionContext
    {
        ProjectId = id,
        SessionId = newSessionId,
        FocusArea = request.FocusArea!,
        ActiveIssues = request.ActiveIssues,
        Summary = request.Summary,
        StartedAt = now,
    };
    memoryDb.SessionContexts.Add(session);
    await memoryDb.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Created($"/api/projects/{id}/sessions/current", new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        started_at = session.StartedAt,
    });
});

// PUT /api/projects/{id}/sessions/current
app.MapPut("/api/projects/{id}/sessions/current", async (
    string id,
    UpdateSessionRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var session = (await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id && s.EndedAt == null)
        .ToListAsync(ct))
        .OrderByDescending(s => s.StartedAt)
        .FirstOrDefault();

    // Auto-create an open session if none exists so agents can always call update_session.
    if (session is null)
    {
        session = new SessionContext
        {
            ProjectId = id,
            SessionId = Guid.NewGuid().ToString("D"),
            FocusArea = request.FocusArea ?? request.Summary ?? "agent run",
            StartedAt = DateTimeOffset.UtcNow,
        };
        memoryDb.SessionContexts.Add(session);
    }

    if (!string.IsNullOrWhiteSpace(request.FocusArea)) session.FocusArea = request.FocusArea!;
    if (request.ActiveIssues is not null) session.ActiveIssues = request.ActiveIssues;
    if (request.Summary is not null) session.Summary = request.Summary;
    if (request.End == true) session.EndedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        started_at = session.StartedAt, ended_at = session.EndedAt,
    });
});

// POST /api/projects/{id}/memory/export
app.MapPost("/api/projects/{id}/memory/export", async (
    string id,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    var decisions = await memoryDb.Decisions.Where(d => d.ProjectId == id).ToListAsync(ct);
    var inbox = await memoryDb.DecisionInbox
        .Where(e => e.ProjectId == id && e.Status == "pending").ToListAsync(ct);
    var memories = await memoryDb.AgentMemory.Where(m => m.ProjectId == id).ToListAsync(ct);
    var session = (await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id && s.EndedAt == null)
        .ToListAsync(ct))
        .OrderByDescending(s => s.StartedAt)
        .FirstOrDefault();

    var decisionDtos = decisions.Select(d => new Scaffolder.Squad.Memory.DecisionExportDto(
        d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList();
    var inboxDtos = inbox.Select(e => new Scaffolder.Squad.Memory.InboxExportDto(
        e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList();
    var memoryDtos = memories.Select(m => new Scaffolder.Squad.Memory.MemoryExportDto(
        m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList();
    var sessionDto = session is null ? null : new Scaffolder.Squad.Memory.SessionExportDto(
        session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary);

    var exporter = new Scaffolder.Squad.Memory.SquadMemoryExporter(project.WorkingDirectory);
    await exporter.ExportAsync(decisionDtos, inboxDtos, memoryDtos, sessionDto, ct);
    return Results.Ok(new { exported = true, decisions = decisions.Count, inbox = inbox.Count, memories = memories.Count });
});

// POST /api/projects/{id}/memory/import
app.MapPost("/api/projects/{id}/memory/import", async (
    string id,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    var importer = new Scaffolder.Squad.Memory.SquadMemoryImporter(project.WorkingDirectory);
    var parsed = importer.ScanInboxFiles().ToList();
    int newCount = 0;
    foreach (var p in parsed)
    {
        var exists = await memoryDb.DecisionInbox.AnyAsync(e => e.ProjectId == id && e.AgentName == p.AgentName && e.Slug == p.Slug, ct);
        if (!exists)
        {
            var now = DateTimeOffset.UtcNow;
            memoryDb.DecisionInbox.Add(new DecisionInboxEntry
            {
                ProjectId = id, AgentName = p.AgentName, Slug = p.Slug,
                Type = p.Type, Title = p.Title, Content = p.Content,
                Rationale = p.Rationale, Status = "pending",
                CreatedAt = now, UpdatedAt = now,
            });
            newCount++;
        }
    }
    await memoryDb.SaveChangesAsync(ct);
    return Results.Ok(new { imported = newCount });
});
    }
}
