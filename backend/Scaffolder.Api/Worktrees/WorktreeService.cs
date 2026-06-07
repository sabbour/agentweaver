using System.Diagnostics;
using Microsoft.Extensions.Options;
using Scaffolder.Api.Configuration;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Worktrees;

public interface IWorktreeService
{
    /// <summary>
    /// Creates a git worktree for the run from originatingBranch.
    /// Returns the created SessionEntity with artifactDir, worktreePath, and originatingCommit.
    /// </summary>
    Task<SessionEntity> CreateWorktreeAsync(Guid runId, string originatingBranch, CancellationToken ct = default);

    /// <summary>
    /// Removes the git worktree for a session. Called on cleanup.
    /// </summary>
    Task RemoveWorktreeAsync(string worktreePath, CancellationToken ct = default);
}

internal sealed class WorktreeService : IWorktreeService
{
    private readonly ScaffolderOptions _options;
    private readonly ILogger<WorktreeService> _logger;

    public WorktreeService(IOptions<ScaffolderOptions> options, ILogger<WorktreeService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SessionEntity> CreateWorktreeAsync(
        Guid runId,
        string originatingBranch,
        CancellationToken ct = default)
    {
        var repoRoot = await ResolveRepoRootAsync(ct);
        var runRoot = ResolveRunRoot(repoRoot);
        Directory.CreateDirectory(runRoot);

        var worktreePath = Path.Combine(runRoot, runId.ToString("N"));
        var worktreeBranchName = $"run/{runId:N}";

        // Get the originating commit SHA
        var commitSha = await RunGitAsync(
            "rev-parse",
            new[] { originatingBranch },
            workingDir: repoRoot,
            ct: ct);
        commitSha = commitSha.Trim();

        // Create the worktree on a new branch tracking the originating branch
        await RunGitAsync(
            "worktree",
            new[] { "add", "-b", worktreeBranchName, worktreePath, originatingBranch },
            workingDir: repoRoot,
            ct: ct);

        _logger.LogInformation(
            "Created worktree for run {RunId} at {WorktreePath} from {Branch} at {Commit}",
            runId, worktreePath, originatingBranch, commitSha);

        return new SessionEntity
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ArtifactDir = worktreePath,
            WorktreePath = worktreePath,
            OriginatingCommit = commitSha
        };
    }

    public async Task RemoveWorktreeAsync(string worktreePath, CancellationToken ct = default)
    {
        try
        {
            var repoRoot = await ResolveRepoRootAsync(ct);
            await RunGitAsync("worktree", new[] { "remove", "--force", worktreePath },
                workingDir: repoRoot, ct: ct);
            _logger.LogInformation("Removed worktree at {WorktreePath}", worktreePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree at {WorktreePath}", worktreePath);
        }
    }

    /// <summary>
    /// Returns the absolute path to the git repository root.
    /// Uses the configured <see cref="ScaffolderOptions.RepoRoot"/> when set;
    /// otherwise auto-detects via <c>git rev-parse --show-toplevel</c>.
    /// </summary>
    private async Task<string> ResolveRepoRootAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.RepoRoot))
            return Path.GetFullPath(_options.RepoRoot);

        var raw = await RunGitAsync(
            "rev-parse",
            new[] { "--show-toplevel" },
            workingDir: Directory.GetCurrentDirectory(),
            ct: ct);

        // git on Windows returns forward-slash paths; GetFullPath normalises to OS separators
        return Path.GetFullPath(raw.Trim());
    }

    /// <summary>
    /// Resolves the run root directory, anchoring a relative <see cref="ScaffolderOptions.RunRoot"/>
    /// to the repository root so worktrees land next to the repo rather than next to the API binary.
    /// </summary>
    private string ResolveRunRoot(string repoRoot)
    {
        var runRoot = _options.RunRoot;
        return Path.IsPathRooted(runRoot)
            ? runRoot
            : Path.GetFullPath(Path.Combine(repoRoot, runRoot));
    }

    private async Task<string> RunGitAsync(
        string command,
        string[] args,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git", new[] { command }.Concat(args))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory()
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {command} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        return stdout;
    }
}
