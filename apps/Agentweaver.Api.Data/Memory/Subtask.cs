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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
