using System.Diagnostics;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

/// <summary>
/// Materializes and owns the verified pod-local workspace used by local execution policies.
/// Prompt/gate behavior is deliberately outside this component so implementation turns can reuse it.
/// </summary>
internal sealed class PodLocalWorkspaceManager
{
    private readonly AgentHostOptions _options;
    private readonly ILogger<PodLocalWorkspaceManager> _logger;
    private PreparedWorkspace? _preparedWorkspace;

    public PodLocalWorkspaceManager(
        IOptions<AgentHostOptions> options,
        ILogger<PodLocalWorkspaceManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PreparedWorkspace> PrepareAsync(
        PodLocalWorkspaceSpec spec,
        CancellationToken ct)
    {
        Validate(spec);
        if (_preparedWorkspace is not null)
        {
            throw new AgentHostConfigurationException(
                "workspace_already_prepared",
                "A pod-local execution workspace has already been prepared for this AgentHost.");
        }

        var configuredScratchRoot = Path.GetFullPath(_options.ExecutionScratchRoot);
        var scratchRoot = Path.GetFullPath(spec.ScratchRoot);
        if (!PathEquals(scratchRoot, configuredScratchRoot))
        {
            throw new AgentHostConfigurationException(
                "workspace_scratch_root_mismatch",
                "The requested scratch root does not match the AgentHost execution-scratch mount.");
        }

        var sourceRepositoryPath = Path.GetFullPath(spec.SourceRepositoryPath);
        var workspacePath = PodLocalExecutionWorkspace.GetWorkspacePath(
            scratchRoot,
            spec.RunId,
            spec.ExpectedTreeHash);
        if (!Directory.Exists(sourceRepositoryPath))
        {
            throw new AgentHostConfigurationException(
                "workspace_source_repository_missing",
                "The configured source repository path does not exist.");
        }

        EnsureScratchCapacity(workspacePath);
        var runRoot = Directory.GetParent(workspacePath)?.FullName
            ?? throw new AgentHostConfigurationException(
                "workspace_path_invalid",
                "The deterministic pod-local workspace path has no parent directory.");
        Directory.CreateDirectory(runRoot);

        if (Directory.Exists(workspacePath)
            && Directory.EnumerateFileSystemEntries(workspacePath).Any())
        {
            throw new AgentHostConfigurationException(
                "workspace_path_not_empty",
                "The deterministic pod-local workspace path was not empty.");
        }

        Directory.CreateDirectory(workspacePath);

        try
        {
            await RunGitAsync(workspacePath, ct, "init", ".").ConfigureAwait(false);
            await RunGitAsync(workspacePath, ct, "remote", "add", "origin", sourceRepositoryPath)
                .ConfigureAwait(false);
            await RunGitAsync(
                    workspacePath,
                    ct,
                    "fetch",
                    "--no-tags",
                    "--depth=1",
                    "origin",
                    spec.SourceRef)
                .ConfigureAwait(false);

            var fetchedCommit = await RunGitAsync(workspacePath, ct, "rev-parse", "FETCH_HEAD")
                .ConfigureAwait(false);
            if (!string.Equals(fetchedCommit, spec.BaseCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentHostConfigurationException(
                    "workspace_base_commit_mismatch",
                    $"Fetched source ref resolved to commit '{fetchedCommit}', expected '{spec.BaseCommitSha}'.");
            }

            var fetchedTree = await RunGitAsync(workspacePath, ct, "rev-parse", "FETCH_HEAD^{tree}")
                .ConfigureAwait(false);
            if (!string.Equals(fetchedTree, spec.ExpectedTreeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentHostConfigurationException(
                    "workspace_tree_mismatch",
                    $"Fetched commit resolved to tree '{fetchedTree}', expected '{spec.ExpectedTreeHash}'.");
            }

            await RunGitAsync(
                    workspacePath,
                    ct,
                    "checkout",
                    "--detach",
                    "--force",
                    spec.BaseCommitSha)
                .ConfigureAwait(false);

            var cacheRoot = ConfigurePackageCaches(workspacePath);
            var prepared = new PreparedWorkspace(
                workspacePath,
                cacheRoot,
                spec.SourceRepositoryPath,
                spec.SourceRef,
                spec.BaseCommitSha,
                spec.ExpectedTreeHash,
                spec.Mode);
            _preparedWorkspace = prepared;

            _logger.LogInformation(
                "Pod-local workspace prepared for run {RunId}: mode={Mode} commit={CommitSha} tree={TreeHash} path={Path}",
                spec.RunId,
                spec.Mode,
                spec.BaseCommitSha,
                spec.ExpectedTreeHash,
                workspacePath);
            return prepared;
        }
        catch
        {
            TryDeleteDirectory(runRoot);
            throw;
        }
    }

    /// <summary>
    /// Returns the immutable inputs needed by the future #253 finalizer. It intentionally performs
    /// no commit or push itself; read-only assembly workspaces fail closed here.
    /// </summary>
    public Task<PreparedWriteback> PrepareWritebackAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workspace = _preparedWorkspace
            ?? throw new AgentHostConfigurationException(
                "workspace_not_prepared",
                "No pod-local workspace has been prepared.");

        if (workspace.Mode == ExecutionWorkspaceMode.LocalReadOnly)
        {
            throw new AgentHostConfigurationException(
                "workspace_writeback_read_only",
                "Write-back cannot be prepared for a read-only pod-local workspace.");
        }

        if (workspace.Mode != ExecutionWorkspaceMode.LocalWritable)
        {
            throw new AgentHostConfigurationException(
                "workspace_writeback_shared",
                "Write-back is only available for writable pod-local workspaces.");
        }

        return Task.FromResult(new PreparedWriteback(
            workspace.WorkspacePath,
            workspace.SourceRepositoryPath,
            workspace.SourceRef,
            workspace.BaseCommitSha,
            workspace.ExpectedTreeHash));
    }

    public Task CleanupAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workspace = _preparedWorkspace;
        _preparedWorkspace = null;
        if (workspace is null)
            return Task.CompletedTask;

        var runRoot = Directory.GetParent(workspace.WorkspacePath)?.FullName;
        if (!string.IsNullOrWhiteSpace(runRoot))
            TryDeleteDirectory(runRoot);
        return Task.CompletedTask;
    }

    internal static void ValidateConfiguration(AgentHostRunConfiguration configuration)
    {
        if (configuration.Purpose == AgentHostPurpose.AssemblyBuildTest
            && configuration.WorkspaceMode != ExecutionWorkspaceMode.LocalReadOnly)
        {
            throw new AgentHostConfigurationException(
                "workspace_mode_invalid",
                "AssemblyBuildTest requires workspaceMode LocalReadOnly.");
        }

        if (configuration.Purpose == AgentHostPurpose.ImplementationTurn
            || configuration.WorkspaceMode == ExecutionWorkspaceMode.LocalWritable)
        {
            throw new AgentHostConfigurationException(
                "implementation_turn_not_enabled",
                "ImplementationTurn and LocalWritable are reserved for issue #253 and are not wired yet.");
        }

        if (configuration.WorkspaceMode == ExecutionWorkspaceMode.Shared)
            return;

        if (string.IsNullOrWhiteSpace(configuration.SourceRepositoryPath)
            || string.IsNullOrWhiteSpace(configuration.SourceRef)
            || !PodLocalExecutionWorkspace.IsGitObjectId(configuration.BaseCommitSha)
            || !PodLocalExecutionWorkspace.IsGitObjectId(configuration.ExpectedTreeHash)
            || string.IsNullOrWhiteSpace(configuration.ScratchRoot))
        {
            throw new AgentHostConfigurationException(
                "workspace_configuration_invalid",
                "Pod-local execution requires sourceRepositoryPath, sourceRef, baseCommitSha, expectedTreeHash, and scratchRoot.");
        }
    }

    private static void Validate(PodLocalWorkspaceSpec spec)
    {
        if (spec.Mode == ExecutionWorkspaceMode.Shared
            || string.IsNullOrWhiteSpace(spec.RunId)
            || string.IsNullOrWhiteSpace(spec.SourceRepositoryPath)
            || string.IsNullOrWhiteSpace(spec.SourceRef)
            || !PodLocalExecutionWorkspace.IsGitObjectId(spec.BaseCommitSha)
            || !PodLocalExecutionWorkspace.IsGitObjectId(spec.ExpectedTreeHash)
            || string.IsNullOrWhiteSpace(spec.ScratchRoot))
        {
            throw new AgentHostConfigurationException(
                "workspace_configuration_invalid",
                "A valid local workspace mode, run, source ref, base commit, tree hash, and scratch root are required.");
        }
    }

    private void EnsureScratchCapacity(string workspacePath)
    {
        var required = Math.Max(0, _options.ExecutionScratchMinimumFreeBytes);
        if (required == 0)
            return;

        var probePath = Directory.Exists(workspacePath)
            ? workspacePath
            : Directory.GetParent(workspacePath)?.FullName ?? _options.ExecutionScratchRoot;
        Directory.CreateDirectory(probePath);

        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && IsPathUnder(probePath, d.RootDirectory.FullName))
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .FirstOrDefault();
        if (drive is null || drive.AvailableFreeSpace < required)
        {
            throw new AgentHostConfigurationException(
                "insufficient_ephemeral_storage",
                $"Pod-local execution requires at least {required} free bytes on execution scratch.");
        }
    }

    private static string ConfigurePackageCaches(string workspacePath)
    {
        const string cacheDirectory = ".agentweaver-cache";
        var cacheRoot = Path.Combine(workspacePath, cacheDirectory);
        var npm = Path.Combine(cacheRoot, "npm");
        var yarn = Path.Combine(cacheRoot, "yarn");
        var pnpmHome = Path.Combine(cacheRoot, "pnpm", "home");
        var pnpmStore = Path.Combine(cacheRoot, "pnpm", "store");
        var xdg = Path.Combine(cacheRoot, "xdg");

        foreach (var path in new[] { npm, yarn, pnpmHome, pnpmStore, xdg })
            Directory.CreateDirectory(path);

        Environment.SetEnvironmentVariable("npm_config_cache", $"{cacheDirectory}/npm");
        Environment.SetEnvironmentVariable("YARN_CACHE_FOLDER", $"{cacheDirectory}/yarn");
        Environment.SetEnvironmentVariable("PNPM_HOME", $"{cacheDirectory}/pnpm/home");
        Environment.SetEnvironmentVariable("PNPM_STORE_DIR", $"{cacheDirectory}/pnpm/store");
        Environment.SetEnvironmentVariable("npm_config_store_dir", $"{cacheDirectory}/pnpm/store");
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", $"{cacheDirectory}/xdg");
        return cacheRoot;
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
        {
            throw new AgentHostConfigurationException(
                "workspace_git_failed",
                "Failed to start git while preparing the pod-local workspace.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var operation = arguments.Length > 0 ? arguments[0] : "unknown";
            throw new AgentHostConfigurationException(
                "workspace_git_failed",
                $"git {operation} failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }

        return stdout;
    }

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Claim deletion removes the emptyDir; cleanup here is best-effort only.
        }
    }
}

internal sealed record PodLocalWorkspaceSpec(
    string RunId,
    string SourceRepositoryPath,
    string SourceRef,
    string BaseCommitSha,
    string ExpectedTreeHash,
    ExecutionWorkspaceMode Mode,
    string ScratchRoot);

internal sealed record PreparedWorkspace(
    string WorkspacePath,
    string CacheRoot,
    string SourceRepositoryPath,
    string SourceRef,
    string BaseCommitSha,
    string ExpectedTreeHash,
    ExecutionWorkspaceMode Mode);

internal sealed record PreparedWriteback(
    string WorkspacePath,
    string SourceRepositoryPath,
    string SourceRef,
    string BaseCommitSha,
    string ExpectedTreeHash);

/// <summary>Typed one-time AgentHost configuration failure returned by <c>POST /configure</c>.</summary>
internal sealed class AgentHostConfigurationException : Exception
{
    public AgentHostConfigurationException(string reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public string Reason { get; }
}
