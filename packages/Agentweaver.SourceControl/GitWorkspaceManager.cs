using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed class GitWorkspaceManager
{
    private const int ManifestVersion = 2;
    private const int MaximumDiffBytes = ProducedRunCaptureLimits.MaximumDiffBytes;
    private const int MaximumCommandOutputBytes = 16 * 1024 * 1024;
    private const int MaximumWorkspaceTreeBytes = 32 * 1024 * 1024;
    private const int MaximumCapturedFileBytes = ProducedRunCaptureLimits.MaximumFileBytes;
    private const int MaximumCapturedTotalBytes = ProducedRunCaptureLimits.MaximumTotalFileBytes;
    private const int MaximumCapturedFiles = ProducedRunCaptureLimits.MaximumFiles;
    private const int MaximumCapturedBatchBytes =
        MaximumCapturedTotalBytes + MaximumCapturedFiles * 64;
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
            return ToWorkspace(request, repositoryPath, manifest);
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
        return ToWorkspace(request, repositoryPath, manifest with { State = "ready" });
    }

    public async Task<GitWorkspace> OpenExistingAsync(
        GitWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        return await OpenExistingAsync(
            new GitWorkspaceCaptureRequest(
                request.Context.Binding,
                request.Context.Repository,
                request.WorkspaceId,
                request.BaseSha,
                request.BranchName),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitWorkspace> OpenExistingAsync(
        GitWorkspaceCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCaptureRequest(request);
        if (!Directory.Exists(_rootPath))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.WorkspaceUnavailable,
                "The source-control workspace is no longer available.");
        EnsureNotReparsePoint(_rootPath);
        var expected = GetWorkspacePaths(request);
        if (!Directory.Exists(expected.WorkspaceRoot) ||
            !File.Exists(expected.ManifestPath) ||
            !Directory.Exists(expected.RepositoryPath))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.WorkspaceUnavailable,
                "The source-control workspace is no longer available.");
        EnsureNotReparsePoint(expected.WorkspaceRoot);
        EnsureNotReparsePoint(expected.ManifestPath);
        EnsureNotReparsePoint(expected.RepositoryPath);

        var manifest = await ReadManifestAsync(expected.ManifestPath, cancellationToken).ConfigureAwait(false);
        ValidateManifestBinding(manifest, request);
        if (manifest.State != "ready" ||
            !Guid.TryParseExact(manifest.WorkspaceIncarnationId, "N", out var incarnationId))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace incarnation is invalid.");
        var remoteUri = _remote.GetCloneUri(request.Repository);
        ValidateRemoteUri(remoteUri);
        await ValidateOwnedRepositoryAsync(
            expected.RepositoryPath, remoteUri, cancellationToken).ConfigureAwait(false);
        return new GitWorkspace(
            request.WorkspaceId,
            request.Binding.RunId,
            request.Binding.Resource.ResourceId,
            request.Binding.Resource.Generation,
            request.BaseSha,
            request.BranchName,
            Path.GetFullPath(expected.RepositoryPath),
            incarnationId);
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
        if (manifest.State != "ready" ||
            !Guid.TryParseExact(manifest.WorkspaceIncarnationId, "N", out var incarnationId) ||
            workspace.WorkspaceIncarnationId != incarnationId)
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

    public async Task<GitWorkspaceCapture> CaptureAsync(
        GitWorkspaceRequest request,
        GitWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateRequest(request);
        return await CaptureAsync(
            new GitWorkspaceCaptureRequest(
                request.Context.Binding,
                request.Context.Repository,
                request.WorkspaceId,
                request.BaseSha,
                request.BranchName),
            workspace,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitWorkspaceCapture> CaptureAsync(
        GitWorkspaceCaptureRequest request,
        GitWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateCaptureRequest(request);
        var expected = GetWorkspacePaths(request);
        if (!string.Equals(
                Path.GetFullPath(workspace.Path), expected.RepositoryPath, PathComparison) ||
            !string.Equals(workspace.RunId, request.Binding.RunId, StringComparison.Ordinal) ||
            !string.Equals(workspace.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(workspace.RepositoryId, request.Binding.Resource.ResourceId, StringComparison.Ordinal) ||
            workspace.ResourceGeneration != request.Binding.Resource.Generation ||
            !string.Equals(workspace.BaseSha, request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workspace.BranchName, request.BranchName, StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The workspace reference does not match the requested run and pinned repository.");

        EnsureNotReparsePoint(expected.WorkspaceRoot);
        EnsureNotReparsePoint(expected.ManifestPath);
        var manifest = await ReadManifestAsync(expected.ManifestPath, cancellationToken).ConfigureAwait(false);
        ValidateManifestBinding(manifest, request);
        if (manifest.State != "ready" ||
            !Guid.TryParseExact(manifest.WorkspaceIncarnationId, "N", out var incarnationId) ||
            workspace.WorkspaceIncarnationId != incarnationId)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace incarnation is invalid.");
        await ValidateOwnedRepositoryAsync(
            expected.RepositoryPath,
            _remote.GetCloneUri(request.Repository),
            cancellationToken).ConfigureAwait(false);

        var emptyGitConfig = Path.Combine(expected.WorkspaceRoot, "git-empty-config");
        EnsureEmptyGitConfig(emptyGitConfig);
        var privateIndex = Path.Combine(expected.WorkspaceRoot, $"capture-{Guid.NewGuid():N}.index");
        try
        {
            await RunGitAsync(
                expected.RepositoryPath,
                ["read-tree", request.BaseSha],
                MaximumCommandOutputBytes,
                emptyGitConfig,
                remoteUri: null,
                credential: null,
                cancellationToken,
                gitIndexFile: privateIndex).ConfigureAwait(false);
            await RunGitAsync(
                expected.RepositoryPath,
                ["add", "--all", "--", "."],
                MaximumCommandOutputBytes,
                emptyGitConfig,
                remoteUri: null,
                credential: null,
                cancellationToken,
                gitIndexFile: privateIndex).ConfigureAwait(false);
            var treeBytes = await RunGitAsync(
                expected.RepositoryPath,
                ["write-tree"],
                128,
                emptyGitConfig,
                remoteUri: null,
                credential: null,
                cancellationToken,
                gitIndexFile: privateIndex).ConfigureAwait(false);
            var treeSha = DecodeGitOutput(treeBytes).Trim();
            if (!IsGitObjectId(treeSha))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an invalid captured output tree identity.");

            var patchBytes = await RunGitAsync(
                expected.RepositoryPath,
                ["diff", "--binary", "--no-ext-diff", "--no-textconv", "--full-index",
                    request.BaseSha, treeSha, "--"],
                MaximumDiffBytes,
                emptyGitConfig,
                remoteUri: null,
                credential: null,
                cancellationToken).ConfigureAwait(false);
            var files = await ReadCapturedFilesAsync(
                expected.RepositoryPath, emptyGitConfig, treeSha, cancellationToken).ConfigureAwait(false);

            return new GitWorkspaceCapture(
                workspace.WorkspaceId,
                workspace.RunId,
                workspace.RepositoryId,
                workspace.ResourceGeneration,
                incarnationId,
                request.BaseSha,
                treeSha,
                DecodeGitOutput(patchBytes),
                files,
                workspace.BranchName);
        }
        finally
        {
            if (File.Exists(privateIndex))
                File.Delete(privateIndex);
            var privateIndexLock = privateIndex + ".lock";
            if (File.Exists(privateIndexLock))
                File.Delete(privateIndexLock);
        }
    }

    public async Task<GitWorkspaceBuildTestSeedDocument> CreateBuildTestSeedAsync(
        GitWorkspaceCaptureRequest request,
        GitWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var capture = await CaptureAsync(request, workspace, cancellationToken).ConfigureAwait(false);
        var baseline = GitWorkspaceCapturePackage.Create(capture);
        var emptyGitConfig = Path.Combine(Path.GetDirectoryName(workspace.Path)!, "git-empty-config");
        var pack = await RunGitAsync(
            workspace.Path,
            ["pack-objects", "--stdout", "--revs", "--delta-base-offset"],
            ProducedRunCaptureLimits.MaximumPackageBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken,
            standardInput: Encoding.ASCII.GetBytes(capture.BaseSha + "\n"),
            outputFailureCode: GitWorkspaceFailureCode.CaptureTooLarge).ConfigureAwait(false);
        var header = Encoding.ASCII.GetBytes(
            "# v2 git bundle\n" + capture.BaseSha.ToLowerInvariant() + " refs/heads/buildtest-base\n\n");
        if ((long)header.Length + pack.Length > ProducedRunCaptureLimits.MaximumPackageBytes)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CaptureTooLarge, "The BuildTest Git bundle exceeds its byte limit.");
        var bundle = new byte[header.Length + pack.Length];
        header.CopyTo(bundle, 0);
        pack.CopyTo(bundle, header.Length);
        var verified = GitWorkspaceCapturePackage.Create(
            await CaptureAsync(request, workspace, cancellationToken).ConfigureAwait(false));
        if (verified.ManifestSha256 != baseline.ManifestSha256 ||
            verified.PackageSha256 != baseline.PackageSha256)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The source workspace changed during BuildTest seed creation.");
        var bytes = GitWorkspaceBuildTestSeedPackage.Create(baseline, bundle);
        return new(baseline, bundle, bytes, GitWorkspaceCapturePackage.Hash(bytes));
    }

    public async Task<GitWorkspace> PrepareBuildTestSeedAsync(
        SandboxBuildTestWorkspaceSource source,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(bytes.Span, source);
        if (!IsValidBranchName(source.BranchName))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.InvalidRequest, "The BuildTest repository branch is invalid.");
        var files = GitWorkspaceBuildTestSeedPackage.ReadBaselineFiles(seed);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (files.Select(file => file.Path).Distinct(comparer).Count() != files.Length)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict, "The BuildTest input paths collide on this filesystem.");
        cancellationToken.ThrowIfCancellationRequested();
        var key = "buildtest-" + Hash(
            source.TenantId + "\0" + source.Identity.ProjectId + "\0" + source.Identity.RunId + "\0" + source.WorkspaceId);
        var workspaceRoot = Path.Combine(_rootPath, key);
        var repositoryPath = Path.Combine(workspaceRoot, "repository");
        var manifestPath = Path.Combine(workspaceRoot, "workspace.json");
        var bindingHash = GitWorkspaceCapturePackage.Hash(JsonSerializer.SerializeToUtf8Bytes(source, JsonOptions));
        var expected = new WorkspaceManifest(
            ManifestVersion, source.Identity.RunId, source.WorkspaceId,
            source.SourceControlResource.ProviderId, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, string.Empty, string.Empty, source.BaseSha,
            source.BranchName, "preparing", source.WorkspaceIncarnationId.ToString("N"), bindingHash);
        Directory.CreateDirectory(_rootPath);
        EnsureNotReparsePoint(_rootPath);
        var lockPath = Path.Combine(_rootPath, key + ".lock");
        if (File.Exists(lockPath))
            EnsureNotReparsePoint(lockPath);
        using var lease = AcquireBuildTestSeedLock(lockPath);
        Directory.CreateDirectory(workspaceRoot);
        EnsureNotReparsePoint(workspaceRoot);
        var manifest = expected;
        if (File.Exists(manifestPath))
        {
            EnsureNotReparsePoint(manifestPath);
            manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest != (expected with { State = manifest.State }) ||
                manifest.State is not ("preparing" or "ready"))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.PathConflict, "The BuildTest preparation belongs to another source.");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(workspaceRoot).Any())
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.PathConflict, "The BuildTest preparation has no ownership manifest.");
            await WriteManifestAsync(manifestPath, expected, cancellationToken).ConfigureAwait(false);
        }
        if (manifest.State == "ready")
        {
            RequireBuildTestSeedRepository(repositoryPath);
            return BuildTestSeedWorkspace(source, repositoryPath);
        }
        var emptyGitConfig = Path.Combine(workspaceRoot, "git-empty-config");
        EnsureEmptyGitConfig(emptyGitConfig);
        var hooks = Path.Combine(workspaceRoot, "git-no-hooks");
        Directory.CreateDirectory(hooks);
        EnsureNotReparsePoint(hooks);
        if (Directory.EnumerateFileSystemEntries(hooks).Any())
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The isolated Git hook directory is not empty.");
        Task<byte[]> GitAsync(string directory, string[] arguments, byte[]? input = null) =>
            RunGitAsync(directory,
                ["-c", "core.hooksPath=" + hooks, "-c", "core.fsmonitor=false",
                    "-c", "protocol.allow=never", "-c", "protocol.file.allow=always",
                    "-c", "maintenance.auto=false", "-c", "gc.auto=0", .. arguments],
                MaximumCommandOutputBytes, emptyGitConfig, null, null, cancellationToken,
                standardInput: input);
        if (Directory.Exists(repositoryPath))
        {
            EnsureNotReparsePoint(repositoryPath);
            if (!Directory.Exists(Path.Combine(repositoryPath, ".git")) &&
                Directory.EnumerateFileSystemEntries(repositoryPath).Any())
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.PathConflict, "The BuildTest repository contains unowned data.");
        }
        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
            await GitAsync(workspaceRoot,
                ["-c", "init.templateDir=", "init", "--initial-branch=" + source.BranchName, repositoryPath])
                .ConfigureAwait(false);
        RequireBuildTestSeedRepository(repositoryPath);
        var expectedPaths = files.Select(file => file.Path).ToHashSet(comparer);
        RequireBuildTestSeedFiles(repositoryPath, expectedPaths, allowMissing: true);
        if (DecodeGitOutput(await GitAsync(repositoryPath, ["remote"]).ConfigureAwait(false)).Length != 0)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The BuildTest input repository has a remote.");
        var bundlePath = Path.Combine(workspaceRoot, "seed.bundle");
        if (File.Exists(bundlePath))
            EnsureNotReparsePoint(bundlePath);
        await File.WriteAllBytesAsync(bundlePath, seed.BundleBytes.ToArray(), cancellationToken).ConfigureAwait(false);
        var heads = DecodeGitOutput(await GitAsync(
            repositoryPath, ["bundle", "list-heads", bundlePath]).ConfigureAwait(false)).Trim();
        if (heads != source.BaseSha + " refs/heads/buildtest-base")
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The BuildTest bundle has a different base reference.");
        await GitAsync(repositoryPath, ["bundle", "verify", bundlePath]).ConfigureAwait(false);
        await GitAsync(repositoryPath,
            ["fetch", "--no-tags", "--no-recurse-submodules", "--", bundlePath, "refs/heads/buildtest-base"])
            .ConfigureAwait(false);
        await GitAsync(repositoryPath, ["update-ref", "refs/heads/" + source.BranchName, source.BaseSha])
            .ConfigureAwait(false);
        foreach (var file in files)
        {
            var path = Path.Combine(repositoryPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var directory = repositoryPath;
            foreach (var segment in file.Path.Split('/').SkipLast(1))
            {
                directory = Path.Combine(directory, segment);
                Directory.CreateDirectory(directory);
                EnsureNotReparsePoint(directory);
            }
            if (File.Exists(path))
                EnsureNotReparsePoint(path);
            await File.WriteAllBytesAsync(path, file.Content.ToArray(), cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, file.Mode == "100755"
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        RequireBuildTestSeedFiles(repositoryPath, expectedPaths);
        await GitAsync(repositoryPath, ["read-tree", "--empty"]).ConfigureAwait(false);
        if (files.Length > 0)
        {
            var paths = string.Concat(files.Select(file =>
                "\"" + Path.Combine(repositoryPath, file.Path.Replace('/', Path.DirectorySeparatorChar))
                    .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"\n"));
            var hashes = DecodeGitOutput(await GitAsync(repositoryPath,
                ["hash-object", "--no-filters", "-w", "--stdin-paths"], Encoding.UTF8.GetBytes(paths))
                .ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (hashes.Length != files.Length || hashes.Any(hash => !IsGitObjectId(hash)))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace, "The BuildTest repository object list is invalid.");
            var index = string.Concat(files.Select((file, position) =>
                file.Mode + " " + hashes[position] + "\t" + file.Path + "\0"));
            await GitAsync(repositoryPath, ["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(index))
                .ConfigureAwait(false);
        }
        var tree = DecodeGitOutput(await GitAsync(repositoryPath, ["write-tree"]).ConfigureAwait(false)).Trim();
        if (tree != source.BaselineTreeSha)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The prepared BuildTest tree differs from its accepted input.");
        await WriteManifestAsync(manifestPath, expected with { State = "ready" }, cancellationToken).ConfigureAwait(false);
        return BuildTestSeedWorkspace(source, repositoryPath);
    }

    private static FileStream AcquireBuildTestSeedLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict, "The BuildTest preparation workspace is unavailable.",
                innerException: exception);
        }
    }

    private static void RequireBuildTestSeedRepository(string path)
    {
        if (!Directory.Exists(path) || !Directory.Exists(Path.Combine(path, ".git")))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The prepared BuildTest repository is missing.");
        EnsureNotReparsePoint(path);
        EnsureNotReparsePoint(Path.Combine(path, ".git"));
    }

    private static void RequireBuildTestSeedFiles(string root, HashSet<string> expected, bool allowMissing = false)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        var found = new HashSet<string>(expected.Comparer);
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Path.GetRelativePath(root, entry) == ".git")
                    continue;
                EnsureNotReparsePoint(entry);
                if (Directory.Exists(entry))
                    directories.Push(entry);
                else
                    found.Add(Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/'));
            }
        if (allowMissing ? !found.IsSubsetOf(expected) : !found.SetEquals(expected))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace, "The BuildTest repository contains undeclared input files.");
    }

    private static GitWorkspace BuildTestSeedWorkspace(SandboxBuildTestWorkspaceSource source, string path) =>
        new(source.WorkspaceId, source.Identity.RunId, source.SourceControlResource.ResourceId,
            source.SourceControlResource.Generation, source.BaseSha, source.BranchName,
            path, source.WorkspaceIncarnationId);

    private async Task<ImmutableArray<GitWorkspaceCapturedFile>> ReadCapturedFilesAsync(
        string repositoryPath,
        string emptyGitConfig,
        string treeSha,
        CancellationToken cancellationToken)
    {
        var treeBytes = await RunGitAsync(
            repositoryPath,
            ["ls-tree", "--full-tree", "-r", "-z", treeSha],
            MaximumWorkspaceTreeBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken).ConfigureAwait(false);
        var entries = ParseTreeEntries(treeBytes);
        if (entries.Count > MaximumCapturedFiles)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CaptureTooLarge,
                "The captured workspace contains too many files.");
        if (entries.Count == 0)
            return ImmutableArray<GitWorkspaceCapturedFile>.Empty;

        var input = Encoding.ASCII.GetBytes(string.Join('\n', entries.Select(entry => entry.ObjectId)) + "\n");
        var output = await RunGitAsync(
            repositoryPath,
            ["cat-file", "--batch"],
            MaximumCapturedBatchBytes,
            emptyGitConfig,
            remoteUri: null,
            credential: null,
            cancellationToken,
            standardInput: input,
            outputFailureCode: GitWorkspaceFailureCode.CaptureTooLarge).ConfigureAwait(false);
        return ParseBlobBatch(entries, output);
    }

    private static List<GitTreeEntry> ParseTreeEntries(byte[] treeBytes)
    {
        var entries = new List<GitTreeEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < treeBytes.Length)
        {
            var end = Array.IndexOf(treeBytes, (byte)0, offset);
            if (end < 0)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an unterminated captured tree entry.");
            var separator = Array.IndexOf(treeBytes, (byte)'\t', offset, end - offset);
            if (separator < 0)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an invalid captured tree entry.");

            var header = Encoding.ASCII.GetString(treeBytes, offset, separator - offset);
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 ||
                parts[1] != "blob" ||
                parts[0] is not ("100644" or "100755" or "120000") ||
                !IsGitObjectId(parts[2]))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "The captured tree contains an unsupported entry.");

            string path;
            try
            {
                path = new UTF8Encoding(false, true).GetString(
                    treeBytes, separator + 1, end - separator - 1);
            }
            catch (DecoderFallbackException exception)
            {
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.PathConflict,
                    "The captured tree contains a path that is not valid UTF-8.",
                    innerException: exception);
            }
            ValidateCapturedPath(path);
            if (!paths.Add(path))
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "The captured tree contains a duplicate path.");
            entries.Add(new(parts[0], parts[2], path));
            offset = end + 1;
        }
        return entries;
    }

    private static ImmutableArray<GitWorkspaceCapturedFile> ParseBlobBatch(
        IReadOnlyList<GitTreeEntry> entries,
        byte[] output)
    {
        var files = ImmutableArray.CreateBuilder<GitWorkspaceCapturedFile>(entries.Count);
        var offset = 0;
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            var headerEnd = Array.IndexOf(output, (byte)'\n', offset);
            if (headerEnd < 0)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an incomplete captured file.");
            var header = Encoding.ASCII.GetString(output, offset, headerEnd - offset)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 3 || header[0] != entry.ObjectId || header[1] != "blob" ||
                !long.TryParse(header[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an invalid captured file header.");
            if (length > MaximumCapturedFileBytes)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CaptureTooLarge,
                    "A captured file exceeded the configured size limit.");
            totalBytes = checked(totalBytes + length);
            if (totalBytes > MaximumCapturedTotalBytes)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CaptureTooLarge,
                    "The captured workspace exceeded the configured size limit.");

            offset = headerEnd + 1;
            if (length > output.Length - offset - 1)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned a truncated captured file.");
            var content = output.AsSpan(offset, checked((int)length)).ToArray();
            offset += checked((int)length);
            if (output[offset++] != (byte)'\n')
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CorruptWorkspace,
                    "Git returned an invalid captured file terminator.");
            files.Add(new(
                entry.Path,
                entry.Mode,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                content.LongLength,
                ImmutableArray.CreateRange(content)));
        }

        if (offset != output.Length)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "Git returned unexpected captured file data.");
        return files.MoveToImmutable();
    }

    private static void ValidateCapturedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path[0] == '/' ||
            path.Contains('\\') ||
            path.Contains(':') ||
            Path.IsPathRooted(path) ||
            path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            string.Equals(path, ".git", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The captured tree contains an invalid relative path.");
    }

    private static bool IsGitObjectId(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

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

        var manifest = CreateManifest(request, "preparing", Guid.NewGuid().ToString("N"));
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
        CancellationToken cancellationToken,
        string? gitIndexFile = null,
        byte[]? standardInput = null,
        GitWorkspaceFailureCode outputFailureCode = GitWorkspaceFailureCode.DiffTooLarge)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
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
        if (gitIndexFile is not null)
            startInfo.Environment["GIT_INDEX_FILE"] = Path.GetFullPath(gitIndexFile);
        var sensitiveValues = new List<string>();
        if (credential is not null)
            sensitiveValues.Add(credential.GetValue());
        if (remoteUri is { Scheme: "https" } &&
            string.Equals(remoteUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (credential is null)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.InvalidRequest,
                    "A broker-redeemed checkout credential is required for the GitHub repository.");
            var authorization = "x-access-token:" + credential.GetValue();
            var encodedAuthorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorization));
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
            process.StandardOutput.BaseStream, maximumOutputBytes, outputFailureCode);
        var errorTask = ReadBoundedOutputAsync(
            process.StandardError.BaseStream, MaximumGitDiagnosticBytes, GitWorkspaceFailureCode.GitFailed);
        var inputTask = standardInput is null
            ? Task.CompletedTask
            : WriteStandardInputAsync(process, standardInput, cancellationToken);
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
            await inputTask.ConfigureAwait(false);
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

    private static async Task WriteStandardInputAsync(
        Process process,
        byte[] input,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.BaseStream.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
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

    private static void ValidateCaptureRequest(GitWorkspaceCaptureRequest request)
    {
        var binding = request.Binding;
        if (binding is null ||
            binding.Seam != ProviderSeam.SourceControl ||
            binding.Resource.Seam != ProviderSeam.SourceControl ||
            !string.Equals(binding.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !string.Equals(binding.Resource.ProviderId, SourceControlProviderIds.GitHub, StringComparison.Ordinal) ||
            !binding.NegotiatedCapabilities.Contains(SourceControlCapabilities.RepositoryCheckout) ||
            string.IsNullOrWhiteSpace(binding.RunId) ||
            request.Repository is null ||
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

    internal static bool IsValidBranchName(string branch) =>
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
        => GetWorkspacePaths(request.Context.Binding.RunId, request.WorkspaceId);

    private (string WorkspaceRoot, string RepositoryPath, string ManifestPath) GetWorkspacePaths(
        GitWorkspaceCaptureRequest request)
        => GetWorkspacePaths(request.Binding.RunId, request.WorkspaceId);

    private (string WorkspaceRoot, string RepositoryPath, string ManifestPath) GetWorkspacePaths(
        string runId,
        string workspaceId)
    {
        var workspaceRoot = Path.Combine(
            _rootPath, Hash(runId + "\0" + workspaceId));
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

    private static WorkspaceManifest CreateManifest(
        GitWorkspaceRequest request,
        string state,
        string workspaceIncarnationId) =>
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
            state,
            workspaceIncarnationId);

    private static void ValidateManifestBinding(WorkspaceManifest manifest, GitWorkspaceRequest request)
    {
        if (!Guid.TryParseExact(manifest.WorkspaceIncarnationId, "N", out _))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace incarnation is invalid.");
        var expected = CreateManifest(request, manifest.State, manifest.WorkspaceIncarnationId);
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

    private static void ValidateManifestBinding(WorkspaceManifest manifest, GitWorkspaceCaptureRequest request)
    {
        if (!Guid.TryParseExact(manifest.WorkspaceIncarnationId, "N", out _))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The source-control workspace incarnation is invalid.");
        if (manifest.Version != ManifestVersion ||
            !string.Equals(manifest.RunId, request.Binding.RunId, StringComparison.Ordinal) ||
            !string.Equals(manifest.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProviderId, request.Binding.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(manifest.RepositoryId, request.Binding.Resource.ResourceId, StringComparison.Ordinal) ||
            manifest.ResourceGeneration != request.Binding.Resource.Generation ||
            !string.Equals(manifest.Owner, request.Repository.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Repository, request.Repository.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.BaseSha, request.BaseSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.BranchName, request.BranchName, StringComparison.Ordinal))
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.PathConflict,
                "The source-control workspace belongs to a different run, repository, or pinned revision.");
    }

    private static GitWorkspace ToWorkspace(
        GitWorkspaceRequest request,
        string repositoryPath,
        WorkspaceManifest manifest) =>
        new(
            request.WorkspaceId,
            request.Context.Binding.RunId,
            request.Context.Binding.Resource.ResourceId,
            request.Context.Binding.Resource.Generation,
            request.BaseSha,
            request.BranchName,
            Path.GetFullPath(repositoryPath),
            Guid.ParseExact(manifest.WorkspaceIncarnationId, "N"));

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
        string State,
        string WorkspaceIncarnationId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BuildTestSeedBindingSha256 = null);

    private sealed record GitTreeEntry(string Mode, string ObjectId, string Path);

    private sealed class GitHubGitRepositoryRemote : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository) =>
            new($"https://github.com/{Uri.EscapeDataString(repository.Owner)}/" +
                $"{Uri.EscapeDataString(repository.Name)}.git");
    }
}
