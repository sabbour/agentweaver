using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Domain;

/// <summary>Immutable review-ready diff bytes and pinned Git tree for one run generation.</summary>
public sealed class RunOutputRevision
{
    public const int CurrentSchemaVersion = 1;

    public string RevisionId { get; }
    public int SchemaVersion { get; }
    public RunId RunId { get; }
    public int LifecycleGeneration { get; }
    public string? WorkflowDigest { get; }
    public bool ManifestIncomplete { get; }
    public string TreeHash { get; }
    public string DiffSha256 { get; }
    public string? PredecessorRevisionId { get; }
    public DateTimeOffset CreatedAt { get; }
    private readonly byte[] _diffBytes;
    public byte[] DiffBytes => (byte[])_diffBytes.Clone();

    public RunOutputRevision(
        string revisionId, int schemaVersion, RunId runId, int lifecycleGeneration,
        string? workflowDigest, bool manifestIncomplete, string treeHash, string diffSha256,
        string? predecessorRevisionId, byte[]? diffBytes, DateTimeOffset createdAt)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new RunOutputRevisionUnavailableException("unsupported_schema");
        if (diffBytes is null)
            throw new RunOutputRevisionUnavailableException("missing_content");
        if (!string.Equals(Sha256(diffBytes), diffSha256, StringComparison.OrdinalIgnoreCase))
            throw new RunOutputRevisionUnavailableException("corrupt_content");
        if (string.IsNullOrWhiteSpace(treeHash) || string.IsNullOrWhiteSpace(revisionId)
            || (manifestIncomplete == (workflowDigest is not null)))
            throw new RunOutputRevisionUnavailableException("invalid_manifest");

        RevisionId = revisionId;
        SchemaVersion = schemaVersion;
        RunId = runId;
        LifecycleGeneration = lifecycleGeneration;
        WorkflowDigest = workflowDigest;
        ManifestIncomplete = manifestIncomplete;
        TreeHash = treeHash;
        DiffSha256 = diffSha256;
        PredecessorRevisionId = predecessorRevisionId;
        _diffBytes = (byte[])diffBytes.Clone();
        CreatedAt = createdAt;
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] EncodeDiff(string diff) => new UTF8Encoding(false, true).GetBytes(diff);

    public bool Matches(Run run, string? revisionId) =>
        string.Equals(RevisionId, revisionId, StringComparison.Ordinal)
        && RunId == run.Id
        && LifecycleGeneration == run.LifecycleGeneration
        && string.Equals(TreeHash, run.TreeHash, StringComparison.Ordinal)
        && string.Equals(WorkflowDigest, run.ExecutableWorkflowContentDigest, StringComparison.Ordinal)
        && run.Diff is not null
        && string.Equals(DiffSha256, Sha256(EncodeDiff(run.Diff)), StringComparison.Ordinal);
}

public sealed class RunOutputRevisionUnavailableException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
