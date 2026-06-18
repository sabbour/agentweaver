using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Maps runId -> pending ExternalRequest. Thread-safe. Supports TryRemove for atomic
/// consumption (replay/double-POST protection).
/// </summary>
public sealed class PendingRequestStore
{
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public void Set(string runId, ExternalRequest request, string ownerUser)
    {
        _pending[runId] = new PendingEntry(request, ownerUser);
    }

    public PendingEntry? Get(string runId) =>
        _pending.TryGetValue(runId, out var entry) ? entry : null;

    /// <summary>
    /// Atomically removes and returns the pending request. Returns null if already consumed.
    /// This guarantees at-most-once delivery (replay/double-POST protection).
    /// </summary>
    public PendingEntry? TryRemove(string runId) =>
        _pending.TryRemove(runId, out var entry) ? entry : null;
}

/// <summary>Pending request entry with owner for IDOR defense.</summary>
public sealed record PendingEntry(ExternalRequest Request, string OwnerUser);
