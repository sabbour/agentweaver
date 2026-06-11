using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.Api.Git;

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
        var sig = new Signature("Scaffolder", "scaffolder@localhost", DateTimeOffset.UtcNow);
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
    /// Clones <paramref name="sourceRepository"/> (e.g. "owner/repo" resolved to
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
