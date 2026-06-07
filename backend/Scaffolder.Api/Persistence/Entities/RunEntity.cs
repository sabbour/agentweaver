using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// Represents a single agent run from submission through completion or review.
/// </summary>
public sealed class RunEntity
{
    public Guid Id { get; set; }

    [MaxLength(500)]
    public required string OriginatingBranch { get; set; }

    public required ModelSource ModelSource { get; set; }

    [MaxLength(10000)]
    public required string TaskPrompt { get; set; }

    /// <summary>
    /// Identity of the user who submitted this run. Recorded as the named human
    /// accountable for the run (FR-024). Preserved for the full retention window.
    /// </summary>
    [MaxLength(500)]
    public required string SubmittedBy { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Queued;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public int MaxSteps { get; set; } = 200;
    public int MaxDurationSeconds { get; set; } = 1800;

    /// <summary>
    /// Foreign key to the Session. Null until the run starts and a worktree is created.
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>
    /// Summary of the diff produced by this run. Null until the run reaches a terminal state.
    /// </summary>
    [MaxLength(5000)]
    public string? DiffSummary { get; set; }

    /// <summary>
    /// Human-readable failure reason. Null unless the run is in Failed or Bounded status.
    /// </summary>
    [MaxLength(2000)]
    public string? FailureReason { get; set; }

    // Navigation
    public SessionEntity? Session { get; set; }
    public ICollection<EventEntity> Events { get; set; } = new List<EventEntity>();
    public ReviewDecisionEntity? ReviewDecision { get; set; }
    public OperationalRecordEntity? OperationalRecord { get; set; }
}