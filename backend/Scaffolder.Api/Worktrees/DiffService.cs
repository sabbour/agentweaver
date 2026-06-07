using System.Diagnostics;

namespace Scaffolder.Api.Worktrees;

public interface IDiffService
{
    /// <summary>
    /// Generates a unified diff of the worktree against the originatingCommit.
    /// Returns raw unified diff text. Returns empty string if no changes.
    /// </summary>
    Task<string> GetDiffAsync(
        string worktreePath,
        string originatingCommit,
        CancellationToken ct = default);
}

internal sealed class DiffService : IDiffService
{
    private readonly ILogger<DiffService> _logger;

    public DiffService(ILogger<DiffService> logger)
    {
        _logger = logger;
    }

    public async Task<string> GetDiffAsync(
        string worktreePath,
        string originatingCommit,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(
            "git",
            new[] { "diff", originatingCommit, "--", "." })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = worktreePath
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git diff process.");

        var diff = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("git diff failed for worktree {Path}: {Error}", worktreePath, stderr);
            throw new InvalidOperationException($"git diff failed: {stderr.Trim()}");
        }

        return diff;
    }
}
