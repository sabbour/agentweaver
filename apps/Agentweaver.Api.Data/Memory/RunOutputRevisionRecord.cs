namespace Agentweaver.Api.Memory;

public sealed class RunOutputRevisionRecord
{
    public string RevisionId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string RunId { get; set; } = "";
    public int LifecycleGeneration { get; set; }
    public string? WorkflowDigest { get; set; }
    public bool ManifestIncomplete { get; set; }
    public string TreeHash { get; set; } = "";
    public string DiffSha256 { get; set; } = "";
    public string? PredecessorRevisionId { get; set; }
    public string? OutputKind { get; set; }
    public string? MergedCommitHash { get; set; }
    public string? WorkPlanId { get; set; }
    public string? MergeEffectId { get; set; }
    public bool AcceptedNoChange { get; set; }
    public byte[]? DiffBytes { get; set; }
    public byte[]? TreeContent { get; set; }
    public string? TreeContentSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
