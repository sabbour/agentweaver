using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Maps runId -> active StreamingRun. Used by the review endpoint to send responses
/// and by the watch loop to track active workflow runs.
/// </summary>
public sealed class RunWorkflowRegistry
{
    private readonly ConcurrentDictionary<string, StreamingRun> _runs = new();

    public void Register(string runId, StreamingRun run) => _runs[runId] = run;

    public StreamingRun? Get(string runId) =>
        _runs.TryGetValue(runId, out var run) ? run : null;

    public bool Remove(string runId) => _runs.TryRemove(runId, out _);
}
