using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for LocalFilesystemWorkspaceProvider and PersistentVolumeWorkspaceProvider.
/// Uses isolated temp directories — no mocks needed.
/// </summary>
public sealed class WorkspaceProviderTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public WorkspaceProviderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    private string NewDir(bool create = false)
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    // =========================================================================
    // Local: WP-01 — ResolveWorkingDirectoryAsync returns canonical full path
    // =========================================================================
    [Fact]
    public async Task Local_ResolveWorkingDirectory_ReturnsCanonicalPath()
    {
        var provider = new LocalFilesystemWorkspaceProvider();
        var dir      = NewDir();

        var resolved = await provider.ResolveWorkingDirectoryAsync(ProjectId.New(), dir);

        resolved.Should().Be(Path.GetFullPath(dir));
    }

    // =========================================================================
    // Local: WP-02 — EnsureWorkspaceAsync creates the directory
    // =========================================================================
    [Fact]
    public async Task Local_EnsureWorkspace_CreatesDirectory()
    {
        var provider = new LocalFilesystemWorkspaceProvider();
        var dir      = NewDir(create: false);

        await provider.EnsureWorkspaceAsync(ProjectId.New(), dir);

        Directory.Exists(dir).Should().BeTrue();
    }

    // =========================================================================
    // Local: WP-03 — IsAvailable returns true for existing directory
    // =========================================================================
    [Fact]
    public void Local_IsAvailable_TrueForExistingDirectory()
    {
        var provider = new LocalFilesystemWorkspaceProvider();
        var dir      = NewDir(create: true);

        provider.IsAvailable(dir).Should().BeTrue();
    }

    // =========================================================================
    // Local: WP-04 — IsAvailable returns false for missing directory
    // =========================================================================
    [Fact]
    public void Local_IsAvailable_FalseForMissingDirectory()
    {
        var provider = new LocalFilesystemWorkspaceProvider();
        var missing  = NewDir(create: false);

        provider.IsAvailable(missing).Should().BeFalse();
    }

    // =========================================================================
    // Local: WP-05 — ReleaseAsync is a no-op (completes without error)
    // =========================================================================
    [Fact]
    public async Task Local_Release_IsNoOp()
    {
        var provider = new LocalFilesystemWorkspaceProvider();
        var dir      = NewDir(create: true);

        var act = async () => await provider.ReleaseAsync(ProjectId.New(), dir);

        await act.Should().NotThrowAsync();
    }

    // =========================================================================
    // Persistent volume: WP-06 — IsAvailable returns false for missing mount path
    // =========================================================================
    [Fact]
    public void PersistentVolume_IsAvailable_FalseWhenPathMissing()
    {
        var mountRoot  = NewDir(create: true);
        var provider   = BuildPersistentVolumeProvider(mountRoot);
        var missingDir = NewDir(create: false); // does not exist

        provider.IsAvailable(missingDir).Should().BeFalse();
    }

    // =========================================================================
    // Persistent volume: WP-07 — ResolveWorkingDirectoryAsync returns
    //   <MountRoot>/<projectId> (ignores requestedPath)
    // =========================================================================
    [Fact]
    public async Task PersistentVolume_Resolve_ReturnsProjectIdSubdirectory()
    {
        var mountRoot  = NewDir(create: true);
        var provider   = BuildPersistentVolumeProvider(mountRoot);
        var projectId  = ProjectId.New();

        var resolved = await provider.ResolveWorkingDirectoryAsync(projectId, "ignored-path");

        resolved.Should().Be(Path.GetFullPath(Path.Combine(mountRoot, projectId.ToString())));
    }

    // =========================================================================
    // Persistent volume: WP-08 — EnsureWorkspaceAsync throws when path missing
    // =========================================================================
    [Fact]
    public async Task PersistentVolume_EnsureWorkspace_ThrowsWhenPathMissing()
    {
        var mountRoot  = NewDir(create: true);
        var provider   = BuildPersistentVolumeProvider(mountRoot);
        var projectId  = ProjectId.New();
        var missingDir = NewDir(create: false);

        var act = async () => await provider.EnsureWorkspaceAsync(projectId, missingDir);

        await act.Should().ThrowAsync<WorkspaceUnavailableException>();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static PersistentVolumeWorkspaceProvider BuildPersistentVolumeProvider(string mountRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workspace:PersistentVolume:MountRoot"] = mountRoot
            })
            .Build();
        return new PersistentVolumeWorkspaceProvider(config);
    }

    // =========================================================================
    // WP-09 — PersistentVolume.IsMountRootHealthy returns true when mount present + writable
    // =========================================================================
    [Fact]
    public void PersistentVolume_IsMountRootHealthy_TrueWhenPresentAndWritable()
    {
        var mountRoot = NewDir(create: true);
        var provider  = BuildPersistentVolumeProvider(mountRoot);

        provider.IsMountRootHealthy().Should().BeTrue();
    }

    // =========================================================================
    // WP-10 — PersistentVolume.IsMountRootHealthy returns false when mount missing
    // =========================================================================
    [Fact]
    public void PersistentVolume_IsMountRootHealthy_FalseWhenMountMissing()
    {
        var mountRoot = NewDir(create: false); // does not exist
        var provider  = BuildPersistentVolumeProvider(mountRoot);

        provider.IsMountRootHealthy().Should().BeFalse();
    }

    // =========================================================================
    // WP-11 — Local.IsMountRootHealthy always returns true
    // =========================================================================
    [Fact]
    public void Local_IsMountRootHealthy_AlwaysTrue()
    {
        var provider = new LocalFilesystemWorkspaceProvider();

        provider.IsMountRootHealthy().Should().BeTrue();
    }

    // =========================================================================
    // WP-12 — Local.AutoAssignsPath is false (honours caller-supplied path)
    // =========================================================================
    [Fact]
    public void Local_AutoAssignsPath_IsFalse()
    {
        var provider = new LocalFilesystemWorkspaceProvider();

        provider.AutoAssignsPath.Should().BeFalse();
    }

    // =========================================================================
    // WP-13 — PersistentVolume.AutoAssignsPath is true (ignores caller-supplied path)
    // =========================================================================
    [Fact]
    public void PersistentVolume_AutoAssignsPath_IsTrue()
    {
        var mountRoot = NewDir(create: true);
        var provider  = BuildPersistentVolumeProvider(mountRoot);

        provider.AutoAssignsPath.Should().BeTrue();
    }
}
