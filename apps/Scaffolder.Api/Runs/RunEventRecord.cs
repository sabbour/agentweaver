namespace Scaffolder.Api.Runs;

public sealed class RunEventRecord
{
    public long Id { get; set; }
    /// <summary>Run ID — may be "{guid}" for the main stream or "{guid}-rai"/"{guid}-scribe" for sub-streams.</summary>
    public string RunId { get; set; } = "";
    public int Sequence { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
