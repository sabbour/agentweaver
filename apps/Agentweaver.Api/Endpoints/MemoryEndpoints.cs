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

    var requestedTags = !string.IsNullOrWhiteSpace(tags)
        ? tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
        : [];

    var memories = (await query.ToListAsync(ct))
        .Where(m => requestedTags.Count == 0 || (m.Tags is not null && requestedTags.Any(tag => m.Tags.Contains($",{tag},"))))
        .OrderByDescending(m => m.CreatedAt)
        .ToList();
    return Results.Ok(memories.Select(m => new
    {
        m.Id, m.AgentName, m.SessionId, m.Type, m.Importance, m.Content, m.Tags,
        created_at = m.CreatedAt, updated_at = m.UpdatedAt,
    }));
});

// GET /api/projects/{id}/agents/{name}/memory
app.MapGet("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    string? type,
    string? importance,
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
        .Where(m => type == null || m.Type == type)
        .Where(m => importance == null || m.Importance == importance)
        .ToListAsync(ct))
        .OrderByDescending(m => m.CreatedAt)
        .ToList();
    return Results.Ok(memories.Select(m => new
    {
        m.Id, m.AgentName, m.SessionId, m.Type, m.Importance, m.Content, m.Tags,
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
        SessionId = request.SessionId,
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.AgentMemory.Add(memory);
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Created($"/api/projects/{id}/agents/{name}/memory/{memory.Id}", new
    {
        memory.Id, memory.AgentName, memory.SessionId, memory.Type, memory.Importance, memory.Content, memory.Tags,
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
        memory.Id, memory.AgentName, memory.SessionId, memory.Type, memory.Importance, memory.Content, memory.Tags,
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
        serialized_state = session.SerializedState,
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
        SerializedState = request.SerializedState,
        StartedAt = now,
    };
    memoryDb.SessionContexts.Add(session);
    await memoryDb.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Created($"/api/projects/{id}/sessions/current", new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        serialized_state = session.SerializedState,
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

    if (session is null)
        return Results.NotFound("No active session");

    if (!string.IsNullOrWhiteSpace(request.FocusArea)) session.FocusArea = request.FocusArea!;
    if (request.ActiveIssues is not null) session.ActiveIssues = request.ActiveIssues;
    if (request.Summary is not null) session.Summary = request.Summary;
    if (request.SerializedState is not null) session.SerializedState = request.SerializedState;
    if (request.End == true) session.EndedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        serialized_state = session.SerializedState,
        started_at = session.StartedAt, ended_at = session.EndedAt,
    });
});

// GET /api/projects/{id}/sessions
app.MapGet("/api/projects/{id}/sessions", async (
    string id,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var sessions = (await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id)
        .ToListAsync(ct))
        .OrderByDescending(s => s.StartedAt)
        .ToList();
    return Results.Ok(sessions.Select(s => new
    {
        s.Id, s.SessionId, s.FocusArea, s.ActiveIssues, s.Summary,
        serialized_state = s.SerializedState,
        started_at = s.StartedAt, ended_at = s.EndedAt,
    }));
});

// GET /api/projects/{id}/sessions/{sessionId}
app.MapGet("/api/projects/{id}/sessions/{sessionId}", async (
    string id,
    string sessionId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var session = await memoryDb.SessionContexts
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.ProjectId == id && s.SessionId == sessionId, ct);
    if (session is null) return Results.NotFound();

    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        serialized_state = session.SerializedState,
        started_at = session.StartedAt, ended_at = session.EndedAt,
    });
});

// PATCH /api/projects/{id}/sessions/{sessionId}
app.MapMethods("/api/projects/{id}/sessions/{sessionId}", new[] { "PATCH" }, async (
    string id,
    string sessionId,
    UpdateSessionRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var session = await memoryDb.SessionContexts
        .FirstOrDefaultAsync(s => s.ProjectId == id && s.SessionId == sessionId && s.EndedAt == null, ct);
    if (session is null) return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(request.FocusArea)) session.FocusArea = request.FocusArea!;
    if (request.ActiveIssues is not null) session.ActiveIssues = request.ActiveIssues;
    if (request.Summary is not null) session.Summary = request.Summary;
    if (request.SerializedState is not null) session.SerializedState = request.SerializedState;
    if (request.End == true) session.EndedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        serialized_state = session.SerializedState,
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

    var decisionDtos = decisions.Select(d => new Agentweaver.Squad.Memory.DecisionExportDto(
        d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList();
    var inboxDtos = inbox.Select(e => new Agentweaver.Squad.Memory.InboxExportDto(
        e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList();
    var memoryDtos = memories.Select(m => new Agentweaver.Squad.Memory.MemoryExportDto(
        m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList();
    var sessionDto = session is null ? null : new Agentweaver.Squad.Memory.SessionExportDto(
        session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary);

    try
    {
        var exporter = new Agentweaver.Squad.Memory.SquadMemoryExporter(project.WorkingDirectory);
        await exporter.ExportAsync(decisionDtos, inboxDtos, memoryDtos, sessionDto, ct);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to export project memory for {ProjectId}.", id);
    }
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

    var importer = new Agentweaver.Squad.Memory.SquadMemoryImporter(project.WorkingDirectory);
    var parsed = importer.ScanInboxFiles().ToList();
    int newCount = 0;
    foreach (var p in parsed)
    {
        var exists = await memoryDb.DecisionInbox.AnyAsync(e => e.ProjectId == id && e.Slug == p.Slug, ct);
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
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new { imported = newCount });
});
    }
}

internal static class MemoryExportHelpers
{
    public static async Task TryExportAsync(
        string projectId,
        string projectWorkingDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct,
        ILogger logger)
    {
        try
        {
            var decisions = await memoryDb.Decisions.Where(d => d.ProjectId == projectId).ToListAsync(ct);
            var inbox = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId && e.Status == "pending").ToListAsync(ct);
            var memories = await memoryDb.AgentMemory.Where(m => m.ProjectId == projectId).ToListAsync(ct);
            var session = (await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .ToListAsync(ct))
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefault();

            var decisionDtos = decisions.Select(d => new Agentweaver.Squad.Memory.DecisionExportDto(
                d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList();
            var inboxDtos = inbox.Select(e => new Agentweaver.Squad.Memory.InboxExportDto(
                e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList();
            var memoryDtos = memories.Select(m => new Agentweaver.Squad.Memory.MemoryExportDto(
                m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList();
            var sessionDto = session is null ? null : new Agentweaver.Squad.Memory.SessionExportDto(
                session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary);

            var exporter = new Agentweaver.Squad.Memory.SquadMemoryExporter(projectWorkingDirectory);
            await exporter.ExportAsync(decisionDtos, inboxDtos, memoryDtos, sessionDto, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to export project memory for {ProjectId}.", projectId);
        }
    }
}
