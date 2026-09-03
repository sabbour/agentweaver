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
using Agentweaver.Api.Sandbox;
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
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var logger = app.ServiceProvider.GetRequiredService<ILogger<Program>>();
// GET /api/projects/{id}/memory — cross-agent search across all memories for a project
// (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/memory", async (
    string id,
    string? type,
    string? tags,
    int? page,
    int? page_size,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;

    IQueryable<AgentMemory> query = memoryDb.AgentMemory.Where(m => m.ProjectId == id);

    if (!string.IsNullOrWhiteSpace(type))
        query = query.Where(m => m.Type == type);

    var requestedTags = !string.IsNullOrWhiteSpace(tags)
        ? tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
        : [];

    var memories = (await query.ToListAsync(ct))
        .Where(m => requestedTags.Count == 0 || (m.Tags is not null && requestedTags.Any(tag => m.Tags.Contains($",{tag},"))))
        .OrderByDescending(m => m.CreatedAt)
        .Select(m => new
        {
            m.Id, m.AgentName, m.SessionId, m.Type, m.Importance, m.Content, m.Tags,
            m.SourceKind, m.SourceIdentity, m.SourceRunId, m.TrustState, m.ApprovedBy, m.ApprovedAt,
            created_at = m.CreatedAt, updated_at = m.UpdatedAt,
        })
        .ToList();
    return Results.Ok(Paging.Of(memories, page, page_size));
});

// GET /api/projects/{id}/agents/{name}/memory (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    string? type,
    string? importance,
    int? page,
    int? page_size,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;
    var memories = (await memoryDb.AgentMemory
        .Where(m => m.ProjectId == id && m.AgentName == name)
        .Where(m => type == null || m.Type == type)
        .Where(m => importance == null || m.Importance == importance)
        .ToListAsync(ct))
        .OrderByDescending(m => m.CreatedAt)
        .Select(m => new
        {
            m.Id, m.AgentName, m.SessionId, m.Type, m.Importance, m.Content, m.Tags,
            m.SourceKind, m.SourceIdentity, m.SourceRunId, m.TrustState, m.ApprovedBy, m.ApprovedAt,
            created_at = m.CreatedAt, updated_at = m.UpdatedAt,
        })
        .ToList();
    return Results.Ok(Paging.Of(memories, page, page_size));
});

// POST /api/projects/{id}/agents/{name}/memory
app.MapPost("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    RecordMemoryRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
    if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "type and content are required." });
    var memoryType = request.Type.Trim().ToLowerInvariant();
    var importance = (request.Importance ?? "medium").Trim().ToLowerInvariant();
    if (!MemoryWritePolicy.IsMemoryType(memoryType))
        return Results.BadRequest(new { error = "type must be core_context, learning, pattern, or update." });
    if (!MemoryWritePolicy.IsImportance(importance))
        return Results.BadRequest(new { error = "importance must be low, medium, or high." });

    var (author, authorFailure) = await RunAuthorship.ResolveAsync(
        httpContext, id, name, runResolver, turnTokens, ct);
    if (authorFailure is not null) return authorFailure;

    var now = DateTimeOffset.UtcNow;
    var normalizedTags = MemoryWritePolicy.NormalizeTags(request.Tags);
    var memory = new AgentMemory
    {
        ProjectId = id,
        AgentName = author!.AgentName,
        Type = memoryType,
        Importance = importance,
        Content = request.Content!,
        Tags = normalizedTags,
        SessionId = request.SessionId,
        SourceKind = author.SourceKind,
        SourceIdentity = author.SourceIdentity,
        SourceRunId = author.SourceRunId,
        TrustState = MemoryTrustStates.Pending,
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.AgentMemory.Add(memory);
    await memoryDb.SaveChangesAsync(ct);
    // The database write is the durable record. Filesystem export rewrites the full project
    // memory snapshot and may target a remote workspace volume, so it must not delay this
    // latency-sensitive agent tool call. Scribe invokes /memory/export explicitly at run end.
    return Results.Created($"/api/projects/{id}/agents/{name}/memory/{memory.Id}", new
    {
        memory.Id, memory.AgentName, memory.SessionId, memory.Type, memory.Importance, memory.Content, memory.Tags,
        memory.SourceKind, memory.SourceIdentity, memory.SourceRunId, memory.TrustState,
        created_at = memory.CreatedAt,
    });
});

// POST /api/projects/{id}/agents/{name}/memory/{memId}/promote
app.MapPost("/api/projects/{id}/agents/{name}/memory/{memId}/promote", async (
    string id,
    string name,
    int memId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    var (approver, authorFailure) = await RunAuthorship.ResolveAsync(
        httpContext, id, requestedAgentName: null, runResolver, turnTokens, ct);
    if (authorFailure is not null) return authorFailure;
    if (approver!.SourceKind == MemorySourceKinds.Run)
    {
        if (!approver.IsCoordinator)
            return Results.Json(new { error = "coordinator_approval_required" }, statusCode: StatusCodes.Status403Forbidden);
    }
    else if (await ProjectAuthorization.RequireAccessAsync(
        httpContext, project, configuration, ProjectRole.Owner, ct) is { } forbid)
    {
        return forbid;
    }

    var memory = await memoryDb.AgentMemory.FindAsync(new object[] { memId }, ct);
    if (memory is null || memory.ProjectId != id || !string.Equals(memory.AgentName, name, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    if (memory.TrustState == MemoryTrustStates.Approved)
        return Results.Ok(new { memory.Id, memory.TrustState, memory.ApprovedBy, memory.ApprovedAt });

    memory.TrustState = MemoryTrustStates.Approved;
    memory.ApprovedBy = approver.SourceIdentity;
    memory.ApprovedAt = DateTimeOffset.UtcNow;
    memory.UpdatedAt = memory.ApprovedAt.Value;
    await memoryDb.SaveChangesAsync(ct);
    return Results.Ok(new { memory.Id, memory.TrustState, memory.ApprovedBy, memory.ApprovedAt });
});

// GET /api/projects/{id}/agents/{name}/memory/{memId}
app.MapGet("/api/projects/{id}/agents/{name}/memory/{memId}", async (
    string id,
    string name,
    int memId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;
    var memory = await memoryDb.AgentMemory.FindAsync(new object[] { memId }, ct);
    if (memory is null || memory.ProjectId != id || memory.AgentName != name) return Results.NotFound();
    return Results.Ok(new
    {
        memory.Id, memory.AgentName, memory.SessionId, memory.Type, memory.Importance, memory.Content, memory.Tags,
        memory.SourceKind, memory.SourceIdentity, memory.SourceRunId, memory.TrustState, memory.ApprovedBy, memory.ApprovedAt,
        created_at = memory.CreatedAt, updated_at = memory.UpdatedAt,
    });
});

// GET /api/projects/{id}/sessions/current
app.MapGet("/api/projects/{id}/sessions/current", async (
    string id,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;
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
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
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
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, logger);
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
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
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
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, logger);
    return Results.Ok(new
    {
        session.Id, session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary,
        serialized_state = session.SerializedState,
        started_at = session.StartedAt, ended_at = session.EndedAt,
    });
});

// GET /api/projects/{id}/sessions (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/sessions", async (
    string id,
    int? page,
    int? page_size,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;
    var sessions = (await memoryDb.SessionContexts
        .Where(s => s.ProjectId == id)
        .ToListAsync(ct))
        .OrderByDescending(s => s.StartedAt)
        .Select(s => new
        {
            s.Id, s.SessionId, s.FocusArea, s.ActiveIssues, s.Summary,
            serialized_state = s.SerializedState,
            started_at = s.StartedAt, ended_at = s.EndedAt,
        })
        .ToList();
    return Results.Ok(Paging.Of(sessions, page, page_size));
});

// GET /api/projects/{id}/sessions/{sessionId}
app.MapGet("/api/projects/{id}/sessions/{sessionId}", async (
    string id,
    string sessionId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct) is { } forbid) return forbid;
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
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
    var session = await memoryDb.SessionContexts
        .FirstOrDefaultAsync(s => s.ProjectId == id && s.SessionId == sessionId && s.EndedAt == null, ct);
    if (session is null) return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(request.FocusArea)) session.FocusArea = request.FocusArea!;
    if (request.ActiveIssues is not null) session.ActiveIssues = request.ActiveIssues;
    if (request.Summary is not null) session.Summary = request.Summary;
    if (request.SerializedState is not null) session.SerializedState = request.SerializedState;
    if (request.End == true) session.EndedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, logger);
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
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;

    var decisionCount = await memoryDb.Decisions.CountAsync(d => d.ProjectId == id && d.Status == "active", ct);
    var inboxCount = await memoryDb.DecisionInbox.CountAsync(e => e.ProjectId == id && e.Status == "pending", ct);
    var memoryCount = await memoryDb.AgentMemory.CountAsync(m => m.ProjectId == id, ct);

    try
    {
        // Explicit sync action (spec #25): must report success OR an actionable error — never a
        // false success. ExportAsync throws on failure so it is surfaced here rather than swallowed.
        await MemoryLedgerExporter.ExportAsync(id, project.WorkingDirectory, memoryDb, ct);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to export project memory for {ProjectId}.", id);
        return Results.Problem(
            title: "Memory export failed",
            detail: $"The team ledger could not be written to the project workspace: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    return Results.Ok(new { exported = true, decisions = decisionCount, inbox = inboxCount, memories = memoryCount });
});

// POST /api/projects/{id}/memory/import
app.MapPost("/api/projects/{id}/memory/import", async (
    string id,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;

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
    var mirrorExported = await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, logger);
    return Results.Ok(new { imported = newCount, mirror_exported = mirrorExported });
});
    }
}

internal static class MemoryExportHelpers
{
    /// <summary>
    /// Best-effort refresh of the workspace file mirror after a DB write. Returns whether the
    /// mirror was written so callers can honestly report <c>mirror_exported</c> instead of implying
    /// unconditional success. Never fails the caller's authoritative DB write.
    /// </summary>
    public static Task<bool> TryExportAsync(
        string projectId,
        string projectWorkingDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct,
        ILogger logger)
        => MemoryLedgerExporter.TryExportAsync(projectId, projectWorkingDirectory, memoryDb, ct, logger);
}
