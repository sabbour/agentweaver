using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed class GitWorkspaceManager
{
    private const int ManifestVersion = 1;
    private const int MaximumDiffBytes = 8 * 1024 * 1024;
    private const int MaximumCommandOutputBytes = 16 * 1024 * 1024;
    private const int MaximumGitDiagnosticBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _rootPath;
    private readonly IGitRepositoryRemote _remote;
    private readonly string _gitExecutable;

    public GitWorkspaceManager(string rootPath)
        : this(rootPath, new GitHubGitRepositoryRemote(), "git")
    {
    }

    internal GitWorkspaceManager(
        string rootPath,
        IGitRepositoryRemote remote,
        string gitExecutable = "git")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable)
            ? throw new ArgumentException("A Git executable is required.", nameof(gitExecutable))
            : gitExecutable;
    }

    public async Task<GitWorkspace> PrepareAsync(
        GitWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        Directory.CreateDirectory(_rootPath);
        EnsureNotReparsePoint(_rootPath);
        var workspaceRoot = Path.Combine(
            _rootPath, Hash(request.Context.Binding.RunId + "\0" + request.WorkspaceId));
        Directory.CreateDirectory(workspaceRoot);
        EnsureNotReparsePoint(workspaceRoot);

        var manifestPath = Path.Combine(workspaceRoot, "workspace.json");
        var repositoryPath = Path.Combine(workspaceRoot, "repository");
        var manifest = await ReadOrCreateManifestAsync(
            manifestPath, workspaceRoot, request, cancellationToken).ConfigureAwait(false);
        var remoteUri = _remote.GetCloneUri(request.Context.Repository);
        ValidateRemoteUri(remoteUri);
        var emptyGitConfig = Path.Combine(workspaceRoot, "git-empty-config");
        EnsureEmptyGitConfig(emptyGitConfig);

        if (manifest.State == "ready")
        {
            await ValidateOwnedRepositoryAsync(
                repositoryPath, remoteUri, cancellationToken).ConfigureAwait(false);
            return ToWorkspace(request, repositoryPath);
        }

        if (Directory.Exists(repositoryPath))
        {
            EnsureNotReparsePoint(repositoryPath);
            if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
            {
                Directory.Delete(repositoryPath, recursive: true);
            }
            else
            {
                await ValidateOwnedRepositoryAsync(
                    repositoryPath, remoteUri, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!Directory.Exists(repositoryPath))
        {
            await RunGitAsync(
                workspaceRoot,
                ["clone", "--no-checkout", "--", remoteUri.AbsoluteUri, repositoryPath],
                MaximumCommandOutputBytes,
                emptyGitConfig,
                remoteUri,
                request.Context.Credential,
                cancellationToken).ConfigureAwait(false);
        }

        await ValidateOwnedRepositoryAsync(repositoryPath, remoteUri, cancellationToken)
            .ConfigureAwait(false);
        await RunGitAsync(
            repositoryPath,
            ["fetch", "--no-tags", "origin", request.BaseSha],
            MaximumCommandOutputBytes,
            emptyGitConfig,
            remoteUri,
            request.Context.Credential,
            cancellationToken).ConfigureAwait(false);
        await RunGitAsync(
            repositoryPath,
            ["checkout", "--force", "-B", request.BranchName, request.BaseSha],
            MaximumCommandOutputBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await WriteManifestAsync(
            manifestPath,
            manifest with { State = "ready" },
            cancellationToken).ConfigureAwait(false);
        return ToWorkspace(request, repositoryPath);
    }

    public async Task<GitWorkspaceDiff> AssembleDiffAsync(
        GitWorkspaceRequest request,
        GitWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateRequest(request);
        var expected = GetWorkspacePaths(request);
        if (!string.Equals(
                Path.GetFullPath(workspace.Path), expected.RepositoryPath, PathComparison) ||
            !string.Equals(workspace.RunId, request.Context.Binding.RunId, StringComparison.Ordinal) ||
            !string.Equals(workspace.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(workspace.RepositoryId, request.Context.Binding.Resource.ResourceId, StringComparison.Ordinal) ||
            workspace.ResourceGeneration != request.Context.Binding.Resource.Generation ||
            !string.Equals(workspace.BaseSha, request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workspace.BranchName, request.BranchName, StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The workspace reference does not match the requested run and pinned repository.");

        EnsureNotReparsePoint(expected.WorkspaceRoot);
        EnsureNotReparsePoint(expected.ManifestPath);
        var manifest = await ReadManifestAsync(expected.ManifestPath, cancellationToken).ConfigureAwait(false);
        ValidateManifestBinding(manifest, request);
        if (manifest.State != "ready")
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace is not ready.");
        await ValidateOwnedRepositoryAsync(
            expected.RepositoryPath,
            _remote.GetCloneUri(request.Context.Repository),
            cancellationToken).ConfigureAwait(false);

        var emptyGitConfig = Path.Combine(expected.WorkspaceRoot, "git-empty-config");
        EnsureEmptyGitConfig(emptyGitConfig);
        await RunGitAsync(
            expected.RepositoryPath,
            ["add", "--intent-to-add", "--", "."],
            MaximumCommandOutputBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var patchBytes = await RunGitAsync(
            expected.RepositoryPath,
            ["diff", "--binary", "--no-ext-diff", "--no-textconv", "--full-index", request.BaseSha, "--"],
            MaximumDiffBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var statusBytes = await RunGitAsync(
            expected.RepositoryPath,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            MaximumCommandOutputBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var headBytes = await RunGitAsync(
            expected.RepositoryPath,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            128,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new GitWorkspaceDiff(
            workspace.WorkspaceId,
            request.BaseSha,
            DecodeGitOutput(headBytes).Trim(),
            DecodeGitOutput(statusBytes),
            DecodeGitOutput(patchBytes));
    }

    private async Task<WorkspaceManifest> ReadOrCreateManifestAsync(
        string manifestPath,
        string workspaceRoot,
        GitWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        if (File.Exists(manifestPath))
        {
            EnsureNotReparsePoint(manifestPath);
            var existing = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            ValidateManifestBinding(existing, request);
            if (existing.State is not ("preparing" or "ready"))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "The source-control workspace has an unknown recovery state.");
            return existing;
        }

        if (Directory.EnumerateFileSystemEntries(workspaceRoot).Any())
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The target source-control workspace contains data without an ownership manifest.");

        var manifest = CreateManifest(request, "preparing");
        await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async Task<WorkspaceManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkspaceManifest>(json, JsonOptions)
                ?? throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "The source-control workspace manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace manifest is invalid.",
                innerException: exception);
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task ValidateOwnedRepositoryAsync(
        string repositoryPath,
        Uri expectedRemote,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryPath))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The owned source-control repository directory is missing.");
        EnsureNotReparsePoint(repositoryPath);
        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The owned source-control repository metadata is missing.");

        var emptyGitConfig = Path.Combine(Path.GetDirectoryName(repositoryPath)!, "git-empty-config");
        EnsureEmptyGitConfig(emptyGitConfig);
        var topLevel = await RunGitAsync(
            repositoryPath,
            ["rev-parse", "--show-toplevel"],
            4096,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Path.GetFullPath(DecodeGitOutput(topLevel).Trim()),
                Path.GetFullPath(repositoryPath),
                PathComparison))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "Git resolved the source-control repository outside its owned workspace.");

        var origin = await RunGitAsync(
            repositoryPath,
            ["remote", "get-url", "origin"],
            4096,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                DecodeGitOutput(origin).Trim(),
                expectedRemote.AbsoluteUri,
                StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The source-control workspace origin does not match its pinned repository.");
    }

    private async Task<byte[]> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes,
        string emptyGitConfig,
        Uri? remoteUri,
        SecretCredential? credential,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var name in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            startInfo.Environment.Remove(name);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = emptyGitConfig;
        var sensitiveValues = new List<string>();
        if (remoteUri is { Scheme: "https" } &&
            string.Equals(remoteUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (credential is null)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.InvalidRequest,
                    "A broker-redeemed checkout credential is required for the GitHub repository.");
            var authorization = "x-access-token:" + credential.GetValue();
            var encodedAuthorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorization));
            sensitiveValues.Add(credential.GetValue());
            sensitiveValues.Add(authorization);
            sensitiveValues.Add(encodedAuthorization);
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: basic " + encodedAuthorization;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.GitUnavailable,
                    "The Git executable did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.GitUnavailable,
                "The Git executable could not be started.",
                innerException: exception);
        }

        var outputTask = ReadBoundedOutputAsync(
            process.StandardOutput.BaseStream, maximumOutputBytes, GitWorkspaceFailureCode.DiffTooLarge);
        var errorTask = ReadBoundedOutputAsync(
            process.StandardError.BaseStream, MaximumGitDiagnosticBytes, GitWorkspaceFailureCode.GitFailed);
        try
        {
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(waitTask, outputTask, errorTask).ConfigureAwait(false);
            if (first == outputTask && outputTask.IsFaulted)
            {
                KillProcessTree(process);
                await outputTask.ConfigureAwait(false);
            }
            if (first == errorTask && errorTask.IsFaulted)
            {
                KillProcessTree(process);
                await errorTask.ConfigureAwait(false);
            }

            await waitTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var diagnostic = Encoding.UTF8.GetString(error).Trim();
                foreach (var sensitiveValue in sensitiveValues)
                    diagnostic = diagnostic.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
                var detail = diagnostic.Length == 0 ? string.Empty : " " + diagnostic;
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.GitFailed,
                    "The Git " + arguments[0] + " operation failed with exit code " +
                    process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + detail,
                    process.ExitCode);
            }
            return output;
        }
        catch
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedOutputAsync(
        Stream stream,
        int maximumBytes,
        GitWorkspaceFailureCode failureCode)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new GitWorkspaceException(
                    failureCode,
                    "Git process output exceeded the configured safety limit.");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private static void KillProcessTree(Process process)
    {
        if (process.HasExited)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }

    private static string DecodeGitOutput(byte[] output)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(output);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.GitFailed,
                "Git returned output that was not valid UTF-8.",
                innerException: exception);
        }
    }

    private static void ValidateRequest(GitWorkspaceRequest request)
    {
        var context = request.Context;
        if (context?.Binding is null ||
            context.Binding.Seam != ProviderSeam.SourceControl ||
            context.Binding.Resource.Seam != ProviderSeam.SourceControl ||
            !string.Equals(
                context.Binding.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !string.Equals(
                context.Binding.Resource.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !context.Binding.NegotiatedCapabilities.Contains(SourceControlCapabilities.RepositoryCheckout) ||
            context.Credential is null ||
            string.IsNullOrWhiteSpace(context.Binding.RunId) ||
            string.IsNullOrWhiteSpace(request.WorkspaceId) ||
            request.WorkspaceId.Length > 128 ||
            request.WorkspaceId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            !IsGitSha(request.BaseSha) ||
            !IsValidBranchName(request.BranchName))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.InvalidRequest,
                "The source-control workspace request is not bound to a valid run, repository, revision, and branch.");
    }

    private static bool IsValidBranchName(string branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.Length <= 255 &&
        branch[0] != '-' &&
        branch.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            !segment.StartsWith(".", StringComparison.Ordinal) &&
            !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.')) &&
        !branch.Contains("..", StringComparison.Ordinal);

    private static bool IsGitSha(string value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private static void ValidateRemoteUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.InvalidRequest,
                "The source-control adapter returned an invalid repository URL.");
    }

    private static void EnsureEmptyGitConfig(string path)
    {
        if (File.Exists(path))
        {
            EnsureNotReparsePoint(path);
            if (new FileInfo(path).Length != 0)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "The isolated Git configuration file is not empty.");
            return;
        }
        using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "A source-control workspace path cannot be a symbolic link or reparse point.");
    }

    private (string WorkspaceRoot, string RepositoryPath, string ManifestPath) GetWorkspacePaths(
        GitWorkspaceRequest request)
    {
        var workspaceRoot = Path.Combine(
            _rootPath, Hash(request.Context.Binding.RunId + "\0" + request.WorkspaceId));
        var fullRoot = Path.GetFullPath(workspaceRoot);
        var relative = Path.GetRelativePath(_rootPath, fullRoot);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The source-control workspace escaped its configured root.");
        return (
            fullRoot,
            Path.Combine(fullRoot, "repository"),
            Path.Combine(fullRoot, "workspace.json"));
    }

    private static WorkspaceManifest CreateManifest(GitWorkspaceRequest request, string state) =>
        new(
            ManifestVersion,
            request.Context.Binding.RunId,
            request.WorkspaceId,
            request.Context.Binding.ProviderId,
            request.Context.Binding.Resource.ResourceId,
            request.Context.Binding.Resource.Generation,
            request.Context.Repository.Owner,
            request.Context.Repository.Name,
            request.BaseSha,
            request.BranchName,
            state);

    private static void ValidateManifestBinding(WorkspaceManifest manifest, GitWorkspaceRequest request)
    {
        var expected = CreateManifest(request, manifest.State);
        if (manifest.Version != ManifestVersion ||
            !string.Equals(manifest.RunId, expected.RunId, StringComparison.Ordinal) ||
            !string.Equals(manifest.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProviderId, expected.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(manifest.RepositoryId, expected.RepositoryId, StringComparison.Ordinal) ||
            manifest.ResourceGeneration != expected.ResourceGeneration ||
            !string.Equals(manifest.Owner, expected.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Repository, expected.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.BaseSha, expected.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.BranchName, expected.BranchName, StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The source-control workspace belongs to a different run, repository, or pinned revision.");
    }

    private static GitWorkspace ToWorkspace(GitWorkspaceRequest request, string repositoryPath) =>
        new(
            request.WorkspaceId,
            request.Context.Binding.RunId,
            request.Context.Binding.Resource.ResourceId,
            request.Context.Binding.Resource.Generation,
            request.BaseSha,
            request.BranchName,
            Path.GetFullPath(repositoryPath));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record WorkspaceManifest(
        int Version,
        string RunId,
        string WorkspaceId,
        string ProviderId,
        string RepositoryId,
        long ResourceGeneration,
        string Owner,
        string Repository,
        string BaseSha,
        string BranchName,
        string State);

    private sealed class GitHubGitRepositoryRemote : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository) =>
            new($"https://github.com/{Uri.EscapeDataString(repository.Owner)}/" +
                $"{Uri.EscapeDataString(repository.Name)}.git");
    }
}
