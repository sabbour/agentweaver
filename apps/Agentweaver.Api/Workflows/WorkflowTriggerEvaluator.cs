namespace Agentweaver.Api.Workflows;

/// <summary>
/// How a coordinator run was invoked, used to decide which workflow triggers are eligible for it.
/// A run is either started explicitly by a person (<see cref="Manual"/>) or picked up automatically by
/// the coordinator heartbeat from the project's Ready bucket (<see cref="Heartbeat"/>).
/// Schedule-trigger dispatch is handled by trigger-task automation, not by these existing invocation
/// kinds.
/// </summary>
public enum WorkflowInvocationKind
{
    /// <summary>A person explicitly started the run (interactive coordinator run).</summary>
    Manual,

    /// <summary>The coordinator heartbeat picked up a Ready backlog task and started the run.</summary>
    Heartbeat,
}

/// <summary>
/// Evaluates a workflow's declared <see cref="WorkflowTrigger"/> against how a run was invoked so the
/// coordinator only considers workflows whose trigger actually matches the invocation. Without this,
/// the declared trigger is pure metadata and any workflow can be selected for any invocation.
///
/// <para>Eligibility mapping:</para>
/// <list type="bullet">
/// <item><see cref="WorkflowInvocationKind.Manual"/> → only <see cref="WorkflowTriggerType.Manual"/>
/// workflows (FR-020: a person explicitly starts the run).</item>
/// <item><see cref="WorkflowInvocationKind.Heartbeat"/> → <see cref="WorkflowTriggerType.Heartbeat"/>
/// workflows (FR-021: periodic pickup) AND <see cref="WorkflowTriggerType.Event"/> workflows whose
/// event is <see cref="WorkflowEventType.TaskAddedToReady"/> (FR-022): a task entering Ready and being
/// picked up by the heartbeat IS the "task added to Ready" event.</item>
/// <item><see cref="WorkflowTriggerType.Schedule"/> definitions carry cadence metadata for scheduled
/// trigger tasks and are not selected by manual/backlog-pickup invocations.</item>
/// </list>
/// </summary>
public static class WorkflowTriggerEvaluator
{
    /// <summary>Returns true when the workflow's declared trigger matches the invocation kind.</summary>
    public static bool IsEligible(WorkflowTrigger trigger, WorkflowInvocationKind kind)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return kind switch
        {
            WorkflowInvocationKind.Manual => trigger.Type == WorkflowTriggerType.Manual,
            WorkflowInvocationKind.Heartbeat =>
                trigger.Type == WorkflowTriggerType.Heartbeat ||
                (trigger.Type == WorkflowTriggerType.Event &&
                 trigger.Event == WorkflowEventType.TaskAddedToReady),
            _ => false,
        };
    }

    /// <summary>
    /// Filters <paramref name="candidates"/> to the workflows whose declared trigger matches
    /// <paramref name="kind"/>, preserving the input order (so a default-first ordering is retained).
    /// Returns an empty list when no candidate matches; callers fall back to the project default in
    /// that case rather than selecting a workflow whose trigger does not match the invocation.
    /// </summary>
    public static IReadOnlyList<WorkflowDefinition> Filter(
        IReadOnlyList<WorkflowDefinition> candidates, WorkflowInvocationKind kind)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Where(w => IsEligible(w.Trigger, kind)).ToList();
    }
}
