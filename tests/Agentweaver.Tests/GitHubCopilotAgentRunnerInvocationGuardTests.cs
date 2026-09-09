using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class GitHubCopilotAgentRunnerInvocationGuardTests
{
    [Fact]
    public async Task RunAfterValidationAsync_RejectionDoesNotStartSdkStream()
    {
        var guard = new RejectingGuard();
        var streamStarted = false;

        var act = async () =>
        {
            await foreach (var _ in GitHubCopilotAgentRunner.RunAfterValidationAsync(
                guard,
                "non-run-generation",
                () =>
                {
                    streamStarted = true;
                    return Stream();
                },
                CancellationToken.None))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("provider changed");
        guard.Calls.Should().Be(1);
        streamStarted.Should().BeFalse("the provider fence runs after setup but before SDK streaming");
    }

    [Fact]
    public async Task RunAfterValidationAsync_SuccessStartsSdkStreamAfterValidation()
    {
        var sequence = new List<string>();
        var guard = new RecordingGuard(sequence);
        var values = new List<int>();

        await foreach (var value in GitHubCopilotAgentRunner.RunAfterValidationAsync(
            guard,
            "non-run-generation",
            () =>
            {
                sequence.Add("stream");
                return Stream();
            },
            CancellationToken.None))
        {
            values.Add(value);
        }

        sequence.Should().Equal("validate", "stream");
        values.Should().Equal(1);
    }

    private static async IAsyncEnumerable<int> Stream()
    {
        await Task.Yield();
        yield return 1;
    }

    private sealed class RejectingGuard : IModelInvocationGuard
    {
        public int Calls { get; private set; }

        public Task ValidateAsync(string runId, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("provider changed");
        }
    }

    private sealed class RecordingGuard(List<string> sequence) : IModelInvocationGuard
    {
        public Task ValidateAsync(string runId, CancellationToken ct)
        {
            sequence.Add("validate");
            return Task.CompletedTask;
        }
    }
}
