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
        var runRoot = Path.GetFullPath(_options.RunRoot);
        Directory.CreateDirectory(runRoot);

        var worktreePath = Path.Combine(runRoot, runId.ToString("N"));
        var worktreeBranchName = $"run/{runId:N}";

        // Get the originating commit SHA
        var commitSha = await RunGitAsync(
            "rev-parse",
            new[] { originatingBranch },
            ct: ct);
        commitSha = commitSha.Trim();

        // Create the worktree on a new branch tracking the originating branch
        await RunGitAsync(
            "worktree",
            new[] { "add", "-b", worktreeBranchName, worktreePath, originatingBranch },
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
            await RunGitAsync("worktree", new[] { "remove", "--force", worktreePath }, ct: ct);
            _logger.LogInformation("Removed worktree at {WorktreePath}", worktreePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree at {WorktreePath}", worktreePath);
        }
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
