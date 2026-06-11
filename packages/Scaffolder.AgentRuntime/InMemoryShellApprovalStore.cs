using System.Collections.Concurrent;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// In-memory shell approval store. Approvals are per-run and ephemeral
/// (lost on process restart — acceptable since runs are also ephemeral).
/// </summary>
public sealed class InMemoryShellApprovalStore : IShellApprovalStore
{
    // runId -> set of approved command hashes
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _approvals = new();

    public void Approve(string runId, string commandHash)
        => _approvals.GetOrAdd(runId, _ => new()).TryAdd(commandHash, 0);

    public bool IsApproved(string runId, string commandHash)
    {
        if (!_approvals.TryGetValue(runId, out var hashes)) return false;
        return hashes.TryRemove(commandHash, out _);
    }

    public void Clear(string runId)
        => _approvals.TryRemove(runId, out _);
}
