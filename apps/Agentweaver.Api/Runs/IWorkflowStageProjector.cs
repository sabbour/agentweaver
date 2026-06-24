using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Seam that resolves the fixed run buckets and maps a coordinator run to its current board bucket.
/// </summary>
public interface IWorkflowStageProjector
{
    /// <summary>
    /// Returns the ordered canonical run buckets exposed after Backlog and Ready.
    /// When <paramref name="definition"/> is supplied and declares explicit <c>stages</c>, those are
    /// used; otherwise falls back to the four hardcoded defaults (Problems, Human Review, Active, Done).
    /// </summary>
    IReadOnlyList<WorkflowStage> GetStages(WorkflowDefinition? definition = null);

    /// <summary>Maps a coordinator run's persisted state to the bucket id it currently occupies.</summary>
    string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage);
}
