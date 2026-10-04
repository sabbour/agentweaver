using System.Text;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>A fail-closed composition of retained outputs against one pinned source tree.</summary>
internal sealed class ImmutableExecutionInputPlan
{
    public string SourceCommitHash { get; }
    public string SourceTreeHash { get; }
    public string CompositeId { get; }
    private readonly byte[] _treeContent;
    public byte[] TreeContent => (byte[])_treeContent.Clone();
    public IReadOnlyList<string> RevisionIds { get; }

    private ImmutableExecutionInputPlan(
        string sourceCommitHash, string sourceTreeHash, string compositeId,
        byte[] treeContent, IReadOnlyList<string> revisionIds)
    {
        SourceCommitHash = sourceCommitHash;
        SourceTreeHash = sourceTreeHash;
        CompositeId = compositeId;
        _treeContent = (byte[])treeContent.Clone();
        RevisionIds = revisionIds.ToArray();
    }

    public static ImmutableExecutionInputPlan Compose(
        string sourceCommitHash, string sourceTreeHash, byte[] sourceTreeContent,
        IReadOnlyList<(BacklogClaimedPrerequisite Claim, RunOutputRevision Revision)> inputs)
    {
        if (string.IsNullOrWhiteSpace(sourceCommitHash) || string.IsNullOrWhiteSpace(sourceTreeHash))
            throw new RunOutputRevisionUnavailableException("source_revision_unavailable");

        var source = RunOutputTree.Decode(sourceTreeContent)
            .ToDictionary(file => file.Path, StringComparer.Ordinal);
        var composed = new Dictionary<string, RunOutputTree.File>(source, StringComparer.Ordinal);
        var changes = new Dictionary<string, (RunOutputTree.File? File, string RevisionId)>(StringComparer.Ordinal);
        using var identity = new MemoryStream();
        using var writer = new BinaryWriter(identity, Encoding.UTF8, leaveOpen: true);
        writer.Write(sourceCommitHash);
        writer.Write(sourceTreeHash);
        writer.Write(RunOutputRevision.Sha256(sourceTreeContent));
        writer.Write(inputs.Count);

        foreach (var (claim, revision) in inputs)
        {
            if (revision.RevisionId != claim.OutputRevisionId || revision.RunId.ToString() != claim.RunId
                || revision.SchemaVersion == RunOutputRevision.FanDeclaredFilesSchemaVersion
                || revision.LifecycleGeneration != claim.LifecycleGeneration
                || revision.TreeHash != claim.TreeHash
                || revision.WorkflowDigest != claim.ExecutableWorkflowContentDigest
                || (claim.Outcome == "accepted_no_change") != revision.AcceptedNoChange
                || revision.ManifestIncomplete)
                throw new RunOutputRevisionUnavailableException("claim_revision_mismatch");
            var files = revision.ResolveFiles().ToDictionary(file => file.Path, StringComparer.Ordinal);
            var touched = revision.AcceptedNoChange
                ? Array.Empty<string>()
                : ParseChangedPaths(revision.DiffBytes);
            foreach (var path in touched)
            {
                files.TryGetValue(path, out var candidate);
                if (changes.TryGetValue(path, out var prior) && !Same(prior.File, candidate))
                    throw new RunOutputRevisionUnavailableException("divergent_prerequisite_overlap");
                changes[path] = (candidate, revision.RevisionId);
                if (candidate is null)
                    composed.Remove(path);
                else
                    composed[path] = candidate;
            }
            writer.Write(revision.RevisionId);
            writer.Write(revision.TreeContentSha256 ?? "");
            writer.Write(revision.DiffSha256);
        }

        var content = RunOutputTree.Encode(composed.Values);
        writer.Write(RunOutputRevision.Sha256(content));
        writer.Flush();
        return new ImmutableExecutionInputPlan(sourceCommitHash, sourceTreeHash,
            "sha256:" + RunOutputRevision.Sha256(identity.ToArray()), content,
            inputs.Select(input => input.Revision.RevisionId).ToArray());
    }

    private static bool Same(RunOutputTree.File? first, RunOutputTree.File? second) =>
        first is null ? second is null
        : second is not null && first.Mode == second.Mode && first.Bytes.AsSpan().SequenceEqual(second.Bytes);

    private static string[] ParseChangedPaths(byte[] diffBytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(diffBytes); }
        catch (DecoderFallbackException) { throw new RunOutputRevisionUnavailableException("unsupported_revision_patch"); }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal))
                continue;
            var pair = line.TrimEnd('\r')["diff --git ".Length..].Split(" b/", 2, StringSplitOptions.None);
            if (pair.Length != 2 || !pair[0].StartsWith("a/", StringComparison.Ordinal)
                || pair[0][2..] != pair[1] || pair[1].Contains('"')
                || pair[1].Contains('\\') || pair[1].Contains('\n')
                || pair[1].Split('/').Any(part => part is "" or "." or ".."))
                throw new RunOutputRevisionUnavailableException("unsupported_revision_patch");
            paths.Add(pair[1]);
        }
        if (paths.Count == 0 && diffBytes.Length != 0)
            throw new RunOutputRevisionUnavailableException("unsupported_revision_patch");
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }
}
