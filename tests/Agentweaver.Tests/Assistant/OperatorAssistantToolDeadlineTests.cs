using System.Diagnostics;
using Agentweaver.AgentRuntime;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agentweaver.Tests.Assistant;

/// <summary>
/// Unit coverage for the per-invocation tool deadline (incident: run 0ffedddf hung forever because a
/// gated <c>coordinator_steer</c> call blocked on a degraded Kubernetes API with no timeout, so the
/// turn loop never produced a tool.result and only the 30-minute idle sweep could end it). Every
/// operator tool is now wrapped in a deadline so a single unbounded downstream dependency can no
/// longer wedge a turn indefinitely.
/// </summary>
public sealed class OperatorAssistantToolDeadlineTests
{
    [Fact]
    public async Task DeadlineTool_WhenInnerHangs_AbortsAndReturnsTimeoutResult_InsteadOfBlockingForever()
    {
        // An inner tool that never completes on its own — it only ends when its token is cancelled,
        // exactly like the coordinator_steer -> pod-release -> K8s API call that hung live.
        var hanging = AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            },
            "coordinator_steer");

        var bounded = OperatorAssistantAgent.CreateDeadlineToolForTests(hanging, TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var result = await bounded.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the deadline must abort the hung call promptly rather than blocking the turn forever");
        result!.ToString().Should().Contain("did not complete within",
            "a timed-out tool must return a model-visible timeout result so the turn completes gracefully");
        result.ToString().Should().Contain("coordinator_steer", "the timeout message must name the tool that stalled");
    }

    [Fact]
    public async Task DeadlineTool_WhenInnerCompletesInTime_ReturnsRealResult_Unchanged()
    {
        var fast = AIFunctionFactory.Create(() => "real-result", "run_status");
        var bounded = OperatorAssistantAgent.CreateDeadlineToolForTests(fast, TimeSpan.FromSeconds(30));

        var result = await bounded.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        result!.ToString().Should().Be("real-result",
            "a tool that finishes inside its deadline must return its genuine result untouched");
    }

    [Fact]
    public async Task DeadlineTool_WhenOuterTokenCancelled_PropagatesCancellation_NotATimeoutResult()
    {
        // A genuine turn cancellation (client disconnect / host shutdown) must NOT be masked as a
        // tool timeout — it has to propagate so the turn loop unwinds correctly.
        var hanging = AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            },
            "project_list_runs");
        var bounded = OperatorAssistantAgent.CreateDeadlineToolForTests(hanging, TimeSpan.FromMinutes(5));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await bounded.InvokeAsync(new AIFunctionArguments(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "turn cancellation must surface as cancellation, not be swallowed into a timeout message");
    }
}
