using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class RemoteAgentProxyDeadlineTests
{
    [Fact]
    public void DefaultWorkerIdleBackstop_IsStrictlyLongerThanInPodIdleWindow()
    {
        var options = new RemoteAgentProxyOptions();

        options.ReadIdleTimeout.Should().Be(
            CopilotAIAgent.DefaultStreamIdleTimeout + RemoteAgentProxyOptions.ReadIdleSafetyMargin);
        options.ReadIdleTimeout.Should().BeGreaterThan(CopilotAIAgent.DefaultStreamIdleTimeout,
            "the worker cannot see shell liveness and must only fire after the pod's own idle watchdog");
    }

    [Fact]
    public async Task QuietTurn_LongerThanFormerIdleWindow_ButWithinConfiguredIdle_Completes()
    {
        var formerIdleWindow = TimeSpan.FromMilliseconds(100);
        var perUpdateGap = TimeSpan.FromMilliseconds(220);
        var options = new RemoteAgentProxyOptions
        {
            ReadIdleTimeout = TimeSpan.FromMilliseconds(500),
            TotalTurnTimeout = TimeSpan.FromSeconds(5),
        };
        var collected = new List<int>();
        var stopwatch = Stopwatch.StartNew();

        await foreach (var item in RemoteAgentProxy.WithWorkerStreamDeadline(
                           token => QuietProgressingStream(perUpdateGap, token),
                           options,
                           "run-quiet-but-healthy"))
        {
            collected.Add(item);
        }

        stopwatch.Stop();
        collected.Should().Equal(1, 2, 3);
        perUpdateGap.Should().BeGreaterThan(formerIdleWindow,
            "each healthy quiet gap exceeds the former worker idle window");
        stopwatch.Elapsed.Should().BeGreaterThan(options.ReadIdleTimeout,
            "the cumulative turn exceeds one idle window, proving each update resets the worker clock");
    }

    [Fact]
    public async Task ReadIdleDeadline_CancelsBlackholedProxyStream()
    {
        var sourceCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RemoteAgentProxyOptions
        {
            ReadIdleTimeout = TimeSpan.FromMilliseconds(80),
            TotalTurnTimeout = TimeSpan.FromSeconds(5),
        };
        var stopwatch = Stopwatch.StartNew();

        var act = async () =>
        {
            await foreach (var _ in RemoteAgentProxy.WithWorkerStreamDeadline(
                               token => BlackholedStream(sourceCancellation, token),
                               options,
                               "run-blackholed-proxy"))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();

        stopwatch.Stop();
        ex.Which.Reason.Should().Be("a2a_stream_idle_timeout");
        ex.Which.IsRetryable.Should().BeTrue();
        await sourceCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the worker deadline must terminate a dead-pod/blackholed stream instead of falling through to the four-hour watch loop");
    }

    [Fact]
    public async Task ProgressingStream_CancelsPerIterationDeadlineTimers()
    {
        const int updateCount = 200;
        var delayTracker = new DeadlineDelayTracker();
        var options = new RemoteAgentProxyOptions
        {
            ReadIdleTimeout = TimeSpan.FromMinutes(20),
            TotalTurnTimeout = TimeSpan.FromMinutes(70),
            DelayAsync = delayTracker.DelayAsync,
        };
        var collected = new List<int>();

        await foreach (var item in RemoteAgentProxy.WithWorkerStreamDeadline(
                           token => RapidProgressStream(updateCount, token),
                           options,
                           "run-rapid-progress"))
        {
            collected.Add(item);
        }

        collected.Should().HaveCount(updateCount);
        delayTracker.Started.Should().Be((updateCount + 1) * 2,
            "idle and total timers are created for each update plus the final completion probe");
        delayTracker.Cancelled.Should().Be(delayTracker.Started,
            "every losing deadline timer must be cancelled as soon as stream progress wins");
        delayTracker.Active.Should().Be(0,
            "no per-update idle or total timer may remain armed after the stream completes");
    }

    [Fact]
    public void TransportReset_IsClassifiedRetryable_UnlessCallerCancelled()
    {
        var reset = new HttpRequestException(
            "Connection reset by peer",
            new IOException("Connection reset by peer"));

        RemoteAgentProxy.IsTransientA2aTransportFailure(reset, CancellationToken.None).Should().BeTrue();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RemoteAgentProxy.IsTransientA2aTransportFailure(reset, cancelled.Token).Should().BeFalse();
    }

    [Fact]
    public void UnsupportedSdkEvent_IsClassifiedForRetry()
    {
        var exception = new NotSupportedException(
            "Only message, task, task update events are supported from A2A agents. Received: None");

        RemoteAgentProxy.IsUnsupportedA2aEvent(exception).Should().BeTrue();
    }

    private static async IAsyncEnumerable<int> QuietProgressingStream(
        TimeSpan perUpdateGap,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in new[] { 1, 2, 3 })
        {
            await Task.Delay(perUpdateGap, ct);
            yield return item;
        }
    }

    private static async IAsyncEnumerable<int> RapidProgressStream(
        int count,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> BlackholedStream(
        TaskCompletionSource sourceCancellation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sourceCancellation.TrySetResult();
            throw;
        }

        yield break;
    }

    private sealed class DeadlineDelayTracker
    {
        private int _started;
        private int _cancelled;
        private int _active;

        public int Started => Volatile.Read(ref _started);
        public int Cancelled => Volatile.Read(ref _cancelled);
        public int Active => Volatile.Read(ref _active);

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _active);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() =>
            {
                if (completion.TrySetCanceled(ct))
                {
                    Interlocked.Increment(ref _cancelled);
                    Interlocked.Decrement(ref _active);
                }
            });
            return completion.Task;
        }
    }
}
