using Microsoft.EntityFrameworkCore;
using System.Data;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Squad.Memory;

namespace Agentweaver.Api.Runs;

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
            await using var tx = await memoryDb.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, ct)
                .ConfigureAwait(false);

            // Step 1: Auto-merge low-risk inbox entries created during this run.
            // Keep the project/agent/status/type filters in SQL. DateTimeOffset comparison stays in
            // memory because EF Core's SQLite provider cannot translate it reliably.
            var relevantPending = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.AgentName == agentName
                         && e.Status == "pending"
                         && (e.Type == "learning"
                             || e.Type == "pattern"
                             || e.Type == "update"
                             || e.Type == "architectural"
                             || e.Type == "scope"))
                .ToListAsync(ct).ConfigureAwait(false);

            var runCandidates = relevantPending
                .Where(e => e.CreatedAt >= runStarted)
                .ToList();

            var toMerge = runCandidates
                .Where(e => AutoMergeTypes.Contains(e.Type))
                .ToList();

            var mergedEntries = new List<(Decision Decision, DecisionInboxEntry Entry)>();

            foreach (var entry in toMerge)
            {
                var decision = new Decision
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
                };

                memoryDb.Decisions.Add(decision);
                mergedEntries.Add((decision, entry));
                entry.Status = "merged";
                entry.MergedAt = now;
                entry.UpdatedAt = now;
            }

            if (mergedEntries.Count > 0)
            {
                await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);

                foreach (var (decision, entry) in mergedEntries)
                {
                    entry.DecisionId = decision.Id;
                }
            }

            // Step 2: Count architectural/scope entries needing coordinator review.
            var pendingReview = runCandidates
                .Count(e => e.Status == "pending"
                         && (e.Type == "architectural" || e.Type == "scope"));

            // Step 3: Append outcome to the current open session.
            // EF Core/SQLite cannot translate DateTimeOffset in ORDER BY — load then sort in memory.
            var session = (await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .ToListAsync(ct).ConfigureAwait(false))
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefault();

            if (session is not null)
            {
                var outcome = $"Run {run.Id} by {agentName} completed.";
                session.Summary = string.IsNullOrEmpty(session.Summary)
                    ? outcome
                    : session.Summary + "\n" + outcome;
            }

            await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

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

        var decisions = (await memoryDb.Decisions
            .Where(d => d.ProjectId == projectId && d.Status == "active")
            .ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        var inbox = await memoryDb.DecisionInbox
            .Where(e => e.ProjectId == projectId && e.Status == "pending")
            .ToListAsync(ct).ConfigureAwait(false);

        var memories = (await memoryDb.AgentMemory
            .Where(m => m.ProjectId == projectId)
            .ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        var session = (await memoryDb.SessionContexts
            .Where(s => s.ProjectId == projectId && s.EndedAt == null)
            .ToListAsync(ct).ConfigureAwait(false))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

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
