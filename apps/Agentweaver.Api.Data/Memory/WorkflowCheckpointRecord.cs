namespace Agentweaver.Api.Memory;

/// <summary>
/// A single MAF workflow checkpoint row, persisted in Postgres so all API/worker replicas read and
/// write the SAME checkpoints with no exclusive file lock and no shared-volume permission dependency.
/// <para>
/// Replaces the per-pod <c>FileSystemJsonCheckpointStore</c> on the production (Postgres) path. Each
/// checkpoint is an independent, unique-PK row, so concurrent writes from different replicas never
/// contend, and Postgres MVCC makes a committed checkpoint immediately visible to the other replica —
/// enabling genuine cross-pod resume.
/// </para>
/// </summary>
public sealed class WorkflowCheckpointRecord
{
    /// <summary>
    /// Logical store discriminator partitioning the two checkpoint stores that previously lived in
    /// separate directories: <c>"runs"</c> (RunWorkflowFactory) and <c>"coordinator"</c>
    /// (CoordinatorWorkflowFactory).
    /// </summary>
    public string StoreName { get; set; } = "";

    /// <summary>MAF session id. For the runs store this is the RunId.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Unique checkpoint id (GUID) generated on create.</summary>
    public string CheckpointId { get; set; } = "";

    /// <summary>Parent checkpoint id, when this checkpoint was created from a parent.</summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>
    /// Mirrors MAF's FileSystem store semantics: whether parent metadata was recorded for this entry.
    /// Used by the index filter so a parent-scoped index query behaves identically to the file store.
    /// </summary>
    public bool HasParentMetadata { get; set; } = true;

    /// <summary>The checkpoint payload (a JSON document), stored as <c>jsonb</c>.</summary>
    public string Payload { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
