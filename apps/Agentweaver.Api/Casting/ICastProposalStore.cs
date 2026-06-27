using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Casting;

public interface ICastProposalStore
{
    void Store(string projectId, CastProposal proposal, string owner);
    (CastProposal? Proposal, string? Owner) Get(string projectId, string proposalId);
    bool Remove(string projectId, string proposalId);
    CastProposal? GetByProject(string projectId);
    IReadOnlyList<(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt)> ListByProject(string projectId);
}
