using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.Api.Git;

/// <summary>
/// Creates or clones a git repository for a newly created project.
/// For blank projects: initializes a new repo and creates an initial empty commit on the
/// configured default branch so WorktreeManager.AddWorktree always finds a branch with a tip.
/// For from-GitHub projects: clones the repository using an ephemeral credential that is
/// never stored or logged.
/// </summary>
public class ProjectGitInitializer
{
    private readonly ILogger<ProjectGitInitializer> _logger;

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

        // Stage nothing — we want an empty initial commit to establish the branch tip.
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


    /// https://github.com/owner/repo) into <paramref name="workingDirectory"/> using
    /// the provided <paramref name="accessToken"/> as an ephemeral credential.
    /// The token is NEVER logged or stored. Returns the default branch name.
    /// </summary>
    public virtual string Clone(string workingDirectory, string sourceRepository, string accessToken)
    {
        // Normalize "owner/repo" -> full HTTPS URL
        var url = sourceRepository.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? sourceRepository
            : $"https://github.com/{sourceRepository}";

        var cloneOptions = new CloneOptions();
        cloneOptions.FetchOptions.CredentialsProvider = (_, _, _) =>
            new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = accessToken   // ephemeral; never stored or logged
            };

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
}
