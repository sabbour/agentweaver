using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>A single workflow-stage column derived from the coordinator topology (descriptor-driven).</summary>
public sealed record WorkflowStage(string Id, string Label, int Order);

/// <summary>Maps persisted coordinator/run state into the board's fixed canonical bucket model.</summary>
public sealed class WorkflowStageProjector : IWorkflowStageProjector
{
    public const string ProblemsStageId = "problems";
    public const string HumanReviewStageId = "human-review";
    public const string ActiveStageId = "active";
    public const string DoneStageId = "done";

    public const string TerminalStageId = DoneStageId;
    public const string TerminalStageLabel = "Done";

    /// <summary>
    /// Returns the non-intake canonical buckets. Backlog and Ready are intake columns owned by
    /// <see cref="BacklogTaskState"/>; all run-backed work folds into these four fixed buckets.
    /// </summary>
    public IReadOnlyList<WorkflowStage> GetStages() => new[]
    {
        new WorkflowStage(ProblemsStageId, "Problems", 0),
        new WorkflowStage(HumanReviewStageId, "Human Review", 1),
        new WorkflowStage(ActiveStageId, "Active", 2),
        new WorkflowStage(DoneStageId, "Done", 3),
    };

    /// <summary>
    /// Maps a coordinator run's authoritative persisted state to the column id it currently occupies.
    /// Terminal run/work-plan states collapse to "terminal"; an in-progress assembly stage maps to its
    /// assembly column; otherwise the card sits in the "coordinator" column while it plans, dispatches,
    /// and awaits its children.
    /// </summary>
    public string CoordinatorRunToStageId(Run coordinatorRun, CoordinatorWorkPlanStage? planStage)
    {
        if (coordinatorRun.Status is RunStatus.Failed or RunStatus.Declined or RunStatus.MergeFailed)
            return ProblemsStageId;

        if (coordinatorRun.Status is RunStatus.Completed or RunStatus.Merged or RunStatus.AssembleReady)
            return DoneStageId;

        if (coordinatorRun.Status is RunStatus.AwaitingReview)
            return HumanReviewStageId;

        var status = planStage?.Status;
        if (status is WorkPlanStatus.AssemblyBlocked or WorkPlanStatus.AssemblyFailed
            or WorkPlanStatus.AssemblyDeclined)
            return ProblemsStageId;

        if (status is WorkPlanStatus.Complete)
            return DoneStageId;

        if (status is WorkPlanStatus.InReview)
            return HumanReviewStageId;

        return planStage?.AssemblyStage switch
        {
            AssemblyStage.Review => HumanReviewStageId,
            AssemblyStage.Done   => DoneStageId,
            _ => ActiveStageId,
        };
    }
}
