namespace Agentweaver.Api.Memory;

/// <summary>EF entity for the runs table (agentweaver.db migration to MemoryDbContext).</summary>
public sealed class RunRecord
{
    public string RunId { get; set; } = "";
    public string RepositoryPath { get; set; } = "";
    public string OriginatingBranch { get; set; } = "";
    public string ModelSource { get; set; } = "";
    public string Task { get; set; } = "";
    public string SubmittingUser { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Result { get; set; }
    public string? WorktreePath { get; set; }
    public string? WorktreeBranch { get; set; }
    public string? TreeHash { get; set; }
    public int StepCount { get; set; }
    public string? Diff { get; set; }
    public string? MergeConflicts { get; set; }
    public string? ProjectId { get; set; }
    public string? ModelId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentCharter { get; set; }
    public string? ReviewedBy { get; set; }
    public string? WorkflowRunId { get; set; }
    public string? MergedCommitHash { get; set; }
    public string? ParentRunId { get; set; }
    public string? SubtaskId { get; set; }
    public string Origin { get; set; } = "interactive";
    public string? RetriedFrom { get; set; }
    public DateTimeOffset? ReviewReadyAt { get; set; }
    public long ReviewWaitMs { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? OwnerId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public long FencingToken { get; set; }
    public int Attempt { get; set; }
}
