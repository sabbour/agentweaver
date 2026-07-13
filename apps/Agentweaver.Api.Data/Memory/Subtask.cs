using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class Subtask
{
    [Key] public int Id { get; set; }
    public int WorkPlanId { get; set; }
    public required string Title { get; set; }
    public required string Scope { get; set; }
    public required string AssignedAgent { get; set; }
    public required string SelectedModelId { get; set; }
    public required string Phase { get; set; }            // none | planning | execution | validation
    /// <summary>
    /// Advisory isolation hint: "worktree" | "shared". This has NO runtime enforcement — all child
    /// runs execute against a single shared worktree (see
    /// <c>Agentweaver.AgentRuntime.RunOrchestrator.StartChildRunAsync</c>). "shared" does NOT mean the
    /// subtask is sandboxed or that it won't write files; it merely signals the subtask reads from
    /// shared context rather than owning its workspace. Because there is no isolation in practice,
    /// every subtask (regardless of this value) must declare its output filenames in <see cref="Scope"/>
    /// so <c>CoordinatorAssemblyService.DoSubtasksConflict</c> can serialize colliding writers.
    /// </summary>
    public required string IsolationStrategy { get; set; } // worktree | shared
    public required string Status { get; set; }            // pending | dispatched | running | rai_flagged | assemble_ready | completed | failed
    public string? ChildRunId { get; set; }
    public string? LockedOutAgents { get; set; }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Req-1, change #1) — the <c>ChildRunId</c> of the PRIOR child run,
    /// captured immediately BEFORE a conscious fresh dispatch (or a lockout rotation) clears
    /// <see cref="ChildRunId"/> to <c>null</c>. This is the durable, mechanically-recoverable pointer to
    /// the prior attempt's work (its run/worktree/diff + the run's integration branch), so a fresh /
    /// rotated agent can be handed the prior diff + integration state instead of starting from a blank
    /// pod. Consumed when composing the retry guidance/handoff bundle. Null until the subtask has been
    /// dispatched-fresh at least once.
    /// </summary>
    public string? PriorChildRunId { get; set; }

    /// <summary>
    /// Optional bespoke charter authored inline by the coordinator's decomposition when no catalog
    /// role adequately covers this subtask's function. When set, it flows to the dispatched child
    /// <see cref="Agentweaver.Domain.Run.AgentCharter"/> and overrides file-based charter resolution,
    /// letting the coordinator mint a domain-specific agent persona without a catalog role. Null when
    /// the subtask maps to a catalog/roster role (the common case).
    /// </summary>
    public string? AgentCharter { get; set; }

    /// <summary>
    /// Recovery guidance attached when a parked/failed subtask is RESUMED via steering
    /// (<see cref="Agentweaver.Api.Coordinator.CoordinatorSteeringService"/>). Carries the human's
    /// steering instruction plus the failure context so the re-dispatched worker re-does the work
    /// against the latest state and addresses the feedback. Null when the subtask has never been
    /// recovered. Appended to the child task by <c>ComposeChildTask</c>.
    /// </summary>
    public string? RecoveryGuidance { get; set; }

    /// <summary>
    /// Number of times this subtask has been auto-resumed by steering recovery. Bounded by a small
    /// cap so a persistently failing/flagged subtask cannot be re-dispatched forever.
    /// </summary>
    public int RecoveryAttempts { get; set; }

    /// <summary>
    /// Number of fresh child runs automatically dispatched after a child terminated with an
    /// infrastructure failure explicitly marked <c>retryable: true</c>. Kept separate from steering
    /// recovery and reviewer lockout state so infrastructure resilience cannot alter those protocols.
    /// </summary>
    public int InfrastructureRetryCount { get; set; }

    /// <summary>
    /// Earliest UTC time at which the dispatch frontier may launch the next infrastructure retry.
    /// Persisted so coordinator restarts and replica failover preserve exponential backoff instead of
    /// recreating an immediate retry storm.
    /// </summary>
    public DateTimeOffset? InfrastructureRetryEligibleAt { get; set; }

    // ── UNIFIED AUTONOMOUS STEERING (rev8) — additive, nullable ─────────────────────────────────
    /// <summary>The steering directive id that last reset this subtask via a conscious fresh dispatch
    /// (direction B). Together with <see cref="LastResetAttempt"/> this is the durable
    /// <c>(directiveId, attempt)</c> idempotency stamp so a crash-recovery replay of the B action is a
    /// no-op for a subtask already reset for this attempt.</summary>
    public int? LastResetDirectiveId { get; set; }

    /// <summary>The action attempt number of the last B reset for this subtask (idempotency key).</summary>
    public int? LastResetAttempt { get; set; }

    /// <summary>While non-null and in the future, this subtask's child worktree/checkpoints/session
    /// meta are RETAINED (terminal checkpoint delete/GC suppressed) so an in-place steer (A) can
    /// resume with context. Bounded by the assembly window + the plan iteration cap so it cannot leak.</summary>
    public DateTimeOffset? SteeringRetentionUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
