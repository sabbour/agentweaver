using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed class GitWorkspaceManagerTests
{
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
        await RunFixtureGitAsync(path, ["add", "--", "README.md"]);
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

    private sealed class LocalGitRepositoryRemote(Uri origin) : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository)
        {
            Assert.Equal("octo/widget", repository.FullName);
            return origin;
        }
    }
}
