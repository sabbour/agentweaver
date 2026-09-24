using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Memory;

public static class MemoryWriteDeduplicator
{
    public static async Task<(AgentMemory Record, bool Created)> GetOrCreateMemoryAsync(
        MemoryDbContext db,
        AgentMemory candidate,
        CancellationToken ct = default)
    {
        candidate.IdentityKey = MemoryIdentity(candidate);
        var existing = await db.AgentMemory
            .FirstOrDefaultAsync(memory => memory.IdentityKey == candidate.IdentityKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
            return (existing, false);

        existing = await db.AgentMemory
            .Where(memory =>
                memory.ProjectId == candidate.ProjectId
                && memory.AgentName == candidate.AgentName
                && memory.SessionId == candidate.SessionId
                && memory.Type == candidate.Type
                && memory.Importance == candidate.Importance
                && memory.Content == candidate.Content
                && memory.Tags == candidate.Tags
                && memory.SourceKind == candidate.SourceKind
                && memory.SourceIdentity == candidate.SourceIdentity
                && memory.SourceRunId == candidate.SourceRunId)
            .OrderBy(memory => memory.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.IdentityKey ??= candidate.IdentityKey;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (existing, false);
        }

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentMemory"
                ("ProjectId", "AgentName", "SessionId", "Type", "Importance", "Content", "Tags",
                 "SourceKind", "SourceIdentity", "SourceRunId", "TrustState", "ApprovedBy",
                 "ApprovedAt", "IdentityKey", "CreatedAt", "UpdatedAt")
            VALUES
                ({candidate.ProjectId}, {candidate.AgentName}, {candidate.SessionId}, {candidate.Type},
                 {candidate.Importance}, {candidate.Content}, {candidate.Tags}, {candidate.SourceKind},
                 {candidate.SourceIdentity}, {candidate.SourceRunId}, {candidate.TrustState},
                 {candidate.ApprovedBy}, {candidate.ApprovedAt}, {candidate.IdentityKey},
                 {candidate.CreatedAt}, {candidate.UpdatedAt})
            ON CONFLICT ("IdentityKey") DO NOTHING
            """, ct).ConfigureAwait(false);

        return (await db.AgentMemory.SingleAsync(
            memory => memory.IdentityKey == candidate.IdentityKey, ct).ConfigureAwait(false),
            inserted == 1);
    }

    public static async Task<(Decision Record, bool Created)> GetOrCreateDecisionAsync(
        MemoryDbContext db,
        Decision candidate,
        CancellationToken ct = default)
    {
        candidate.IdentityKey = DecisionIdentity(candidate);
        var existing = await db.Decisions
            .FirstOrDefaultAsync(decision => decision.IdentityKey == candidate.IdentityKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
            return (existing, false);

        existing = await db.Decisions
            .Where(decision =>
                decision.ProjectId == candidate.ProjectId
                && decision.AgentName == candidate.AgentName
                && decision.Type == candidate.Type
                && decision.Status == candidate.Status
                && decision.Title == candidate.Title
                && decision.Content == candidate.Content
                && decision.Rationale == candidate.Rationale
                && decision.Tags == candidate.Tags
                && decision.SupersededById == candidate.SupersededById
                && decision.SourceKind == candidate.SourceKind
                && decision.SourceIdentity == candidate.SourceIdentity
                && decision.SourceRunId == candidate.SourceRunId)
            .OrderBy(decision => decision.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.IdentityKey ??= candidate.IdentityKey;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (existing, false);
        }

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Decisions"
                ("ProjectId", "AgentName", "Type", "Status", "Title", "Content", "Rationale", "Tags",
                 "SupersededById", "SourceKind", "SourceIdentity", "SourceRunId", "TrustState",
                 "ApprovedBy", "ApprovedAt", "IdentityKey", "CreatedAt", "UpdatedAt")
            VALUES
                ({candidate.ProjectId}, {candidate.AgentName}, {candidate.Type}, {candidate.Status},
                 {candidate.Title}, {candidate.Content}, {candidate.Rationale}, {candidate.Tags},
                 {candidate.SupersededById}, {candidate.SourceKind}, {candidate.SourceIdentity},
                 {candidate.SourceRunId}, {candidate.TrustState}, {candidate.ApprovedBy},
                 {candidate.ApprovedAt}, {candidate.IdentityKey}, {candidate.CreatedAt},
                 {candidate.UpdatedAt})
            ON CONFLICT ("IdentityKey") DO NOTHING
            """, ct).ConfigureAwait(false);

        return (await db.Decisions.SingleAsync(
            decision => decision.IdentityKey == candidate.IdentityKey, ct).ConfigureAwait(false),
            inserted == 1);
    }

    public static void RefreshDecisionIdentity(Decision decision) =>
        decision.IdentityKey = DecisionIdentity(decision);

    private static string MemoryIdentity(AgentMemory memory) => Hash(
        "memory", memory.ProjectId, memory.AgentName, memory.SessionId, memory.Type,
        memory.Importance, memory.Content, memory.Tags, memory.SourceKind,
        memory.SourceIdentity, memory.SourceRunId);

    private static string DecisionIdentity(Decision decision) => Hash(
        "decision", decision.ProjectId, decision.AgentName, decision.Type, decision.Status,
        decision.Title, decision.Content, decision.Rationale, decision.Tags,
        decision.SupersededById?.ToString(), decision.SourceKind,
        decision.SourceIdentity, decision.SourceRunId,
        decision.Status == "active" ? null : decision.Id.ToString());

    private static string Hash(params string?[] values) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
}
