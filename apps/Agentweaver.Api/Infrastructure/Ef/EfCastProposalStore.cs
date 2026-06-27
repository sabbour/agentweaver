using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Memory;
using Agentweaver.Squad.Model;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed cast proposal store with in-memory write-through cache.
/// Replaces the SQLite-backed CastProposalStore for Postgres deployments.
/// </summary>
public sealed class EfCastProposalStore : ICastProposalStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private sealed record Entry(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _byProject = new(StringComparer.Ordinal);
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public EfCastProposalStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    public void Store(string projectId, CastProposal proposal, string owner)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Ttl);
        var entry = new Entry(proposal, owner, expiresAt);
        _byProject[projectId] = entry;

        // Persist to DB (best-effort — in-memory cache is the hot path)
        try
        {
            using var db = _factory.CreateDbContext();
            var existing = db.CastProposals.FirstOrDefault(p => p.Id == proposal.ProposalId);
            if (existing is not null)
            {
                existing.Owner = owner;
                existing.ExpiresAt = expiresAt;
                existing.ProposalJson = JsonSerializer.Serialize(proposal, _jsonOptions);
            }
            else
            {
                db.CastProposals.Add(new CastProposalRecord
                {
                    Id = proposal.ProposalId,
                    ProjectId = projectId,
                    Owner = owner,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = expiresAt,
                    ProposalJson = JsonSerializer.Serialize(proposal, _jsonOptions),
                });
            }
            db.SaveChanges();

            // Remove any other proposals for this project (only one active per project)
            db.CastProposals
                .Where(p => p.ProjectId == projectId && p.Id != proposal.ProposalId)
                .ExecuteDelete();
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

    public IReadOnlyList<(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt)> ListByProject(string projectId)
    {
        try
        {
            using var db = _factory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var rows = db.CastProposals
                .AsNoTracking()
                .Where(p => p.ProjectId == projectId && p.ExpiresAt > now)
                .ToList();

            var results = new List<(CastProposal, string, DateTimeOffset)>();
            foreach (var row in rows)
            {
                var proposal = JsonSerializer.Deserialize<CastProposal>(row.ProposalJson, _jsonOptions);
                if (proposal is not null)
                    results.Add((proposal, row.Owner, row.ExpiresAt));
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
            using var db = _factory.CreateDbContext();
            var row = db.CastProposals.AsNoTracking()
                .FirstOrDefault(p => p.ProjectId == projectId && p.Id == proposalId);
            if (row is null) return (null, null);

            if (DateTimeOffset.UtcNow > row.ExpiresAt)
            {
                PurgeFromDb(proposalId);
                return (null, null);
            }

            var proposal = JsonSerializer.Deserialize<CastProposal>(row.ProposalJson, _jsonOptions);
            if (proposal is not null)
                _byProject[projectId] = new Entry(proposal, row.Owner, row.ExpiresAt);
            return (proposal, row.Owner);
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
            using var db = _factory.CreateDbContext();
            db.CastProposals.Where(p => p.Id == proposalId).ExecuteDelete();
        }
        catch { /* best-effort */ }
    }
}
