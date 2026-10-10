using System.Text.Json;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SuspendResumeCoordinatorTests
{
    [Theory]
    [InlineData("Suspend", "Reserved", "Fenced")]
    [InlineData("Suspend", "Fenced", "Draining")]
    [InlineData("Suspend", "Draining", "Checkpointed")]
    [InlineData("Suspend", "Checkpointed", "JournalFlushed")]
    [InlineData("Suspend", "JournalFlushed", "WorkspaceFlushed")]
    [InlineData("Suspend", "WorkspaceFlushed", "ManifestCommitted")]
    [InlineData("Suspend", "ManifestCommitted", "PlacementReleased")]
    [InlineData("Resume", "Reserved", "ManifestValidated")]
    [InlineData("Resume", "ManifestValidated", "EnvironmentReady")]
    [InlineData("Resume", "EnvironmentReady", "EgressVerified")]
    [InlineData("Resume", "EgressVerified", "FenceAdvanced")]
    [InlineData("Resume", "FenceAdvanced", "Dispatched")]
    public void PhaseMachineAllowsOnlyTheNextOrderedTransition(
        string kind,
        string current,
        string next)
    {
        Assert.True(SessionSuspendResumePhaseMachine.CanAdvance(
            Enum.Parse<SessionSuspendResumeOperationKind>(kind),
            Enum.Parse<SessionSuspendResumeOperationPhase>(current),
            Enum.Parse<SessionSuspendResumeOperationPhase>(next)));
    }

    [Theory]
    [InlineData("Suspend", "Reserved", "Draining")]
    [InlineData("Suspend", "Fenced", "ManifestValidated")]
    [InlineData("Resume", "Reserved", "Fenced")]
    [InlineData("Resume", "ManifestValidated", "EgressVerified")]
    [InlineData("Suspend", "PlacementReleased", "Interrupted")]
    [InlineData("Resume", "Dispatched", "Interrupted")]
    public void PhaseMachineRejectsSkippedCrossOperationAndTerminalTransitions(
        string kind,
        string current,
        string next)
    {
        Assert.False(SessionSuspendResumePhaseMachine.CanAdvance(
            Enum.Parse<SessionSuspendResumeOperationKind>(kind),
            Enum.Parse<SessionSuspendResumeOperationPhase>(current),
            Enum.Parse<SessionSuspendResumeOperationPhase>(next)));
    }

    [Theory]
    [InlineData("Suspend")]
    [InlineData("Resume")]
    public void AnyNonterminalOperationCanBecomeInterrupted(
        string kind)
    {
        var operationKind = Enum.Parse<SessionSuspendResumeOperationKind>(kind);
        Assert.True(SessionSuspendResumePhaseMachine.CanAdvance(
            operationKind, SessionSuspendResumeOperationPhase.Reserved, SessionSuspendResumeOperationPhase.Interrupted));
        Assert.True(SessionSuspendResumePhaseMachine.CanAdvance(
            operationKind, SessionSuspendResumeOperationPhase.EgressVerified, SessionSuspendResumeOperationPhase.Interrupted));
        Assert.False(SessionSuspendResumePhaseMachine.CanAdvance(
            operationKind, SessionSuspendResumeOperationPhase.Interrupted, SessionSuspendResumeOperationPhase.Interrupted));
    }

    [Theory]
    [InlineData("reserved", "Reserved")]
    [InlineData("not_applicable", "NotApplicable")]
    [InlineData("workspace_flushed", "WorkspaceFlushed")]
    [InlineData("interrupted", "Interrupted")]
    public void PersistedPhasesUseExactClosedValues(
        string value,
        string expected)
    {
        var expectedPhase = Enum.Parse<SessionSuspendResumeOperationPhase>(expected);
        Assert.Equal(expectedPhase, SessionSuspendResumePhaseMachine.ParsePhase(value));
        Assert.Equal(value, SessionSuspendResumePhaseMachine.ToPersistenceValue(expectedPhase));
    }

    [Fact]
    public void UnknownPersistedPhaseFailsClosed()
    {
        var exception = Assert.Throws<CoordinationException>(
            () => SessionSuspendResumePhaseMachine.ParsePhase("success"));

        Assert.Equal("suspend_resume_operation_phase_invalid", exception.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task CoordinatorReturnsExplicitUnavailableWithoutSuccessShapedFallback()
    {
        using var services = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await new SessionSuspendResumeCoordinator().Unavailable().ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        body.Position = 0;
        using var response = await JsonDocument.ParseAsync(body);
        Assert.Equal(
            "suspend_resume_owner_unavailable",
            response.RootElement.GetProperty("error").GetString());
        Assert.False(response.RootElement.TryGetProperty("suspended", out _));
        Assert.False(response.RootElement.TryGetProperty("resumed", out _));
    }
}
