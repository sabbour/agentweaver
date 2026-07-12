using System.Diagnostics;
using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Regression tests for the streaming-turn inactivity watchdog
/// (<see cref="AsyncStreamIdleTimeout.WithIdleTimeout{T}"/>). Guards the root-cause fix for
/// orchestrations that hang forever in <c>in_progress</c> when a Copilot SDK turn stalls with
/// no output.
/// </summary>
public sealed class AsyncStreamIdleTimeoutTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    [Fact]
    public async Task PassesThroughAllItems_WhenSourceIsHealthy()
    {
        var collected = new List<int>();

        await foreach (var item in Source(1, 2, 3).WithIdleTimeout(TimeSpan.FromSeconds(30), "run-ok", Logger))
            collected.Add(item);

        collected.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ThrowsRetryableProviderException_WhenSourceStallsBeforeFirstChunk()
    {
        // Source never yields and never completes — the exact production hang (no time-to-first
        // chunk). With a tiny idle window the watchdog must abort quickly.
        var act = async () =>
        {
            await foreach (var _ in HangsForever<int>().WithIdleTimeout(TimeSpan.FromMilliseconds(80), "run-hung", Logger))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_turn_stalled");
        ex.Which.IsRetryable.Should().BeTrue();
        ex.Which.FailureKind.Should().Be(AgentProviderFailureKind.ProviderUnavailable);
    }

    [Fact]
    public async Task ResetsWindowOnEachChunk_SoSlowButProgressingStreamSucceeds()
    {
        // Each chunk arrives after a delay shorter than the idle window; cumulative time exceeds
        // the window but no single gap does. A resetting watchdog must let this through.
        var collected = new List<int>();

        await foreach (var item in SlowSource(perChunkDelay: TimeSpan.FromMilliseconds(60), 1, 2, 3, 4)
                           .WithIdleTimeout(TimeSpan.FromMilliseconds(200), "run-slow", Logger))
        {
            collected.Add(item);
        }

        collected.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task PropagatesCallerCancellation_AsOperationCanceled_NotProviderException()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(80));

        var act = async () =>
        {
            await foreach (var _ in HangsForever<int>()
                               .WithIdleTimeout(TimeSpan.FromMinutes(5), "run-cancel", Logger, cts.Token))
            {
            }
        };

        // Real cancellation must surface as OperationCanceledException, never the hung-turn provider
        // exception — the run was deliberately cancelled, not stalled.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PassThrough_WhenTimeoutIsZero_DisablesWatchdog()
    {
        var collected = new List<int>();

        await foreach (var item in Source(7, 8).WithIdleTimeout(TimeSpan.Zero, "run-off", Logger))
            collected.Add(item);

        collected.Should().Equal(7, 8);
    }

    [Fact]
    public async Task ActiveShell_UsesHardDeadline_EmitsHeartbeats_AndTerminates()
    {
        using var tracker = new ShellExecutionTracker();
        tracker.TryStartObservedExecution(
            "shell-call-254",
            "command-hash-254",
            TimeSpan.FromMilliseconds(180)).Should().BeTrue();
        var heartbeats = new List<ShellExecutionSnapshot>();
        var terminated = false;

        var act = async () =>
        {
            await foreach (var _ in HangsForever<int>().WithToolAwareWatchdog(
                               new StreamWatchdogOptions(
                                   IdleTimeout: TimeSpan.FromMilliseconds(40),
                                   TotalTurnTimeout: TimeSpan.FromSeconds(5),
                                   ShellHeartbeatInterval: TimeSpan.FromMilliseconds(30)),
                               tracker,
                               "run-shell-timeout",
                               Logger,
                               heartbeats.Add,
                               _ =>
                               {
                                   terminated = true;
                                   return Task.CompletedTask;
                               }))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("shell_execution_timeout",
            "an active shell uses its hard deadline instead of the shorter ordinary idle window");
        terminated.Should().BeTrue("the hard timeout must terminate the shell process tree");
        heartbeats.Should().HaveCountGreaterThanOrEqualTo(2,
            "a healthy silent shell emits progress often enough to reset the coordinator stall window");
    }

    [Fact]
    public async Task NoActiveShell_UsesIdleDeadline_NotShellDeadline()
    {
        using var tracker = new ShellExecutionTracker();

        var act = async () =>
        {
            await foreach (var _ in HangsForever<int>().WithToolAwareWatchdog(
                               new StreamWatchdogOptions(
                                   IdleTimeout: TimeSpan.FromMilliseconds(60),
                                   TotalTurnTimeout: TimeSpan.FromSeconds(5),
                                   ShellHeartbeatInterval: TimeSpan.FromMilliseconds(20)),
                               tracker,
                               "run-idle-timeout",
                               Logger,
                               onShellHeartbeat: null,
                               onShellHardTimeout: null))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_turn_stalled");
    }

    [Fact]
    public async Task TotalTurnTimeout_WithActiveShell_ForceStopsProcessTree()
    {
        using var tracker = new ShellExecutionTracker();
        tracker.TryStartObservedExecution(
            "shell-total-timeout",
            "command-total-timeout",
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        var terminated = false;

        var act = async () =>
        {
            await foreach (var _ in HangsForever<int>().WithToolAwareWatchdog(
                               new StreamWatchdogOptions(
                                   IdleTimeout: TimeSpan.FromMinutes(5),
                                   TotalTurnTimeout: TimeSpan.FromMilliseconds(80),
                                   ShellHeartbeatInterval: TimeSpan.FromMilliseconds(20)),
                               tracker,
                               "run-total-timeout",
                               Logger,
                               onShellHeartbeat: null,
                               _ =>
                               {
                                   terminated = true;
                                   return Task.CompletedTask;
                               }))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_turn_timeout");
        terminated.Should().BeTrue(
            "every watchdog-owned timeout must force-stop an active shell, not only its shell deadline");
    }

    [Fact]
    public async Task WatchdogTimeout_CancellationIgnoringSource_ReturnsAfterBoundedCleanup()
    {
        var source = new CancellationIgnoringSource();
        var stopwatch = Stopwatch.StartNew();

        var act = async () =>
        {
            await foreach (var _ in source.WithToolAwareWatchdog(
                               new StreamWatchdogOptions(
                                   IdleTimeout: TimeSpan.FromMilliseconds(60),
                                   TotalTurnTimeout: TimeSpan.FromSeconds(5),
                                   ShellHeartbeatInterval: TimeSpan.Zero,
                                   CleanupTimeout: TimeSpan.FromMilliseconds(40)),
                               shellTracker: null,
                               "run-cancellation-ignoring-source",
                               Logger,
                               onShellHeartbeat: null,
                               onShellHardTimeout: null))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();

        stopwatch.Stop();
        ex.Which.ErrorCode.Should().Be("github_copilot_turn_stalled");
        await source.DisposeCalled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        source.PendingMove.IsCompleted.Should().BeTrue(
            "forced disposal releases the cancellation-ignoring MoveNext task");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "bounded cleanup must not await a cancellation-ignoring SDK MoveNext forever");
    }

    private static async IAsyncEnumerable<int> Source(params int[] items)
    {
        foreach (var i in items)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> SlowSource(
        TimeSpan perChunkDelay,
        params int[] items)
    {
        foreach (var i in items)
        {
            await Task.Delay(perChunkDelay);
            yield return i;
        }
    }

    private static async IAsyncEnumerable<T> HangsForever<T>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private sealed class CancellationIgnoringSource : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _pendingMove =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> PendingMove => _pendingMove.Task;
        public int Current => 0;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => new(_pendingMove.Task);

        public ValueTask DisposeAsync()
        {
            DisposeCalled.TrySetResult();
            _pendingMove.TrySetResult(false);
            return ValueTask.CompletedTask;
        }
    }
}
