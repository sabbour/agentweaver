using System.Collections.Concurrent;
using Scaffolder.Squad.Model;

namespace Scaffolder.Api.Casting;

/// <summary>
/// Process-local, thread-safe store for pending cast proposals.
/// At most one active proposal per project — a new proposal supersedes any prior one.
/// Proposals expire after 30 minutes.
/// </summary>
public sealed class CastProposalStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private sealed record Entry(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt);

    // key = projectId
    private readonly ConcurrentDictionary<string, Entry> _byProject = new(StringComparer.Ordinal);

    public void Store(string projectId, CastProposal proposal, string owner)
    {
        var entry = new Entry(proposal, owner, DateTimeOffset.UtcNow.Add(Ttl));
        _byProject[projectId] = entry;
    }

    public (CastProposal? Proposal, string? Owner) Get(string projectId, string proposalId)
    {
        if (!_byProject.TryGetValue(projectId, out var entry))
            return (null, null);

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _byProject.TryRemove(projectId, out _);
            return (null, null);
        }

        if (!string.Equals(entry.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
            return (null, null);

        return (entry.Proposal, entry.Owner);
    }

    public bool Remove(string projectId, string proposalId)
    {
        if (!_byProject.TryGetValue(projectId, out var entry))
            return false;

        if (!string.Equals(entry.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
            return false;

        return _byProject.TryRemove(projectId, out _);
    }

    public CastProposal? GetByProject(string projectId)
    {
        if (!_byProject.TryGetValue(projectId, out var entry))
            return null;

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _byProject.TryRemove(projectId, out _);
            return null;
        }

        return entry.Proposal;
    }
}
