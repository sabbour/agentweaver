using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class DecisionInboxEntry
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentName { get; set; }
    public required string Slug { get; set; }        // kebab-case identifier
    public required string Type { get; set; }        // architectural | scope | process | pattern
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? Rationale { get; set; }
    public required string Status { get; set; }      // pending | merged | rejected
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
}
