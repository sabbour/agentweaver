using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class AgentMemory
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentName { get; set; }
    public string? SessionId { get; set; }
    public required string Type { get; set; }        // core_context | learning | pattern | update
    public required string Importance { get; set; }  // high | medium | low
    public required string Content { get; set; }
    public string? Tags { get; set; }                // comma-separated; "cross-team" enables cross-agent sharing
    public string SourceKind { get; set; } = MemorySourceKinds.Legacy;
    public string? SourceIdentity { get; set; }
    public string? SourceRunId { get; set; }
    public string TrustState { get; set; } = MemoryTrustStates.Legacy;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
