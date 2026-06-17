using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Memory;

public sealed class SteeringDirective
{
    [Key] public int Id { get; set; }
    public required string CoordinatorRunId { get; set; }
    public string? TargetChildRunId { get; set; }
    public required string Kind { get; set; }         // redirect | pause | stop | amend
    public required string Instruction { get; set; }
    public required string Status { get; set; }       // pending | queued | relayed | applied
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RelayedAt { get; set; }
}
