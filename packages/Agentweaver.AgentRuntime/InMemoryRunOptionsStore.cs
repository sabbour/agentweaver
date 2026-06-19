using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// In-memory <see cref="IRunOptionsStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Mirrors the threading model of <see cref="InMemoryToolApprovalGate"/> / <see cref="InMemoryQuestionGate"/>:
/// a process-lifetime singleton that survives within a run and is cleared on completion.
/// </summary>
public sealed class InMemoryRunOptionsStore : IRunOptionsStore
{
    private readonly ConcurrentDictionary<string, RunOptions> _options = new();

    /// <inheritdoc />
    public void Set(string runId, RunOptions options) => _options[runId] = options;

    /// <inheritdoc />
    public RunOptions Get(string runId) =>
        _options.TryGetValue(runId, out var opts) ? opts : new RunOptions();

    /// <inheritdoc />
    public void SetAutoApproveTools(string runId, bool enabled) =>
        _options.AddOrUpdate(
            runId,
            _ => new RunOptions(AutoApproveTools: enabled),
            (_, existing) => existing with { AutoApproveTools = enabled });

    /// <inheritdoc />
    public void SetAutopilot(string runId, bool enabled) =>
        _options.AddOrUpdate(
            runId,
            _ => new RunOptions(Autopilot: enabled),
            (_, existing) => existing with { Autopilot = enabled });

    /// <inheritdoc />
    public void Clear(string runId) => _options.TryRemove(runId, out _);
}
