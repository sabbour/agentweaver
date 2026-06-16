using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Memory;

public sealed class Decision
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentName { get; set; }
    public required string Type { get; set; }        // architectural | scope | process | pattern
    public required string Status { get; set; }      // active | superseded | rejected
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? Rationale { get; set; }
    public string? Tags { get; set; }                // comma-separated
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
