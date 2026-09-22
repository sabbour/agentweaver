namespace Agentweaver.Api.Memory;

/// <summary>Postgres persistence record for the run-database terminal-outcome outbox.</summary>
public sealed class TerminalRunOutcomeRecord
{
    public string RunId { get; set; } = "";
    public int LifecycleGeneration { get; set; }
    public string Status { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProjectedAt { get; set; }
}
