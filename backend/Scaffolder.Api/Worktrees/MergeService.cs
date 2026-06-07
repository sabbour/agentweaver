using System.Diagnostics;

namespace Scaffolder.Api.Worktrees;

/// <summary>
/// Result of a merge operation.
/// </summary>
public enum MergeOutcome
{
    Merged,
    Conflict,
    Failed
}

/// <summary>
/// T041: Merges a run's worktree into its originating branch.
///
/// Strategy (git CLI):
///   1. Commit any uncommitted changes in the worktree.
///   2. Obtain the worktree HEAD SHA.
///   3. Locate the main repository root from the worktree's git directory.
///   4. Check out the originating branch in the main repository.
///   5. Merge the worktree HEAD with --no-ff.
///   6. On conflict: abort the merge and return Conflict (branch unmodified).
///
/// The originating branch is NEVER modified on Conflict or Failed outcomes.
/// </summary>
public sealed class MergeService
{
    private readonly ILogger<MergeService> _logger;

    public MergeService(ILogger<MergeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Merges the worktree into <paramref name="originatingBranch"/>.
    /// Returns the outcome and an optional diagnostic message.
    /// </summary>
    public async Task<(MergeOutcome Outcome, string? ErrorMessage)> MergeAsync(
        string worktreePath,
        string originatingBranch,
        CancellationToken ct = default)
    {
        try
        {
            // Step 1: Stage and commit any uncommitted changes in the worktree.
            await RunGitAsync(worktreePath, "add -A", ct);
            var (statusOut, _) = await RunGitAsync(worktreePath, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(statusOut))
            {
                await RunGitAsync(
                    worktreePath,
                    "commit -m \"chore: agent run output\"",
                    ct);
            }

            // Step 2: Get the worktree HEAD SHA to merge.
            var (headSha, _) = await RunGitAsync(worktreePath, "rev-parse HEAD", ct);
            headSha = headSha.Trim();

            // Step 3: Locate the main repository root.
            var mainRoot = await ResolveMainRepoRootAsync(worktreePath, ct);

            // Step 4: Check out the originating branch.
            var (checkoutOut, checkoutCode) = await RunGitAsync(
                mainRoot, $"checkout {originatingBranch}", ct);

            if (checkoutCode != 0)
            {
                return (MergeOutcome.Failed,
                    $"Failed to check out branch '{originatingBranch}': {checkoutOut}");
            }

            // Step 5: Merge the worktree HEAD.
            var (mergeOut, mergeCode) = await RunGitAsync(
                mainRoot,
                $"merge {headSha} --no-ff -m \"Merge agent run into {originatingBranch}\"",
                ct);

            if (mergeCode == 0)
            {
                _logger.LogInformation(
                    "MergeService: successfully merged worktree SHA {Sha} into '{Branch}'",
                    headSha, originatingBranch);
                return (MergeOutcome.Merged, null);
            }

            // Step 6: Conflict detected — abort to leave the branch unmodified.
            if (mergeOut.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
                || mergeCode == 1)
            {
                await RunGitAsync(mainRoot, "merge --abort", ct);
                _logger.LogWarning(
                    "MergeService: merge conflict detected for SHA {Sha} into '{Branch}'. Aborted.",
                    headSha, originatingBranch);
                return (MergeOutcome.Conflict,
                    $"Merge conflict detected. Branch '{originatingBranch}' is unchanged. " +
                    $"Git output: {mergeOut.Trim()}");
            }

            return (MergeOutcome.Failed,
                $"Merge exited with code {mergeCode}: {mergeOut.Trim()}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "MergeService: unexpected error merging worktree '{WorktreePath}' " +
                "into '{Branch}'", worktreePath, originatingBranch);
            return (MergeOutcome.Failed, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<string> ResolveMainRepoRootAsync(
        string worktreePath, CancellationToken ct)
    {
        // git rev-parse --git-common-dir returns the shared .git directory
        // (i.e. the main repo's .git), which is the parent of the main repo root.
        var (commonDir, code) = await RunGitAsync(
            worktreePath, "rev-parse --git-common-dir", ct);

        if (code == 0)
        {
            var gitCommonDir = Path.IsPathRooted(commonDir.Trim())
                ? commonDir.Trim()
                : Path.Combine(worktreePath, commonDir.Trim());

            var mainRoot = Path.GetDirectoryName(gitCommonDir.TrimEnd('/', '\\'));
            if (mainRoot is not null && Directory.Exists(mainRoot))
            {
                return mainRoot;
            }
        }

        // Fallback: navigate up from worktree using git rev-parse --show-toplevel
        var (topLevel, _) = await RunGitAsync(worktreePath, "rev-parse --show-toplevel", ct);
        return topLevel.Trim();
    }

    private static async Task<(string Output, int ExitCode)> RunGitAsync(
        string workingDir,
        string arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var combined = (stdout + stderr).Trim();
        return (combined, process.ExitCode);
    }
}
