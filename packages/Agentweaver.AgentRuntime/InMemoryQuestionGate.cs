using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// In-memory <see cref="IQuestionGate"/> that uses <see cref="TaskCompletionSource{T}"/> to
/// suspend the <c>ask_question</c> tool call until the operator supplies an answer.
/// The gate is keyed by <c>(runId, requestId)</c>; each requestId may only be answered once.
/// Mirrors <see cref="InMemoryToolApprovalGate"/>.
/// </summary>
public sealed class InMemoryQuestionGate : IQuestionGate
{
    // Two-level dictionary: runId → requestId → TCS (answer string, or null on timeout/clear)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskCompletionSource<string?>>> _pending = new();

    /// <inheritdoc />
    public async Task<string?> AskAsync(
        string runId,
        string requestId,
        string question,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var runPending = _pending.GetOrAdd(runId, _ => new ConcurrentDictionary<string, TaskCompletionSource<string?>>());
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Atomically register or replace an existing entry for this requestId.
        // If a duplicate arrives (retry), the previous TCS is resolved as null so it doesn't leak.
        runPending.AddOrUpdate(requestId,
            addValueFactory: _ => tcs,
            updateValueFactory: (_, existing) => { existing.TrySetResult(null); return tcs; });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var reg = cts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            runPending.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc />
    public bool Answer(string runId, string requestId, string answer)
    {
        if (!_pending.TryGetValue(runId, out var runPending)) return false;
        if (!runPending.TryGetValue(requestId, out var tcs)) return false;
        return tcs.TrySetResult(answer);
    }

    /// <inheritdoc />
    public void Clear(string runId)
    {
        if (_pending.TryRemove(runId, out var runPending))
        {
            foreach (var tcs in runPending.Values)
                tcs.TrySetResult(null);
        }
    }
}
