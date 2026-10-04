using Agentweaver.Domain;
using Agentweaver.Api.Coordinator;
using LibGit2Sharp;

namespace Agentweaver.Api.Git;

public static class RunOutputTreeCapture
{
    internal sealed record Source(string CommitHash, string TreeHash, byte[] TreeContent);

    internal static (string CommitHash, byte[] Files) CaptureDeclaredFiles(
        string repositoryPath, string branchName, string expectedTreeHash,
        IReadOnlyList<string> declaredPaths)
    {
        if (declaredPaths.Count == 0)
            throw new RunOutputRevisionUnavailableException("missing_declared_paths");
        using var repository = new Repository(repositoryPath);
        var commit = repository.Branches[branchName]?.Tip
            ?? throw new RunOutputRevisionUnavailableException("source_revision_unavailable");
        if (!string.Equals(commit.Tree.Sha, expectedTreeHash, StringComparison.Ordinal))
            throw new RunOutputRevisionUnavailableException("source_revision_mismatch");
        var files = new List<RunOutputTree.File>(declaredPaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in declaredPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/')
                || path.Contains('\\') || path.Contains(':') || Path.IsPathFullyQualified(path)
                || !seen.Add(path)
                || path.Split('/').Any(part => part is "" or "." or ".."))
                throw new RunOutputRevisionUnavailableException("invalid_declared_path");
            var entry = commit.Tree[path]
                ?? throw new RunOutputRevisionUnavailableException("declared_file_missing");
            if (entry.Target is not Blob blob || entry.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile))
                throw new RunOutputRevisionUnavailableException("declared_file_not_regular");
            if (blob.Size > WorktreeManager.MaxContentBytes)
                throw new RunOutputRevisionUnavailableException("declared_file_too_large");
            using var stream = blob.GetContentStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files.Add(new RunOutputTree.File(path, (int)entry.Mode, buffer.ToArray()));
        }
        return (commit.Sha, RunOutputTree.Encode(files));
    }

    internal static Source CaptureSource(string repositoryPath, string branchName)
    {
        using var repository = new Repository(repositoryPath);
        var commit = repository.Branches[branchName]?.Tip
            ?? throw new RunOutputRevisionUnavailableException("source_revision_unavailable");
        return new Source(commit.Id.Sha, commit.Tree.Id.Sha, Capture(repositoryPath, commit.Tree.Id.Sha));
    }

    internal static string Materialize(string repositoryPath, ImmutableExecutionInputPlan plan)
    {
        using var repository = new Repository(repositoryPath);
        var source = repository.Lookup<Commit>(plan.SourceCommitHash)
            ?? throw new RunOutputRevisionUnavailableException("source_revision_unavailable");
        if (source.Tree.Id.Sha != plan.SourceTreeHash)
            throw new RunOutputRevisionUnavailableException("source_revision_mismatch");

        try
        {
            var definition = TreeDefinition.From(source.Tree);
            var expected = RunOutputTree.Decode(plan.TreeContent);
            var paths = expected.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in RunOutputTree.Decode(Capture(repositoryPath, plan.SourceTreeHash)))
                if (!paths.Contains(file.Path))
                    definition.Remove(file.Path);
            foreach (var file in expected)
            {
                var blob = repository.ObjectDatabase.CreateBlob(new MemoryStream(file.Bytes, writable: false));
                definition.Add(file.Path, blob, (Mode)file.Mode);
            }
            var tree = repository.ObjectDatabase.CreateTree(definition);
            if (!Capture(repositoryPath, tree.Id.Sha).AsSpan().SequenceEqual(plan.TreeContent))
                throw new RunOutputRevisionUnavailableException("materialized_tree_mismatch");
            var signature = new Signature("Agentweaver", "agentweaver@localhost", DateTimeOffset.UnixEpoch);
            return repository.ObjectDatabase.CreateCommit(
                signature, signature, $"Immutable execution input {plan.CompositeId}",
                tree, [source], prettifyMessage: false).Id.Sha;
        }
        catch (LibGit2SharpException)
        {
            throw new RunOutputRevisionUnavailableException("materialization_conflict");
        }
    }

    public static byte[] Capture(string repositoryPath, string treeHash)
    {
        using var repository = new Repository(repositoryPath);
        var tree = repository.Lookup<Tree>(treeHash)
            ?? throw new RunOutputRevisionUnavailableException("missing_tree");
        var files = new List<RunOutputTree.File>();
        Visit(tree, "", files);
        return RunOutputTree.Encode(files);
    }

    private static void Visit(Tree tree, string prefix, List<RunOutputTree.File> files)
    {
        foreach (var entry in tree)
        {
            var path = prefix + entry.Name;
            if (entry.Target is Tree child)
            {
                Visit(child, path + "/", files);
            }
            else if (entry.Target is Blob blob)
            {
                using var content = blob.GetContentStream();
                using var bytes = new MemoryStream();
                content.CopyTo(bytes);
                files.Add(new RunOutputTree.File(path, (int)entry.Mode, bytes.ToArray()));
            }
            else
            {
                throw new RunOutputRevisionUnavailableException("unsupported_tree_entry");
            }
        }
    }
}
