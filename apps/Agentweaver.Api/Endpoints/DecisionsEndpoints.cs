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

public static class DecisionsEndpoints
{
    public static void MapDecisionsEndpoints(this IEndpointRouteBuilder app)
    {
// -----------------------------------------------------------------------
// Memory / Decision Inbox endpoints
// -----------------------------------------------------------------------

// POST /api/projects/{id}/decisions/inbox
app.MapPost("/api/projects/{id}/decisions/inbox", async (
    string id,
    SubmitDecisionInboxRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (await ProjectAuthorization.RequireAccessAsync(httpContext, project, configuration, ProjectRole.Contributor, ct) is { } forbid) return forbid;
    if (string.IsNullOrWhiteSpace(request.AgentName) || string.IsNullOrWhiteSpace(request.Slug)
        || string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "agent_name, slug, type, and content are required." });
    var entryType = request.Type.Trim().ToLowerInvariant();
    if (!MemoryWritePolicy.IsInboxType(entryType))
        return Results.BadRequest(new { error = "Unsupported decision inbox type." });
    var (author, authorFailure) = await RunAuthorship.ResolveAsync(
        httpContext, id, request.AgentName, runResolver, turnTokens, ct);
    if (authorFailure is not null) return authorFailure;
    var submittedAgentName = author!.AgentName;

    // De-collision helper: converts an agent name to a safe kebab-case slug segment.
    static string SlugSegment(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // Load only the first existing entry for this (ProjectId, Slug) pair.
    var exists = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.ProjectId == id && e.Slug == request.Slug, ct);

    string effectiveSlug = request.Slug!;

    if (exists is not null)
    {
        var sameAgent = string.Equals(
            exists.AgentName, submittedAgentName, StringComparison.OrdinalIgnoreCase);
        if (sameAgent && exists.Status != "pending")
            return Results.Conflict(new { error = "Entry has already been merged or rejected." });

        if (sameAgent
            && exists.SourceKind == author.SourceKind
            && exists.SourceIdentity == author.SourceIdentity)
        {
            // Same agent retrying the same slug — idempotent update in place (retry-safe).
            exists.Type = entryType;
            exists.Title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title! : request.Slug!;
            exists.Content = request.Content!;
            exists.Rationale = request.Rationale;
            exists.SourceRunId = author.SourceRunId;
            exists.UpdatedAt = DateTimeOffset.UtcNow;
            await memoryDb.SaveChangesAsync(ct);
            try
            {
                await ledgerSync.RefreshAsync(id, project.WorkingDirectory, ct);
            }
            catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
            {
                return Results.Conflict(new { error = "decision_ledger_conflict", conflicts = ex.Conflicts });
            }
            return Results.Ok(new
            {
                exists.Id, exists.AgentName, exists.Slug, exists.Type, exists.Title, exists.Content,
                exists.Rationale, exists.Status, exists.SourceKind, exists.SourceIdentity, exists.SourceRunId,
                decision_id = exists.DecisionId, merged_at = exists.MergedAt,
                created_at = exists.CreatedAt, updated_at = exists.UpdatedAt,
            });
        }

        // Different agent submitted the same slug — de-collide to avoid silently losing a peer
        // decision. Scheme: "{original}--{agent-segment}", then "...--2", "...--3", etc.
        // NOTE: There is a residual TOCTOU race — two concurrent different-agent submissions can
        // independently read the same slug set and both choose the same de-collided slug, resulting
        // in two entries sharing that slug. This is acceptable for the inbox's append-only semantics
        // (no data is lost). Adding a unique DB index on (ProjectId, Slug) would eliminate the race
        // but requires a migration; left as a follow-up.
        var agentSegment = SlugSegment(submittedAgentName);
        effectiveSlug = $"{request.Slug}--{agentSegment}";
        if (await memoryDb.DecisionInbox.AnyAsync(e => e.ProjectId == id && e.Slug == effectiveSlug, ct))
        {
            int counter = 2;
            string candidate;
            do
            {
                candidate = $"{request.Slug}--{agentSegment}--{counter++}";
            } while (await memoryDb.DecisionInbox.AnyAsync(e => e.ProjectId == id && e.Slug == candidate, ct));
            effectiveSlug = candidate;
        }
    }

    var now = DateTimeOffset.UtcNow;
    var entry = new DecisionInboxEntry
    {
        ProjectId = id,
        AgentName = submittedAgentName,
        Slug = effectiveSlug,
        Type = entryType,
        Title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title! : effectiveSlug,
        Content = request.Content!,
        Rationale = request.Rationale,
        Status = "pending",
        SourceKind = author.SourceKind,
        SourceIdentity = author.SourceIdentity,
        SourceRunId = author.SourceRunId,
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.DecisionInbox.Add(entry);
    await memoryDb.SaveChangesAsync(ct);
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Created($"/api/projects/{id}/decisions/inbox/{entry.Id}", new
    {
        entry.Id, entry.AgentName, entry.Slug, entry.Type, entry.Title, entry.Content,
        entry.Rationale, entry.Status, entry.SourceKind, entry.SourceIdentity, entry.SourceRunId,
        decision_id = entry.DecisionId, merged_at = entry.MergedAt,
        created_at = entry.CreatedAt, updated_at = entry.UpdatedAt,
    });
});

// GET /api/projects/{id}/decisions/inbox (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/decisions/inbox", async (
    string id,
    string? status,
    string? type,
    string? agent,
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
    var statusFilter = status ?? "pending";
    var entries = (await memoryDb.DecisionInbox
        .Where(e => e.ProjectId == id)
        .Where(e => e.Status == statusFilter)
        .Where(e => type == null || e.Type == type)
        .Where(e => agent == null || e.AgentName == agent)
        .ToListAsync(ct))
        .OrderByDescending(e => e.CreatedAt)
        .Select(e => new
        {
            e.Id, e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale, e.Status,
            e.SourceKind, e.SourceIdentity, e.SourceRunId,
            decision_id = e.DecisionId, merged_at = e.MergedAt,
            created_at = e.CreatedAt, updated_at = e.UpdatedAt,
        })
        .ToList();
    return Results.Ok(Paging.Of(entries, page, page_size));
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/merge
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/merge", async (
    string id,
    int entryId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;

    var promotion = await DecisionPromotion.PromoteEntryAsync(
        memoryDb, id, entryId, DateTimeOffset.UtcNow, approver!.SourceIdentity, ct);
    if (promotion is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    var response = new
    {
        id = promotion.Entry.Id,
        promotion.Entry.Status,
        decisionId = promotion.Decision.Id,
        mergedAt = promotion.Entry.MergedAt,
    };
    return promotion.Promoted
        ? Results.Created($"/api/projects/{id}/decisions/{promotion.Decision.Id}", response)
        : Results.Ok(response);
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/promote (alias for /merge)
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/promote", async (
    string id,
    int entryId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;

    var promotion = await DecisionPromotion.PromoteEntryAsync(
        memoryDb, id, entryId, DateTimeOffset.UtcNow, approver!.SourceIdentity, ct);
    if (promotion is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Ok(new
    {
        id = promotion.Entry.Id,
        promotion.Entry.Status,
        decisionId = promotion.Decision.Id,
        mergedAt = promotion.Entry.MergedAt,
    });
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/reject
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/reject", async (
    string id,
    int entryId,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var (_, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;

    var entry = await DecisionPromotion.RejectEntryAsync(
        memoryDb, id, entryId, DateTimeOffset.UtcNow, ct);
    if (entry is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Ok(new { entry.Id, entry.Status });
});

// GET /api/projects/{id}/decisions (paginated; see Contracts.PagedResult<T>)
app.MapGet("/api/projects/{id}/decisions", async (
    string id,
    string? status,
    string? type,
    string? agent,
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
    var statusFilter = status ?? "active";
    var decisions = (await memoryDb.Decisions
        .Where(d => d.ProjectId == id)
        .Where(d => d.Status == statusFilter)
        .Where(d => type == null || d.Type == type)
        .Where(d => agent == null || d.AgentName == agent)
        .ToListAsync(ct))
        .OrderByDescending(d => d.UpdatedAt)
        .ThenByDescending(d => d.Id)
        .Select(DecisionResponse)
        .ToList();
    return Results.Ok(Paging.Of(decisions, page, page_size));
});

// GET /api/projects/{id}/decisions/{decisionId}
app.MapGet("/api/projects/{id}/decisions/{decisionId}", async (
    string id,
    int decisionId,
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
    var decision = await memoryDb.Decisions.FindAsync(new object[] { decisionId }, ct);
    if (decision is null || decision.ProjectId != id) return Results.NotFound();
    return Results.Ok(DecisionResponse(decision));
});

// POST /api/projects/{id}/decisions/{decisionId}/approve
app.MapPost("/api/projects/{id}/decisions/{decisionId}/approve", async (
    string id,
    int decisionId,
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
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;

    var decision = await memoryDb.Decisions.FindAsync(new object[] { decisionId }, ct);
    if (decision is null || decision.ProjectId != id) return Results.NotFound();
    if (decision.Revision != request.ExpectedRevision.Value)
        return RevisionConflict(decision.Revision);

    decision.TrustState = MemoryTrustStates.Approved;
    decision.ApprovedBy = approver!.SourceIdentity;
    decision.ApprovedAt = DateTimeOffset.UtcNow;
    decision.UpdatedAt = decision.ApprovedAt.Value;
    decision.RevisionActor = approver.SourceKind == MemorySourceKinds.Run ? approver.AgentName : "project-owner";
    decision.RevisionReason = request.Reason ?? "approved";
    try
    {
        await memoryDb.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        memoryDb.ChangeTracker.Clear();
        var current = await memoryDb.Decisions.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == decisionId && d.ProjectId == id, ct);
        return current is null ? Results.NotFound() : RevisionConflict(current.Revision);
    }
    return Results.Ok(new
    {
        decision.Id, decision.TrustState, decision.ApprovedBy, decision.ApprovedAt, decision.Revision,
    });
});

// POST /api/projects/{id}/decisions
app.MapPost("/api/projects/{id}/decisions", async (
    string id,
    CreateDecisionRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;
    if (string.IsNullOrWhiteSpace(request.AgentName) || string.IsNullOrWhiteSpace(request.Type)
        || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "agent_name, type, title, and content are required." });
    var decisionType = request.Type.Trim().ToLowerInvariant();
    if (!MemoryWritePolicy.IsDecisionType(decisionType))
        return Results.BadRequest(new { error = "type must be architectural, scope, process, or technical." });

    var now = DateTimeOffset.UtcNow;
    var normalizedTags = MemoryWritePolicy.NormalizeTags(request.Tags);
    var decision = new Decision
    {
        ProjectId = id,
        AgentName = approver!.SourceKind == MemorySourceKinds.Run ? approver.AgentName : request.AgentName!,
        Type = decisionType,
        Status = "active",
        Title = request.Title!,
        Content = request.Content!,
        Rationale = request.Rationale,
        Tags = normalizedTags,
        SourceKind = approver.SourceKind,
        SourceIdentity = approver.SourceIdentity,
        SourceRunId = approver.SourceRunId,
        TrustState = MemoryTrustStates.Approved,
        ApprovedBy = approver.SourceIdentity,
        ApprovedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };
    var (storedDecision, created) = await MemoryWriteDeduplicator
        .GetOrCreateDecisionAsync(memoryDb, decision, ct);
    try
    {
        await ledgerSync.RefreshAsync(id, project.WorkingDirectory, ct);
    }
    catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
    {
        return Results.Conflict(new { error = "decision_ledger_conflict", conflicts = ex.Conflicts });
    }
    var response = DecisionResponse(storedDecision);
    return created
        ? Results.Created($"/api/projects/{id}/decisions/{storedDecision.Id}", response)
        : Results.Ok(response);
});

// PUT /api/projects/{id}/decisions/{decisionId}
app.MapPut("/api/projects/{id}/decisions/{decisionId}", async (
    string id,
    int decisionId,
    UpdateDecisionRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
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
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;

    var decision = await memoryDb.Decisions.AsNoTracking()
        .SingleOrDefaultAsync(d => d.Id == decisionId, ct);
    if (decision is null || decision.ProjectId != id) return Results.NotFound();

    var content = !string.IsNullOrWhiteSpace(request.Content) ? request.Content! : decision.Content;
    var rationale = request.Rationale ?? decision.Rationale;
    var status = decision.Status;
    if (!string.IsNullOrWhiteSpace(request.Status))
    {
        status = request.Status.Trim().ToLowerInvariant();
        if (!KnowledgeLifecycleStates.IsValid(status))
            return Results.BadRequest(new { error = "status must be active, superseded, or archived." });
    }
    if (request.SupersededById is not null)
        status = KnowledgeLifecycleStates.Superseded;

    var result = await KnowledgeRevisionWriter.UpdateDecisionAsync(
        memoryDb, decisionId, request.ExpectedRevision.Value, content, rationale, status,
        status == KnowledgeLifecycleStates.Superseded
            ? request.SupersededById ?? decision.SupersededById
            : null,
        approver!.SourceKind == MemorySourceKinds.Run ? approver.AgentName : "project-owner",
        request.Reason ?? "updated through API", approver.SourceIdentity, ct);
    if (result.Status == KnowledgeWriteStatus.NotFound) return Results.NotFound();
    if (result.Status == KnowledgeWriteStatus.Stale) return RevisionConflict(result.CurrentRevision);
    if (result.Status is KnowledgeWriteStatus.InvalidReplacement or KnowledgeWriteStatus.ReplacementCycle)
        return Results.Conflict(new { error = result.Status == KnowledgeWriteStatus.ReplacementCycle
            ? "replacement_cycle" : "invalid_replacement" });
    decision = result.Record!;
    try
    {
        await ledgerSync.RefreshAsync(id, project.WorkingDirectory, ct);
    }
    catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
    {
        return Results.Conflict(new { error = "decision_ledger_conflict", conflicts = ex.Conflicts });
    }
    return Results.Ok(DecisionResponse(decision));
});

// GET /api/projects/{id}/decisions/{decisionId}/revisions
app.MapGet("/api/projects/{id}/decisions/{decisionId}/revisions", async (
    string id,
    int decisionId,
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
    if (!await memoryDb.Decisions.AsNoTracking()
            .AnyAsync(d => d.Id == decisionId && d.ProjectId == id, ct))
        return Results.NotFound();
    var revisions = (await memoryDb.DecisionRevisions.AsNoTracking()
            .Where(r => r.ProjectId == id && r.DecisionId == decisionId)
            .ToListAsync(ct))
        .OrderByDescending(r => r.Revision)
        .Select(DecisionRevisionResponse)
        .ToList();
    return Results.Ok(Paging.Of(revisions, page, page_size));
});

// GET /api/projects/{id}/decisions/{decisionId}/revisions/{revision}
app.MapGet("/api/projects/{id}/decisions/{decisionId}/revisions/{revision}", async (
    string id,
    int decisionId,
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
    var item = await memoryDb.DecisionRevisions.AsNoTracking()
        .SingleOrDefaultAsync(r => r.ProjectId == id && r.DecisionId == decisionId && r.Revision == revision, ct);
    return item is null ? Results.NotFound() : Results.Ok(DecisionRevisionResponse(item));
});

// GET /api/projects/{id}/decisions/{decisionId}/compare
app.MapGet("/api/projects/{id}/decisions/{decisionId}/compare", async (
    string id,
    int decisionId,
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
    var revisions = await memoryDb.DecisionRevisions.AsNoTracking()
        .Where(r => r.ProjectId == id && r.DecisionId == decisionId
            && (r.Revision == from_revision || r.Revision == to_revision))
        .ToListAsync(ct);
    var from = revisions.SingleOrDefault(r => r.Revision == from_revision);
    var to = revisions.SingleOrDefault(r => r.Revision == to_revision);
    return from is null || to is null
        ? Results.NotFound()
        : Results.Ok(new { from = DecisionRevisionResponse(from), to = DecisionRevisionResponse(to) });
});

// POST /api/projects/{id}/decisions/{decisionId}/restore
app.MapPost("/api/projects/{id}/decisions/{decisionId}/restore", async (
    string id,
    int decisionId,
    RestoreKnowledgeRequest request,
    HttpContext httpContext,
    IProjectStore projectStore,
    IConfiguration configuration,
    MemoryDbContext memoryDb,
    DecisionLedgerSyncService ledgerSync,
    IRunSubmittingUserResolver runResolver,
    IRunAuthorshipCapabilityStore turnTokens,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var (approver, approvalFailure) = await RunAuthorship.ResolveApproverAsync(
        httpContext, project, configuration, runResolver, turnTokens, ct);
    if (approvalFailure is not null) return approvalFailure;
    if (request.ExpectedRevision is null or < 1 || request.Revision is null or < 1)
        return Results.BadRequest(new { error = "expected_revision and revision are required." });
    if (!await memoryDb.Decisions.AsNoTracking()
            .AnyAsync(d => d.Id == decisionId && d.ProjectId == id, ct))
        return Results.NotFound();

    var result = await KnowledgeRevisionWriter.RestoreDecisionAsync(
        memoryDb, decisionId, request.ExpectedRevision.Value, request.Revision.Value,
        approver!.SourceKind == MemorySourceKinds.Run ? approver.AgentName : "project-owner",
        request.Reason ?? $"restored revision {request.Revision.Value}", ct);
    if (result.Status == KnowledgeWriteStatus.NotFound) return Results.NotFound();
    if (result.Status == KnowledgeWriteStatus.Stale) return RevisionConflict(result.CurrentRevision);
    await ledgerSync.TryRefreshAsync(id, project.WorkingDirectory, ct);
    return Results.Ok(DecisionResponse(result.Record!));
});

        static object DecisionResponse(Decision decision) => new
        {
            decision.Id,
            decision.AgentName,
            decision.Type,
            decision.Status,
            decision.Title,
            decision.Content,
            decision.Rationale,
            decision.Tags,
            decision.SourceKind,
            decision.SourceIdentity,
            decision.SourceRunId,
            decision.TrustState,
            decision.ApprovedBy,
            decision.ApprovedAt,
            superseded_by_id = decision.SupersededById,
            decision.Revision,
            current_revision_id = decision.CurrentRevisionId,
            created_at = decision.CreatedAt,
            updated_at = decision.UpdatedAt,
        };

        static object DecisionRevisionResponse(DecisionRevision revision) => new
        {
            revision_id = revision.RevisionId,
            decision_id = revision.DecisionId,
            revision = revision.Revision,
            previous_revision_id = revision.PreviousRevisionId,
            revision.Actor,
            source_run_id = revision.SourceRunId,
            reason = SandboxOutputRedactor.Default.Redact(revision.Reason),
            agent_name = revision.AgentName,
            revision.Type,
            revision.Status,
            title = SandboxOutputRedactor.Default.Redact(revision.Title),
            content = SandboxOutputRedactor.Default.Redact(revision.Content),
            rationale = SandboxOutputRedactor.Default.Redact(revision.Rationale ?? ""),
            tags = SandboxOutputRedactor.Default.Redact(revision.Tags ?? ""),
            superseded_by_id = revision.SupersededById,
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
                message = "The decision changed. Reload it and retry with the current revision.",
                current_revision = currentRevision,
            });
    }
}
