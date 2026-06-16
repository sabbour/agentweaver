using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Domain;
using Scaffolder.Squad.Memory;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Closes the memory flywheel after a project run reaches a terminal state.
/// Auto-merges low-risk inbox entries (learning/pattern/update) into AgentMemory,
/// exports to .squad/ and .agentweaver/context/, and updates the current session.
/// </summary>
public sealed class PostRunScribeService(
    SqliteRunStore runStore,
    MemoryDbContext memoryDb,
    IProjectStore projectStore,
    ILogger<PostRunScribeService> logger)
{
    public async Task RunAsync(string runId, CancellationToken ct = default)
    {
        var run = await runStore.GetAsync(RunId.Parse(runId), ct);
        if (run is null || string.IsNullOrEmpty(run.AgentName) || !run.ProjectId.HasValue)
        {
            logger.LogDebug("PostRunScribe skipped for run {RunId} — no agent name or project", runId);
            return;
        }
        var projectId = run.ProjectId.Value.ToString();
        var agentName = run.AgentName;
        var runStarted = run.StartedAt;

        try
        {
            // Step 1: Auto-merge low-risk inbox entries submitted during this run
            var toMerge = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.AgentName == agentName
                         && e.Status == "pending"
                         && (e.Type == "learning" || e.Type == "pattern" || e.Type == "update")
                         && e.CreatedAt >= runStarted)
                .ToListAsync(ct);

            foreach (var entry in toMerge)
            {
                var memory = new AgentMemory
                {
                    ProjectId = projectId,
                    AgentName = agentName,
                    Type = entry.Type,
                    Importance = "medium",
                    Content = entry.Content,
                    Tags = null,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                memoryDb.AgentMemory.Add(memory);
                entry.Status = "merged";
                entry.MergedAt = DateTimeOffset.UtcNow;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // Step 2: Update current session summary
            var session = await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync(ct);

            if (session is not null)
            {
                var outcome = $"Run {runId} by {agentName} completed. Auto-merged {toMerge.Count} inbox entries.";
                session.Summary = string.IsNullOrEmpty(session.Summary)
                    ? outcome
                    : session.Summary + "\n" + outcome;
            }

            await memoryDb.SaveChangesAsync(ct);

            // Step 3: Export to .squad/
            if (!ProjectId.TryParse(projectId, out var parsedProjectId))
                return;

            var project = await projectStore.GetAsync(parsedProjectId, ct);
            if (project?.WorkingDirectory is not null)
            {
                var decisions = await memoryDb.Decisions
                    .Where(d => d.ProjectId == projectId && d.Status == "active")
                    .ToListAsync(ct);
                var inbox = await memoryDb.DecisionInbox
                    .Where(e => e.ProjectId == projectId && e.Status == "pending")
                    .ToListAsync(ct);
                var memories = await memoryDb.AgentMemory
                    .Where(m => m.ProjectId == projectId)
                    .ToListAsync(ct);

                var decisionDtos = decisions.Select(d => new DecisionExportDto(
                    d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList();
                var inboxDtos = inbox.Select(e => new InboxExportDto(
                    e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList();
                var memoryDtos = memories.Select(m => new MemoryExportDto(
                    m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList();
                var sessionDto = session is null ? null : new SessionExportDto(
                    session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary);

                var exporter = new SquadMemoryExporter(project.WorkingDirectory);
                await exporter.ExportAsync(decisionDtos, inboxDtos, memoryDtos, sessionDto, ct);
            }

            logger.LogInformation(
                "PostRunScribe: run {RunId} — auto-merged {Merged} entries into AgentMemory, export complete",
                runId, toMerge.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostRunScribe pass failed for run {RunId} — memory context unchanged", runId);
        }
    }
}
