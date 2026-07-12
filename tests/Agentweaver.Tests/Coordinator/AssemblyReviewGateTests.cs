using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="AssemblyReviewGate"/> — the ONE collective human-review gate (D5).
///
/// <para>Regression focus: the gate MUST wait indefinitely for the human operator and must never
/// self-fault on a wall-clock timeout. A prior bug armed a <c>timeoutCts</c> that threw
/// <see cref="TimeoutException"/> after <c>Coordinator:AssemblyReviewTimeoutMinutes</c> (default 60),
/// which was not caught by the awaiter and faulted runs that were correctly parked awaiting review.</para>
/// </summary>
public sealed class AssemblyReviewGateTests
{
    private static AssemblyReviewDecision Approve(string reviewer = "alice") =>
        new(Approved: true, RequestChanges: false, Feedback: null, TargetFiles: null, Reviewer: reviewer);

    [Fact]
    public void ArmAsync_does_not_complete_on_its_own_even_with_a_tiny_configured_timeout()
    {
        // Configure the (now-ignored) legacy timeout to an absurdly small value. Before the fix this
        // would fault the gate almost immediately; after the fix the setting is ignored entirely.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:AssemblyReviewTimeoutMinutes"] = "0.0001", // ~6 ms
            })
            .Build();
        var gate = new AssemblyReviewGate(config);

        var task = gate.ArmAsync("run-1", "alice", CancellationToken.None);

        // Give any stray timer far more than the configured 6 ms to (incorrectly) fire.
        Thread.Sleep(200);

        task.IsCompleted.Should().BeFalse("the human-review gate waits indefinitely and never self-times-out");
        gate.IsArmed("run-1").Should().BeTrue();
    }

    [Fact]
    public async Task TrySubmit_completes_the_armed_gate_with_the_decision()
    {
        var gate = new AssemblyReviewGate();
        var task = gate.ArmAsync("run-2", "alice", CancellationToken.None);

        var result = gate.TrySubmit("run-2", "alice", Approve());

        result.Should().Be(AssemblyReviewSubmitResult.Accepted);
        var decision = await task.WaitAsync(TimeSpan.FromSeconds(5));
        decision.Approved.Should().BeTrue();
        gate.IsArmed("run-2").Should().BeFalse();
    }

    [Fact]
    public async Task ArmAsync_is_cancelled_when_the_host_token_fires()
    {
        var gate = new AssemblyReviewGate();
        using var cts = new CancellationTokenSource();
        var task = gate.ArmAsync("run-3", "alice", cts.Token);

        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        gate.IsArmed("run-3").Should().BeFalse();
    }

    [Fact]
    public void TrySubmit_from_a_non_owner_is_forbidden_and_leaves_the_gate_armed()
    {
        var gate = new AssemblyReviewGate();
        var task = gate.ArmAsync("run-4", "alice", CancellationToken.None);

        var result = gate.TrySubmit("run-4", "mallory", Approve("mallory"));

        result.Should().Be(AssemblyReviewSubmitResult.Forbidden);
        task.IsCompleted.Should().BeFalse();
        gate.IsArmed("run-4").Should().BeTrue();
    }

    [Fact]
    public void Second_TrySubmit_after_consumption_reports_not_armed()
    {
        var gate = new AssemblyReviewGate();
        _ = gate.ArmAsync("run-5", "alice", CancellationToken.None);

        gate.TrySubmit("run-5", "alice", Approve()).Should().Be(AssemblyReviewSubmitResult.Accepted);
        gate.TrySubmit("run-5", "alice", Approve()).Should().Be(AssemblyReviewSubmitResult.NotArmed);
    }

    [Fact]
    public void TrySubmit_matches_owner_by_github_login_when_provided()
    {
        var gate = new AssemblyReviewGate();
        // Gate owner is the captured GitHub login (backlog-pickup identity shape).
        var task = gate.ArmAsync("run-6", "octocat", CancellationToken.None);

        var result = gate.TrySubmit("run-6", callerUser: "api-key-principal", Approve(), callerGitHubLogin: "octocat");

        result.Should().Be(AssemblyReviewSubmitResult.Accepted);
        task.IsCompletedSuccessfully.Should().BeTrue();
    }
}
