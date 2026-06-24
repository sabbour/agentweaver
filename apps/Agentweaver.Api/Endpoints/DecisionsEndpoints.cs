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
        || string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Title)
        || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "agent_name, slug, type, title, and content are required." });

    var exists = await memoryDb.DecisionInbox
        .FirstOrDefaultAsync(e => e.ProjectId == id && e.AgentName == request.AgentName && e.Slug == request.Slug && e.Status == "pending", ct);
    if (exists is not null)
    {
        exists.Type = request.Type!;
        exists.Title = request.Title!;
        exists.Content = request.Content!;
        exists.Rationale = request.Rationale;
        exists.UpdatedAt = DateTimeOffset.UtcNow;
        await memoryDb.SaveChangesAsync(ct);
        return Results.Ok(new { exists.Id, exists.Slug, exists.Status });
    }

    var now = DateTimeOffset.UtcNow;
    var entry = new DecisionInboxEntry
    {
        ProjectId = id,
        AgentName = request.AgentName!,
        Slug = request.Slug!,
        Type = request.Type!,
        Title = request.Title!,
        Content = request.Content!,
        Rationale = request.Rationale,
        Status = "pending",
        CreatedAt = now,
        UpdatedAt = now,
    };
    memoryDb.DecisionInbox.Add(entry);
    await memoryDb.SaveChangesAsync(ct);
    return Results.Created($"/api/projects/{id}/decisions/inbox/{entry.Id}", new { entry.Id, entry.Slug, entry.Status });
});

// GET /api/projects/{id}/decisions/inbox
app.MapGet("/api/projects/{id}/decisions/inbox", async (
    string id,
    IProjectStore projectStore,
    MemoryDbContext memoryDb,
    CancellationToken ct) =>
{
    if (!ProjectId.TryParse(id, out var projectId))
        return Results.BadRequest(new { error = "Invalid project id." });
    var project = await projectStore.GetAsync(projectId, ct);
    if (project is null) return Results.NotFound();
    var entries = (await memoryDb.DecisionInbox
        .Where(e => e.ProjectId == id)
        .ToListAsync(ct))
        .OrderByDescending(e => e.CreatedAt)
        .ToList();
    return Results.Ok(entries.Select(e => new
    {
        e.Id, e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale, e.Status,
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

    if (entry.Type == "architectural" || entry.Type == "scope")
        return Results.BadRequest(new { error = "Coordinator-reserved decisions must be promoted by the coordinator agent." });

    var now = DateTimeOffset.UtcNow;
    var decision = DecisionPromotion.PromoteEntry(memoryDb, entry, now);
    await memoryDb.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { entry.Id, entry.Status, decisionId = decision.Id });
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

    if (entry.Type == "architectural" || entry.Type == "scope")
        return Results.BadRequest(new { error = "Coordinator-reserved decisions must be promoted by the coordinator agent." });

    var now = DateTimeOffset.UtcNow;
    var decision = DecisionPromotion.PromoteEntry(memoryDb, entry, now);
    await memoryDb.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { entry.Id, entry.Status, decisionId = decision.Id });
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
    var decisions = (await memoryDb.Decisions
        .Where(d => d.ProjectId == id)
        .Where(d => status == null || d.Status == status)
        .Where(d => type == null || d.Type == type)
        .Where(d => agent == null || d.AgentName == agent)
        .ToListAsync(ct))
        .OrderByDescending(d => d.CreatedAt)
        .ToList();
    return Results.Ok(decisions.Select(d => new
    {
        d.Id, d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.Tags,
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

    if (!string.IsNullOrWhiteSpace(request.Status)) decision.Status = request.Status!;
    if (!string.IsNullOrWhiteSpace(request.Content)) decision.Content = request.Content!;
    if (request.Rationale is not null) decision.Rationale = request.Rationale;
    decision.UpdatedAt = DateTimeOffset.UtcNow;
    await memoryDb.SaveChangesAsync(ct);
    return Results.Ok(new
    {
        decision.Id, decision.Status, decision.Content, decision.Rationale,
        updated_at = decision.UpdatedAt,
    });
});
    }
}
