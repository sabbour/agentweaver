using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Memory;

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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
