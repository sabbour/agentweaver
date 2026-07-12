using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentweaver.AgentRuntime.Workflow;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class RemoteAgentProxyDeadlineTests
{
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
}
