using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.Tests;

public sealed class QuestionGateTests
{
    private static InMemoryQuestionGate CreateGate() => new();

    [Fact]
    public async Task AskAsync_Suspends_Until_Answered()
    {
        var gate = CreateGate();
        const string runId = "run-1";
        const string requestId = "req-1";

        var askTask = gate.AskAsync(runId, requestId, "Which framework?", TimeSpan.FromMinutes(5), CancellationToken.None);

        // The ask must not be resolved before an answer arrives.
        askTask.IsCompleted.Should().BeFalse();

        var resolved = gate.Answer(runId, requestId, "Use xUnit");
        resolved.Should().BeTrue();

        (await askTask).Should().Be("Use xUnit");
    }

    [Fact]
    public async Task Answer_UnknownRequest_ReturnsFalse()
    {
        var gate = CreateGate();
        gate.Answer("run-x", "missing", "anything").Should().BeFalse();

        // A pending request for a different id is also not resolved by a mismatched answer.
        var askTask = gate.AskAsync("run-x", "req-1", "Q?", TimeSpan.FromMinutes(5), CancellationToken.None);
        gate.Answer("run-x", "req-2", "wrong").Should().BeFalse();
        askTask.IsCompleted.Should().BeFalse();

        gate.Answer("run-x", "req-1", "right").Should().BeTrue();
        (await askTask).Should().Be("right");
    }

    [Fact]
    public async Task AskAsync_Timeout_ResolvesToNull()
    {
        var gate = CreateGate();
        var answer = await gate.AskAsync("run-2", "req-1", "Q?", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        answer.Should().BeNull();
    }

    [Fact]
    public async Task Clear_ResolvesPending_ToNull()
    {
        var gate = CreateGate();
        const string runId = "run-3";

        var askTask = gate.AskAsync(runId, "req-1", "Q?", TimeSpan.FromMinutes(5), CancellationToken.None);
        askTask.IsCompleted.Should().BeFalse();

        gate.Clear(runId);

        (await askTask).Should().BeNull();

        // After clear, a late answer finds nothing pending.
        gate.Answer(runId, "req-1", "too late").Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_ResolvesToNull()
    {
        var gate = CreateGate();
        using var cts = new CancellationTokenSource();

        var askTask = gate.AskAsync("run-4", "req-1", "Q?", TimeSpan.FromMinutes(5), cts.Token);
        cts.Cancel();

        (await askTask).Should().BeNull();
    }
}
