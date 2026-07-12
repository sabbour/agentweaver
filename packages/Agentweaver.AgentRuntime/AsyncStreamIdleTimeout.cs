using System.Runtime.CompilerServices;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Inactivity watchdog for streaming agent turns.
///
/// <para>
/// Root cause this guards against: a GitHub Copilot SDK streaming turn can hang <b>inside the
/// SDK</b> after the inner agent is constructed — it yields no chunk, never completes, and never
/// observes the run cancellation token. Observed in production: a turn logged "Inner Copilot
/// AIAgent created" and then produced <b>zero</b> events for hours, permanently stranding the run
/// in <c>in_progress</c> (the whole orchestration can never reach a terminal state). Because the
/// SDK is an external dependency we cannot patch, AgentWeaver must bound each turn itself.
/// </para>
///
/// <para>
/// <see cref="WithIdleTimeout{T}"/> re-arms a linked-token timer before every
/// <c>MoveNextAsync</c>. If the source produces no chunk within <paramref name="idleTimeout"/>,
/// the turn is aborted with a <b>retryable</b> <see cref="AgentProviderException"/> so the run
/// fails cleanly (and can be picked up again) instead of hanging forever. Any delivered chunk
/// resets the window, so legitimately slow first tokens and long-running tool calls keep the turn
/// alive. Real run cancellation (the caller's token) propagates unchanged as
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </summary>
internal static class AsyncStreamIdleTimeout
{
    public static async IAsyncEnumerable<T> WithIdleTimeout<T>(
        this IAsyncEnumerable<T> source,
        TimeSpan idleTimeout,
        string runId,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A non-positive timeout disables the watchdog (pass-through), preserving the original
        // "wait indefinitely" behavior for callers that opt out.
        if (idleTimeout <= TimeSpan.Zero)
        {
            await foreach (var passthrough in source.WithCancellation(ct).ConfigureAwait(false))
                yield return passthrough;
            yield break;
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = source.GetAsyncEnumerator(idleCts.Token);

        while (true)
        {
            // (Re)arm the inactivity window before awaiting the next chunk. The first arm covers
            // time-to-first-chunk (the exact hang observed); subsequent arms cover the gap between
            // consecutive chunks (e.g. a long tool execution between its start and complete events).
            idleCts.CancelAfter(idleTimeout);

            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Idle watchdog tripped: the source produced no output for the whole window while
                // the caller's token is still live. Treat as a hung turn.
                logger.LogError(
                    "Streaming turn produced no output for {IdleSeconds:n0}s (runId={RunId}); treating as a hung turn and failing the run (retryable).",
                    idleTimeout.TotalSeconds, runId);

                throw new AgentProviderException(
                    ModelSource.GitHubCopilot,
                    AgentProviderFailureKind.ProviderUnavailable,
                    "github_copilot_turn_stalled",
                    $"The GitHub Copilot turn stalled with no output for {idleTimeout.TotalSeconds:n0} seconds and was aborted. Retry the run.",
                    isRetryable: true);
            }

            if (!moved)
                yield break;

            yield return enumerator.Current;
        }
    }
}
