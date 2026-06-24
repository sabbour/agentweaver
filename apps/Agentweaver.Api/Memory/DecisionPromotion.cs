using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Shared inbox to active <see cref="Decision"/> promotion logic. Used by both the
/// <c>POST /api/projects/{id}/decisions/inbox/{entryId}/merge</c> endpoint and the
/// coordinator-side finalization backstop so the mapping lives in exactly one place.
/// </summary>
public static class DecisionPromotion
{
    /// <summary>
    /// Decision types the Coordinator (not the per-run Scribe) is responsible for reviewing and
    /// promoting. The Scribe auto-merges learning/pattern/update; these are left for the Coordinator.
    /// </summary>
    public static readonly string[] CoordinatorReviewTypes = ["architectural", "scope"];

    /// <summary>
    /// Marks a pending inbox <paramref name="entry"/> as merged and adds the corresponding active
    /// <see cref="Decision"/> to <paramref name="db"/>. Does NOT save or open a transaction — the
    /// caller owns persistence so it can batch and control transactional scope.
    /// </summary>
    public static Decision PromoteEntry(MemoryDbContext db, DecisionInboxEntry entry, DateTimeOffset now)
    {
        entry.Status = "merged";
        entry.UpdatedAt = now;
        entry.MergedAt = now;

        var decision = new Decision
        {
            ProjectId = entry.ProjectId,
            AgentName = entry.AgentName,
            Type = entry.Type,
            Status = "active",
            Title = entry.Title,
            Content = entry.Content,
            Rationale = entry.Rationale,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Decisions.Add(decision);
        return decision;
    }

    /// <summary>
    /// Backstop used by the Coordinator finalization pass: promotes every still-pending
    /// architectural/scope inbox entry for <paramref name="projectId"/> into an active decision.
    /// Transactional and idempotent (only pending entries are touched). Returns the number promoted.
    /// </summary>
    public static async Task<int> PromotePendingCoordinatorDecisionsAsync(
        MemoryDbContext db, string projectId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // EF Core/SQLite cannot translate array.Contains in WHERE — filter the type set in memory.
        var pending = (await db.DecisionInbox
            .Where(e => e.ProjectId == projectId && e.Status == "pending")
            .ToListAsync(ct).ConfigureAwait(false))
            .Where(e => CoordinatorReviewTypes.Contains(e.Type))
            .ToList();

        if (pending.Count == 0)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in pending)
            PromoteEntry(db, entry, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return pending.Count;
    }
}
