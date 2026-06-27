using System.Text.Json.Serialization;
using LibGit2Sharp;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Projects;

/// <summary>
/// Outcome of a project workspace operation. The HTTP layer maps these to status codes; the MCP
/// layer can branch on them directly without depending on ASP.NET Core result types.
/// </summary>
public enum WorkspaceOutcome
{
    Ok,
    NotFound,
    InvalidPath,
}

/// <summary>A browsable git ref for a project workspace: base branch, active worktree, or assembly branch.</summary>
public sealed record WorkspaceRef
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }            // "base" | "worktree" | "assembly"
    [JsonPropertyName("branch")] public required string Branch { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("run_id")] public string? RunId { get; init; }
    [JsonPropertyName("run_status")] public string? RunStatus { get; init; }
    [JsonPropertyName("originating_branch")] public string? OriginatingBranch { get; init; }
}

/// <summary>Response body for GET /api/projects/{id}/workspace/refs.</summary>
public sealed record WorkspaceRefsResponse
{
    [JsonPropertyName("current_branch")] public required string CurrentBranch { get; init; }
    [JsonPropertyName("refs")] public required IReadOnlyList<WorkspaceRef> Refs { get; init; }
}

public sealed record WorkspaceRefsResult(WorkspaceOutcome Outcome, WorkspaceRefsResponse? Value);
public sealed record WorkspaceListResult(WorkspaceOutcome Outcome, IReadOnlyList<WorkspaceNode>? Nodes);
public sealed record WorkspaceContentResult(WorkspaceOutcome Outcome, WorkspaceFileContent? Value);

/// <summary>
/// Read-only, project-level workspace browsing. Lists the browsable refs (project base branch,
/// active run worktrees, and coordinator assembly branches), enumerates a ref's file tree, and serves file content with syntax
/// highlighting hints. Reuses the run workspace helpers (commit-tree enumeration, path validation /
/// containment, blob/content building) so project and run browsing share a single implementation.
/// The service is the shared surface the REST endpoints and the MCP layer both bind to.
/// </summary>
public sealed class ProjectWorkspaceService
{
    private const int MaxContentBytes = 1 * 1024 * 1024; // 1 MB
    private const int BinaryProbeBytes = 8192;

    private readonly IProjectStore _projectStore;
    private readonly IRunStore _runStore;

    public ProjectWorkspaceService(IProjectStore projectStore, IRunStore runStore)
    {
        _projectStore = projectStore;
        _runStore = runStore;
    }

    /// <summary>
    /// Lists the browsable refs for a project: base first, then active worktrees, then coordinator
    /// assembly integration branches that still exist. Returns NotFound when the project is missing
    /// or not owned by the caller.
    /// </summary>
    public async Task<WorkspaceRefsResult> ListRefsAsync(ProjectId projectId, CallerContext caller, CancellationToken ct)
    {
        var project = await _projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return new WorkspaceRefsResult(WorkspaceOutcome.NotFound, null);

        var refs = new List<WorkspaceRef>
        {
            new()
            {
                Kind = "base",
                Branch = project.DefaultBranch,
                Label = $"{project.DefaultBranch} (base)",
            },
        };

        var projectRuns = await _runStore.GetRunsByProjectAsync(projectId, ct: ct).ConfigureAwait(false);
        foreach (var run in GetWorktreeRuns(projectRuns))
        {
            refs.Add(new WorkspaceRef
            {
                Kind = "worktree",
                Branch = run.WorktreeBranch!,
                Label = BuildWorktreeLabel(run),
                RunId = run.Id.ToString(),
                RunStatus = run.Status.ToApiString(),
                OriginatingBranch = run.OriginatingBranch,
            });
        }

        foreach (var run in projectRuns.Where(IsCoordinatorRun))
        {
            var branch = CoordinatorAssemblyService.IntegrationBranchName(run.Id.ToString());
            var repoPath = string.IsNullOrEmpty(run.RepositoryPath) ? project.WorkingDirectory : run.RepositoryPath;
            if (!BranchExists(repoPath, branch)) continue;
            refs.Add(new WorkspaceRef
            {
                Kind = "assembly",
                Branch = branch,
                Label = $"Assembly {run.Id.ToString()[..8]} ({run.Status.ToApiString()})",
                RunId = run.Id.ToString(),
                RunStatus = run.Status.ToApiString(),
                OriginatingBranch = run.OriginatingBranch,
            });
        }

        return new WorkspaceRefsResult(
            WorkspaceOutcome.Ok,
            new WorkspaceRefsResponse { CurrentBranch = project.DefaultBranch, Refs = refs });
    }

    /// <summary>
    /// Enumerates the file tree for a ref. The base branch (or an omitted ref) reads the branch tip
    /// commit tree; an active worktree ref reads the live worktree directory so uncommitted work is
    /// visible, falling back to the branch tip tree when the worktree is gone. Unknown refs return
    /// NotFound; an empty/not-yet-built tree returns an empty list.
    /// </summary>
    public async Task<WorkspaceListResult> ListWorkspaceAsync(
        ProjectId projectId, CallerContext caller, string? @ref, CancellationToken ct)
    {
        var project = await _projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return new WorkspaceListResult(WorkspaceOutcome.NotFound, null);

        var resolved = await ResolveRefAsync(project, projectId, @ref, ct).ConfigureAwait(false);
        if (resolved is null)
            return new WorkspaceListResult(WorkspaceOutcome.NotFound, null);

        var nodes = new List<WorkspaceNode>();

        if (resolved.WorktreeDirectory is { } worktreeRoot)
        {
            EnumerateWorktreeDirectory(worktreeRoot, nodes);
        }
        else
        {
            EnumerateBranchTip(resolved.RepositoryPath, resolved.Branch, nodes);
        }

        var sorted = nodes
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToList();

        return new WorkspaceListResult(WorkspaceOutcome.Ok, sorted);
    }

    /// <summary>
    /// Serves a file's content for a ref with a syntax-highlighting language hint. Worktree refs read
    /// from the live worktree directory with path containment; the base branch (or a worktree whose
    /// directory is gone) reads the blob from the branch tip tree. Invalid paths return InvalidPath;
    /// missing files or unknown refs return NotFound. 1 MB cap and binary detection mirror the run path.
    /// </summary>
    public async Task<WorkspaceContentResult> GetFileContentAsync(
        ProjectId projectId, CallerContext caller, string path, string? @ref, CancellationToken ct)
    {
        var project = await _projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null || !caller.Owns(project.Owner))
            return new WorkspaceContentResult(WorkspaceOutcome.NotFound, null);

        if (!EndpointHelpers.TryValidateRelativePath(path, out var normalizedPath))
            return new WorkspaceContentResult(WorkspaceOutcome.InvalidPath, null);

        var resolved = await ResolveRefAsync(project, projectId, @ref, ct).ConfigureAwait(false);
        if (resolved is null)
            return new WorkspaceContentResult(WorkspaceOutcome.NotFound, null);

        if (resolved.WorktreeDirectory is { } worktreeRoot)
        {
            var root = worktreeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, normalizedPath));

            // Path containment: reject any resolved path that escapes the worktree root.
            var rootWithSep = root + Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(rootWithSep, cmp))
                return new WorkspaceContentResult(WorkspaceOutcome.InvalidPath, null);

            if (!File.Exists(fullPath))
                return new WorkspaceContentResult(WorkspaceOutcome.NotFound, null);

            var content = await ReadWorktreeFileAsync(fullPath, normalizedPath, ct).ConfigureAwait(false);
            return new WorkspaceContentResult(WorkspaceOutcome.Ok, content);
        }

        var blobContent = TryReadBranchBlob(resolved.RepositoryPath, resolved.Branch, normalizedPath);
        return blobContent is null
            ? new WorkspaceContentResult(WorkspaceOutcome.NotFound, null)
            : new WorkspaceContentResult(WorkspaceOutcome.Ok, blobContent);
    }

    /// <summary>
    /// Resolves a ref string to a repository + branch and, for worktree refs with a present directory,
    /// the worktree path to enumerate. Returns null when the ref is not in the allowed set.
    /// </summary>
    private async Task<ResolvedRef?> ResolveRefAsync(
        Project project, ProjectId projectId, string? @ref, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@ref) ||
            string.Equals(@ref, project.DefaultBranch, StringComparison.Ordinal))
        {
            return new ResolvedRef(project.WorkingDirectory, project.DefaultBranch, null);
        }

        var runs = await _runStore.GetRunsByProjectAsync(projectId, ct: ct).ConfigureAwait(false);
        foreach (var run in GetWorktreeRuns(runs))
        {
            if (string.Equals(run.WorktreeBranch, @ref, StringComparison.Ordinal))
            {
                var repoPath = string.IsNullOrEmpty(run.RepositoryPath) ? project.WorkingDirectory : run.RepositoryPath;
                var dir = !string.IsNullOrEmpty(run.WorktreePath) && Directory.Exists(run.WorktreePath)
                    ? run.WorktreePath
                    : null;
                return new ResolvedRef(repoPath, run.WorktreeBranch!, dir);
            }
        }

        foreach (var run in runs.Where(IsCoordinatorRun))
        {
            var branch = CoordinatorAssemblyService.IntegrationBranchName(run.Id.ToString());
            var repoPath = string.IsNullOrEmpty(run.RepositoryPath) ? project.WorkingDirectory : run.RepositoryPath;
            if (string.Equals(branch, @ref, StringComparison.Ordinal) &&
                BranchExists(repoPath, branch))
            {
                return new ResolvedRef(repoPath, branch, null);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the project's runs that expose a browsable worktree ref: a non-empty worktree branch
    /// with a present worktree directory on disk. Terminal-removed statuses (failed/declined/merge_failed/
    /// completed) and merged runs no longer have a worktree on disk and are excluded by the directory
    /// check. Ordered most-recent first (the store already returns started_at descending).
    /// </summary>
    private static IReadOnlyList<Run> GetWorktreeRuns(IReadOnlyList<Run> runs) =>
        runs
            .Where(r =>
                r.Status is not (RunStatus.Failed or RunStatus.Declined or RunStatus.MergeFailed
                    or RunStatus.Completed or RunStatus.Merged or RunStatus.Pending) &&
                !string.IsNullOrEmpty(r.WorktreeBranch) &&
                !string.IsNullOrEmpty(r.WorktreePath) &&
                Directory.Exists(r.WorktreePath))
            .ToList();

    private static bool IsCoordinatorRun(Run run) =>
        run.ParentRunId is null && string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal);

    private static void EnumerateWorktreeDirectory(string worktreePath, List<WorkspaceNode> nodes)
    {
        var worktreeRoot = worktreePath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var dir in Directory.GetDirectories(worktreeRoot, "*", SearchOption.AllDirectories))
        {
            var rel = dir.Substring(worktreeRoot.Length)
                         .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Replace('\\', '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            nodes.Add(new WorkspaceNode { Path = rel, IsFolder = true, Status = null });
        }

        foreach (var file in Directory.GetFiles(worktreeRoot, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(worktreeRoot.Length)
                          .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Replace('\\', '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            nodes.Add(new WorkspaceNode { Path = rel, IsFolder = false, Status = null });
        }
    }

    private static async Task<WorkspaceFileContent> ReadWorktreeFileAsync(
        string fullPath, string normalizedPath, CancellationToken ct)
    {
        bool isBinaryFile;
        using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buf = new byte[BinaryProbeBytes];
            int read = await probe.ReadAsync(buf.AsMemory(0, BinaryProbeBytes), ct).ConfigureAwait(false);
            isBinaryFile = buf.AsSpan(0, read).IndexOf((byte)0) >= 0;
        }

        if (isBinaryFile)
        {
            return new WorkspaceFileContent
            {
                Path = normalizedPath,
                Content = null,
                IsBinary = true,
                Language = EndpointHelpers.DetectLanguage(normalizedPath),
            };
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxContentBytes)
        {
            return new WorkspaceFileContent
            {
                Path = normalizedPath,
                Content = null,
                IsBinary = false,
                Language = "too_large",
            };
        }

        var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        return new WorkspaceFileContent
        {
            Path = normalizedPath,
            Content = content,
            IsBinary = false,
            Language = EndpointHelpers.DetectLanguage(normalizedPath),
        };
    }

    private static void EnumerateBranchTip(string repositoryPath, string branch, List<WorkspaceNode> nodes)
    {
        if (string.IsNullOrEmpty(repositoryPath) || !Directory.Exists(repositoryPath))
            return;
        try
        {
            using var repo = new Repository(repositoryPath);
            var commit = FindBranchTip(repo, branch);
            if (commit is not null)
                EndpointHelpers.EnumerateGitTree(commit.Tree, "", nodes);
        }
        catch (RepositoryNotFoundException)
        {
            // Not-yet-built or missing repo lists as empty rather than failing.
        }
    }

    private static WorkspaceFileContent? TryReadBranchBlob(string repositoryPath, string branch, string normalizedPath)
    {
        if (string.IsNullOrEmpty(repositoryPath) || !Directory.Exists(repositoryPath))
            return null;
        try
        {
            using var repo = new Repository(repositoryPath);
            var commit = FindBranchTip(repo, branch);
            if (commit is null)
                return null;

            var gitPath = normalizedPath.Replace('\\', '/');
            var treeEntry = commit[gitPath];
            if (treeEntry is null || treeEntry.TargetType != TreeEntryTargetType.Blob)
                return null;

            // Build the content inside the repo scope so blob lazy-loading uses a live handle.
            return EndpointHelpers.BuildBlobContent((Blob)treeEntry.Target, normalizedPath);
        }
        catch (RepositoryNotFoundException)
        {
            return null;
        }
    }

    private static bool BranchExists(string repositoryPath, string branch)
    {
        if (string.IsNullOrEmpty(repositoryPath) || !Directory.Exists(repositoryPath))
            return false;
        try
        {
            using var repo = new Repository(repositoryPath);
            return FindBranchTip(repo, branch) is not null;
        }
        catch (RepositoryNotFoundException)
        {
            return false;
        }
    }

    private static Commit? FindBranchTip(Repository repo, string branch) =>
        repo.Branches[branch]?.Tip ?? repo.Branches[$"refs/heads/{branch}"]?.Tip;

    private static string BuildWorktreeLabel(Run run)
    {
        var firstLine = run.Task.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (firstLine.Length > 60)
            firstLine = firstLine[..60].TrimEnd() + "...";
        var label = string.IsNullOrEmpty(firstLine) ? run.Id.ToString() : firstLine;
        return $"{label} ({run.Status.ToApiString()})";
    }

    private sealed record ResolvedRef(string RepositoryPath, string Branch, string? WorktreeDirectory);
}
