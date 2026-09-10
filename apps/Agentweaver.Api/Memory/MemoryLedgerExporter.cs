using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LibGit2Sharp;
using Agentweaver.Squad.Memory;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Single source of truth for materializing the authoritative DB-backed team ledger
/// (active decisions, pending inbox entries, agent memory, and the current session) into the
/// on-disk <c>.squad/</c> + <c>.agentweaver/context/</c> file mirror for a given target directory.
///
/// <para>
/// Consolidates logic that was previously duplicated across the <c>/memory/export</c> endpoint,
/// the per-write best-effort refresh (<c>MemoryExportHelpers.TryExportAsync</c>), and
/// <see cref="Agentweaver.Api.Runs.PostRunScribeService"/>. It is also used to mirror the ledger
/// into a run's git worktree immediately before commit, so the ledger rides the same commit/push
/// flow as the run's other changes (issue #539).
/// </para>
/// </summary>
internal static class MemoryLedgerExporter
{
    internal sealed record ExportResult(IReadOnlyList<string> Files);

    private static readonly string[] FixedExportPaths =
    [
        ".squad/decisions.md",
        ".squad/identity/now.md",
        ".agentweaver/context/boundaries.md",
        ".agentweaver/context/patterns.md",
    ];

    /// <summary>
    /// Queries the project's authoritative memory state and writes the file mirror into
    /// <paramref name="targetDirectory"/>. <b>Throws</b> on failure so explicit sync actions can
    /// surface an actionable error rather than reporting a false success.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(
        string projectId,
        string targetDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct)
    {
        // Only ACTIVE decisions are authoritative "accepted state" (spec #25). Superseded/archived
        // decisions must not be mirrored as live team boundaries.
        var decisions = (await memoryDb.Decisions
                .Where(d => d.ProjectId == projectId
                         && d.Status == "active"
                         && d.TrustState == MemoryTrustStates.Approved)
                .ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        var inbox = await memoryDb.DecisionInbox
            .Where(e => e.ProjectId == projectId && e.Status == "pending")
            .ToListAsync(ct).ConfigureAwait(false);

        var memories = (await memoryDb.AgentMemory
                .Where(m => m.ProjectId == projectId
                         && m.TrustState != MemoryTrustStates.Legacy
                         && (m.Type != "pattern" || m.TrustState == MemoryTrustStates.Approved))
                .ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        // EF Core/SQLite cannot translate DateTimeOffset in ORDER BY — load then sort in memory.
        var session = (await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .ToListAsync(ct).ConfigureAwait(false))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        var exporter = new SquadMemoryExporter(targetDirectory);
        var files = await exporter.ExportAsync(
            decisions.Select(d => new DecisionExportDto(
                d.AgentName, d.Type, d.Status, d.Title, d.Content, d.Rationale, d.CreatedAt)).ToList(),
            inbox.Select(e => new InboxExportDto(
                e.AgentName, e.Slug, e.Type, e.Title, e.Content, e.Rationale)).ToList(),
            memories.Select(m => new MemoryExportDto(
                m.AgentName, m.Type, m.Content, m.CreatedAt)).ToList(),
            session is null ? null : new SessionExportDto(
                session.SessionId, session.FocusArea, session.ActiveIssues, session.Summary),
            ct).ConfigureAwait(false);
        return new ExportResult(files);
    }

    /// <summary>
    /// Best-effort variant used by incidental refresh paths that must not fail an otherwise
    /// successful DB write. Never swallows silently: returns <c>false</c> and logs a warning on
    /// failure so callers can honestly report whether the file mirror was updated.
    /// </summary>
    public static async Task<bool> TryExportAsync(
        string projectId,
        string targetDirectory,
        MemoryDbContext memoryDb,
        CancellationToken ct,
        ILogger logger)
    {
        try
        {
            await ExportAsync(projectId, targetDirectory, memoryDb, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to export project memory ledger for {ProjectId} to {TargetDirectory}.",
                projectId, targetDirectory);
            return false;
        }
    }

    /// <summary>
    /// True when the project has committed ledger content worth mirroring into a repository
    /// worktree (at least one active decision or any agent memory). Guards against writing an empty
    /// <c>decisions.md</c> into repositories that never used the memory feature.
    /// </summary>
    public static async Task<bool> HasExportableContentAsync(
        string projectId,
        MemoryDbContext memoryDb,
        CancellationToken ct)
    {
        if (await memoryDb.Decisions
                .AnyAsync(d => d.ProjectId == projectId
                            && d.Status == "active"
                            && d.TrustState == MemoryTrustStates.Approved, ct)
                .ConfigureAwait(false))
            return true;

        return await memoryDb.AgentMemory
            .AnyAsync(m => m.ProjectId == projectId
                        && m.TrustState != MemoryTrustStates.Legacy
                        && (m.Type != "pattern" || m.TrustState == MemoryTrustStates.Approved), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the generated ledger files to the project's default branch without staging or
    /// committing unrelated working-tree changes. The explicit memory export action is therefore
    /// visible through the project workspace browser, which reads committed branch trees.
    /// </summary>
    public static Task CommitExportAsync(
        string workingDirectory,
        string defaultBranch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var repo = new Repository(workingDirectory);
        var branch = repo.Branches[defaultBranch]
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' was not found.");
        var parent = branch.Tip
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' has no commit.");

        var generatedPaths = FixedExportPaths
            .Concat(Directory.Exists(Path.Combine(workingDirectory, ".squad", "decisions", "inbox"))
                ? Directory.GetFiles(
                    Path.Combine(workingDirectory, ".squad", "decisions", "inbox"), "*.md")
                    .Select(path => Path.GetRelativePath(workingDirectory, path).Replace('\\', '/'))
                : [])
            .Concat(Directory.Exists(Path.Combine(workingDirectory, ".squad", "agents"))
                ? Directory.GetDirectories(Path.Combine(workingDirectory, ".squad", "agents"))
                    .Select(path => Path.Combine(path, "history.md"))
                    .Where(File.Exists)
                    .Select(path => Path.GetRelativePath(workingDirectory, path).Replace('\\', '/'))
                : [])
            .Concat(EnumerateTreePaths(parent.Tree)
                .Where(path => path.StartsWith(".squad/decisions/inbox/", StringComparison.Ordinal)
                    || path.StartsWith(".squad/agents/", StringComparison.Ordinal)
                        && path.EndsWith("/history.md", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var tree = TreeDefinition.From(parent.Tree);
        var indexUpdates = new List<(string Path, Blob? Blob)>();
        foreach (var relativePath in generatedPaths)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                var blob = repo.ObjectDatabase.CreateBlob(fullPath);
                tree.Add(relativePath, blob, Mode.NonExecutableFile);
                indexUpdates.Add((relativePath, blob));
            }
            else
            {
                tree.Remove(relativePath);
                indexUpdates.Add((relativePath, null));
            }
        }

        var treeId = repo.ObjectDatabase.CreateTree(tree);
        if (string.Equals(treeId.Sha, parent.Tree.Sha, StringComparison.Ordinal))
            return Task.CompletedTask;

        var signature = new Signature("Agentweaver", "agentweaver@localhost", DateTimeOffset.UtcNow);
        var commit = repo.ObjectDatabase.CreateCommit(
            signature,
            signature,
            "Export project memory",
            treeId,
            new[] { parent },
            prettifyMessage: true);
        repo.Refs.UpdateTarget(branch.Reference, commit.Id.Sha);
        if (branch.IsCurrentRepositoryHead)
        {
            foreach (var update in indexUpdates)
            {
                if (update.Blob is null)
                    repo.Index.Remove(update.Path);
                else
                    repo.Index.Add(update.Blob, update.Path, Mode.NonExecutableFile);
            }
            repo.Index.Write();
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<string> EnumerateTreePaths(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                foreach (var child in EnumerateTreePaths((Tree)entry.Target, path))
                    yield return child;
            }
            else
            {
                yield return path;
            }
        }
    }
}
