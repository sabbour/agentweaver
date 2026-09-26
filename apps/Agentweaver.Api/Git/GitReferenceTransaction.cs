using System.Diagnostics;
using System.Text;

namespace Agentweaver.Api.Git;

internal enum GitReferenceUpdateKind
{
    Applied,
    ExpectedOldMismatch,
    MissingRef,
    MissingObject,
    SymbolicRef,
    Failure,
}

internal sealed record GitReferenceUpdateResult(
    GitReferenceUpdateKind Kind,
    string? CurrentOid = null,
    string? Error = null);

internal enum GitCheckoutConvergenceKind
{
    Converged,
    NotCheckedOut,
    RefMoved,
    PreStateMismatch,
    Failure,
}

internal sealed record GitCheckoutPreState(
    string WorktreePath,
    string FullRef,
    string HeadOid,
    string IndexTreeOid);

internal sealed record GitCheckoutConvergenceResult(
    GitCheckoutConvergenceKind Kind,
    string? Error = null);

/// <summary>
/// Native Git ref compare-and-swap and proof-bound checked-out worktree convergence.
/// Ref mutation and checkout materialization are deliberately separate crash boundaries.
/// </summary>
internal static class GitReferenceTransaction
{
    internal static GitReferenceUpdateResult CompareExchange(
        string repositoryPath,
        string fullRef,
        string newOid,
        string expectedOldOid)
    {
        var format = RunGit(repositoryPath, "rev-parse", "--show-object-format");
        if (!format.Succeeded)
            return Failure(format);

        var oidLength = format.Stdout.Trim() switch
        {
            "sha1" => 40,
            "sha256" => 64,
            var value => 0,
        };
        if (oidLength == 0 || !IsOid(newOid, oidLength) || !IsOid(expectedOldOid, oidLength))
            return new GitReferenceUpdateResult(GitReferenceUpdateKind.Failure, Error: "invalid_oid");

        if (!fullRef.StartsWith("refs/", StringComparison.Ordinal)
            || fullRef.Any(char.IsWhiteSpace)
            || fullRef.Contains("..", StringComparison.Ordinal))
        {
            return new GitReferenceUpdateResult(GitReferenceUpdateKind.Failure, Error: "invalid_ref");
        }

        var symbolic = RunGit(repositoryPath, "symbolic-ref", "--quiet", fullRef);
        if (symbolic.Succeeded)
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.SymbolicRef,
                Error: "symbolic_ref");
        if (symbolic.ExitCode is not 1)
            return Failure(symbolic);

        var current = ResolveOid(repositoryPath, fullRef);
        if (current.Kind != GitReferenceUpdateKind.Applied)
            return current;
        if (!string.Equals(current.CurrentOid, expectedOldOid, StringComparison.Ordinal))
        {
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.ExpectedOldMismatch,
                current.CurrentOid,
                "expected_old_mismatch");
        }

        var commit = RunGit(repositoryPath, "cat-file", "-e", $"{newOid}^{{commit}}");
        if (!commit.Succeeded)
        {
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.MissingObject,
                current.CurrentOid,
                "missing_commit_object");
        }

        var update = RunGit(
            repositoryPath,
            "update-ref",
            "--no-deref",
            fullRef,
            newOid,
            expectedOldOid);
        if (update.Succeeded)
        {
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.Applied,
                newOid);
        }

        var observed = ResolveOid(repositoryPath, fullRef);
        if (observed.Kind == GitReferenceUpdateKind.MissingRef)
            return observed;
        if (observed.Kind == GitReferenceUpdateKind.Applied
            && !string.Equals(observed.CurrentOid, expectedOldOid, StringComparison.Ordinal))
        {
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.ExpectedOldMismatch,
                observed.CurrentOid,
                "expected_old_mismatch");
        }

        return Failure(update, observed.CurrentOid);
    }

    internal static GitCheckoutPreState? CaptureCheckedOutPreState(
        string repositoryPath,
        string fullRef,
        string expectedHeadOid,
        out string? error)
    {
        error = null;
        var worktree = FindCheckedOutWorktree(repositoryPath, fullRef, out error);
        if (worktree is null)
            return null;

        if (!string.Equals(worktree.HeadOid, expectedHeadOid, StringComparison.Ordinal))
        {
            error = "checked_out_head_mismatch";
            return null;
        }

        var indexTree = RunGit(worktree.Path, "write-tree");
        if (!indexTree.Succeeded)
        {
            error = "index_unavailable";
            return null;
        }

        var headTree = RunGit(repositoryPath, "rev-parse", $"{expectedHeadOid}^{{tree}}");
        if (!headTree.Succeeded
            || !string.Equals(indexTree.Stdout.Trim(), headTree.Stdout.Trim(), StringComparison.Ordinal))
        {
            error = "index_dirty";
            return null;
        }

        var trackedWorktree = RunGit(worktree.Path, "diff-files", "--quiet", "--");
        if (!trackedWorktree.Succeeded)
        {
            error = trackedWorktree.ExitCode == 1
                ? "tracked_worktree_dirty"
                : "tracked_worktree_inspection_failed";
            return null;
        }

        return new GitCheckoutPreState(
            worktree.Path,
            fullRef,
            expectedHeadOid,
            indexTree.Stdout.Trim());
    }

    internal static GitCheckoutConvergenceResult ConvergeCheckedOut(
        string repositoryPath,
        GitCheckoutPreState? preState,
        string intendedOid,
        Action? beforeMaterialize = null)
    {
        if (preState is null)
            return new GitCheckoutConvergenceResult(GitCheckoutConvergenceKind.NotCheckedOut);

        var current = ResolveOid(repositoryPath, preState.FullRef);
        if (current.Kind != GitReferenceUpdateKind.Applied
            || !string.Equals(current.CurrentOid, intendedOid, StringComparison.Ordinal))
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.RefMoved,
                current.CurrentOid);
        }

        var worktree = FindCheckedOutWorktree(repositoryPath, preState.FullRef, out var worktreeError);
        if (worktree is null
            || !string.Equals(worktree.Path, preState.WorktreePath, PathComparison)
            || !string.Equals(worktree.HeadOid, intendedOid, StringComparison.Ordinal))
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                worktreeError ?? "checked_out_worktree_changed");
        }

        var indexTree = RunGit(preState.WorktreePath, "write-tree");
        var trackedWorktree = RunGit(preState.WorktreePath, "diff-files", "--quiet", "--");
        if (!indexTree.Succeeded
            || !trackedWorktree.Succeeded
            || !string.Equals(indexTree.Stdout.Trim(), preState.IndexTreeOid, StringComparison.Ordinal))
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                "checked_out_pre_state_changed");
        }

        var targetTree = RunGit(repositoryPath, "rev-parse", $"{intendedOid}^{{tree}}");
        if (!targetTree.Succeeded)
            return new GitCheckoutConvergenceResult(GitCheckoutConvergenceKind.Failure, "missing_target_tree");

        var untracked = RunGit(preState.WorktreePath, "ls-files", "--others", "--exclude-standard", "-z");
        var ignored = RunGit(
            preState.WorktreePath,
            "ls-files",
            "--others",
            "--ignored",
            "--exclude-standard",
            "-z");
        var changed = RunGit(
            repositoryPath,
            "diff-tree",
            "--no-commit-id",
            "--name-only",
            "-r",
            "-z",
            preState.HeadOid,
            intendedOid);
        if (!untracked.Succeeded || !ignored.Succeeded || !changed.Succeeded)
            return new GitCheckoutConvergenceResult(GitCheckoutConvergenceKind.Failure, "path_inspection_failed");

        var untrackedPaths = SplitNull(untracked.Stdout)
            .Concat(SplitNull(ignored.Stdout))
            .ToArray();
        if (SplitNull(changed.Stdout).Any(changedPath =>
                untrackedPaths.Any(untrackedPath => PathsCollide(changedPath, untrackedPath))))
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                "untracked_path_collision");
        }

        beforeMaterialize?.Invoke();
        using var guard = TryAcquireConvergenceGuard(
            preState.WorktreePath,
            preState.FullRef);
        if (guard is null)
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                "checked_out_state_changed");
        }

        var guardedWorktree = FindCheckedOutWorktree(repositoryPath, preState.FullRef, out _);
        if (guardedWorktree is null
            || !string.Equals(guardedWorktree.Path, preState.WorktreePath, PathComparison)
            || !string.Equals(guardedWorktree.HeadOid, intendedOid, StringComparison.Ordinal))
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                "checked_out_worktree_changed");
        }

        var readTree = RunGit(
            preState.WorktreePath,
            "read-tree",
            "-m",
            "-u",
            preState.HeadOid,
            intendedOid);
        if (!readTree.Succeeded)
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.PreStateMismatch,
                "checked_out_state_changed");
        }

        var verifiedIndex = RunGit(preState.WorktreePath, "write-tree");
        var verifiedWorktree = RunGit(preState.WorktreePath, "diff-files", "--quiet", "--");
        var verifiedRef = ResolveOid(repositoryPath, preState.FullRef);
        var verifiedHead = RunGit(preState.WorktreePath, "rev-parse", "HEAD");
        if (verifiedRef.Kind != GitReferenceUpdateKind.Applied
            || !string.Equals(verifiedRef.CurrentOid, intendedOid, StringComparison.Ordinal)
            || !verifiedHead.Succeeded
            || !string.Equals(verifiedHead.Stdout.Trim(), intendedOid, StringComparison.Ordinal)
            || !verifiedIndex.Succeeded
            || !string.Equals(verifiedIndex.Stdout.Trim(), targetTree.Stdout.Trim(), StringComparison.Ordinal)
            || !verifiedWorktree.Succeeded)
        {
            return new GitCheckoutConvergenceResult(
                GitCheckoutConvergenceKind.Failure,
                "post_convergence_verification_failed");
        }

        return new GitCheckoutConvergenceResult(GitCheckoutConvergenceKind.Converged);
    }

    private static GitConvergenceGuard? TryAcquireConvergenceGuard(
        string worktreePath,
        string fullRef)
    {
        var headGuard = TryAcquireGuard(
            worktreePath,
            "option no-deref",
            $"symref-verify HEAD {fullRef}");
        return headGuard is null ? null : new GitConvergenceGuard(headGuard);
    }

    private static GitTransactionGuard? TryAcquireGuard(
        string workingDirectory,
        params string[] commands)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.ArgumentList.Add("update-ref");
        process.StartInfo.ArgumentList.Add("--stdin");

        try
        {
            process.Start();
            process.StandardInput.Write("start\n");
            foreach (var command in commands)
                process.StandardInput.Write($"{command}\n");
            process.StandardInput.Write("prepare\n");
            process.StandardInput.Flush();

            if (!string.Equals(process.StandardOutput.ReadLine(), "start: ok", StringComparison.Ordinal)
                || !string.Equals(process.StandardOutput.ReadLine(), "prepare: ok", StringComparison.Ordinal))
            {
                process.StandardInput.Close();
                process.WaitForExit();
                process.Dispose();
                return null;
            }

            return new GitTransactionGuard(process);
        }
        catch
        {
            process.Dispose();
            return null;
        }
    }

    private static GitReferenceUpdateResult ResolveOid(string repositoryPath, string fullRef)
    {
        var result = RunGit(repositoryPath, "rev-parse", "--verify", fullRef);
        if (result.Succeeded)
        {
            return new GitReferenceUpdateResult(
                GitReferenceUpdateKind.Applied,
                result.Stdout.Trim());
        }

        return result.ExitCode == 128
            ? new GitReferenceUpdateResult(GitReferenceUpdateKind.MissingRef, Error: "missing_ref")
            : Failure(result);
    }

    private static WorktreeRef? FindCheckedOutWorktree(
        string repositoryPath,
        string fullRef,
        out string? error)
    {
        error = null;
        var result = RunGit(repositoryPath, "worktree", "list", "--porcelain", "-z");
        if (!result.Succeeded)
        {
            error = "worktree_enumeration_failed";
            return null;
        }

        WorktreeRef? match = null;
        string? path = null;
        string? head = null;
        foreach (var field in SplitNull(result.Stdout))
        {
            if (field.Length == 0)
            {
                if (path is not null && head is not null)
                {
                    path = null;
                    head = null;
                }
                continue;
            }

            if (field.StartsWith("worktree ", StringComparison.Ordinal))
                path = field["worktree ".Length..];
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal))
                head = field["HEAD ".Length..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal)
                     && string.Equals(field["branch ".Length..], fullRef, StringComparison.Ordinal))
            {
                if (path is null || head is null || match is not null)
                {
                    error = "ambiguous_checked_out_ref";
                    return null;
                }

                match = new WorktreeRef(Path.GetFullPath(path), head);
            }
        }

        return match;
    }

    private static bool IsOid(string value, int expectedLength) =>
        value.Length == expectedLength
        && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool PathsCollide(string first, string second) =>
        string.Equals(first, second, PathComparison)
        || first.StartsWith(second + "/", PathComparison)
        || second.StartsWith(first + "/", PathComparison);

    private static IEnumerable<string> SplitNull(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static GitReferenceUpdateResult Failure(GitResult result, string? currentOid = null) =>
        new(
            GitReferenceUpdateKind.Failure,
            currentOid,
            string.IsNullOrWhiteSpace(result.Stderr) ? "git_failed" : result.Stderr.Trim());

    private static GitResult RunGit(string repositoryPath, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new GitResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new GitResult(-1, string.Empty, ex.GetType().Name);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record WorktreeRef(string Path, string HeadOid);
    private sealed record GitResult(int ExitCode, string Stdout, string Stderr)
    {
        public bool Succeeded => ExitCode == 0;
    }

    private sealed class GitConvergenceGuard(GitTransactionGuard headGuard) : IDisposable
    {
        public void Dispose() => headGuard.Dispose();
    }

    private sealed class GitTransactionGuard(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Write("abort\n");
                    process.StandardInput.Close();
                    process.WaitForExit();
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
