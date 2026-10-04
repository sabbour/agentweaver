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
using Agentweaver.SandboxExec;

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
    string? q,
    string? type,
    string? tags,
    string? agent,
    string? status,
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

    if (q?.Length > 256)
        return Results.BadRequest(new { error = "q must be 256 characters or fewer." });
    var statusFilter = string.IsNullOrWhiteSpace(status)
        ? KnowledgeLifecycleStates.Active
        : status.Trim().ToLowerInvariant();
    if (statusFilter != "all" && !KnowledgeLifecycleStates.IsValid(statusFilter))
        return Results.BadRequest(new { error = "status must be active, superseded, archived, or all." });

    IQueryable<AgentMemory> query = memoryDb.AgentMemory.Where(m => m.ProjectId == id);
    if (statusFilter != "all")
        query = query.Where(m => m.Status == statusFilter);

    if (!string.IsNullOrWhiteSpace(type))
        query = query.Where(m => m.Type == type);
    if (!string.IsNullOrWhiteSpace(agent))
        query = query.Where(m => m.AgentName == agent);

    var requestedTags = !string.IsNullOrWhiteSpace(tags)
        ? tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
        : [];

    var memories = (await query.ToListAsync(ct))
        .Where(m => requestedTags.Count == 0 || (m.Tags is not null && requestedTags.Any(tag => m.Tags.Contains($",{tag},"))))
        .Where(m => string.IsNullOrWhiteSpace(q)
            || m.Content.Contains(q, StringComparison.OrdinalIgnoreCase)
            || m.AgentName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (m.Tags?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
        .OrderByDescending(m => m.UpdatedAt)
        .ThenByDescending(m => m.Id)
        .Select(MemoryResponse)
        .ToList();
    return Results.Ok(Paging.Of(memories, page, page_size));
});

// GET /api/projects/{id}/agents/{name}/memory (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/agents/{name}/memory", async (
    string id,
    string name,
    string? type,
    string? importance,
    string? status,
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
    var statusFilter = string.IsNullOrWhiteSpace(status)
        ? KnowledgeLifecycleStates.Active
        : status.Trim().ToLowerInvariant();
    if (statusFilter != "all" && !KnowledgeLifecycleStates.IsValid(statusFilter))
        return Results.BadRequest(new { error = "status must be active, superseded, archived, or all." });
    var memories = (await memoryDb.AgentMemory
        .Where(m => m.ProjectId == id && m.AgentName == name)
        .Where(m => type == null || m.Type == type)
        .Where(m => importance == null || m.Importance == importance)
        .ToListAsync(ct))
        .Where(m => statusFilter == "all" || m.Status == statusFilter)
        .OrderByDescending(m => m.UpdatedAt)
        .ThenByDescending(m => m.Id)
        .Select(MemoryResponse)
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
    var (storedMemory, created) = await MemoryWriteDeduplicator
        .GetOrCreateMemoryAsync(memoryDb, memory, ct);
    // The database write is the durable record. Filesystem export rewrites the full project
    // memory snapshot and may target a remote workspace volume, so it must not delay this
    // latency-sensitive agent tool call. Scribe invokes /memory/export explicitly at run end.
    var response = MemoryResponse(storedMemory);
    return created
        ? Results.Created($"/api/projects/{id}/agents/{name}/memory/{storedMemory.Id}", response)
        : Results.Ok(response);
});

// PUT /api/projects/{id}/agents/{name}/memory/{memId}
app.MapPut("/api/projects/{id}/agents/{name}/memory/{memId}", async (
    string id,
    string name,
    int memId,
    UpdateMemoryRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
    if (request.Type is null && request.Content is null && request.Importance is null && request.Tags is null
        && request.Status is null && request.ReplacedById is null)
        return Results.BadRequest(new { error = "type, content, importance, tags, status, or replaced_by_id is required." });

    var memory = await memoryDb.AgentMemory
        .AsNoTracking()
        .SingleOrDefaultAsync(m => m.Id == memId, ct);
    if (memory is null || memory.ProjectId != id || !string.Equals(memory.AgentName, name, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    if (request.ExpectedRevision is null or < 1)
        return Results.BadRequest(new { error = "expected_revision is required." });

    var memoryType = memory.Type;
    var importance = memory.Importance;
    var content = memory.Content;
    var tags = memory.Tags;
    if (request.Type is not null)
    {
        memoryType = request.Type.Trim().ToLowerInvariant();
        if (!MemoryWritePolicy.IsMemoryType(memoryType))
            return Results.BadRequest(new { error = "type must be core_context, learning, pattern, or update." });
    }
    if (request.Importance is not null)
    {
        importance = request.Importance.Trim().ToLowerInvariant();
        if (!MemoryWritePolicy.IsImportance(importance))
            return Results.BadRequest(new { error = "importance must be low, medium, or high." });
    }
    if (request.Content is not null)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return Results.BadRequest(new { error = "content must not be empty." });
        content = request.Content;
    }
    if (request.Tags is not null)
    {
        tags = MemoryWritePolicy.NormalizeTags(request.Tags);
    }
    var lifecycle = request.Status?.Trim().ToLowerInvariant() ?? memory.Status;
    if (!KnowledgeLifecycleStates.IsValid(lifecycle))
        return Results.BadRequest(new { error = "status must be active, superseded, or archived." });

    var result = await KnowledgeRevisionWriter.UpdateMemoryAsync(
        memoryDb, memId, request.ExpectedRevision.Value, memoryType, importance, content, tags,
        lifecycle,
        lifecycle == KnowledgeLifecycleStates.Superseded
            ? request.ReplacedById ?? memory.ReplacedById
            : null,
        "project-contributor",
        request.Reason ?? "updated through API", ct);
    if (result.Status == KnowledgeWriteStatus.NotFound) return Results.NotFound();
    if (result.Status == KnowledgeWriteStatus.Stale)
        return RevisionConflict(result.CurrentRevision);
    if (result.Status is KnowledgeWriteStatus.InvalidReplacement or KnowledgeWriteStatus.ReplacementCycle)
        return Results.Conflict(new { error = result.Status == KnowledgeWriteStatus.ReplacementCycle
            ? "replacement_cycle" : "invalid_replacement" });

    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Ok(MemoryResponse(result.Record!));
});

// POST /api/projects/{id}/agents/{name}/memory/{memId}/promote
app.MapPost("/api/projects/{id}/agents/{name}/memory/{memId}/promote", async (
    string id,
    string name,
    int memId,
    ExpectedRevisionRequest request,
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
    if (request.ExpectedRevision is null or < 1)
        return Results.BadRequest(new { error = "expected_revision is required." });

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

    var memory = await memoryDb.AgentMemory
        .AsNoTracking()
        .SingleOrDefaultAsync(m => m.Id == memId, ct);
    if (memory is null || memory.ProjectId != id || !string.Equals(memory.AgentName, name, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    if (memory.Revision != request.ExpectedRevision.Value)
        return RevisionConflict(memory.Revision);
    if (memory.TrustState == MemoryTrustStates.Approved)
        return Results.Ok(new { memory.Id, memory.TrustState, memory.ApprovedBy, memory.ApprovedAt, memory.Revision });

    var approvedAt = DateTimeOffset.UtcNow;
    if (!await MemoryPromotionHelpers.TryPromoteReviewedAsync(
        memoryDb, memory, approver.SourceIdentity, approvedAt,
        approver.SourceKind == MemorySourceKinds.Run ? approver.AgentName : "project-owner",
        request.Reason ?? "approved", ct))
    {
        var current = await memoryDb.AgentMemory
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == memId, ct);
        if (current is null || current.ProjectId != id ||
            !string.Equals(current.AgentName, name, StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }
        if (current.TrustState == MemoryTrustStates.Approved)
            return Results.Ok(new { current.Id, current.TrustState, current.ApprovedBy, current.ApprovedAt });

        return RevisionConflict(current.Revision);
    }

    return Results.Ok(new
    {
        memory.Id,
        TrustState = MemoryTrustStates.Approved,
        ApprovedBy = approver.SourceIdentity,
        ApprovedAt = approvedAt,
        Revision = memory.Revision + 1,
    });
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
    return Results.Ok(MemoryResponse(memory));
});

// GET /api/projects/{id}/agents/{name}/memory/{memId}/revisions
app.MapGet("/api/projects/{id}/agents/{name}/memory/{memId}/revisions", async (
    string id,
    string name,
    int memId,
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
    var memory = await memoryDb.AgentMemory.AsNoTracking()
        .SingleOrDefaultAsync(m => m.Id == memId && m.ProjectId == id && m.AgentName == name, ct);
    if (memory is null) return Results.NotFound();

    var revisions = (await memoryDb.AgentMemoryRevisions.AsNoTracking()
            .Where(r => r.ProjectId == id && r.MemoryId == memId)
            .ToListAsync(ct))
        .OrderByDescending(r => r.Revision)
        .Select(MemoryRevisionResponse)
        .ToList();
    return Results.Ok(Paging.Of(revisions, page, page_size));
});

// GET /api/projects/{id}/agents/{name}/memory/{memId}/revisions/{revision}
app.MapGet("/api/projects/{id}/agents/{name}/memory/{memId}/revisions/{revision}", async (
    string id,
    string name,
    int memId,
    int revision,
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
    var record = await memoryDb.AgentMemory.AsNoTracking()
        .AnyAsync(m => m.Id == memId && m.ProjectId == id && m.AgentName == name, ct);
    if (!record) return Results.NotFound();
    var item = await memoryDb.AgentMemoryRevisions.AsNoTracking()
        .SingleOrDefaultAsync(r => r.ProjectId == id && r.MemoryId == memId && r.Revision == revision, ct);
    return item is null ? Results.NotFound() : Results.Ok(MemoryRevisionResponse(item));
});

// GET /api/projects/{id}/agents/{name}/memory/{memId}/compare
app.MapGet("/api/projects/{id}/agents/{name}/memory/{memId}/compare", async (
    string id,
    string name,
    int memId,
    int from_revision,
    int to_revision,
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
    if (!await memoryDb.AgentMemory.AsNoTracking()
            .AnyAsync(m => m.Id == memId && m.ProjectId == id && m.AgentName == name, ct))
        return Results.NotFound();
    var revisions = await memoryDb.AgentMemoryRevisions.AsNoTracking()
        .Where(r => r.ProjectId == id && r.MemoryId == memId
            && (r.Revision == from_revision || r.Revision == to_revision))
        .ToListAsync(ct);
    var from = revisions.SingleOrDefault(r => r.Revision == from_revision);
    var to = revisions.SingleOrDefault(r => r.Revision == to_revision);
    return from is null || to is null
        ? Results.NotFound()
        : Results.Ok(new { from = MemoryRevisionResponse(from), to = MemoryRevisionResponse(to) });
});

// POST /api/projects/{id}/agents/{name}/memory/{memId}/restore
app.MapPost("/api/projects/{id}/agents/{name}/memory/{memId}/restore", async (
    string id,
    string name,
    int memId,
    RestoreKnowledgeRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
    if (request.ExpectedRevision is null or < 1 || request.Revision is null or < 1)
        return Results.BadRequest(new { error = "expected_revision and revision are required." });
    if (!await memoryDb.AgentMemory.AsNoTracking()
            .AnyAsync(m => m.Id == memId && m.ProjectId == id && m.AgentName == name, ct))
        return Results.NotFound();

    var result = await KnowledgeRevisionWriter.RestoreMemoryAsync(
        memoryDb, memId, request.ExpectedRevision.Value, request.Revision.Value,
        "project-contributor", request.Reason ?? $"restored revision {request.Revision.Value}", ct);
    if (result.Status == KnowledgeWriteStatus.NotFound) return Results.NotFound();
    if (result.Status == KnowledgeWriteStatus.Stale) return RevisionConflict(result.CurrentRevision);
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Ok(MemoryResponse(result.Record!));
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
    DecisionLedgerSyncService ledgerSync,
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
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
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
    DecisionLedgerSyncService ledgerSync,
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
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
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
    DecisionLedgerSyncService ledgerSync,
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
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
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
    DecisionLedgerSyncService ledgerSync,
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

    MemoryLedgerExporter.ExportResult export;
    try
    {
        // Explicit sync action (spec #25): must report success OR an actionable error — never a
        // false success. ExportAsync throws on failure so it is surfaced here rather than swallowed.
        export = (await ledgerSync.ExportAndCommitAsync(
            id, project.WorkingDirectory, project.DefaultBranch, ct)).Export;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
    {
        return Results.Conflict(new { error = "decision_ledger_conflict", conflicts = ex.Conflicts });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to export project memory for {ProjectId}.", id);
        return Results.Problem(
            title: "Memory export failed",
            detail: $"The team ledger could not be written to the project workspace: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    return Results.Ok(new
    {
        exported = true,
        decisions = decisionCount,
        inbox = inboxCount,
        memories = memoryCount,
        files = export.Files,
    });
});

app.MapPost("/api/projects/{id}/scribe/finalize", async (
    string id,
    FinalizeScribeRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IRunAuthorshipCapabilityStore capabilityStore,
    ScribeFinalizationService finalization,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "invalid_project" });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (!RunId.TryParse(request.RunId, out var runId))
        return Results.BadRequest(new { error = "invalid_run" });

    var capabilityRunId = httpContext.Request.Headers[RunAuthorshipHeaders.RunId].ToString();
    var capabilityToken = httpContext.Request.Headers[RunAuthorshipHeaders.RunToken].ToString();
    if (!string.Equals(capabilityRunId, request.RunId, StringComparison.Ordinal)
        || !await capabilityStore.ValidateAsync(capabilityRunId, capabilityToken, ct)
            .ConfigureAwait(false))
    {
        return Results.Json(
            new { error = "invalid_scribe_capability" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var result = await finalization.FinalizeAsync(
        projectId,
        runId,
        request.LifecycleGeneration,
        request.AgentName,
        request.SubmittingUser,
        request.TerminalStatus,
        ct).ConfigureAwait(false);
    if (!result.Completed)
    {
        return Results.Json(
            new { error = result.Error },
            statusCode: result.Error == "scribe_run_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new { completed = true });
}).InternalService();

// POST /api/projects/{id}/memory/import
app.MapPost("/api/projects/{id}/memory/import", async (
    string id,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;

    try
    {
        var sync = await ledgerSync.RefreshAsync(id, project.WorkingDirectory, ct);
        return Results.Ok(new { imported = sync.Imported, mirror_exported = true });
    }
    catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
    {
        return Results.Conflict(new { error = "decision_ledger_conflict", conflicts = ex.Conflicts });
    }
});

        static object MemoryResponse(AgentMemory memory) => new
        {
            memory.Id,
            memory.AgentName,
            memory.SessionId,
            memory.Type,
            memory.Importance,
            memory.Content,
            memory.Tags,
            memory.Status,
            replaced_by_id = memory.ReplacedById,
            memory.SourceKind,
            memory.SourceIdentity,
            memory.SourceRunId,
            memory.TrustState,
            memory.ApprovedBy,
            memory.ApprovedAt,
            memory.Revision,
            current_revision_id = memory.CurrentRevisionId,
            created_at = memory.CreatedAt,
            updated_at = memory.UpdatedAt,
        };

        static object MemoryRevisionResponse(AgentMemoryRevision revision) => new
        {
            revision_id = revision.RevisionId,
            memory_id = revision.MemoryId,
            revision = revision.Revision,
            previous_revision_id = revision.PreviousRevisionId,
            revision.Actor,
            source_run_id = revision.SourceRunId,
            reason = SandboxOutputRedactor.Default.Redact(revision.Reason),
            agent_name = revision.AgentName,
            session_id = revision.SessionId,
            revision.Type,
            revision.Importance,
            content = SandboxOutputRedactor.Default.Redact(revision.Content),
            tags = SandboxOutputRedactor.Default.Redact(revision.Tags ?? ""),
            revision.Status,
            replaced_by_id = revision.ReplacedById,
            source_kind = revision.SourceKind,
            source_identity_fingerprint = revision.SourceIdentityFingerprint,
            source_run_reference = revision.SourceRunReference,
            trust_state = revision.TrustState,
            approved_by_fingerprint = revision.ApprovedByFingerprint,
            approved_at = revision.ApprovedAt,
            created_at = revision.CreatedAt,
        };

        static IResult RevisionConflict(int? currentRevision) =>
            Results.Conflict(new
            {
                error = "stale_revision",
                message = "The knowledge record changed. Reload it and retry with the current revision.",
                current_revision = currentRevision,
            });
    }
}

internal static class MemoryPromotionHelpers
{
    public static async Task<bool> TryApplyUpdateAsync(
        MemoryDbContext memoryDb,
        AgentMemory reviewed,
        string type,
        string importance,
        string content,
        string? tags,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var result = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            memoryDb,
            reviewed.Id,
            reviewed.Revision,
            type,
            importance,
            content,
            tags,
            reviewed.Status,
            reviewed.ReplacedById,
            reviewed.AgentName,
            "updated",
            ct);
        return result.Status == KnowledgeWriteStatus.Updated;
    }

    public static async Task<bool> TryPromoteReviewedAsync(
        MemoryDbContext memoryDb,
        AgentMemory reviewed,
        string? approvedBy,
        DateTimeOffset approvedAt,
        CancellationToken ct) =>
        await TryPromoteReviewedAsync(
            memoryDb, reviewed, approvedBy, approvedAt, reviewed.AgentName, "approved", ct);

    public static async Task<bool> TryPromoteReviewedAsync(
        MemoryDbContext memoryDb,
        AgentMemory reviewed,
        string? approvedBy,
        DateTimeOffset approvedAt,
        string actor,
        string reason,
        CancellationToken ct)
    {
        memoryDb.ChangeTracker.Clear();
        var memory = await memoryDb.AgentMemory.SingleOrDefaultAsync(candidate =>
            candidate.Id == reviewed.Id
            && candidate.ProjectId == reviewed.ProjectId
            && candidate.AgentName == reviewed.AgentName, ct);
        if (memory is null
            || memory.Revision != reviewed.Revision
            || memory.Type != reviewed.Type
            || memory.Importance != reviewed.Importance
            || memory.Content != reviewed.Content
            || memory.Tags != reviewed.Tags
            || memory.TrustState != MemoryTrustStates.Pending)
            return false;

        memory.TrustState = MemoryTrustStates.Approved;
        memory.ApprovedBy = approvedBy;
        memory.ApprovedAt = approvedAt;
        memory.UpdatedAt = approvedAt;
        memory.RevisionActor = actor;
        memory.RevisionReason = reason;
        try
        {
            await memoryDb.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
