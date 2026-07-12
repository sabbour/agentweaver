using System.Runtime.CompilerServices;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentRuntime;

/// <summary>Configuration for the agent-turn streaming watchdog.</summary>
internal sealed record StreamWatchdogOptions(
    TimeSpan IdleTimeout,
    TimeSpan TotalTurnTimeout,
    TimeSpan ShellHeartbeatInterval,
    TimeSpan CleanupTimeout = default);

/// <summary>
/// Tool-aware watchdog for streaming agent turns. A normal stream gap is bounded by
/// <see cref="StreamWatchdogOptions.IdleTimeout"/>. While a shell is active, its tracker deadline
/// replaces that idle clock and periodic heartbeats keep the coordinator's stall clock alive.
/// A separate total-turn deadline bounds streams that keep producing non-shell progress forever.
/// </summary>
internal static class AsyncStreamIdleTimeout
{
    public static IAsyncEnumerable<T> WithIdleTimeout<T>(
        this IAsyncEnumerable<T> source,
        TimeSpan idleTimeout,
        string runId,
        ILogger logger,
        CancellationToken ct = default) =>
        source.WithToolAwareWatchdog(
            new StreamWatchdogOptions(idleTimeout, TimeSpan.Zero, TimeSpan.Zero),
            shellTracker: null,
            runId,
            logger,
            onShellHeartbeat: null,
            onShellHardTimeout: null,
            turnStartedAt: null,
            ct);

    public static async IAsyncEnumerable<T> WithToolAwareWatchdog<T>(
        this IAsyncEnumerable<T> source,
        StreamWatchdogOptions options,
        ShellExecutionTracker? shellTracker,
        string runId,
        ILogger logger,
        Action<ShellExecutionSnapshot>? onShellHeartbeat,
        Func<ShellExecutionSnapshot, Task>? onShellHardTimeout,
        DateTimeOffset? turnStartedAt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var startedAt = turnStartedAt ?? DateTimeOffset.UtcNow;
        var totalDeadline = options.TotalTurnTimeout > TimeSpan.Zero
            ? startedAt.Add(options.TotalTurnTimeout)
            : DateTimeOffset.MaxValue;
        var lastProgressAt = startedAt;
        var cleanupTimeout = options.CleanupTimeout > TimeSpan.Zero
            ? options.CleanupTimeout
            : TimeSpan.FromSeconds(1);
        ShellExecutionSnapshot? priorShell = null;
        DateTimeOffset nextHeartbeatAt = DateTimeOffset.MaxValue;

        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = source.GetAsyncEnumerator(sourceCts.Token);
        var abandonEnumerator = false;
        Task<bool>? pendingMove = null;

        try
        {
            while (true)
            {
                if (DateTimeOffset.UtcNow >= totalDeadline)
                {
                    abandonEnumerator = true;
                    sourceCts.Cancel();
                    await ForceStopActiveShellAsync(
                        shellTracker?.Observe().ActiveExecution ?? priorShell,
                        onShellHardTimeout,
                        logger,
                        runId,
                        "total-turn deadline").ConfigureAwait(false);
                    throw new AgentProviderException(
                        ModelSource.GitHubCopilot,
                        AgentProviderFailureKind.ProviderUnavailable,
                        "github_copilot_turn_timeout",
                        $"The GitHub Copilot turn exceeded its total deadline of {options.TotalTurnTimeout.TotalMinutes:n0} minutes and was aborted.",
                        isRetryable: true);
                }

                pendingMove = enumerator.MoveNextAsync().AsTask();

                while (!pendingMove.IsCompleted)
                {
                    if (ct.IsCancellationRequested)
                    {
                        abandonEnumerator = true;
                        sourceCts.Cancel();
                        throw new OperationCanceledException(ct);
                    }

                    var now = DateTimeOffset.UtcNow;
                    var observation = shellTracker?.Observe();
                    var activeShell = observation?.ActiveExecution;

                    if (!ReferenceEquals(activeShell, priorShell))
                    {
                        if (priorShell is not null && activeShell is null)
                            lastProgressAt = now;

                        priorShell = activeShell;
                        nextHeartbeatAt = activeShell is not null &&
                                          options.ShellHeartbeatInterval > TimeSpan.Zero
                            ? now.Add(options.ShellHeartbeatInterval)
                            : DateTimeOffset.MaxValue;
                    }

                    if (now >= totalDeadline)
                    {
                        abandonEnumerator = true;
                        sourceCts.Cancel();
                        await ForceStopActiveShellAsync(
                            activeShell,
                            onShellHardTimeout,
                            logger,
                            runId,
                            "total-turn deadline").ConfigureAwait(false);
                        throw new AgentProviderException(
                            ModelSource.GitHubCopilot,
                            AgentProviderFailureKind.ProviderUnavailable,
                            "github_copilot_turn_timeout",
                            $"The GitHub Copilot turn exceeded its total deadline of {options.TotalTurnTimeout.TotalMinutes:n0} minutes and was aborted.",
                            isRetryable: true);
                    }

                    if (activeShell is not null && now >= activeShell.Deadline)
                    {
                        abandonEnumerator = true;
                        sourceCts.Cancel();
                        await ForceStopActiveShellAsync(
                            activeShell,
                            onShellHardTimeout,
                            logger,
                            runId,
                            "shell hard deadline").ConfigureAwait(false);

                        throw new AgentProviderException(
                            ModelSource.GitHubCopilot,
                            AgentProviderFailureKind.ProviderUnavailable,
                            "shell_execution_timeout",
                            $"Shell execution exceeded its hard deadline of {(activeShell.Deadline - activeShell.StartedAt).TotalMinutes:n0} minutes and was terminated.",
                            isRetryable: true);
                    }

                    var idleDeadline = activeShell is null && options.IdleTimeout > TimeSpan.Zero
                        ? lastProgressAt.Add(options.IdleTimeout)
                        : DateTimeOffset.MaxValue;
                    if (now >= idleDeadline)
                    {
                        abandonEnumerator = true;
                        sourceCts.Cancel();
                        await ForceStopActiveShellAsync(
                            activeShell,
                            onShellHardTimeout,
                            logger,
                            runId,
                            "idle deadline").ConfigureAwait(false);
                        logger.LogError(
                            "Streaming turn produced no output for {IdleSeconds:n0}s (runId={RunId}); treating as a hung turn.",
                            options.IdleTimeout.TotalSeconds,
                            runId);
                        throw new AgentProviderException(
                            ModelSource.GitHubCopilot,
                            AgentProviderFailureKind.ProviderUnavailable,
                            "github_copilot_turn_stalled",
                            $"The GitHub Copilot turn stalled with no output for {options.IdleTimeout.TotalSeconds:n0} seconds and was aborted. Retry the run.",
                            isRetryable: true);
                    }

                    if (activeShell is not null && now >= nextHeartbeatAt)
                    {
                        try
                        {
                            onShellHeartbeat?.Invoke(activeShell);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Failed to emit active-shell heartbeat (runId={RunId}, toolCallId={ToolCallId})",
                                runId,
                                activeShell.ToolCallId);
                        }
                        nextHeartbeatAt = now.Add(options.ShellHeartbeatInterval);
                    }

                    var wakeAt = Min(totalDeadline, idleDeadline, activeShell?.Deadline ?? DateTimeOffset.MaxValue, nextHeartbeatAt);
                    if (wakeAt == DateTimeOffset.MaxValue)
                    {
                        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        var unlimitedChangeTask = observation is null
                            ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                            : shellTracker!.WaitForChangeAsync(observation.Version, ct);
                        await Task.WhenAny(pendingMove, cancellationTask, unlimitedChangeTask).ConfigureAwait(false);
                        continue;
                    }

                    var delay = wakeAt - DateTimeOffset.UtcNow;
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.Zero;

                    using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delayTask = Task.Delay(delay, wakeCts.Token);
                    var changeTask = observation is null
                        ? Task.Delay(Timeout.InfiniteTimeSpan, wakeCts.Token)
                        : shellTracker!.WaitForChangeAsync(observation.Version, wakeCts.Token);
                    var completed = await Task.WhenAny(pendingMove, delayTask, changeTask).ConfigureAwait(false);
                    wakeCts.Cancel();
                    if (ReferenceEquals(completed, pendingMove))
                        break;
                }

                if (ct.IsCancellationRequested)
                {
                    abandonEnumerator = true;
                    sourceCts.Cancel();
                    throw new OperationCanceledException(ct);
                }

                var moved = await pendingMove.ConfigureAwait(false);
                pendingMove = null;
                if (!moved)
                    yield break;

                lastProgressAt = DateTimeOffset.UtcNow;
                yield return enumerator.Current;
            }
        }
        finally
        {
            if (abandonEnumerator && pendingMove is not null)
            {
                await ObserveAndDisposeAbandonedEnumeratorAsync(
                    pendingMove,
                    enumerator,
                    cleanupTimeout,
                    logger,
                    runId).ConfigureAwait(false);
            }
            else
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static DateTimeOffset Min(
        DateTimeOffset first,
        DateTimeOffset second,
        DateTimeOffset third,
        DateTimeOffset fourth) =>
        new[] { first, second, third, fourth }.Min();

    private static async Task ForceStopActiveShellAsync(
        ShellExecutionSnapshot? activeShell,
        Func<ShellExecutionSnapshot, Task>? onShellHardTimeout,
        ILogger logger,
        string runId,
        string deadline)
    {
        if (activeShell is null || onShellHardTimeout is null)
            return;

        try
        {
            await onShellHardTimeout(activeShell).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to terminate the shell process tree after {Deadline} elapsed (runId={RunId}, toolCallId={ToolCallId})",
                deadline,
                runId,
                activeShell.ToolCallId);
        }
    }

    private static async Task ObserveAndDisposeAbandonedEnumeratorAsync<T>(
        Task<bool> pendingMove,
        IAsyncEnumerator<T> enumerator,
        TimeSpan cleanupTimeout,
        ILogger logger,
        string runId)
    {
        var pendingCompleted = ReferenceEquals(
            await Task.WhenAny(pendingMove, Task.Delay(cleanupTimeout)).ConfigureAwait(false),
            pendingMove);
        if (pendingCompleted)
        {
            try
            {
                await pendingMove.ConfigureAwait(false);
            }
            catch
            {
                // The watchdog already surfaced the typed failure.
            }
        }
        else
        {
            logger.LogWarning(
                "Streaming source ignored cancellation for {CleanupMilliseconds:n0}ms; forcing enumerator disposal for run {RunId}",
                cleanupTimeout.TotalMilliseconds,
                runId);
        }

        try
        {
            var disposeTask = enumerator.DisposeAsync().AsTask();
            if (ReferenceEquals(
                    await Task.WhenAny(disposeTask, Task.Delay(cleanupTimeout)).ConfigureAwait(false),
                    disposeTask))
            {
                await disposeTask.ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning(
                    "Streaming enumerator disposal exceeded the {CleanupMilliseconds:n0}ms cleanup bound for run {RunId}",
                    cleanupTimeout.TotalMilliseconds,
                    runId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Abandoned streaming enumerator cleanup failed for run {RunId}", runId);
        }
    }
}
