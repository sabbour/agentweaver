using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Seam that resolves descriptor-driven workflow columns and maps a coordinator run to its current
/// stage. Introduced to make the FR-019 fallback branch in <see cref="BoardProjectionService"/>
/// testable without altering production behaviour (Principle VII).
/// </summary>
public interface IWorkflowStageProjector
{
    /// <summary>
    /// Returns the ordered workflow stages derived from the coordinator topology, or throws / returns
    /// an empty list when the topology cannot be resolved (caller sets workflow_stages_available=false).
    /// </summary>
    IReadOnlyList<WorkflowStage> GetStages();

    /// <summary>Maps a coordinator run's persisted state to the column id it currently occupies.</summary>
    string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage);
}
