using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Memory;
using Scaffolder.Domain;
using Scaffolder.Squad.Memory;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Closes the memory flywheel after each successful project run:
/// 1. Auto-merges low-risk inbox entries (learning / pattern / update) into decisions.
/// 2. Appends the run outcome to the current open session summary.
/// 3. Exports the updated memory state to .squad/ and .agentweaver/context/.
///
/// Non-blocking — all exceptions are caught and logged. The run terminal state is
/// never affected by failures in this service.
/// </summary>
public sealed class PostRunScribeService(
    MemoryDbContext memoryDb,
    IProjectStore projectStore,
    ILogger<PostRunScribeService> logger)
{
    private static readonly string[] AutoMergeTypes = ["learning", "pattern", "update"];

    public async Task RunAsync(Run run, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(run.AgentName) || !run.ProjectId.HasValue) return;

        var projectId = run.ProjectId.Value.ToString();
        var agentName = run.AgentName;
        var runStarted = run.StartedAt;

        try
        {
            var now = DateTimeOffset.UtcNow;

            // Step 1: Auto-merge low-risk inbox entries created during this run.
            var toMerge = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.AgentName == agentName
                         && e.Status == "pending"
                         && AutoMergeTypes.Contains(e.Type)
                         && e.CreatedAt >= runStarted)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var entry in toMerge)
            {
                memoryDb.Decisions.Add(new Decision
                {
                    ProjectId = projectId,
                    AgentName = entry.AgentName,
                    Type = entry.Type,
                    Status = "active",
                    Title = entry.Title,
                    Content = entry.Content,
                    Rationale = entry.Rationale,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                entry.Status = "merged";
                entry.MergedAt = now;
                entry.UpdatedAt = now;
            }

            // Step 2: Count architectural/scope entries needing coordinator review.
            var pendingReview = await memoryDb.DecisionInbox
                .CountAsync(e => e.ProjectId == projectId
                              && e.AgentName == agentName
                              && e.Status == "pending"
                              && (e.Type == "architectural" || e.Type == "scope")
                              && e.CreatedAt >= runStarted, ct).ConfigureAwait(false);

            // Step 3: Append outcome to the current open session.
            var session = await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (session is not null)
            {
                var outcome = $"Run {run.Id} by {agentName} completed.";
                session.Summary = string.IsNullOrEmpty(session.Summary)
                    ? outcome
                    : session.Summary + "\n" + outcome;
            }

            await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "PostRunScribe: run {RunId} — auto-merged {Merged}, {Review} pending coordinator review",
                run.Id, toMerge.Count, pendingReview);

            // Step 4: Export to .squad/ and .agentweaver/context/.
            await ExportAsync(run.ProjectId.Value, projectId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "PostRunScribe pass failed for run {RunId} — memory context unchanged", run.Id);
        }
    }

    private async Task ExportAsync(ProjectId pid, string projectId, CancellationToken ct)
    {
        var project = await projectStore.GetAsync(pid, ct).ConfigureAwait(false);
        if (project is null || string.IsNullOrEmpty(project.WorkingDirectory)) return;

        var decisions = await memoryDb.Decisions
            .Where(d => d.ProjectId == projectId && d.Status == "active")
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var inbox = await memoryDb.DecisionInbox
            .Where(e => e.ProjectId == projectId && e.Status == "pending")
            .ToListAsync(ct).ConfigureAwait(false);

        var memories = await memoryDb.AgentMemory
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var session = await memoryDb.SessionContexts
            .Where(s => s.ProjectId == projectId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var exporter = new SquadMemoryExporter(project.WorkingDirectory);

        await exporter.ExportAsync(
            decisions.Select(d => new DecisionExportDto(
                d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList(),
            inbox.Select(e => new InboxExportDto(
                e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList(),
            memories.Select(m => new MemoryExportDto(
                m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList(),
            session is null ? null : new SessionExportDto(
                session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary),
            ct).ConfigureAwait(false);
    }
}
