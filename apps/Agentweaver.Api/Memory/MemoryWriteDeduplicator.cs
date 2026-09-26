using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

        db.AgentMemory.Add(candidate);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (candidate, true);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var winner = await db.AgentMemory.SingleOrDefaultAsync(
                memory => memory.IdentityKey == candidate.IdentityKey, ct).ConfigureAwait(false);
            if (winner is null)
                throw;
            return (winner, false);
        }
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

        db.Decisions.Add(candidate);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (candidate, true);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var winner = await db.Decisions.SingleOrDefaultAsync(
                decision => decision.IdentityKey == candidate.IdentityKey, ct).ConfigureAwait(false);
            if (winner is null)
                throw;
            return (winner, false);
        }
    }

    public static void RefreshDecisionIdentity(Decision decision) =>
        decision.IdentityKey = DecisionIdentity(decision);

    public static void RefreshMemoryIdentity(AgentMemory memory) =>
        memory.IdentityKey = MemoryIdentity(memory);

    private static string MemoryIdentity(AgentMemory memory) => Hash(
        "memory", memory.ProjectId, memory.AgentName, memory.SessionId, memory.Type,
        memory.Importance, memory.Content, memory.Tags, memory.SourceKind,
        memory.SourceIdentity, memory.SourceRunId, memory.Status,
        memory.ReplacedById?.ToString());

    private static string DecisionIdentity(Decision decision) => Hash(
        "decision", decision.ProjectId, decision.AgentName, decision.Type, decision.Status,
        decision.Title, decision.Content, decision.Rationale, decision.Tags,
        decision.SupersededById?.ToString(), decision.SourceKind,
        decision.SourceIdentity, decision.SourceRunId,
        decision.Status == "active" ? null : decision.Id.ToString());

    private static string Hash(params string?[] values) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                or SqliteException { SqliteErrorCode: 19 })
                return true;
        }

        return false;
    }
}
