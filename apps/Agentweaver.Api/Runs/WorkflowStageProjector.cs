using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>A single workflow-stage column derived from the coordinator topology (descriptor-driven).</summary>
public sealed record WorkflowStage(string Id, string Label, int Order);

/// <summary>
/// Derives the board's workflow columns from <see cref="CoordinatorGraphDescriptor"/> (never a
/// hardcoded list) and maps a coordinator run's persisted state to the column it currently occupies.
/// </summary>
public sealed class WorkflowStageProjector : IWorkflowStageProjector
{
    public const string TerminalStageId = "terminal";
    public const string TerminalStageLabel = "Done";

    /// <summary>
    /// Builds the canonical, plan-independent coordinator topology and projects its ordered backbone
    /// nodes into columns. A node projects to a column iff its node_type is not "subtask" (subtask
    /// nodes are per-run fan-out, rendered nested under their coordinator card). One terminal "Done"
    /// stage is appended as the FR-016a sink. Throws/returns empty only if the descriptor cannot be
    /// resolved (the caller then sets workflow_stages_available = false).
    /// </summary>
    public IReadOnlyList<WorkflowStage> GetStages()
    {
        var descriptor = CoordinatorGraphDescriptor.BuildEmpty(Guid.Empty.ToString("D"));

        var stages = new List<WorkflowStage>();
        var order = 0;
        foreach (var node in descriptor.Nodes)
        {
            if (string.Equals(node.NodeType, "subtask", StringComparison.Ordinal))
                continue;
            stages.Add(new WorkflowStage(node.Id, node.Label, order++));
        }

        stages.Add(new WorkflowStage(TerminalStageId, TerminalStageLabel, order));
        return stages;
    }

    /// <summary>
    /// Maps a coordinator run's authoritative persisted state to the column id it currently occupies.
    /// Terminal run/work-plan states collapse to "terminal"; an in-progress assembly stage maps to its
    /// assembly column; otherwise the card sits in the "coordinator" column while it plans, dispatches,
    /// and awaits its children.
    /// </summary>
    public string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage)
    {
        if (coordinatorRun.Status is RunStatus.Completed or RunStatus.Merged or RunStatus.Failed
            or RunStatus.Declined or RunStatus.MergeFailed)
            return TerminalStageId;

        var status = planStage?.Status;
        if (status is WorkPlanStatus.Complete or WorkPlanStatus.AssemblyBlocked
            or WorkPlanStatus.AssemblyFailed or WorkPlanStatus.AssemblyDeclined)
            return TerminalStageId;

        return planStage?.AssemblyStage switch
        {
            AssemblyStage.Rai    => CoordinatorGraphDescriptor.AssemblyRaiNodeId,
            AssemblyStage.Review => CoordinatorGraphDescriptor.AssemblyReviewNodeId,
            AssemblyStage.Merge  => CoordinatorGraphDescriptor.AssemblyMergeNodeId,
            AssemblyStage.Scribe => CoordinatorGraphDescriptor.AssemblyScribeNodeId,
            AssemblyStage.Done   => TerminalStageId,
            _ => CoordinatorGraphDescriptor.CoordinatorNodeId,
        };
    }
}
