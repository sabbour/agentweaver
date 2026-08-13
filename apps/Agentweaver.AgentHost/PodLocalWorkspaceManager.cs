using System.Diagnostics;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

/// <summary>
/// Materializes and owns the verified pod-local workspace used by local execution policies.
/// Prompt/gate behavior is deliberately outside this component so implementation turns can reuse it.
/// </summary>
internal sealed class PodLocalWorkspaceManager
{
    private static readonly HashSet<string> NestedRepositoryScanExcludedDirectories = new(
        [
            ".git",
            ".next",
            "bin",
            "build",
            "dist",
            "node_modules",
            "obj",
        ],
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly AgentHostOptions _options;
    private readonly ILogger<PodLocalWorkspaceManager> _logger;
    private readonly IRunWorkspaceRegistrar? _workspaceRegistrar;
    private PreparedWorkspace? _preparedWorkspace;
    private string? _agentScratchPath;
    private string? _fallbackWorkspacePath;

    public PodLocalWorkspaceManager(
        IOptions<AgentHostOptions> options,
        ILogger<PodLocalWorkspaceManager> logger,
        ISandboxExecutor? sandboxExecutor = null)
    {
        _options = options.Value;
        _logger = logger;
        _workspaceRegistrar = sandboxExecutor as IRunWorkspaceRegistrar;
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

            ConfigureRuntimeHome(spec.RunId, workspacePath);
            var prepared = new PreparedWorkspace(
                spec.RunId,
                workspacePath,
                spec.SourceRepositoryPath,
                spec.SourceRef,
                spec.BaseCommitSha,
                spec.ExpectedTreeHash,
                spec.Mode,
                spec.CommitAuthorName,
                spec.CommitAuthorEmail);
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
    /// Creates an empty run-scoped directory for non-deliverable agent working files.
    /// It is outside every worktree and is removed with the AgentHost lifecycle.
    /// </summary>
    public string PrepareAgentScratchDirectory(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_agentScratchPath is not null)
        {
            throw new AgentHostConfigurationException(
                "agent_scratch_already_prepared",
                "A run-scoped agent scratch directory has already been prepared for this AgentHost.");
        }

        var scratchRoot = Path.GetFullPath(_options.ExecutionScratchRoot);
        var scratchPath = PodLocalExecutionWorkspace.GetAgentScratchPath(scratchRoot, runId);
        Directory.CreateDirectory(scratchPath);
        if (Directory.EnumerateFileSystemEntries(scratchPath).Any())
        {
            throw new AgentHostConfigurationException(
                "agent_scratch_not_empty",
                "The run-scoped agent scratch directory was not empty.");
        }

        _agentScratchPath = scratchPath;
        return scratchPath;
    }

    public string PrepareFallbackWorkspaceDirectory(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_fallbackWorkspacePath is not null)
        {
            throw new AgentHostConfigurationException(
                "fallback_workspace_already_prepared",
                "A pod-private fallback workspace has already been prepared for this AgentHost.");
        }

        var path = Path.Combine(
            Path.GetFullPath(_options.ExecutionScratchRoot),
            "fallback-workspace",
            PodLocalExecutionWorkspace.GetRunHash(runId));
        Directory.CreateDirectory(path);
        _fallbackWorkspacePath = path;
        return path;
    }

    /// <summary>
    /// Captures the final working tree through a platform-owned alternate index, creates one commit
    /// parented directly to the immutable base, and publishes it to a unique temporary shared ref.
    /// </summary>
    public async Task<PreparedWriteback> PrepareWritebackAsync(CancellationToken ct = default)
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

        var runRoot = Directory.GetParent(workspace.WorkspacePath)?.FullName
            ?? throw new AgentHostConfigurationException(
                "workspace_path_invalid",
                "The pod-local workspace path has no run root for its platform index.");
        var indexPath = Path.Combine(runRoot, $".agentweaver-index-{Guid.NewGuid():N}");
        var gitEnvironment = new Dictionary<string, string?>
        {
            ["GIT_INDEX_FILE"] = indexPath,
        };

        try
        {
            await RunGitWithEnvironmentAsync(
                workspace.WorkspacePath,
                gitEnvironment,
                ct,
                "read-tree",
                workspace.BaseCommitSha).ConfigureAwait(false);
            await RunGitWithEnvironmentAsync(
                workspace.WorkspacePath,
                gitEnvironment,
                ct,
                "add",
                "-A",
                "--",
                ".").ConfigureAwait(false);

            var nestedRoots = FindNestedRepositoryRoots(workspace.WorkspacePath, ct);
            if (nestedRoots.Count > 0)
            {
                await StageNestedRepositoryContentsAsync(
                    workspace.WorkspacePath,
                    runRoot,
                    nestedRoots,
                    gitEnvironment,
                    ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Run {RunId}: flattened {Count} nested git repository root(s) into the parent write-back without .git metadata: {Roots}",
                    workspace.RunId,
                    nestedRoots.Count,
                    string.Join(", ", nestedRoots));
            }

            var resultTree = await RunGitWithEnvironmentAsync(
                workspace.WorkspacePath,
                gitEnvironment,
                ct,
                "write-tree").ConfigureAwait(false);
            var residualGitlinks = await GetGitlinkPathsAsync(
                workspace.WorkspacePath,
                resultTree,
                gitEnvironment,
                ct).ConfigureAwait(false);
            if (residualGitlinks.Count > 0)
            {
                throw new AgentHostConfigurationException(
                    "writeback_invalid",
                    $"Write-back tree contains unflattened nested repositories: {string.Join(", ", residualGitlinks)}.");
            }

            var changedPaths = await GetChangedPathsAsync(
                workspace.WorkspacePath,
                workspace.BaseCommitSha,
                gitEnvironment,
                ct).ConfigureAwait(false);

            if (string.Equals(
                    resultTree,
                    workspace.ExpectedTreeHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new PreparedWriteback(
                    workspace.RunId,
                    workspace.SourceRef,
                    WritebackRef: null,
                    workspace.BaseCommitSha,
                    workspace.BaseCommitSha,
                    workspace.ExpectedTreeHash,
                    ChangedPathCount: 0);
            }

            var commitEnvironment = new Dictionary<string, string?>(gitEnvironment)
            {
                ["GIT_AUTHOR_NAME"] = workspace.CommitAuthorName,
                ["GIT_AUTHOR_EMAIL"] = workspace.CommitAuthorEmail,
                ["GIT_COMMITTER_NAME"] = workspace.CommitAuthorName,
                ["GIT_COMMITTER_EMAIL"] = workspace.CommitAuthorEmail,
            };
            var resultCommit = await RunGitWithEnvironmentAsync(
                workspace.WorkspacePath,
                commitEnvironment,
                ct,
                "commit-tree",
                resultTree,
                "-p",
                workspace.BaseCommitSha,
                "-m",
                $"Agentweaver run {workspace.RunId}").ConfigureAwait(false);
            var writebackRef =
                $"{PodLocalExecutionWorkspace.WritebackRefPrefix}" +
                $"{PodLocalExecutionWorkspace.GetRunHash(workspace.RunId)}/{Guid.NewGuid():N}";

            await RunGitAsync(
                workspace.WorkspacePath,
                ct,
                "push",
                "--no-force",
                "origin",
                $"{resultCommit}:{writebackRef}").ConfigureAwait(false);
            await RunGitAsync(
                workspace.WorkspacePath,
                ct,
                "checkout",
                "--detach",
                "--force",
                resultCommit).ConfigureAwait(false);
            _preparedWorkspace = workspace with
            {
                BaseCommitSha = resultCommit,
                ExpectedTreeHash = resultTree,
            };

            _logger.LogInformation(
                "Prepared pod-local write-back for run {RunId}: ref={WritebackRef} base={BaseCommit} result={ResultCommit} tree={ResultTree} changedPaths={ChangedPathCount}",
                workspace.RunId,
                writebackRef,
                workspace.BaseCommitSha,
                resultCommit,
                resultTree,
                changedPaths.Count);

            return new PreparedWriteback(
                workspace.RunId,
                workspace.SourceRef,
                writebackRef,
                workspace.BaseCommitSha,
                resultCommit,
                resultTree,
                changedPaths.Count);
        }
        finally
        {
            TryDeleteFile(indexPath);
            TryDeleteFile(indexPath + ".lock");
        }
    }

    public Task CleanupAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var workspace = _preparedWorkspace;
        _preparedWorkspace = null;
        if (workspace is not null)
        {
            var runRoot = Directory.GetParent(workspace.WorkspacePath)?.FullName;
            if (!string.IsNullOrWhiteSpace(runRoot))
                TryDeleteDirectory(runRoot);
        }

        var agentScratchPath = _agentScratchPath;
        _agentScratchPath = null;
        if (!string.IsNullOrWhiteSpace(agentScratchPath))
            TryDeleteDirectory(agentScratchPath);

        var fallbackWorkspacePath = _fallbackWorkspacePath;
        _fallbackWorkspacePath = null;
        if (!string.IsNullOrWhiteSpace(fallbackWorkspacePath))
            TryDeleteDirectory(fallbackWorkspacePath);
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
            && configuration.WorkspaceMode != ExecutionWorkspaceMode.LocalWritable)
        {
            throw new AgentHostConfigurationException(
                "workspace_mode_invalid",
                "ImplementationTurn requires workspaceMode LocalWritable.");
        }

        if (configuration.WorkspaceMode == ExecutionWorkspaceMode.LocalWritable
            && configuration.Purpose != AgentHostPurpose.ImplementationTurn)
        {
            throw new AgentHostConfigurationException(
                "workspace_mode_invalid",
                "LocalWritable is only valid for ImplementationTurn.");
        }

        if (configuration.Purpose == AgentHostPurpose.ImplementationTurn
            && !string.Equals(
                configuration.SourceRef,
                $"agentweaver/{configuration.RunId}",
                StringComparison.Ordinal))
        {
            throw new AgentHostConfigurationException(
                "workspace_source_ref_invalid",
                "ImplementationTurn must fetch the authoritative agentweaver/<childRunId> branch.");
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

        if (configuration.WorkspaceMode == ExecutionWorkspaceMode.LocalWritable
            && (string.IsNullOrWhiteSpace(configuration.CommitAuthorName)
                || string.IsNullOrWhiteSpace(configuration.CommitAuthorEmail)))
        {
            throw new AgentHostConfigurationException(
                "workspace_commit_identity_invalid",
                "Writable pod-local execution requires the platform commit author name and email.");
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

        if (spec.Mode == ExecutionWorkspaceMode.LocalWritable
            && (string.IsNullOrWhiteSpace(spec.CommitAuthorName)
                || string.IsNullOrWhiteSpace(spec.CommitAuthorEmail)))
        {
            throw new AgentHostConfigurationException(
                "workspace_commit_identity_invalid",
                "Writable pod-local execution requires the platform commit author name and email.");
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

    /// <summary>
    /// Creates the deterministic run HOME and binds it immutably to the resolved workspace.
    /// Shared and pod-local workspace modes use this same registration path.
    /// </summary>
    public string ConfigureRuntimeHome(string runId, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workspace = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new AgentHostConfigurationException(
                "workspace_path_missing",
                "The effective AgentHost workspace does not exist.");
        }

        var home = Path.Combine(
            Path.GetFullPath(_options.ExecutionScratchRoot),
            "runtime-home",
            PodLocalExecutionWorkspace.GetRunHash(runId));
        var cache = Path.Combine(home, ".cache");
        var data = Path.Combine(home, ".local", "share");
        var config = Path.Combine(home, ".config");
        foreach (var path in new[] { home, cache, data, config })
            Directory.CreateDirectory(path);

        _workspaceRegistrar?.RegisterRuntimeHome(workspace, home);
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cache);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        return home;
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments) =>
        await RunGitWithEnvironmentAsync(
            workingDirectory,
            environment: null,
            ct,
            arguments).ConfigureAwait(false);

    private static async Task<string> RunGitWithEnvironmentAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
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
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.hooksPath=/dev/null");
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                process.StartInfo.Environment[name] = value;
        }

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

    private static async Task<IReadOnlyList<string>> GetChangedPathsAsync(
        string workspacePath,
        string baseCommitSha,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var output = await RunGitWithEnvironmentAsync(
            workspacePath,
            environment,
            ct,
            "diff",
            "--cached",
            "--name-only",
            "-z",
            baseCommitSha).ConfigureAwait(false);
        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> FindNestedRepositoryRoots(
        string workspacePath,
        CancellationToken ct,
        Action<string>? onDirectoryVisited = null)
    {
        var roots = new List<string>();
        var pending = new Stack<string>();
        pending.Push(workspacePath);

        try
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                onDirectoryVisited?.Invoke(directory);
                if (!PathEquals(directory, workspacePath))
                {
                    var metadataPath = Path.Combine(directory, ".git");
                    if (Directory.Exists(metadataPath) || File.Exists(metadataPath))
                    {
                        roots.Add(Path.GetRelativePath(workspacePath, directory)
                            .Replace('\\', '/'));
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var metadataPath = Path.Combine(child, ".git");
                    if (NestedRepositoryScanExcludedDirectories.Contains(Path.GetFileName(child))
                        && !Directory.Exists(metadataPath)
                        && !File.Exists(metadataPath))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentHostConfigurationException(
                "writeback_invalid",
                "Nested repositories could not be safely discovered from the pod-local filesystem.",
                ex);
        }

        return roots
            .OrderByDescending(GetPathDepth)
            .ThenBy(root => root, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task StageNestedRepositoryContentsAsync(
        string workspacePath,
        string runRoot,
        IReadOnlyList<string> nestedRoots,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var movedMetadata = new List<NestedRepositoryMetadata>();
        Exception? failure = null;

        try
        {
            foreach (var nestedRoot in nestedRoots)
            {
                ct.ThrowIfCancellationRequested();
                var metadataPath = Path.Combine(
                    workspacePath,
                    nestedRoot.Replace('/', Path.DirectorySeparatorChar),
                    ".git");
                var backupPath = Path.Combine(
                    runRoot,
                    $".agentweaver-nested-git-{Guid.NewGuid():N}");
                MoveGitMetadata(metadataPath, backupPath);
                movedMetadata.Add(new NestedRepositoryMetadata(metadataPath, backupPath));
            }

            foreach (var nestedRoot in nestedRoots.OrderBy(GetPathDepth))
            {
                await RunGitWithEnvironmentAsync(
                    workspacePath,
                    environment,
                    ct,
                    "rm",
                    "--cached",
                    "-r",
                    "-f",
                    "--ignore-unmatch",
                    "--",
                    nestedRoot).ConfigureAwait(false);
            }

            foreach (var nestedRoot in nestedRoots)
            {
                await RunGitWithEnvironmentAsync(
                    workspacePath,
                    environment,
                    ct,
                    "add",
                    "-A",
                    "--",
                    nestedRoot).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        for (var index = movedMetadata.Count - 1; index >= 0; index--)
        {
            try
            {
                RestoreGitMetadata(movedMetadata[index]);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (failure is OperationCanceledException cancellation)
            throw cancellation;

        if (failure is not null)
        {
            throw new AgentHostConfigurationException(
                "writeback_nested_repository_failed",
                "Nested repository contents could not be safely captured without their .git metadata.",
                failure);
        }
    }

    private static async Task<IReadOnlyList<string>> GetGitlinkPathsAsync(
        string workspacePath,
        string treeHash,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        var output = await RunGitWithEnvironmentAsync(
            workspacePath,
            environment,
            ct,
            "ls-tree",
            "-r",
            "-z",
            treeHash).ConfigureAwait(false);
        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("160000 ", StringComparison.Ordinal))
            .Select(entry =>
            {
                var separator = entry.IndexOf('\t');
                return separator >= 0 ? entry[(separator + 1)..] : entry;
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static int GetPathDepth(string path) =>
        path.Count(character => character is '/' or '\\') + 1;

    private static void MoveGitMetadata(string sourcePath, string backupPath)
    {
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, backupPath);
            return;
        }

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, backupPath);
            return;
        }

        throw new IOException($"Nested repository metadata was not found at '{sourcePath}'.");
    }

    private static void RestoreGitMetadata(NestedRepositoryMetadata metadata)
    {
        if (Directory.Exists(metadata.BackupPath))
        {
            Directory.Move(metadata.BackupPath, metadata.OriginalPath);
            return;
        }

        if (File.Exists(metadata.BackupPath))
        {
            File.Move(metadata.BackupPath, metadata.OriginalPath);
            return;
        }

        throw new IOException(
            $"Nested repository metadata backup was not found at '{metadata.BackupPath}'.");
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The execution-scratch claim is deleted with the pod; cleanup is best-effort.
        }
    }

    private sealed record NestedRepositoryMetadata(string OriginalPath, string BackupPath);
}

internal sealed record PodLocalWorkspaceSpec(
    string RunId,
    string SourceRepositoryPath,
    string SourceRef,
    string BaseCommitSha,
    string ExpectedTreeHash,
    ExecutionWorkspaceMode Mode,
    string ScratchRoot,
    string? CommitAuthorName = null,
    string? CommitAuthorEmail = null);

internal sealed record PreparedWorkspace(
    string RunId,
    string WorkspacePath,
    string SourceRepositoryPath,
    string SourceRef,
    string BaseCommitSha,
    string ExpectedTreeHash,
    ExecutionWorkspaceMode Mode,
    string? CommitAuthorName,
    string? CommitAuthorEmail);

/// <summary>Typed one-time AgentHost configuration failure returned by <c>POST /configure</c>.</summary>
internal sealed class AgentHostConfigurationException : Exception
{
    public AgentHostConfigurationException(string reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public string Reason { get; }
}
