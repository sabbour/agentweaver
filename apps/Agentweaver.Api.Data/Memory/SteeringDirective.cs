using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class SteeringDirective
{
    [Key] public int Id { get; set; }
    public required string CoordinatorRunId { get; set; }
    public string? TargetChildRunId { get; set; }
    public required string Kind { get; set; }         // redirect | pause | stop | amend
    public required string Instruction { get; set; }
    public required string Status { get; set; }       // pending | queued | relayed | decided | executing | applied
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RelayedAt { get; set; }

    // ── UNIFIED AUTONOMOUS STEERING (rev8) — additive, all nullable ─────────────────────────────
    /// <summary>Source that emitted the signal: human-review | rai | rubberduck | build-test | agent |
    /// coordinator | step. Null on legacy rows created before unified steering.</summary>
    public string? Source { get; set; }

    /// <summary>Severity/kind of the feedback: advisory | request-changes | blocking.</summary>
    public string? Severity { get; set; }

    /// <summary>Serialized <c>TargetScope</c> { kind, subtaskIds?, childRunId? } the signal addresses.</summary>
    public string? TargetScopeJson { get; set; }

    /// <summary>Aggregate tree hash the feedback was produced against (staleness guard). Advisory.</summary>
    public string? TreeHash { get; set; }

    // ── Durable action-intent state machine (rev8 §3c/§3d) ──────────────────────────────────────
    /// <summary>The coordinator's chosen direction once decided: in_place_steer | dispatch_fresh |
    /// proceed | advisory. Recorded atomically with the <c>relayed→decided</c> transition.</summary>
    public string? DecidedAction { get; set; }

    /// <summary>The budget attempt number stamped at decision time; the idempotency key component for
    /// the A/B execution effect markers. Single-incremented in the decision transaction.</summary>
    public int? ActionAttempt { get; set; }

    /// <summary>Execution-phase lease timestamp; set on <c>decided→executing</c> so a stale
    /// <c>decided</c>/<c>executing</c> directive can be re-driven by recovery.</summary>
    public DateTimeOffset? ExecStartedAt { get; set; }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §6, loop-bound fix) — bounded per-directive EXECUTION retry
    /// counter, SEPARATE from the steering budget (<see cref="WorkPlan.SteeringIterations"/> /
    /// <see cref="Subtask.RecoveryAttempts"/>, incremented exactly once at decision time). A Decision-A
    /// revision that completes/errors BEFORE writing any checkpoint would otherwise leave the effect
    /// marker <c>initiated</c> forever and be re-driven without re-incrementing the steering budget. This
    /// counter is CAS-incremented on every execution re-drive; once it reaches
    /// <c>CoordinatorSteeringDecider.MaxExecutionAttempts</c> the directive is terminalized to a visible
    /// needs-attention state instead of looping.
    /// </summary>
    public int ExecutionAttempts { get; set; }
}
