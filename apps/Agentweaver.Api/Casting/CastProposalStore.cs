using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Casting;

/// <summary>
/// SQLite-backed, thread-safe store for pending cast proposals.
/// At most one active proposal per project — a new proposal supersedes any prior one.
/// Proposals expire after 30 minutes. An in-memory index provides fast path reads
/// while SQLite provides durability across restarts.
/// </summary>
public sealed class CastProposalStore : ICastProposalStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private sealed record Entry(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt);

    // In-memory write-through cache: project_id -> latest entry
    private readonly ConcurrentDictionary<string, Entry> _byProject = new(StringComparer.Ordinal);
    private readonly SqliteDb _db;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public CastProposalStore(SqliteDb db) => _db = db;

    public void Store(string projectId, CastProposal proposal, string owner)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Ttl);
        var entry = new Entry(proposal, owner, expiresAt);
        _byProject[projectId] = entry;

        // Persist to DB (best-effort — in-memory cache is the hot path)
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO cast_proposals (id, project_id, owner, created_at, expires_at, proposal_json)
                VALUES ($id, $projectId, $owner, $createdAt, $expiresAt, $proposalJson);
                """;
            cmd.Parameters.AddWithValue("$id", proposal.ProposalId);
            cmd.Parameters.AddWithValue("$projectId", projectId);
            cmd.Parameters.AddWithValue("$owner", owner);
            cmd.Parameters.AddWithValue("$createdAt", Ts(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$expiresAt", Ts(expiresAt));
            cmd.Parameters.AddWithValue("$proposalJson", JsonSerializer.Serialize(proposal, _jsonOptions));
            cmd.ExecuteNonQuery();

            // Remove any other proposals for this project (only one active per project)
            using var del = connection.CreateCommand();
            del.CommandText = "DELETE FROM cast_proposals WHERE project_id = $projectId AND id != $id;";
            del.Parameters.AddWithValue("$projectId", projectId);
            del.Parameters.AddWithValue("$id", proposal.ProposalId);
            del.ExecuteNonQuery();
        }
        catch { /* best-effort: in-memory cache is authoritative for same-process reads */ }
    }

    public (CastProposal? Proposal, string? Owner) Get(string projectId, string proposalId)
    {
        // Fast path: in-memory cache
        if (_byProject.TryGetValue(projectId, out var entry))
        {
            if (DateTimeOffset.UtcNow > entry.ExpiresAt)
            {
                _byProject.TryRemove(projectId, out _);
                PurgeFromDb(proposalId);
                return (null, null);
            }
            if (!string.Equals(entry.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
                return (null, null);
            return (entry.Proposal, entry.Owner);
        }

        // Slow path: load from DB (e.g. after a restart)
        return LoadFromDb(projectId, proposalId);
    }

    public bool Remove(string projectId, string proposalId)
    {
        bool found = false;
        if (_byProject.TryGetValue(projectId, out var entry) &&
            string.Equals(entry.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
        {
            found = _byProject.TryRemove(projectId, out _);
        }
        else
        {
            // Not in cache — check DB
            var (proposal, _) = LoadFromDb(projectId, proposalId);
            found = proposal is not null;
        }
        PurgeFromDb(proposalId);
        return found;
    }

    public CastProposal? GetByProject(string projectId)
    {
        if (_byProject.TryGetValue(projectId, out var entry))
        {
            if (DateTimeOffset.UtcNow > entry.ExpiresAt)
            {
                _byProject.TryRemove(projectId, out _);
                return null;
            }
            return entry.Proposal;
        }
        return null;
    }

    // Returns all active (non-expired) proposals for a project from the DB.
    public IReadOnlyList<(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt)> ListByProject(string projectId)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT proposal_json, owner, expires_at FROM cast_proposals WHERE project_id = $projectId AND expires_at > $now;";
            cmd.Parameters.AddWithValue("$projectId", projectId);
            cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
            using var reader = cmd.ExecuteReader();
            var results = new List<(CastProposal, string, DateTimeOffset)>();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var owner = reader.GetString(1);
                var expiresAt = DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);
                var proposal = JsonSerializer.Deserialize<CastProposal>(json, _jsonOptions);
                if (proposal is not null)
                    results.Add((proposal, owner, expiresAt));
            }
            return results;
        }
        catch
        {
            return [];
        }
    }

    private (CastProposal? Proposal, string? Owner) LoadFromDb(string projectId, string proposalId)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT proposal_json, owner, expires_at FROM cast_proposals WHERE project_id = $projectId AND id = $id;";
            cmd.Parameters.AddWithValue("$projectId", projectId);
            cmd.Parameters.AddWithValue("$id", proposalId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (null, null);

            var json = reader.GetString(0);
            var owner = reader.GetString(1);
            var expiresAt = DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);

            if (DateTimeOffset.UtcNow > expiresAt)
            {
                PurgeFromDb(proposalId);
                return (null, null);
            }

            var proposal = JsonSerializer.Deserialize<CastProposal>(json, _jsonOptions);
            if (proposal is not null)
            {
                // Warm the cache
                _byProject[projectId] = new Entry(proposal, owner, expiresAt);
            }
            return (proposal, owner);
        }
        catch
        {
            return (null, null);
        }
    }

    private void PurgeFromDb(string proposalId)
    {
        try
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cast_proposals WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", proposalId);
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort */ }
    }

    private SqliteConnection OpenConnection()
    {
        // Synchronous helper — proposal operations are lightweight
        var task = _db.OpenConnectionAsync();
        task.GetAwaiter().GetResult();
        return task.Result;
    }

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
}
