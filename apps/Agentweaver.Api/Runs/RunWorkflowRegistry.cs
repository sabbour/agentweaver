using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Maps runId -> active StreamingRun. Used by the review endpoint to send responses
/// and by the watch loop to track active workflow runs.
/// </summary>
public sealed class RunWorkflowRegistry
{
    private readonly ConcurrentDictionary<string, (StreamingRun Run, CancellationTokenSource Cts)> _runs = new();

    /// <summary>
    /// Registers an active run using a pre-created <see cref="CancellationTokenSource"/> that
    /// was already passed into the workflow execution so that <see cref="Abandon"/> can cancel it.
    /// The registry takes ownership of <paramref name="cts"/> and disposes it on removal.
    /// </summary>
    public CancellationToken Register(string runId, StreamingRun run, CancellationTokenSource cts)
    {
        (StreamingRun Run, CancellationTokenSource Cts)? replaced = null;
        _runs.AddOrUpdate(
            runId,
            _ => (run, cts),
            (_, existing) =>
            {
                replaced = existing;
                return (run, cts);
            });

        if (replaced is { } previous)
        {
            previous.Cts.Cancel();
            previous.Cts.Dispose();
        }

        return cts.Token;
    }

    public StreamingRun? Get(string runId) =>
        _runs.TryGetValue(runId, out var pair) ? pair.Run : null;

    public bool Abandon(string runId)
    {
        if (!_runs.TryRemove(runId, out var pair))
            return false;

        pair.Cts.Cancel();
        pair.Cts.Dispose();
        return true;
    }

    public bool Remove(string runId) => Abandon(runId);
}
