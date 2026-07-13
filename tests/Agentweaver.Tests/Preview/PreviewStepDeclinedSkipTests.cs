using Agentweaver.Api.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Regression coverage for spec-006 code-review Finding 2: the deterministic preview step must be
/// SKIPPED on a DECLINED build-test verdict (<c>Approved:false, RequestChanges:false</c>), which
/// terminates the run as <c>assembly_declined</c>. It must still run for APPROVED and
/// REQUEST_CHANGES. The step is ALWAYS the behavior (no feature flag) — the only guards are the
/// DECLINED skip here and the infra-unavailable self-skip inside PreviewStep. Exercises the exact
/// guard used in the coordinator gate loop
/// (<see cref="CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep"/>).
/// </summary>
public sealed class PreviewStepDeclinedSkipTests
{
    [Fact]
    public async Task CoordinatorBoundary_SwallowsInternalCancellation_ButPropagatesCallerCancellation()
    {
        await CoordinatorAssemblyService.RunPreviewStepDefensivelyAsync(
            () => Task.FromException(new TaskCanceledException("internal timeout")),
            "run-1",
            NullLogger.Instance,
            CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = () => CoordinatorAssemblyService.RunPreviewStepDefensivelyAsync(
            () => Task.FromCanceled(cts.Token),
            "run-1",
            NullLogger.Instance,
            cts.Token);

        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    private static readonly CollectiveGateDecision Approved = new(Approved: true, RequestChanges: false, Feedback: null);
    private static readonly CollectiveGateDecision RequestChanges = new(Approved: false, RequestChanges: true, Feedback: "fix");
    private static readonly CollectiveGateDecision Declined = new(Approved: false, RequestChanges: false, Feedback: null);

    [Fact]
    public void Declined_Verdict_SkipsPreviewStep_EvenWhenWired()
    {
        CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep(
            hasStep: true, buildTest: Declined)
            .Should().BeFalse("a DECLINED assembly is about to be torn down");
    }

    [Fact]
    public void Approved_Verdict_RunsPreviewStep_WhenWired()
    {
        CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep(
            hasStep: true, buildTest: Approved)
            .Should().BeTrue();
    }

    [Fact]
    public void RequestChanges_Verdict_RunsPreviewStep_WhenWired()
    {
        CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep(
            hasStep: true, buildTest: RequestChanges)
            .Should().BeTrue("preview is still shown at human review for a REQUEST_CHANGES verdict");
    }

    [Fact]
    public void Unwired_AlwaysSkips_EvenOnApproved()
    {
        CoordinatorAssemblyService.ShouldRunDeterministicPreviewStep(
            hasStep: false, buildTest: Approved).Should().BeFalse();
    }
}
