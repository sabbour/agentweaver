using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Tests for <see cref="WorkflowStageProjector"/>. The board exposes fixed canonical buckets and
/// maps persisted run/work-plan state into those buckets.
/// </summary>
public sealed class WorkflowStageProjectorTests
{
    private static Run RunWith(RunStatus status) => new()
    {
        Id                = RunId.New(),
        RepositoryPath    = "/repo",
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "t",
        SubmittingUser    = "alice",
        Status            = status,
        StartedAt         = DateTimeOffset.UtcNow,
        AgentName         = "Coordinator",
    };

    [Fact]
    public void GetStages_ReturnsFixedCanonicalRunBuckets()
    {
        var projector = new WorkflowStageProjector();

        var stages = projector.GetStages();

        stages.Select(s => (s.Id, s.Label)).Should().Equal(
            (WorkflowStageProjector.ProblemsStageId, "Problems"),
            (WorkflowStageProjector.HumanReviewStageId, "Human Review"),
            (WorkflowStageProjector.ActiveStageId, "Active"),
            (WorkflowStageProjector.DoneStageId, "Done"));
        stages.Select(s => s.Order).Should().Equal(Enumerable.Range(0, stages.Count));
    }

    // =========================================================================
    // FR-016: a coordinator run's persisted state maps to the column it occupies.
    // =========================================================================
    [Theory]
    [InlineData(WorkPlanStatus.Planned, null, WorkflowStageProjector.ActiveStageId)]
    [InlineData(WorkPlanStatus.Dispatching, null, WorkflowStageProjector.ActiveStageId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Rai, WorkflowStageProjector.ActiveStageId)]
    [InlineData(WorkPlanStatus.InReview, AssemblyStage.Review, WorkflowStageProjector.HumanReviewStageId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Merge, WorkflowStageProjector.ActiveStageId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Scribe, WorkflowStageProjector.ActiveStageId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Done, WorkflowStageProjector.DoneStageId)]
    [InlineData(WorkPlanStatus.Complete, null, WorkflowStageProjector.DoneStageId)]
    [InlineData(WorkPlanStatus.AssemblyBlocked, null, WorkflowStageProjector.ProblemsStageId)]
    [InlineData(WorkPlanStatus.AssemblyFailed, null, WorkflowStageProjector.ProblemsStageId)]
    [InlineData(WorkPlanStatus.AssemblyDeclined, null, WorkflowStageProjector.ProblemsStageId)]
    public void CoordinatorRunToStageId_MapsInProgressRun_ByWorkPlanStage(
        string status, string? assemblyStage, string expectedStageId)
    {
        var projector = new WorkflowStageProjector();
        var run = RunWith(RunStatus.InProgress);
        var planStage = new CoordinatorWorkPlanStage(status, assemblyStage);

        projector.CoordinatorRunToStageId(run, planStage).Should().Be(expectedStageId);
    }

    [Fact]
    public void CoordinatorRunToStageId_WithNoWorkPlan_SitsInCoordinatorColumn()
    {
        var projector = new WorkflowStageProjector();
        var run = RunWith(RunStatus.InProgress);

        projector.CoordinatorRunToStageId(run, null)
            .Should().Be(WorkflowStageProjector.ActiveStageId);
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Merged)]
    [InlineData(RunStatus.AssembleReady)]
    public void CoordinatorRunToStageId_DoneRunStatus_CollapsesToDone_RegardlessOfPlan(RunStatus status)
    {
        var projector = new WorkflowStageProjector();
        var run = RunWith(status);

        projector.CoordinatorRunToStageId(run, new CoordinatorWorkPlanStage(WorkPlanStatus.Assembling, AssemblyStage.Rai))
            .Should().Be(WorkflowStageProjector.DoneStageId);
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Declined)]
    [InlineData(RunStatus.MergeFailed)]
    public void CoordinatorRunToStageId_ProblemRunStatus_CollapsesToProblems_RegardlessOfPlan(RunStatus status)
    {
        var projector = new WorkflowStageProjector();
        var run = RunWith(status);

        projector.CoordinatorRunToStageId(run, new CoordinatorWorkPlanStage(WorkPlanStatus.Assembling, AssemblyStage.Rai))
            .Should().Be(WorkflowStageProjector.ProblemsStageId);
    }
}
