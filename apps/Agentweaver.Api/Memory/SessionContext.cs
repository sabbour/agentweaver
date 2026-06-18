using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public sealed class SessionContext
{
    [Key] public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string SessionId { get; set; }
    public required string FocusArea { get; set; }
    public string? ActiveIssues { get; set; }        // comma-separated issue refs
    public string? Summary { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
