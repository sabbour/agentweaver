using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Memory;

public static class KnowledgeRevisionBackfill
{
    public static async Task EnsureLegacyFingerprintsAsync(
        MemoryDbContext db,
        CancellationToken ct = default)
    {
        var memoryRevisions = await db.AgentMemoryRevisions
            .Where(revision => revision.Revision == 1
                && (revision.SourceIdentityFingerprint == null
                    || (revision.ApprovedAt != null && revision.ApprovedByFingerprint == null)))
            .ToListAsync(ct);
        var decisionRevisions = await db.DecisionRevisions
            .Where(revision => revision.Revision == 1
                && (revision.SourceIdentityFingerprint == null
                    || (revision.ApprovedAt != null && revision.ApprovedByFingerprint == null)))
            .ToListAsync(ct);
        if (memoryRevisions.Count == 0 && decisionRevisions.Count == 0)
            return;

        var memoryIds = memoryRevisions.Select(revision => revision.MemoryId).ToArray();
        var decisionIds = decisionRevisions.Select(revision => revision.DecisionId).ToArray();
        var memories = await db.AgentMemory.AsNoTracking()
            .Where(memory => memoryIds.Contains(memory.Id))
            .ToDictionaryAsync(memory => memory.Id, ct);
        var decisions = await db.Decisions.AsNoTracking()
            .Where(decision => decisionIds.Contains(decision.Id))
            .ToDictionaryAsync(decision => decision.Id, ct);

        foreach (var revision in memoryRevisions)
        {
            if (!memories.TryGetValue(revision.MemoryId, out var memory))
                continue;
            revision.SourceIdentityFingerprint ??= Fingerprint(memory.SourceIdentity);
            revision.ApprovedByFingerprint ??= Fingerprint(memory.ApprovedBy);
        }

        foreach (var revision in decisionRevisions)
        {
            if (!decisions.TryGetValue(revision.DecisionId, out var decision))
                continue;
            revision.SourceIdentityFingerprint ??= Fingerprint(decision.SourceIdentity);
            revision.ApprovedByFingerprint ??= Fingerprint(decision.ApprovedBy);
        }

        db.SuppressKnowledgeRevisionCapture = true;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.SuppressKnowledgeRevisionCapture = false;
        }
    }

    private static string? Fingerprint(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
