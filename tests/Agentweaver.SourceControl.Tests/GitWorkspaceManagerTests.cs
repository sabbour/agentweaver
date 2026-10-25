using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed partial class GitWorkspaceManagerTests
{
    [Fact]
    public async Task WorkspaceRevisionLeaseWaitsForManagedRevisionMutationLease()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            const string runId = "run-lease";
            const string workspaceId = "workspace-lease";
            var key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(runId + "\0" + workspaceId)))[..32].ToLowerInvariant();
            Directory.CreateDirectory(Path.Combine(temporaryRoot, key));
            var manager = new GitWorkspaceManager(
                temporaryRoot, new UnavailableGitRepositoryRemote());
            await using var held = await manager.AcquireWorkspaceRevisionLeaseAsync(
                runId, workspaceId, CancellationToken.None);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var waiting = manager.AcquireWorkspaceRevisionLeaseAsync(
                runId, workspaceId, cancellation.Token);

            await Task.Delay(75);
            Assert.False(waiting.IsCompleted);
            await held.DisposeAsync();
            await using var released = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparesBuildTestSeedWithoutCheckoutCredentialsAndNeverReseedsReadyWorkspace(bool empty)
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot, empty);
            var preparationRoot = Path.Combine(temporaryRoot, "buildtest");
            var manager = new GitWorkspaceManager(preparationRoot, new UnavailableGitRepositoryRemote());
            var workspace = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            Assert.NotEqual(fixture.Original.Path, workspace.Path);
            Assert.Equal(fixture.Source.WorkspaceIncarnationId, workspace.WorkspaceIncarnationId);
            Assert.Equal(fixture.Source.BaseSha,
                (await RunFixtureGitAsync(workspace.Path, ["rev-parse", "HEAD"])).Trim());
            Assert.Equal(fixture.Source.BaselineTreeSha,
                (await RunFixtureGitAsync(workspace.Path, ["write-tree"])).Trim());
            Assert.Empty(await RunFixtureGitAsync(workspace.Path, ["remote"]));
            var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Seed.Bytes, fixture.Source);
            foreach (var file in seed.Baseline.Files)
            {
                var path = Path.Combine(workspace.Path, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(GitWorkspaceCapturePackage.ExtractFile(
                    seed.BaselinePackageBytes.ToArray(), seed.Baseline,
                    GitWorkspaceCapturePackage.Hash(seed.BaselinePackageBytes.AsSpan()), file.Path),
                    await File.ReadAllBytesAsync(path));
            }
            Assert.False(File.Exists(Path.Combine(workspace.Path, "delete-me.txt")));
            if (!empty)
                Assert.Contains("100755", await RunFixtureGitAsync(workspace.Path, ["ls-files", "--stage", "--", "run.sh"]));
            Assert.Equal(fixture.OriginalIndex,
                await File.ReadAllBytesAsync(Path.Combine(fixture.Original.Path, ".git", "index")));
            var manifestPath = Path.Combine(Path.GetDirectoryName(workspace.Path)!, "workspace.json");
            var manifestBefore = await File.ReadAllBytesAsync(manifestPath);
            Assert.DoesNotContain("transient-token", Encoding.UTF8.GetString(manifestBefore));
            Assert.DoesNotContain("transient-token",
                await File.ReadAllTextAsync(Path.Combine(workspace.Path, ".git", "config")));

            await File.WriteAllBytesAsync(Path.Combine(workspace.Path, "result.bin"), [255, 0, 1]);
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "README.md"), "command changed input\n");
            var indexBefore = await File.ReadAllBytesAsync(Path.Combine(workspace.Path, ".git", "index"));
            var noGitManager = new GitWorkspaceManager(
                preparationRoot, new UnavailableGitRepositoryRemote(), "missing-buildtest-git-" + Guid.NewGuid().ToString("N"));
            var repeated = await noGitManager.PrepareBuildTestSeedAsync(
                fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            Assert.Equal(workspace, repeated);
            Assert.Equal(new byte[] { 255, 0, 1 }, await File.ReadAllBytesAsync(Path.Combine(workspace.Path, "result.bin")));
            Assert.Equal("command changed input\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "README.md")));
            Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(manifestPath));
            Assert.Equal(indexBefore, await File.ReadAllBytesAsync(Path.Combine(workspace.Path, ".git", "index")));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationResumesOnlyItsOwnIncompleteCopy()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var manager = new GitWorkspaceManager(Path.Combine(temporaryRoot, "buildtest"), new UnavailableGitRepositoryRemote());
            var workspace = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            await SetPreparationStateAsync(workspace.Path, "preparing");
            File.Delete(Path.Combine(workspace.Path, "README.md"));
            File.Delete(Path.Combine(workspace.Path, ".git", "index"));
            var resumed = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            Assert.Equal(workspace, resumed);
            Assert.Equal("dirty input\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "README.md")));
            Assert.Equal(fixture.Source.BaselineTreeSha,
                (await RunFixtureGitAsync(workspace.Path, ["write-tree"])).Trim());
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(workspace.Path)!, "workspace.json")))!;
            Assert.Equal("ready", manifest["state"]!.GetValue<string>());
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationRejectsUndeclaredDataBeforeOverwritingInput()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var manager = new GitWorkspaceManager(Path.Combine(temporaryRoot, "buildtest"), new UnavailableGitRepositoryRemote());
            var workspace = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            await SetPreparationStateAsync(workspace.Path, "preparing");
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "README.md"), "preserve changed data\n");
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "unowned.txt"), "preserve unowned data\n");
            var index = await File.ReadAllBytesAsync(Path.Combine(workspace.Path, ".git", "index"));
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.CorruptWorkspace, exception.Code);
            Assert.Equal("preserve changed data\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "README.md")));
            Assert.Equal("preserve unowned data\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "unowned.txt")));
            Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(workspace.Path, ".git", "index")));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationRejectsChangedBindingAndConcurrentPreparation()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var preparationRoot = Path.Combine(temporaryRoot, "buildtest");
            var manager = new GitWorkspaceManager(preparationRoot, new UnavailableGitRepositoryRemote());
            var workspace = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            var changed = fixture.Source with { ActorSubject = "other-actor" };
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(changed, fixture.Seed.Bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
            var lockPath = Directory.GetFiles(preparationRoot, "*.lock").Single();
            using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
            Assert.Equal("dirty input\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "README.md")));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationRejectsCorruptionAndCancellationBeforeCreatingPaths()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var preparationRoot = Path.Combine(temporaryRoot, "buildtest");
            var manager = new GitWorkspaceManager(preparationRoot, new UnavailableGitRepositoryRemote());
            var corrupt = fixture.Seed.Bytes.ToArray();
            corrupt[^1] ^= 1;
            await Assert.ThrowsAsync<ArgumentException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, corrupt, CancellationToken.None));
            Assert.False(Directory.Exists(preparationRoot));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, new CancellationToken(canceled: true)));
            Assert.False(Directory.Exists(preparationRoot));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationRejectsBundleWithADifferentReferenceBeforeCopyingFiles()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var bundlePath = Path.Combine(temporaryRoot, "different-reference.bundle");
            await RunFixtureGitAsync(fixture.Original.Path, ["bundle", "create", bundlePath, "HEAD"]);
            var bundle = await File.ReadAllBytesAsync(bundlePath);
            var bytes = GitWorkspaceBuildTestSeedPackage.Create(fixture.Seed.Baseline, bundle);
            var source = fixture.Source with
            {
                SeedSha256 = GitWorkspaceCapturePackage.Hash(bytes),
                SeedByteLength = bytes.Length,
                BundleSha256 = GitWorkspaceCapturePackage.Hash(bundle),
                BundleByteLength = bundle.Length
            };
            var preparationRoot = Path.Combine(temporaryRoot, "buildtest");
            var manager = new GitWorkspaceManager(preparationRoot, new UnavailableGitRepositoryRemote());
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(source, bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.CorruptWorkspace, exception.Code);
            var repositoryPath = Path.Combine(Directory.GetDirectories(preparationRoot).Single(), "repository");
            Assert.False(File.Exists(Path.Combine(repositoryPath, "README.md")));
            Assert.False(File.Exists(Path.Combine(repositoryPath, "delete-me.txt")));
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(repositoryPath)!, "workspace.json")))!;
            Assert.Equal("preparing", manifest["state"]!.GetValue<string>());
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationPreservesWorkspaceWithoutOwnershipManifest()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var preparationRoot = Path.Combine(temporaryRoot, "buildtest");
            var key = "buildtest-" + Hash(fixture.Source.TenantId + "\0" + fixture.Source.Identity.ProjectId +
                "\0" + fixture.Source.Identity.RunId + "\0" + fixture.Source.WorkspaceId);
            var directory = Path.Combine(preparationRoot, key);
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, "unowned.txt");
            await File.WriteAllTextAsync(marker, "keep\n");
            var manager = new GitWorkspaceManager(preparationRoot, new UnavailableGitRepositoryRemote());
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
            Assert.Equal("keep\n", await File.ReadAllTextAsync(marker));
            Assert.False(File.Exists(Path.Combine(directory, "workspace.json")));
            Assert.False(Directory.Exists(Path.Combine(directory, "repository")));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task BuildTestPreparationRejectsLinkedDirectoryWithoutWritingThroughIt()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        string? linkedPath = null;
        try
        {
            var fixture = await CreateBuildTestSeedFixtureAsync(temporaryRoot);
            var manager = new GitWorkspaceManager(Path.Combine(temporaryRoot, "buildtest"), new UnavailableGitRepositoryRemote());
            var workspace = await manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None);
            await SetPreparationStateAsync(workspace.Path, "preparing");
            linkedPath = Path.Combine(workspace.Path, "nested");
            File.Delete(Path.Combine(linkedPath, "space \u00e9.bin"));
            Directory.Delete(linkedPath);
            var target = Path.Combine(temporaryRoot, "link-target");
            Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "space \u00e9.bin");
            await File.WriteAllBytesAsync(marker, [7, 8, 9]);
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { "/c", "mklink", "/J", linkedPath, target })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await output + await errors);
            }
            else
            {
                Directory.CreateSymbolicLink(linkedPath, target);
            }
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(() =>
                manager.PrepareBuildTestSeedAsync(fixture.Source, fixture.Seed.Bytes, CancellationToken.None));
            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(marker));
        }
        finally
        {
            if (linkedPath is not null && Directory.Exists(linkedPath) &&
                (File.GetAttributes(linkedPath) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(linkedPath);
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task ExportsCredentialFreeSeedWithARealCompleteBaseBundleAndFullInventory()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var sourceRepository = Path.Combine(temporaryRoot, "source-repository");
            var baseSha = await CreateGitRepositoryAsync(sourceRepository);
            var manager = new GitWorkspaceManager(
                Path.Combine(temporaryRoot, "workspaces"),
                new LocalGitRepositoryRemote(new Uri(sourceRepository)));
            var request = CreateRequest("seed-run", "seed-workspace", baseSha);
            var workspace = await manager.PrepareAsync(request, CancellationToken.None);
            var captureRequest = new GitWorkspaceCaptureRequest(
                request.Context.Binding, request.Context.Repository,
                request.WorkspaceId, request.BaseSha, request.BranchName);
            var index = Path.Combine(workspace.Path, ".git", "index");
            var indexBefore = await File.ReadAllBytesAsync(index);
            var seed = await manager.CreateBuildTestSeedAsync(captureRequest, workspace, CancellationToken.None);
            var bundlePath = Path.Combine(temporaryRoot, "base.bundle");
            await File.WriteAllBytesAsync(bundlePath, seed.BundleBytes);
            await RunFixtureGitAsync(workspace.Path, ["bundle", "verify", bundlePath]);
            var clone = Path.Combine(temporaryRoot, "seed-clone");
            await RunFixtureGitAsync(
                temporaryRoot, ["-c", "core.autocrlf=false", "clone", "--branch", "buildtest-base", bundlePath, clone]);

            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(workspace.Path, "README.md")),
                await File.ReadAllBytesAsync(Path.Combine(clone, "README.md")));
            Assert.Equal(baseSha, seed.Baseline.Manifest.BaseSha);
            Assert.Equal(2, seed.Baseline.Manifest.Files.Length);
            Assert.Equal(indexBefore, await File.ReadAllBytesAsync(index));
            Assert.Equal(seed.Sha256, GitWorkspaceCapturePackage.Hash(seed.Bytes));
            Assert.DoesNotContain("transient-token", Encoding.UTF8.GetString(seed.BundleBytes));
            Assert.DoesNotContain("transient-token", Encoding.UTF8.GetString(seed.Baseline.ManifestBytes));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task CreatesIsolatedRunWorkspacesAndRecoversOwnedWorkspace()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var sourceRepository = Path.Combine(temporaryRoot, "source-repository");
            var baseSha = await CreateGitRepositoryAsync(sourceRepository);
            var workspaceRoot = Path.Combine(temporaryRoot, "workspaces");
            var manager = new GitWorkspaceManager(
                workspaceRoot,
                new LocalGitRepositoryRemote(new Uri(sourceRepository)));
            var firstRequest = CreateRequest("run-1", "agent-1", baseSha);

            var firstWorkspace = await manager.PrepareAsync(firstRequest, CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(firstWorkspace.Path, "README.md"), "updated in run one\n");
            await File.WriteAllTextAsync(
                Path.Combine(firstWorkspace.Path, "new-file.txt"), "untracked content\n");
            var diff = await manager.AssembleDiffAsync(
                firstRequest, firstWorkspace, CancellationToken.None);

            Assert.Contains("updated in run one", diff.Patch, StringComparison.Ordinal);
            Assert.Contains("new-file.txt", diff.Patch, StringComparison.Ordinal);
            Assert.Contains("M README.md", diff.Status, StringComparison.Ordinal);
            Assert.Contains("new-file.txt", diff.Status, StringComparison.Ordinal);

            var recovered = await manager.PrepareAsync(firstRequest, CancellationToken.None);
            Assert.Equal(firstWorkspace.Path, recovered.Path);
            var recoveredDiff = await manager.AssembleDiffAsync(
                firstRequest, recovered, CancellationToken.None);
            Assert.Equal(diff.Patch, recoveredDiff.Patch);

            var secondRequest = CreateRequest("run-2", "agent-1", baseSha);
            var secondWorkspace = await manager.PrepareAsync(secondRequest, CancellationToken.None);
            Assert.NotEqual(firstWorkspace.Path, secondWorkspace.Path);
            var secondDiff = await manager.AssembleDiffAsync(
                secondRequest, secondWorkspace, CancellationToken.None);
            Assert.Empty(secondDiff.Status);
            Assert.Empty(secondDiff.Patch);

            var manifest = await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(firstWorkspace.Path)!, "workspace.json"));
            var gitConfig = await File.ReadAllTextAsync(
                Path.Combine(firstWorkspace.Path, ".git", "config"));
            Assert.DoesNotContain("transient-token", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("transient-token", gitConfig, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task CapturesDirtyDeletedAndUntrackedFilesWithoutChangingGitIndex()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var sourceRepository = Path.Combine(temporaryRoot, "source-repository");
            var baseSha = await CreateGitRepositoryAsync(sourceRepository);
            var manager = new GitWorkspaceManager(
                Path.Combine(temporaryRoot, "workspaces"),
                new LocalGitRepositoryRemote(new Uri(sourceRepository)));
            var request = CreateRequest("run-capture", "workspace-capture", baseSha);
            var workspace = await manager.PrepareAsync(request, CancellationToken.None);

            var modifiedContent = Encoding.UTF8.GetBytes("updated README\n");
            var untrackedContent = Encoding.UTF8.GetBytes("new output\n");
            await File.WriteAllBytesAsync(
                Path.Combine(workspace.Path, "README.md"), modifiedContent);
            File.Delete(Path.Combine(workspace.Path, "delete-me.txt"));
            await File.WriteAllBytesAsync(
                Path.Combine(workspace.Path, "new-output.txt"), untrackedContent);

            var indexPath = Path.Combine(workspace.Path, ".git", "index");
            var indexBeforeCapture = await File.ReadAllBytesAsync(indexPath);
            var captureRequest = new GitWorkspaceCaptureRequest(
                request.Context.Binding, request.Context.Repository,
                request.WorkspaceId, request.BaseSha, request.BranchName);
            var reopened = await manager.OpenExistingAsync(captureRequest, CancellationToken.None);
            Assert.Equal(workspace.Path, reopened.Path);
            var capture = await manager.CaptureAsync(
                captureRequest, reopened, CancellationToken.None);
            var document = GitWorkspaceCapturePackage.Create(capture);
            var manifest = GitWorkspaceCapturePackage.ParseManifest(document.ManifestBytes);
            GitWorkspaceCapturePackage.VerifyPackage(
                document.PackageBytes, manifest, document.PackageSha256);
            var indexAfterCapture = await File.ReadAllBytesAsync(indexPath);
            var files = capture.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var manifestFiles = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);

            Assert.Equal(indexBeforeCapture, indexAfterCapture);
            Assert.Equal(request.BaseSha, capture.BaseSha);
            Assert.Equal(40, capture.OutputTreeSha.Length);
            Assert.Equal(request.BranchName, manifest.BranchName);
            Assert.Equal(
                new[] { "README.md", "new-output.txt" },
                files.Keys.Order(StringComparer.Ordinal).ToArray());
            AssertCapturedFile(files["README.md"], "100644", modifiedContent);
            AssertCapturedFile(files["new-output.txt"], "100644", untrackedContent);
            Assert.Equal(document.ManifestSha256, GitWorkspaceCapturePackage.Hash(document.ManifestBytes));
            Assert.Equal(document.PatchSha256, GitWorkspaceCapturePackage.Hash(document.PatchBytes));
            Assert.Equal(document.PackageSha256, GitWorkspaceCapturePackage.Hash(document.PackageBytes));
            Assert.Equal(files["README.md"].Sha256, manifestFiles["README.md"].Sha256);
            Assert.Equal(files["new-output.txt"].ByteLength, manifestFiles["new-output.txt"].ByteLength);
            Assert.Equal(
                modifiedContent,
                GitWorkspaceCapturePackage.ExtractFile(
                    document.PackageBytes, manifest, document.PackageSha256, "README.md"));
            Assert.Contains("delete-me.txt", capture.Patch, StringComparison.Ordinal);
            Assert.Contains("new-output.txt", capture.Patch, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(workspace.Path)!,
                "capture-*.index*"));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task RedactsCheckoutCredentialFromGitDiagnostics()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        var credentialValue = "transient-token-" + Guid.NewGuid().ToString("N");
        try
        {
            var request = CreateRequest(
                "run-redaction",
                "workspace-redaction",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            request = request with
            {
                Context = new SourceControlOperationContext(
                    request.Context.Binding,
                    request.Context.Repository,
                    new SecretCredential(
                        credentialValue, DateTimeOffset.UtcNow.AddMinutes(3)))
            };
            var remotePath = Path.Combine(temporaryRoot, "missing-" + credentialValue);
            var manager = new GitWorkspaceManager(
                Path.Combine(temporaryRoot, "workspaces"),
                new LocalGitRepositoryRemote(new Uri(remotePath)));

            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(
                () => manager.PrepareAsync(request, CancellationToken.None));

            Assert.Equal(GitWorkspaceFailureCode.GitFailed, exception.Code);
            Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(credentialValue, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task PreservesUnownedWorkspacePathInsteadOfDeletingIt()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var sourceRepository = Path.Combine(temporaryRoot, "source-repository");
            var baseSha = await CreateGitRepositoryAsync(sourceRepository);
            var workspaceRoot = Path.Combine(temporaryRoot, "workspaces");
            var request = CreateRequest("run-1", "agent-1", baseSha);
            var workspacePath = Path.Combine(
                workspaceRoot,
                Hash(request.Context.Binding.RunId + "\0" + request.WorkspaceId));
            Directory.CreateDirectory(workspacePath);
            var marker = Path.Combine(workspacePath, "user-data.txt");
            await File.WriteAllTextAsync(marker, "keep");

            var manager = new GitWorkspaceManager(
                workspaceRoot,
                new LocalGitRepositoryRemote(new Uri(sourceRepository)));
            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(
                () => manager.PrepareAsync(request, CancellationToken.None));

            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
            Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    [Fact]
    public async Task RejectsWorkspaceReferenceFromDifferentRun()
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var sourceRepository = Path.Combine(temporaryRoot, "source-repository");
            var baseSha = await CreateGitRepositoryAsync(sourceRepository);
            var manager = new GitWorkspaceManager(
                Path.Combine(temporaryRoot, "workspaces"),
                new LocalGitRepositoryRemote(new Uri(sourceRepository)));
            var original = CreateRequest("run-1", "agent-1", baseSha);
            var workspace = await manager.PrepareAsync(original, CancellationToken.None);
            var otherRun = CreateRequest("run-2", "agent-1", baseSha);

            var exception = await Assert.ThrowsAsync<GitWorkspaceException>(
                () => manager.AssembleDiffAsync(otherRun, workspace, CancellationToken.None));

            Assert.Equal(GitWorkspaceFailureCode.PathConflict, exception.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task<BuildTestSeedFixture> CreateBuildTestSeedFixtureAsync(string root, bool empty = false)
    {
        var sourceRepository = Path.Combine(root, "source-repository");
        _ = await CreateGitRepositoryAsync(sourceRepository);
        await File.WriteAllTextAsync(Path.Combine(sourceRepository, "run.sh"), "#!/bin/sh\nexit 0\n");
        await RunFixtureGitAsync(sourceRepository, ["add", "--", "run.sh"]);
        await RunFixtureGitAsync(sourceRepository, ["update-index", "--chmod=+x", "--", "run.sh"]);
        await RunFixtureGitAsync(sourceRepository, ["commit", "-m", "executable input"]);
        var baseSha = (await RunFixtureGitAsync(sourceRepository, ["rev-parse", "HEAD"])).Trim();
        var manager = new GitWorkspaceManager(Path.Combine(root, "workspaces"),
            new LocalGitRepositoryRemote(new Uri(sourceRepository)));
        var request = CreateRequest("buildtest-run", "buildtest-workspace", baseSha);
        var workspace = await manager.PrepareAsync(request, CancellationToken.None);
        File.Delete(Path.Combine(workspace.Path, "delete-me.txt"));
        if (empty)
        {
            File.Delete(Path.Combine(workspace.Path, "README.md"));
            File.Delete(Path.Combine(workspace.Path, "run.sh"));
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "README.md"), "dirty input\n");
            Directory.CreateDirectory(Path.Combine(workspace.Path, "nested"));
            await File.WriteAllBytesAsync(Path.Combine(workspace.Path, "nested", "space \u00e9.bin"), [0, 255, 13, 10, 42]);
        }
        var originalIndex = await File.ReadAllBytesAsync(Path.Combine(workspace.Path, ".git", "index"));
        var capture = new GitWorkspaceCaptureRequest(request.Context.Binding, request.Context.Repository,
            request.WorkspaceId, request.BaseSha, request.BranchName);
        var seed = await manager.CreateBuildTestSeedAsync(capture, workspace, CancellationToken.None);
        var source = new SandboxBuildTestWorkspaceSource(
            2, new("project", request.Context.Binding.RunId, "session"), "tenant", "issuer", "actor",
            "source-pin", new string('a', 64), request.Context.Binding.Resource,
            request.WorkspaceId, workspace.WorkspaceIncarnationId, request.BranchName, baseSha,
            seed.Baseline.Manifest.OutputTreeSha, "seed-reference", seed.Sha256, seed.Bytes.Length,
            GitWorkspaceCapturePackage.Hash(seed.BundleBytes), seed.BundleBytes.Length,
            seed.Baseline.ManifestSha256, seed.Baseline.ManifestBytes.Length);
        return new(seed, source, workspace, originalIndex);
    }

    private static async Task SetPreparationStateAsync(string repositoryPath, string state)
    {
        var path = Path.Combine(Path.GetDirectoryName(repositoryPath)!, "workspace.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        manifest["state"] = state;
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
    }

    private sealed record BuildTestSeedFixture(
        GitWorkspaceBuildTestSeedDocument Seed,
        SandboxBuildTestWorkspaceSource Source,
        GitWorkspace Original,
        byte[] OriginalIndex);

    private sealed class UnavailableGitRepositoryRemote : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository) =>
            throw new InvalidOperationException("BuildTest preparation must not resolve a repository remote.");
    }

    private static GitWorkspaceRequest CreateRequest(
        string runId,
        string workspaceId,
        string baseSha)
    {
        var descriptor = GitHubSourceControlAdapter.CreateDescriptor();
        var registration = new ProviderRegistration(descriptor, true, "options-v1", 1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []).Value!;
        var resolver = new ProviderResolver(catalog);
        var candidate = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))).Value!.Candidate!;
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout);
        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl, SourceControlProviderIds.GitHub, "123", 123);
        var binding = resolver.Pin(
            runId,
            candidate,
            resource.ResourceId,
            new ResourceNegotiation(resource, capabilities)).Value!;
        var context = new SourceControlOperationContext(
            binding,
            new SourceControlRepositoryIdentity("octo", "widget"),
            new SecretCredential("transient-token", DateTimeOffset.UtcNow.AddHours(1)));
        return new GitWorkspaceRequest(context, workspaceId, baseSha, "agent/" + runId);
    }

    private static async Task<string> CreateGitRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        await RunFixtureGitAsync(path, ["init", "--initial-branch=main"]);
        await RunFixtureGitAsync(path, ["config", "--local", "user.name", "SourceControl Test"]);
        await RunFixtureGitAsync(path, ["config", "--local", "user.email", "source-control@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "original\n");
        await File.WriteAllTextAsync(Path.Combine(path, "delete-me.txt"), "delete me\n");
        await RunFixtureGitAsync(path, ["add", "--", "README.md", "delete-me.txt"]);
        await RunFixtureGitAsync(path, ["commit", "-m", "initial"]);
        var sha = (await RunFixtureGitAsync(path, ["rev-parse", "--verify", "HEAD^{commit}"])).Trim();
        Assert.Equal(40, sha.Length);
        return sha;
    }

    private static async Task<string> RunFixtureGitAsync(string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            startInfo.Environment.Remove(key);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fixture process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        _ = await errorTask;
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentweaver-sourcecontrol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private static void AssertCapturedFile(
        GitWorkspaceCapturedFile file,
        string expectedMode,
        byte[] expectedContent)
    {
        Assert.Equal(expectedMode, file.Mode);
        Assert.Equal(expectedContent.LongLength, file.ByteLength);
        Assert.Equal(expectedContent, file.Content.AsSpan().ToArray());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedContent)).ToLowerInvariant(),
            file.Sha256);
    }

    private sealed class LocalGitRepositoryRemote(Uri origin) : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository)
        {
            Assert.Equal("octo/widget", repository.FullName);
            return origin;
        }
    }
}
