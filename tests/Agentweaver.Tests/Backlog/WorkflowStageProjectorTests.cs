using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Tests for <see cref="WorkflowStageProjector"/> (FR-015 / FR-016 / FR-019, SC-004). The board's
/// workflow columns are DERIVED from <see cref="CoordinatorGraphDescriptor"/>, never a hardcoded
/// list, so a change to the coordinator topology changes the columns with no projector edit.
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

    // =========================================================================
    // FR-015 / SC-004: columns are dynamically derived from the descriptor backbone.
    // =========================================================================
    [Fact]
    public void GetStages_DerivesOrderedBackbone_FromDescriptor_ExcludingSubtasks_AppendingTerminal()
    {
        var projector = new WorkflowStageProjector();

        // What the board SHOULD show is, by definition, the descriptor's non-subtask backbone (in
        // node order) plus the terminal sink. Computing the expectation from the descriptor itself
        // proves the projector reads the topology rather than hardcoding columns: add/rename a
        // backbone node and this assertion (and the board) change with zero projector edits.
        var descriptor = CoordinatorGraphDescriptor.BuildEmpty(Guid.Empty.ToString("D"));
        var expected = descriptor.Nodes
            .Where(n => !string.Equals(n.NodeType, "subtask", StringComparison.Ordinal))
            .Select(n => (n.Id, n.Label))
            .Append((WorkflowStageProjector.TerminalStageId, WorkflowStageProjector.TerminalStageLabel))
            .ToList();

        var stages = projector.GetStages();

        stages.Select(s => (s.Id, s.Label)).Should().Equal(expected);
        stages.Select(s => s.Order).Should().Equal(Enumerable.Range(0, stages.Count));

        // Concrete backbone the board renders today (regression anchor for the documented topology).
        stages.Select(s => s.Id).Should().Equal(
            CoordinatorGraphDescriptor.CoordinatorNodeId,
            CoordinatorGraphDescriptor.AssemblyRaiNodeId,
            CoordinatorGraphDescriptor.AssemblyReviewNodeId,
            CoordinatorGraphDescriptor.AssemblyMergeNodeId,
            CoordinatorGraphDescriptor.AssemblyScribeNodeId,
            WorkflowStageProjector.TerminalStageId);

        // The Human Review gate node is retained (only subtask fan-out nodes are excluded).
        stages.Should().Contain(s =>
            s.Id == CoordinatorGraphDescriptor.AssemblyReviewNodeId && s.Label == "Human Review");
    }

    [Fact]
    public void GetStages_ExcludesSubtaskFanoutNodes_EvenWhenDescriptorHasThem()
    {
        var projector = new WorkflowStageProjector();
        var stages = projector.GetStages();

        // No projected column carries a subtask node id (subtasks render nested under their card).
        stages.Should().NotContain(s => s.Id.StartsWith("plan:subtask-", StringComparison.Ordinal));
    }

    // =========================================================================
    // FR-016: a coordinator run's persisted state maps to the column it occupies.
    // =========================================================================
    [Theory]
    [InlineData(WorkPlanStatus.Planned, null, CoordinatorGraphDescriptor.CoordinatorNodeId)]
    [InlineData(WorkPlanStatus.Dispatching, null, CoordinatorGraphDescriptor.CoordinatorNodeId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Rai, CoordinatorGraphDescriptor.AssemblyRaiNodeId)]
    [InlineData(WorkPlanStatus.InReview, AssemblyStage.Review, CoordinatorGraphDescriptor.AssemblyReviewNodeId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Merge, CoordinatorGraphDescriptor.AssemblyMergeNodeId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Scribe, CoordinatorGraphDescriptor.AssemblyScribeNodeId)]
    [InlineData(WorkPlanStatus.Assembling, AssemblyStage.Done, WorkflowStageProjector.TerminalStageId)]
    [InlineData(WorkPlanStatus.Complete, null, WorkflowStageProjector.TerminalStageId)]
    [InlineData(WorkPlanStatus.AssemblyBlocked, null, WorkflowStageProjector.TerminalStageId)]
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
            .Should().Be(CoordinatorGraphDescriptor.CoordinatorNodeId);
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Merged)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Declined)]
    [InlineData(RunStatus.MergeFailed)]
    public void CoordinatorRunToStageId_TerminalRunStatus_CollapsesToTerminal_RegardlessOfPlan(RunStatus status)
    {
        var projector = new WorkflowStageProjector();
        var run = RunWith(status);

        // Even mid-assembly plan state cannot keep a terminal run out of the Done column.
        projector.CoordinatorRunToStageId(run, new CoordinatorWorkPlanStage(WorkPlanStatus.Assembling, AssemblyStage.Rai))
            .Should().Be(WorkflowStageProjector.TerminalStageId);
    }
}
