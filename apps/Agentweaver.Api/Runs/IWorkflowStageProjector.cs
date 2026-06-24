using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Seam that resolves the fixed run buckets and maps a coordinator run to its current board bucket.
/// </summary>
public interface IWorkflowStageProjector
{
    /// <summary>
    /// Returns the ordered canonical run buckets exposed after Backlog and Ready.
    /// </summary>
    IReadOnlyList<WorkflowStage> GetStages();

    /// <summary>Maps a coordinator run's persisted state to the bucket id it currently occupies.</summary>
    string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage);
}
