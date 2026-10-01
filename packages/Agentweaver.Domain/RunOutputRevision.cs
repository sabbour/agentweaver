using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Domain;

/// <summary>Immutable output diff bytes and pinned Git tree for one run generation.</summary>
public sealed class RunOutputRevision
{
    public const int CurrentSchemaVersion = 1;
    public const int CollectiveSchemaVersion = 2;
    public const int NoChangeSchemaVersion = 3;
    public const int CollectiveCandidateSchemaVersion = 4;
    public const int FanDeclaredFilesSchemaVersion = 5;

    public string RevisionId { get; }
    public int SchemaVersion { get; }
    public RunId RunId { get; }
    public int LifecycleGeneration { get; }
    public string? WorkflowDigest { get; }
    public bool ManifestIncomplete { get; }
    public string TreeHash { get; }
    public string DiffSha256 { get; }
    public string? PredecessorRevisionId { get; }
    public string? OutputKind { get; }
    public string? MergedCommitHash { get; }
    public string? WorkPlanId { get; }
    public string? MergeEffectId { get; }
    public bool AcceptedNoChange { get; }
    public string? TreeContentSha256 { get; }
    private readonly byte[]? _treeContent;
    public byte[]? TreeContent => _treeContent is null ? null : (byte[])_treeContent.Clone();
    public DateTimeOffset CreatedAt { get; }
    private readonly byte[] _diffBytes;
    public byte[] DiffBytes => (byte[])_diffBytes.Clone();

    public RunOutputRevision(
        string revisionId, int schemaVersion, RunId runId, int lifecycleGeneration,
        string? workflowDigest, bool manifestIncomplete, string treeHash, string diffSha256,
        string? predecessorRevisionId, byte[]? diffBytes, DateTimeOffset createdAt,
        string? outputKind = null, string? mergedCommitHash = null, string? workPlanId = null,
        string? mergeEffectId = null, bool acceptedNoChange = false,
        byte[]? treeContent = null, string? treeContentSha256 = null)
    {
        if (schemaVersion is not (CurrentSchemaVersion or CollectiveSchemaVersion or NoChangeSchemaVersion
            or CollectiveCandidateSchemaVersion or FanDeclaredFilesSchemaVersion))
            throw new RunOutputRevisionUnavailableException("unsupported_schema");
        if (diffBytes is null)
            throw new RunOutputRevisionUnavailableException("missing_content");
        if (!string.Equals(Sha256(diffBytes), diffSha256, StringComparison.OrdinalIgnoreCase))
            throw new RunOutputRevisionUnavailableException("corrupt_content");
        if (treeContent is not null || treeContentSha256 is not null)
        {
            if (treeContent is null || !string.Equals(Sha256(treeContent), treeContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new RunOutputRevisionUnavailableException("corrupt_content");
            RunOutputTree.Decode(treeContent);
        }
        // Collective output completeness is determined by its captured files; coordinators may have no workflow digest.
        if (string.IsNullOrWhiteSpace(treeHash) || string.IsNullOrWhiteSpace(revisionId)
            || (schemaVersion == CurrentSchemaVersion && manifestIncomplete == (workflowDigest is not null))
            || (schemaVersion == CollectiveSchemaVersion
                && (outputKind != "collective" || string.IsNullOrWhiteSpace(mergedCommitHash)
                    || string.IsNullOrWhiteSpace(workPlanId) || string.IsNullOrWhiteSpace(mergeEffectId)
                    || (manifestIncomplete && workflowDigest is not null)))
            || (schemaVersion == CollectiveCandidateSchemaVersion
                && (outputKind != "collective" || string.IsNullOrWhiteSpace(workPlanId)
                    || mergedCommitHash is not null || mergeEffectId is not null || treeContent is null
                    || (manifestIncomplete && workflowDigest is not null)))
            || (schemaVersion == NoChangeSchemaVersion
                && (outputKind != "no_change" || !acceptedNoChange || diffBytes.Length != 0
                    || string.IsNullOrWhiteSpace(mergedCommitHash)
                    || workPlanId is not null || mergeEffectId is not null
                    || treeContent is null || manifestIncomplete != (workflowDigest is null)))
            || (schemaVersion == FanDeclaredFilesSchemaVersion
                && (outputKind != "fan_declared_files" || manifestIncomplete || treeContent is null
                    || treeHash.Length != 40 || !treeHash.All(Uri.IsHexDigit)
                    || mergedCommitHash?.Length != 40 || !mergedCommitHash.All(Uri.IsHexDigit)
                    || string.IsNullOrWhiteSpace(workPlanId)
                    || mergeEffectId is not null || acceptedNoChange || diffBytes.Length != 0
                    || RunOutputTree.Decode(treeContent).Count == 0
                    || RunOutputTree.Decode(treeContent).Any(file =>
                        file.Mode is not (33188 or 33261) || file.Path.Contains(':')))))
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
        OutputKind = outputKind;
        MergedCommitHash = mergedCommitHash;
        WorkPlanId = workPlanId;
        MergeEffectId = mergeEffectId;
        AcceptedNoChange = acceptedNoChange;
        TreeContentSha256 = treeContentSha256;
        _treeContent = treeContent is null ? null : (byte[])treeContent.Clone();
        _diffBytes = (byte[])diffBytes.Clone();
        CreatedAt = createdAt;
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] EncodeDiff(string diff) => new UTF8Encoding(false, true).GetBytes(diff);

    public IReadOnlyList<RunOutputTree.File> ResolveFiles()
    {
        if (ManifestIncomplete)
            throw new RunOutputRevisionUnavailableException("incomplete_manifest");
        return RunOutputTree.Decode(_treeContent);
    }

    public RunOutputTree.File ResolveFile(string path) =>
        ResolveFiles().FirstOrDefault(file => file.Path == path)
        ?? throw new RunOutputRevisionUnavailableException("file_not_found");

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
