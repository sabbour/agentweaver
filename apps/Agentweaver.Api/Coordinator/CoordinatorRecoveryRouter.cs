namespace Agentweaver.Api.Coordinator;

/// <summary>
/// The action restart recovery must take for an interrupted coordinator run, derived purely from its
/// persisted work-plan status. Extracted from <see cref="CoordinatorRunService"/> so the routing table
/// (the part most prone to regressions) is unit-testable without constructing the heavyweight service.
/// </summary>
public enum CoordinatorRecoveryAction
{
    /// <summary>No work plan yet — still in the checkpointed spec draft/confirm phase. Resume the MAF workflow.</summary>
    ResumeSpecPhase,

    /// <summary>A work plan exists but has no subtasks — nothing to dispatch; finalize per the spec status.</summary>
    FinalizeNoSubtasks,

    /// <summary>Children were (or were about to be) in flight — reset in-flight subtasks and re-arm dispatch.</summary>
    Dispatch,

    /// <summary>All children terminal, awaiting collective assembly — re-arm assembly (DB CAS claims it).</summary>
    Assemble,

    /// <summary>Crashed mid-assembly or at the collective review gate — reset plan to awaiting_assembly and re-run assembly.</summary>
    ReArmAssembly,

    /// <summary>Plan reached <c>complete</c> but the run row was never finalized — settle the run as completed.</summary>
    SettleComplete,

    /// <summary>Plan reached a blocked/failed/declined terminal but the run row was never finalized — settle the run as failed.</summary>
    SettleFailed,
}

/// <summary>
/// Pure routing table for coordinator restart recovery. Maps a persisted work-plan status to the
/// recovery action. Keeping this side-effect free lets the full phase matrix be asserted cheaply.
/// </summary>
public static class CoordinatorRecoveryRouter
{
    /// <param name="hasPlan">True when a <see cref="WorkPlan"/> row exists for the coordinator run.</param>
    /// <param name="hasSubtasks">True when the plan has at least one subtask.</param>
    /// <param name="workPlanStatus">The persisted <see cref="WorkPlan.Status"/> (ignored when <paramref name="hasPlan"/> is false).</param>
    public static CoordinatorRecoveryAction Route(bool hasPlan, bool hasSubtasks, string? workPlanStatus)
    {
        if (!hasPlan)
            return CoordinatorRecoveryAction.ResumeSpecPhase;
        if (!hasSubtasks)
            return CoordinatorRecoveryAction.FinalizeNoSubtasks;

        return workPlanStatus switch
        {
            WorkPlanStatus.Planned => CoordinatorRecoveryAction.Dispatch,
            WorkPlanStatus.Dispatching => CoordinatorRecoveryAction.Dispatch,
            WorkPlanStatus.AwaitingAssembly => CoordinatorRecoveryAction.Assemble,
            WorkPlanStatus.Assembling => CoordinatorRecoveryAction.ReArmAssembly,
            WorkPlanStatus.InReview => CoordinatorRecoveryAction.ReArmAssembly,
            WorkPlanStatus.Complete => CoordinatorRecoveryAction.SettleComplete,
            WorkPlanStatus.AssemblyBlocked => CoordinatorRecoveryAction.Assemble,
            WorkPlanStatus.AssemblyFailed => CoordinatorRecoveryAction.SettleFailed,
            WorkPlanStatus.AssemblyDeclined => CoordinatorRecoveryAction.SettleFailed,
            // Unknown/forward-incompatible status: re-arm dispatch defensively rather than stranding the run.
            _ => CoordinatorRecoveryAction.Dispatch,
        };
    }
}
