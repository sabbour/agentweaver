using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Agentweaver.Squad.Memory;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Single source of truth for materializing the authoritative DB-backed team ledger
/// (active decisions, pending inbox entries, agent memory, and the current session) into the
/// on-disk <c>.squad/</c> + <c>.agentweaver/context/</c> file mirror for a given target directory.
///
/// <para>
/// Consolidates logic that was previously duplicated across the <c>/memory/export</c> endpoint,
/// the per-write best-effort refresh (<c>MemoryExportHelpers.TryExportAsync</c>), and
/// <see cref="Agentweaver.Api.Runs.PostRunScribeService"/>. It is also used to mirror the ledger
/// into a run's git worktree immediately before commit, so the ledger rides the same commit/push
/// flow as the run's other changes (issue #539).
/// </para>
/// </summary>
internal static class MemoryLedgerExporter
{
    /// <summary>
    /// Queries the project's authoritative memory state and writes the file mirror into
    /// <paramref name="targetDirectory"/>. <b>Throws</b> on failure so explicit sync actions can
    /// surface an actionable error rather than reporting a false success.
    /// </summary>
    public static async Task ExportAsync(
        string projectId,
        string targetDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct)
    {
        // Only ACTIVE decisions are authoritative "accepted state" (spec #25). Superseded/archived
        // decisions must not be mirrored as live team boundaries.
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

        // EF Core/SQLite cannot translate DateTimeOffset in ORDER BY — load then sort in memory.
        var session = (await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .ToListAsync(ct).ConfigureAwait(false))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        var exporter = new SquadMemoryExporter(targetDirectory);
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

    /// <summary>
    /// Best-effort variant used by incidental refresh paths that must not fail an otherwise
    /// successful DB write. Never swallows silently: returns <c>false</c> and logs a warning on
    /// failure so callers can honestly report whether the file mirror was updated.
    /// </summary>
    public static async Task<bool> TryExportAsync(
        string projectId,
        string targetDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct,
        ILogger logger)
    {
        try
        {
            await ExportAsync(projectId, targetDirectory, memoryDb, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to export project memory ledger for {ProjectId} to {TargetDirectory}.",
                projectId, targetDirectory);
            return false;
        }
    }

    /// <summary>
    /// True when the project has committed ledger content worth mirroring into a repository
    /// worktree (at least one active decision or any agent memory). Guards against writing an empty
    /// <c>decisions.md</c> into repositories that never used the memory feature.
    /// </summary>
    public static async Task<bool> HasExportableContentAsync(
        string projectId,
        MemoryDbContext memoryDb,
        CancellationToken ct)
    {
        if (await memoryDb.Decisions
                .AnyAsync(d => d.ProjectId == projectId && d.Status == "active", ct)
                .ConfigureAwait(false))
            return true;

        return await memoryDb.AgentMemory
            .AnyAsync(m => m.ProjectId == projectId, ct)
            .ConfigureAwait(false);
    }
}
