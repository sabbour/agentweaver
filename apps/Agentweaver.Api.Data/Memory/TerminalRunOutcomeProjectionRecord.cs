namespace Agentweaver.Api.Memory;

/// <summary>Event-store uniqueness claim for one projected terminal lifecycle generation.</summary>
public sealed class TerminalRunOutcomeProjectionRecord
{
    public string RunId { get; set; } = "";
    public int LifecycleGeneration { get; set; }
}
