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
    public string Provenance { get; set; } = MemoryProvenance.AgentAuthored;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class MemoryProvenance
{
    public const string AgentAuthored = "agent-authored";
    public const string HumanReviewed = "human-reviewed";
    public const string SystemGenerated = "system-generated";

    public static bool IsTrusted(string? provenance) =>
        provenance is HumanReviewed or SystemGenerated;
}
