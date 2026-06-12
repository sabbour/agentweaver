using LibGit2Sharp;
using Scaffolder.SandboxFs;
using Scaffolder.Squad.Model;

namespace Scaffolder.Squad.Sync;

public sealed class SquadGitScribe
{
    private readonly string _workingDirectory;

    public SquadGitScribe(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Diffs the .squad/ working tree against HEAD. Returns the list of changes
    /// and a change_set_hash over their paths and content.
    /// Returns NothingToSync = true when there are no changes.
    /// </summary>
    public SyncStatus GetStatus()
    {
        var repoPath = Repository.Discover(_workingDirectory);
        if (repoPath is null)
            throw new InvalidOperationException("Working directory is not inside a git repository.");

        using var repo = new Repository(repoPath);
        var changes = new List<SyncChange>();

        var treeChanges = repo.Diff.Compare<TreeChanges>(
            repo.Head.Tip?.Tree,
            DiffTargets.WorkingDirectory | DiffTargets.Index);

        foreach (var entry in treeChanges)
        {
            var path = entry.Path;
            if (!path.StartsWith(".squad/", StringComparison.Ordinal))
                continue;

            var kind = entry.Status switch
            {
                ChangeKind.Added => SyncChangeKind.Added,
                ChangeKind.Untracked => SyncChangeKind.Added,
                ChangeKind.Deleted => SyncChangeKind.Removed,
                _ => SyncChangeKind.Modified,
            };

            changes.Add(new SyncChange(path, kind));
        }

        var hash = ComputeChangeSetHash(repo, changes);
        return new SyncStatus(changes, hash, changes.Count == 0);
    }

    /// <summary>
    /// Verifies the expected hash, then stages each .squad/ file individually and commits.
    /// Rejects if the hash doesn't match (state changed since review).
    /// Never stages files outside .squad/.
    /// </summary>
    public string Commit(string expectedHash, string? message, string authorName, string authorEmail)
    {
        var repoPath = Repository.Discover(_workingDirectory);
        if (repoPath is null)
            throw new InvalidOperationException("Working directory is not inside a git repository.");

        using var repo = new Repository(repoPath);

        var current = GetStatus();
        if (current.NothingToSync)
            throw new InvalidOperationException("Nothing to sync.");

        if (!string.Equals(current.ChangeSetHash, expectedHash, StringComparison.Ordinal))
            throw new SyncStateChangedException("The .squad/ tree changed since you last reviewed it. Fetch the current status and review again.");

        foreach (var change in current.Changes)
        {
            if (!change.RelativePath.StartsWith(".squad/", StringComparison.Ordinal) &&
                !change.RelativePath.StartsWith(".squad\\", StringComparison.Ordinal))
                throw new InvalidOperationException($"Path '{change.RelativePath}' is outside .squad/ — aborting sync.");

            // Validate the full path is within working directory
            SandboxPathValidator.ValidateAndResolve(change.RelativePath, _workingDirectory);

            if (change.Kind == SyncChangeKind.Removed)
                repo.Index.Remove(change.RelativePath);
            else
                Commands.Stage(repo, change.RelativePath);
        }
        repo.Index.Write();

        var sig = new Signature(authorName, authorEmail, DateTimeOffset.UtcNow);
        var commitMessage = string.IsNullOrWhiteSpace(message)
            ? "sync: update .squad/ team definitions"
            : message;

        var commit = repo.Commit(commitMessage, sig, sig);
        return commit.Sha;
    }

    private static string ComputeChangeSetHash(Repository repo, IReadOnlyList<SyncChange> changes)
    {
        if (changes.Count == 0) return string.Empty;

        var workingDir = repo.Info.WorkingDirectory;
        var parts = changes
            .OrderBy(c => c.RelativePath, StringComparer.Ordinal)
            .Select(c =>
            {
                string content = string.Empty;
                if (c.Kind != SyncChangeKind.Removed)
                {
                    try
                    {
                        // Validate the path is inside the working directory before reading.
                        var resolvedPath = SandboxPathValidator.ValidateAndResolve(c.RelativePath, workingDir);
                        // Skip reparse points (symlinks / junctions).
                        var attrs = File.GetAttributes(resolvedPath);
                        if ((attrs & FileAttributes.ReparsePoint) == 0 && File.Exists(resolvedPath))
                            content = File.ReadAllText(resolvedPath);
                    }
                    catch { /* path invalid or unreadable — treat as empty content */ }
                }
                return c.RelativePath + "|" + content;
            });

        var combined = string.Concat(parts);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record SyncStatus(
    IReadOnlyList<SyncChange> Changes,
    string ChangeSetHash,
    bool NothingToSync);
