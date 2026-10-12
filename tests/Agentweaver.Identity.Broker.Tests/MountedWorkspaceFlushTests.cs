using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class MountedWorkspaceFlushTests
{
    [Fact]
    public async Task TreeHashIsStableAndChangesWithWorkspaceContent()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "aw-flush-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "aw-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(firstRoot, "nested"));
        Directory.CreateDirectory(Path.Combine(secondRoot, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(firstRoot, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(firstRoot, "nested", "b.txt"), "bravo");
            File.WriteAllText(Path.Combine(secondRoot, "nested", "b.txt"), "bravo");
            File.WriteAllText(Path.Combine(secondRoot, "a.txt"), "alpha");

            var firstHash = await MountedWorkspaceFlush.ComputeTreeSha256Async(
                firstRoot, CancellationToken.None);
            var secondHash = await MountedWorkspaceFlush.ComputeTreeSha256Async(
                secondRoot, CancellationToken.None);
            Assert.Equal(firstHash, secondHash);
            Assert.Matches("^[0-9a-f]{64}$", firstHash);

            File.WriteAllText(Path.Combine(secondRoot, "nested", "b.txt"), "changed");
            Assert.NotEqual(firstHash, await MountedWorkspaceFlush.ComputeTreeSha256Async(
                secondRoot, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FlushDoesNotCreateEvidenceWhenLinuxSyncfsIsUnavailable()
    {
        if (OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), "aw-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
                MountedWorkspaceFlush.FlushAndCreateReceiptAsync(
                    root, CreateRequest(), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkspaceVolumeFlushRequest CreateRequest()
    {
        var spec = new WorkspaceVolumeSpec(
            "workspace",
            "project",
            new(WorkspaceVolumeOwnerKind.Run, "run"),
            "environment",
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteOnce,
            1,
            "test-storage",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Delete,
            []);
        return new(
            spec,
            1,
            new(new("tenant", "project", "run", "environment"), 1),
            new(ProviderSeam.Storage, "test-storage", "workspace-resource", 1),
            0,
            "flush-operation");
    }
}
