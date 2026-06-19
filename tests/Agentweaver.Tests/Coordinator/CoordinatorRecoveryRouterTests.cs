using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="CoordinatorRecoveryRouter"/> — the restart-recovery routing table that
/// decides, from an interrupted coordinator run's persisted work-plan status, how the orchestration
/// is resumed. This is the regression-prone heart of "survive restarts": every work-plan phase must
/// map to the correct recovery action so an interrupted orchestration is re-armed, never stranded.
/// </summary>
public sealed class CoordinatorRecoveryRouterTests
{
    [Fact]
    public void NoWorkPlan_ResumesSpecPhase()
    {
        CoordinatorRecoveryRouter.Route(hasPlan: false, hasSubtasks: false, workPlanStatus: null)
            .Should().Be(CoordinatorRecoveryAction.ResumeSpecPhase);
    }

    [Fact]
    public void PlanWithoutSubtasks_Finalizes()
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: false, WorkPlanStatus.Planned)
            .Should().Be(CoordinatorRecoveryAction.FinalizeNoSubtasks);
    }

    [Theory]
    [InlineData(WorkPlanStatus.Planned)]
    [InlineData(WorkPlanStatus.Dispatching)]
    public void DispatchPhases_ReArmDispatch(string status)
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, status)
            .Should().Be(CoordinatorRecoveryAction.Dispatch);
    }

    [Fact]
    public void AwaitingAssembly_ReArmsAssembly()
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, WorkPlanStatus.AwaitingAssembly)
            .Should().Be(CoordinatorRecoveryAction.Assemble);
    }

    [Theory]
    [InlineData(WorkPlanStatus.Assembling)]
    [InlineData(WorkPlanStatus.InReview)]
    public void MidAssemblyOrReview_ReArmsAssemblyFromScratch(string status)
    {
        // The integration build + RAI + in-memory review gate are gone after a restart, so these
        // phases must reset to awaiting_assembly and re-run the (idempotent) assembly core.
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, status)
            .Should().Be(CoordinatorRecoveryAction.ReArmAssembly);
    }

    [Fact]
    public void Complete_SettlesRunAsCompleted()
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, WorkPlanStatus.Complete)
            .Should().Be(CoordinatorRecoveryAction.SettleComplete);
    }

    [Theory]
    [InlineData(WorkPlanStatus.AssemblyBlocked)]
    [InlineData(WorkPlanStatus.AssemblyFailed)]
    [InlineData(WorkPlanStatus.AssemblyDeclined)]
    public void TerminalFailureStates_SettleRunAsFailed(string status)
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, status)
            .Should().Be(CoordinatorRecoveryAction.SettleFailed);
    }

    [Fact]
    public void UnknownStatus_DefaultsToDispatchDefensively()
    {
        CoordinatorRecoveryRouter.Route(hasPlan: true, hasSubtasks: true, "some_future_status")
            .Should().Be(CoordinatorRecoveryAction.Dispatch);
    }

    [Fact]
    public void NoPlan_TakesPrecedenceOverStatus()
    {
        // hasPlan=false short-circuits to spec-phase regardless of any stale status argument.
        CoordinatorRecoveryRouter.Route(hasPlan: false, hasSubtasks: true, WorkPlanStatus.Assembling)
            .Should().Be(CoordinatorRecoveryAction.ResumeSpecPhase);
    }
}
