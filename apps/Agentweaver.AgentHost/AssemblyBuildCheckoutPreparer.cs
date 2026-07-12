using System.Diagnostics;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;

namespace Agentweaver.AgentHost;

/// <summary>Prepares and verifies the pod-local checkout used by assembly Build/Test and preview.</summary>
internal sealed class AssemblyBuildCheckoutPreparer
{
    private readonly AgentHostOptions _options;
    private readonly ILogger<AssemblyBuildCheckoutPreparer> _logger;

    public AssemblyBuildCheckoutPreparer(
        IOptions<AgentHostOptions> options,
        ILogger<AssemblyBuildCheckoutPreparer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> PrepareAsync(AgentHostRunConfiguration configuration, CancellationToken ct)
    {
        Validate(configuration);

        var sourceRepositoryPath = Path.GetFullPath(configuration.SourceRepositoryPath!);
        var localExecutionPath = Path.GetFullPath(configuration.LocalExecutionPath!);
        var expectedPath = AssemblyBuildTestExecution.GetCheckoutPath(
            _options.BuildScratchRoot,
            configuration.RunId,
            configuration.ExpectedTreeHash!);
        if (!PathEquals(localExecutionPath, expectedPath))
        {
            throw new AgentHostConfigurationException(
                "assembly_checkout_path_mismatch",
                $"Local execution path did not match the deterministic path for run '{configuration.RunId}'.");
        }

        if (!Directory.Exists(sourceRepositoryPath))
        {
            throw new AgentHostConfigurationException(
                "assembly_source_repository_missing",
                "The configured source repository path does not exist.");
        }

        EnsureScratchCapacity(localExecutionPath);
        var parent = Directory.GetParent(localExecutionPath)?.FullName
            ?? throw new AgentHostConfigurationException(
                "assembly_checkout_path_invalid",
                "The configured local execution path has no parent directory.");
        Directory.CreateDirectory(parent);

        if (Directory.Exists(localExecutionPath)
            && Directory.EnumerateFileSystemEntries(localExecutionPath).Any())
        {
            throw new AgentHostConfigurationException(
                "assembly_checkout_path_not_empty",
                "The deterministic local execution path was not empty.");
        }

        Directory.CreateDirectory(localExecutionPath);

        try
        {
            await RunGitAsync(localExecutionPath, ct, "init", ".").ConfigureAwait(false);
            await RunGitAsync(localExecutionPath, ct, "remote", "add", "origin", sourceRepositoryPath)
                .ConfigureAwait(false);
            await RunGitAsync(
                    localExecutionPath,
                    ct,
                    "fetch",
                    "--no-tags",
                    "--depth=1",
                    "origin",
                    configuration.IntegrationRef!)
                .ConfigureAwait(false);

            var fetchedCommit = await RunGitAsync(localExecutionPath, ct, "rev-parse", "FETCH_HEAD")
                .ConfigureAwait(false);
            if (!string.Equals(fetchedCommit, configuration.CommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentHostConfigurationException(
                    "assembly_checkout_commit_mismatch",
                    $"Fetched integration ref resolved to commit '{fetchedCommit}', expected '{configuration.CommitSha}'.");
            }

            var fetchedTree = await RunGitAsync(localExecutionPath, ct, "rev-parse", "FETCH_HEAD^{tree}")
                .ConfigureAwait(false);
            if (!string.Equals(fetchedTree, configuration.ExpectedTreeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentHostConfigurationException(
                    "assembly_checkout_tree_mismatch",
                    $"Fetched commit resolved to tree '{fetchedTree}', expected '{configuration.ExpectedTreeHash}'.");
            }

            await RunGitAsync(
                    localExecutionPath,
                    ct,
                    "checkout",
                    "--detach",
                    "--force",
                    configuration.CommitSha!)
                .ConfigureAwait(false);

            ConfigurePackageCaches(localExecutionPath);
            _logger.LogInformation(
                "Assembly Build/Test checkout prepared for run {RunId}: commit={CommitSha} tree={TreeHash} path={Path}",
                configuration.RunId,
                configuration.CommitSha,
                configuration.ExpectedTreeHash,
                localExecutionPath);
            return localExecutionPath;
        }
        catch
        {
            TryDeleteDirectory(localExecutionPath);
            throw;
        }
    }

    internal static void Validate(AgentHostRunConfiguration configuration)
    {
        if (configuration.Purpose != AgentHostPurpose.AssemblyBuildTest)
            return;

        if (string.IsNullOrWhiteSpace(configuration.SourceRepositoryPath)
            || string.IsNullOrWhiteSpace(configuration.IntegrationRef)
            || !AssemblyBuildTestExecution.IsGitObjectId(configuration.CommitSha)
            || !AssemblyBuildTestExecution.IsGitObjectId(configuration.ExpectedTreeHash)
            || string.IsNullOrWhiteSpace(configuration.LocalExecutionPath))
        {
            throw new AgentHostConfigurationException(
                "assembly_checkout_configuration_invalid",
                "AssemblyBuildTest requires sourceRepositoryPath, integrationRef, commitSha, expectedTreeHash, and localExecutionPath.");
        }
    }

    private void EnsureScratchCapacity(string localExecutionPath)
    {
        var required = Math.Max(0, _options.BuildScratchMinimumFreeBytes);
        if (required == 0)
            return;

        var probePath = Directory.Exists(localExecutionPath)
            ? localExecutionPath
            : Directory.GetParent(localExecutionPath)?.FullName ?? _options.BuildScratchRoot;
        Directory.CreateDirectory(probePath);

        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && IsPathUnder(probePath, d.RootDirectory.FullName))
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .FirstOrDefault();
        if (drive is null || drive.AvailableFreeSpace < required)
        {
            throw new AgentHostConfigurationException(
                "insufficient_ephemeral_storage",
                $"Assembly Build/Test requires at least {required} free bytes on build scratch.");
        }
    }

    private static void ConfigurePackageCaches(string checkoutPath)
    {
        var checkoutParent = Directory.GetParent(checkoutPath)?.FullName
            ?? throw new AgentHostConfigurationException(
                "assembly_checkout_path_invalid",
                "The local checkout path has no parent for package caches.");
        var cacheRoot = Path.Combine(checkoutParent, "cache", Path.GetFileName(checkoutPath));
        var npm = Path.Combine(cacheRoot, "npm");
        var yarn = Path.Combine(cacheRoot, "yarn");
        var pnpmHome = Path.Combine(cacheRoot, "pnpm", "home");
        var pnpmStore = Path.Combine(cacheRoot, "pnpm", "store");
        var xdg = Path.Combine(cacheRoot, "xdg");

        foreach (var path in new[] { npm, yarn, pnpmHome, pnpmStore, xdg })
            Directory.CreateDirectory(path);

        Environment.SetEnvironmentVariable("npm_config_cache", npm);
        Environment.SetEnvironmentVariable("YARN_CACHE_FOLDER", yarn);
        Environment.SetEnvironmentVariable("PNPM_HOME", pnpmHome);
        Environment.SetEnvironmentVariable("PNPM_STORE_DIR", pnpmStore);
        Environment.SetEnvironmentVariable("npm_config_store_dir", pnpmStore);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", xdg);
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
                "assembly_checkout_git_failed",
                "Failed to start git while preparing the assembly checkout.");
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
                "assembly_checkout_git_failed",
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

/// <summary>Typed one-time AgentHost configuration failure returned by <c>POST /configure</c>.</summary>
internal sealed class AgentHostConfigurationException : Exception
{
    public AgentHostConfigurationException(string reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public string Reason { get; }
}
