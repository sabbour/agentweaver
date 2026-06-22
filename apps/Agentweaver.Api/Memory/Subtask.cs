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
    public required string IsolationStrategy { get; set; } // worktree | shared
    public required string Status { get; set; }            // pending | dispatched | running | rai_flagged | assemble_ready | completed | failed
    public string? ChildRunId { get; set; }
    public string? LockedOutAgents { get; set; }

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
