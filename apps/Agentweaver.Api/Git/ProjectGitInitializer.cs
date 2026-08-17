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
    /// only the default branch tip, while skill imports retain refs such as historical tags.
    /// The token is NEVER logged or stored. Returns the default branch name.
    /// </summary>
    public virtual string Clone(
        string workingDirectory,
        string sourceRepository,
        string accessToken,
        GitClonePurpose purpose)
    {
        // Normalize "owner/repo" -> full HTTPS URL
        var url = sourceRepository.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? sourceRepository
            : $"https://github.com/{sourceRepository}";

        var cloneOptions = CreateCloneOptions(accessToken, purpose);

        _logger.LogInformation(
            "Cloning repository {Repository} into {Path}",
            sourceRepository, workingDirectory);

        var repoPath = Repository.Clone(url, workingDirectory, cloneOptions);
        using var repo = new Repository(repoPath);

        // Derive default branch from HEAD symbolic ref
        var defaultBranch = repo.Head.FriendlyName;
        _logger.LogInformation(
            "Clone complete; default branch is {Branch}", defaultBranch);
        return defaultBranch;
    }

    internal static CloneOptions CreateCloneOptions(string accessToken, GitClonePurpose purpose)
    {
        var options = new CloneOptions();
        if (purpose == GitClonePurpose.ProjectCreation)
            options.FetchOptions.Depth = ProjectCreationCloneDepth;

        options.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-access-token",
            Password = accessToken // ephemeral; never stored or logged
        };
        return options;
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
