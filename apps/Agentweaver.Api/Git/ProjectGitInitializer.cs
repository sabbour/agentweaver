using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.Api.Git;

public enum GitClonePurpose
{
    ProjectCreation,
    SkillImport,
}

/// <summary>
/// Creates or clones a git repository for a newly created project.
/// For blank projects: initializes a new repo and creates an initial empty commit on the
/// configured default branch so WorktreeManager.AddWorktree always finds a branch with a tip.
/// For from-GitHub projects: clones the repository using an ephemeral credential that is
/// never stored or logged.
/// </summary>
public class ProjectGitInitializer
{
    /// <summary>
    /// A project creation only needs the default branch tip: Agentweaver creates run branches and
    /// worktrees from that tip rather than inspecting or rewriting repository history.
    /// </summary>
    internal const int ProjectCreationCloneDepth = 1;
    private readonly ILogger<ProjectGitInitializer> _logger;

    /// <summary>
    /// Baseline ignore rules seeded into blank projects so dependency/build artifacts are never
    /// captured by scope-independent staging. Covers the common Node, Python, JVM, and OS caches.
    /// </summary>
    internal const string BaselineGitignore =
        "# Dependencies\n" +
        "node_modules/\n" +
        ".venv/\n" +
        "venv/\n" +
        "__pycache__/\n" +
        "*.pyc\n" +
        ".pytest_cache/\n" +
        "\n" +
        "# Build output\n" +
        "dist/\n" +
        "build/\n" +
        ".next/\n" +
        "out/\n" +
        "target/\n" +
        "bin/\n" +
        "obj/\n" +
        "\n" +
        "# Logs & environment\n" +
        "*.log\n" +
        ".env\n" +
        ".env.*\n" +
        "\n" +
        "# OS cruft\n" +
        ".DS_Store\n" +
        "Thumbs.db\n";

    public ProjectGitInitializer(ILogger<ProjectGitInitializer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes a blank git repository at <paramref name="workingDirectory"/> and creates
    /// an initial empty commit on <paramref name="defaultBranch"/> so the repo is not unborn.
    /// Returns the branch name that was created (may differ from the requested name if the
    /// repo's init.defaultBranch config overrides it — we explicitly create the branch).
    /// </summary>
    public virtual string InitBlank(string workingDirectory, string defaultBranch)
    {
        Repository.Init(workingDirectory);
        using var repo = new Repository(workingDirectory);

        // Seed a baseline .gitignore so greenfield projects don't commit dependency/build junk once
        // WorktreeManager staging captures every changed file (issue #222). Never clobber an existing
        // .gitignore if the caller already placed one.
        var gitignorePath = Path.Combine(workingDirectory, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, BaselineGitignore);
            Commands.Stage(repo, ".gitignore");
        }

        // Commit the baseline (or an empty commit if a .gitignore was already present) to establish
        // the branch tip so WorktreeManager.AddWorktree always finds a branch with a tip.
        var sig = new Signature("Agentweaver", "agentweaver@localhost", DateTimeOffset.UtcNow);
        repo.Commit(
            "Initial commit",
            sig,
            sig,
            new CommitOptions { AllowEmptyCommit = true });

        // LibGit2Sharp init creates HEAD on "master" by default regardless of git config.
        // Rename to the desired default branch.
        var head = repo.Head;
        if (!head.FriendlyName.Equals(defaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            repo.Refs.Rename(head.CanonicalName, $"refs/heads/{defaultBranch}");
        }

        _logger.LogInformation(
            "Initialized blank repository at {Path} on branch {Branch}",
            workingDirectory, defaultBranch);

        return defaultBranch;
    }

    /// <summary>
    /// Stages all untracked and modified files in <paramref name="workingDirectory"/> and creates
    /// a commit with <paramref name="message"/>. No-ops when there is nothing to commit (e.g. all
    /// scaffold writes failed). Called after scaffold materialization so the initial git tree
    /// reflects the project's starting state rather than the empty-commit baseline.
    /// </summary>
    public virtual void CommitAllUntracked(string workingDirectory, string message)
    {
        if (!Directory.Exists(workingDirectory)) return;

        try
        {
            using var repo = new Repository(workingDirectory);

            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                IncludeIgnored = false,
                RecurseUntrackedDirs = true,
                RecurseIgnoredDirs = false,
            });

            var toStage = status
                .Where(e => e.State != FileStatus.Ignored && e.State != 0)
                .Select(e => e.FilePath)
                .ToList();

            if (toStage.Count == 0) return;

            Commands.Stage(repo, toStage);

            var sig = new Signature("Agentweaver", "agentweaver@localhost", DateTimeOffset.UtcNow);
            repo.Commit(message, sig, sig);

            _logger.LogInformation(
                "Committed {Count} scaffold file(s) at {Path} ({Message})",
                toStage.Count, workingDirectory, message);
        }
        catch (Exception ex)
        {
            // Best-effort — never fail project creation if the commit fails.
            _logger.LogWarning(ex, "Failed to commit scaffold files at {Path}", workingDirectory);
        }
    }


    /// Clones <paramref name="sourceRepository"/> into <paramref name="workingDirectory"/> using
    /// the provided <paramref name="accessToken"/> as an ephemeral credential. The explicit
    /// <paramref name="purpose"/> selects the required history depth: project creation fetches
    /// only the default branch tip, while skill imports shallow-clone branch-scoped GitHub tree URLs
    /// when possible and otherwise retain the full ref set needed for tag/ref resolution.
    /// The token is NEVER logged or stored. Returns the default branch name.
    /// </summary>
    public virtual string Clone(
        string workingDirectory,
        string sourceRepository,
        string accessToken,
        GitClonePurpose purpose)
    {
        var url = NormalizeCloneTarget(sourceRepository);
        var branchName = purpose == GitClonePurpose.SkillImport
            ? TryGetSingleSegmentTreeBranchName(sourceRepository)
            : null;

        _logger.LogInformation(
            "Cloning repository {Repository} into {Path}",
            sourceRepository, workingDirectory);

        string repoPath;
        try
        {
            repoPath = Repository.Clone(url, workingDirectory, CreateCloneOptions(accessToken, purpose, branchName));
        }
        catch (LibGit2SharpException ex) when (purpose == GitClonePurpose.SkillImport && !string.IsNullOrWhiteSpace(branchName))
        {
            // A simple /tree/{ref}/... URL is often a branch path. Try the depth-1 branch clone first
            // because preview/import only needs the files at that tip, then fall back to a full clone
            // if the ref is actually a tag/commit or otherwise not fetchable as a branch.
            _logger.LogInformation(
                ex,
                "Shallow skill-import clone for {Repository} ref {Ref} failed; retrying with full history",
                sourceRepository,
                branchName);
            ResetFailedCloneDirectory(workingDirectory);
            repoPath = Repository.Clone(url, workingDirectory, CreateCloneOptions(accessToken, purpose));
        }
        using var repo = new Repository(repoPath);

        // Derive default branch from HEAD symbolic ref.
        var defaultBranch = repo.Head?.FriendlyName ?? repo.Head?.CanonicalName ?? "main";
        _logger.LogInformation(
            "Clone complete; default branch is {Branch}", defaultBranch);
        return defaultBranch;
    }

    private static string NormalizeCloneTarget(string sourceRepository)
    {
        // Normalize owner/repo and GitHub tree/blob URLs to a cloneable repository URL.
        if (Uri.TryCreate(sourceRepository, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var repoName = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? parts[1][..^4]
                    : parts[1];
                return $"https://github.com/{parts[0]}/{repoName}.git";
            }
        }

        return sourceRepository.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? sourceRepository
            : $"https://github.com/{sourceRepository}";
    }

    // A branch-based GitHub tree URL already identifies the branch tip that contains the skill path,
    // so a depth-1 clone is enough to preview/import the files. We still retry with full history if
    // the ref turns out to be a tag/commit rather than a branch, and slash-containing refs never
    // match this helper because their boundary is ambiguous until the repo is cloned.
    internal static string? TryGetSingleSegmentTreeBranchName(string sourceRepository)
    {
        if (!Uri.TryCreate(sourceRepository, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5
            || !string.Equals(parts[2], "tree", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[3]))
            return null;

        return parts[3];
    }

    internal static CloneOptions CreateCloneOptions(string accessToken, GitClonePurpose purpose, string? branchName = null)
    {
        var options = new CloneOptions
        {
            IsBare = false,
        };
        if (purpose == GitClonePurpose.ProjectCreation)
            options.FetchOptions.Depth = ProjectCreationCloneDepth;
        else if (purpose == GitClonePurpose.SkillImport && !string.IsNullOrWhiteSpace(branchName))
        {
            options.FetchOptions.Depth = ProjectCreationCloneDepth;
            options.BranchName = branchName;
        }

        options.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-access-token",
            Password = accessToken // ephemeral; never stored or logged
        };
        return options;
    }

    private static void ResetFailedCloneDirectory(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(workingDirectory))
        {
            var attrs = File.GetAttributes(entry);
            if (attrs.HasFlag(FileAttributes.Directory))
                Directory.Delete(entry, recursive: true);
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
    }

    /// <summary>
    /// Points the local repository at <paramref name="workingDirectory"/> at a newly created GitHub
    /// remote and pushes <paramref name="branchName"/> to it, so a project's existing local history
    /// (e.g. a <c>Blank</c>-origin project's commits) is published to a repo attached after the fact,
    /// rather than leaving the new GitHub repository empty (issue: allow creating a GitHub repository
    /// for a project that has none connected). Adds an <c>origin</c> remote if none exists yet, or
    /// reconfigures the URL of an existing one — Blank projects never have a remote, but this is
    /// defensive against a caller re-running the flow. The credential is ephemeral and is NEVER stored
    /// or logged.
    /// </summary>
    public virtual void PushToNewRemote(string workingDirectory, string remoteUrl, string branchName, string accessToken)
    {
        using var repo = new Repository(workingDirectory);

        var origin = repo.Network.Remotes["origin"];
        if (origin is null)
            repo.Network.Remotes.Add("origin", remoteUrl);
        else if (!string.Equals(origin.Url, remoteUrl, StringComparison.Ordinal))
            repo.Network.Remotes.Update("origin", r => r.Url = remoteUrl);

        origin = repo.Network.Remotes["origin"];

        var branch = repo.Branches[branchName];
        if (branch is null)
            throw new InvalidOperationException($"Local branch '{branchName}' was not found in '{workingDirectory}'.");

        var pushOptions = new PushOptions
        {
            CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials
                {
                    Username = "x-access-token",
                    Password = accessToken   // ephemeral; never stored or logged
                }
        };

        repo.Network.Push(origin, branch.CanonicalName, pushOptions);
        repo.Branches.Update(branch, b => b.Remote = "origin", b => b.UpstreamBranch = branch.CanonicalName);

        _logger.LogInformation(
            "Pushed branch {Branch} from {Path} to newly connected remote {RemoteUrl}",
            branchName, workingDirectory, remoteUrl);
    }
}
