using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class WorkPlan
{
    [Key] public int Id { get; set; }
    public int OutcomeSpecId { get; set; }
    public required string ProjectId { get; set; }
    public required string CoordinatorRunId { get; set; }
    public string? IsolationSummary { get; set; }
    public string? IntegrationBranch { get; set; }

    /// <summary>
    /// The id of the functional workflow the coordinator selected for this task (Feature 015 US5).
    /// Persisted so downstream phases (decomposition, dispatch) drive execution from the selected
    /// topology rather than treating selection as advisory. Null when the project carries a single
    /// workflow (selection skipped) — downstream consumers fall back to the project default.
    /// </summary>
    public string? WorkflowId { get; set; }
    public required string Status { get; set; }      // planned | dispatching | awaiting_assembly | assembling | in_review | complete | assembly_blocked | assembly_failed | assembly_declined

    /// <summary>
    /// Phase 3 collective-assembly progress stage (null until assembly starts): rai | review |
    /// merge | scribe | done. Drives the coordinator graph node-flip (planned -&gt; live).
    /// </summary>
    public string? AssemblyStage { get; set; }

    /// <summary>
    /// Assembly stage that produced the current parked/terminal status. Unlike <see cref="AssemblyStage"/>,
    /// this does not advance when the failure scribe runs, so the UI can distinguish the gate/action
    /// that failed from later cleanup stages. Null when assembly stopped before any collective gate ran.
    /// </summary>
    public string? AssemblyTerminalStage { get; set; }

    /// <summary>
    /// Durable reason for the current parked/terminal assembly status (for example
    /// <c>assembly_blocked: ineligible_subtasks [47]</c> or <c>assembly_merge_failed: merge_error</c>).
    /// </summary>
    public string? AssemblyStatusReason { get; set; }

    /// <summary>Timestamp the work plan transitioned awaiting_assembly -&gt; assembling (the
    /// exactly-once CAS claim). Null until assembly is claimed.</summary>
    public DateTimeOffset? AssemblyStartedAt { get; set; }

    /// <summary>
    /// The Kubernetes pod (hostname) that currently owns the coordinator dispatch loop for this plan.
    /// Set atomically when a pod starts or re-arms dispatch; used by <c>CoordinatorReconciler</c> as a
    /// distributed lease so only one replica drives a given run even when <c>IsDispatchActive</c> is
    /// pod-local. Null until first dispatch, cleared on terminal status transitions.
    /// </summary>
    public string? CoordinatorPodId { get; set; }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8): per-plan count of steering iterations applied. Bounded by
    /// the configurable plan cap (default 6) so gate-driven steering cannot loop forever; at the cap
    /// the decider escalates to human review / terminal instead of steering again.
    /// </summary>
    public int SteeringIterations { get; set; }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B): per-plan count of HUMAN-review round-trips taken after the
    /// autonomous steering budget was exhausted and the plan was escalated to the human-review gate. A
    /// human request-changes resets the autonomous <see cref="SteeringIterations"/> budget (a fresh
    /// mandate) so the coordinator can converge again under human guidance; this counter bounds that
    /// reset (default cap 3, <c>CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips</c>). Once the
    /// cap is reached the budget is NO LONGER reset — autonomy stops re-steering and the plan simply
    /// parks (again) at human review (never terminal, never a hidden loop). Atomically incremented so the
    /// reset+backstop decision is cross-replica/crash-safe.
    /// </summary>
    public int HumanReviewRoundTrips { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
