using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class Decision
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentName { get; set; }
    public required string Type { get; set; }        // architectural | process | scope | technical
    public required string Status { get; set; }      // active | superseded | archived
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? Rationale { get; set; }
    public string? Tags { get; set; }                // comma-separated
    public int? SupersededById { get; set; }         // FK -> Decision.Id
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
