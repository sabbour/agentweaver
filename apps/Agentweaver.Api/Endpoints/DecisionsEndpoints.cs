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

public static class DecisionsEndpoints
{
    public static void MapDecisionsEndpoints(this WebApplication app)
    {
// -----------------------------------------------------------------------
// Memory / Decision Inbox endpoints
// -----------------------------------------------------------------------

// POST /api/projects/{id}/decisions/inbox
app.MapPost("/api/projects/{id}/decisions/inbox", async (
    string id,
    SubmitDecisionInboxRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.AgentName) || string.IsNullOrWhiteSpace(request.Slug)
        || string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "agent_name, slug, type, and content are required." });

    // De-collision helper: converts an agent name to a safe kebab-case slug segment.
    static string SlugSegment(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // Load only the first existing entry for this (ProjectId, Slug) pair.
    var exists = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.ProjectId == id && e.Slug == request.Slug, ct);

    string effectiveSlug = request.Slug!;

    if (exists is not null)
    {
        if (exists.AgentName == request.AgentName)
        {
            // Same agent retrying the same slug — idempotent update in place (retry-safe).
            if (exists.Status != "pending")
                return Results.Conflict(new { error = "Entry has already been merged or rejected." });

            exists.Type = request.Type!;
            exists.Title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title! : request.Slug!;
            exists.Content = request.Content!;
            exists.Rationale = request.Rationale;
            exists.UpdatedAt = DateTimeOffset.UtcNow;
            await memoryDb.SaveChangesAsync(ct);
            await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
            return Results.Ok(new
            {
                exists.Id, exists.AgentName, exists.Slug, exists.Type, exists.Title, exists.Content,
                exists.Rationale, exists.Status,
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
        var agentSegment = SlugSegment(request.AgentName!);
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
        AgentName = request.AgentName!,
        Slug = effectiveSlug,
        Type = request.Type!,
        Title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title! : effectiveSlug,
        Content = request.Content!,
        Rationale = request.Rationale,
        Status = "pending",
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.DecisionInbox.Add(entry);
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Created($"/api/projects/{id}/decisions/inbox/{entry.Id}", new
    {
        entry.Id, entry.AgentName, entry.Slug, entry.Type, entry.Title, entry.Content,
        entry.Rationale, entry.Status,
        decision_id = entry.DecisionId, merged_at = entry.MergedAt,
        created_at = entry.CreatedAt, updated_at = entry.UpdatedAt,
    });
});

// GET /api/projects/{id}/decisions/inbox
app.MapGet("/api/projects/{id}/decisions/inbox", async (
    string id,
    string? status,
    string? type,
    string? agent,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var statusFilter = status ?? "pending";
    var entries = (await memoryDb.DecisionInbox
        .Where(e => e.ProjectId == id)
        .Where(e => e.Status == statusFilter)
        .Where(e => type == null || e.Type == type)
        .Where(e => agent == null || e.AgentName == agent)
        .ToListAsync(ct))
        .OrderByDescending(e => e.CreatedAt)
        .ToList();
    return Results.Ok(entries.Select(e => new
    {
        e.Id, e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale, e.Status,
        decision_id = e.DecisionId, merged_at = e.MergedAt,
        created_at = e.CreatedAt, updated_at = e.UpdatedAt,
    }));
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/merge
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/merge", async (
    string id,
    int entryId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    await using var tx = await memoryDb.Database.BeginTransactionAsync(ct);
    var entry = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.Id == entryId && e.ProjectId == id && e.Status == "pending", ct);
    if (entry is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    var now = DateTimeOffset.UtcNow;
    var decision = await DecisionPromotion.PromoteEntry(memoryDb, entry, now, ct);
    await tx.CommitAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Created($"/api/projects/{id}/decisions/{decision.Id}", new
    {
        id = entry.Id,
        entry.Status,
        decisionId = decision.Id,
        mergedAt = entry.MergedAt,
    });
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/promote (alias for /merge)
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/promote", async (
    string id,
    int entryId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    await using var tx = await memoryDb.Database.BeginTransactionAsync(ct);
    var entry = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.Id == entryId && e.ProjectId == id && e.Status == "pending", ct);
    if (entry is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    var now = DateTimeOffset.UtcNow;
    var decision = await DecisionPromotion.PromoteEntry(memoryDb, entry, now, ct);
    await tx.CommitAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new
    {
        id = entry.Id,
        entry.Status,
        decisionId = decision.Id,
        mergedAt = entry.MergedAt,
    });
});

// POST /api/projects/{id}/decisions/inbox/{entryId}/reject
app.MapPost("/api/projects/{id}/decisions/inbox/{entryId}/reject", async (
    string id,
    int entryId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    await using var tx = await memoryDb.Database.BeginTransactionAsync(ct);
    var entry = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.Id == entryId && e.ProjectId == id && e.Status == "pending", ct);
    if (entry is null)
        return Results.Conflict(new { error = "Entry is not pending or does not exist." });

    entry.Status = "rejected";
    entry.UpdatedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new { entry.Id, entry.Status });
});

// GET /api/projects/{id}/decisions
app.MapGet("/api/projects/{id}/decisions", async (
    string id,
    string? status,
    string? type,
    string? agent,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var statusFilter = status ?? "active";
    var decisions = (await memoryDb.Decisions
        .Where(d => d.ProjectId == id)
        .Where(d => d.Status == statusFilter)
        .Where(d => type == null || d.Type == type)
        .Where(d => agent == null || d.AgentName == agent)
        .ToListAsync(ct))
        .OrderByDescending(d => d.CreatedAt)
        .ToList();
    return Results.Ok(decisions.Select(d => new
    {
        d.Id, d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.Tags,
        superseded_by_id = d.SupersededById,
        created_at = d.CreatedAt, updated_at = d.UpdatedAt,
    }));
});

// GET /api/projects/{id}/decisions/{decisionId}
app.MapGet("/api/projects/{id}/decisions/{decisionId}", async (
    string id,
    int decisionId,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var decision = await memoryDb.Decisions.FindAsync(new object[] { decisionId }, ct);
    if (decision is null || decision.ProjectId != id) return Results.NotFound();
    return Results.Ok(new
    {
        decision.Id, decision.AgentName, decision.Type, decision.Status,
        decision.Title, decision.Content, decision.Rationale, decision.Tags,
        superseded_by_id = decision.SupersededById,
        created_at = decision.CreatedAt, updated_at = decision.UpdatedAt,
    });
});

// POST /api/projects/{id}/decisions
app.MapPost("/api/projects/{id}/decisions", async (
    string id,
    CreateDecisionRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.AgentName) || string.IsNullOrWhiteSpace(request.Type)
        || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "agent_name, type, title, and content are required." });

    var now = DateTimeOffset.UtcNow;
    var rawTags = request.Tags;
    var normalizedTags = !string.IsNullOrWhiteSpace(rawTags)
        ? "," + string.Join(",", rawTags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)) + ","
        : null;
    var decision = new Decision
    {
        ProjectId = id,
        AgentName = request.AgentName!,
        Type = request.Type!,
        Status = "active",
        Title = request.Title!,
        Content = request.Content!,
        Rationale = request.Rationale,
        Tags = normalizedTags,
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.Decisions.Add(decision);
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Created($"/api/projects/{id}/decisions/{decision.Id}", new
    {
        decision.Id, decision.AgentName, decision.Type, decision.Status,
        decision.Title, decision.Content, decision.Rationale, decision.Tags,
        created_at = decision.CreatedAt,
    });
});

// PUT /api/projects/{id}/decisions/{decisionId}
app.MapPut("/api/projects/{id}/decisions/{decisionId}", async (
    string id,
    int decisionId,
    UpdateDecisionRequest request,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();

    var decision = await memoryDb.Decisions.FindAsync(new object[] { decisionId }, ct);
    if (decision is null || decision.ProjectId != id) return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(request.Content)) decision.Content = request.Content!;
    if (request.Rationale is not null) decision.Rationale = request.Rationale;
    if (!string.IsNullOrWhiteSpace(request.Status))
    {
        decision.Status = request.Status!;
    }
    if (request.SupersededById is not null)
    {
        var supersedingDecision = await memoryDb.Decisions
            .FirstOrDefaultAsync(d => d.Id == request.SupersededById.Value && d.ProjectId == id, ct);
        if (supersedingDecision is null) return Results.NotFound();
        decision.SupersededById = request.SupersededById.Value;
        decision.Status = "superseded";
    }
    decision.UpdatedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    await MemoryExportHelpers.TryExportAsync(id, project.WorkingDirectory, memoryDb, ct, app.Logger);
    return Results.Ok(new
    {
        decision.Id, decision.Status, decision.Content, decision.Rationale,
        superseded_by_id = decision.SupersededById,
        updated_at = decision.UpdatedAt,
    });
});
    }
}
