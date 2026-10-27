using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeHostWorkspaceMountObservationTests
{
    [LinuxOnlyFact]
    public void ObserveUsesRealProcMountAndRejectsRootAndOrdinaryDirectoryFallbacks()
    {
        var observer = new LinuxRuntimeWorkspaceMountObserver();
        var observedAt = DateTimeOffset.UtcNow;
        var proc = observer.Observe("/proc", expectedReadOnly: false, "linux-test-pod", observedAt);

        Assert.Equal("/proc", proc.MountPath);
        Assert.Equal("proc", proc.FileSystemType);
        Assert.NotEqual(0UL, proc.MountId);
        Assert.False(proc.ReadOnly);
        Assert.Equal(observedAt, proc.ObservedAt);
        Assert.Matches("^[0-9a-f]{64}$", proc.MountInfoSha256);

        var rootFailure = Assert.Throws<RuntimeAuthorizationException>(() =>
            observer.Observe("/", expectedReadOnly: false, "linux-test-pod", observedAt));
        Assert.Equal("runtime_workspace_mount_mismatch", rootFailure.Code);

        var ordinaryDirectory = Directory.CreateTempSubdirectory("agentweaver-mount-observer-");
        try
        {
            var directoryFailure = Assert.Throws<RuntimeAuthorizationException>(() =>
                observer.Observe(ordinaryDirectory.FullName, expectedReadOnly: false, "linux-test-pod", observedAt));
            Assert.Equal("runtime_workspace_mount_mismatch", directoryFailure.Code);
        }
        finally
        {
            ordinaryDirectory.Delete();
        }
    }

    [Fact]
    public void ReadReceiptUsesOpenedDirectoryMountAndDoesNotExposeMountSource()
    {
        var source = "classified-storage-source";
        var mountInfo = MountLine(42, "0:48", "/workspace", "rw", source);

        var receipt = LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
            mountInfo, "/workspace", expectedReadOnly: false, "pod-uid",
            DateTimeOffset.UtcNow, 42, 0, 48, 1234);

        Assert.Equal(42UL, receipt.MountId);
        Assert.Equal(0U, receipt.DeviceMajor);
        Assert.Equal(48U, receipt.DeviceMinor);
        Assert.False(receipt.ReadOnly);
        Assert.Equal("ext4", receipt.FileSystemType);
        Assert.DoesNotContain(source, System.Text.Json.JsonSerializer.Serialize(receipt));
        Assert.Matches("^[0-9a-f]{64}$", receipt.MountInfoSha256);
    }

    [Fact]
    public void ReadReceiptDecodesMountPointEscapesExactly()
    {
        var mountInfo = MountLine(42, "0:48", "/workspace\\040data", "rw", "opaque-source");

        var receipt = LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
            mountInfo, "/workspace data", expectedReadOnly: false, "pod-uid",
            DateTimeOffset.UtcNow, 42, 0, 48, 1234);

        Assert.Equal("/workspace data", receipt.MountPath);
    }

    [Fact]
    public void ReadReceiptIncludesReadOnlySuperblockState()
    {
        var mountInfo = MountLine(42, "0:48", "/workspace", "rw", "opaque-source",
            superblockMode: "ro");

        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
                mountInfo, "/workspace", expectedReadOnly: false, "pod-uid",
                DateTimeOffset.UtcNow, 42, 0, 48, 1234));
        Assert.Equal("runtime_workspace_mount_mismatch", failure.Code);

        var receipt = LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
            mountInfo, "/workspace", expectedReadOnly: true, "pod-uid",
            DateTimeOffset.UtcNow, 42, 0, 48, 1234);
        Assert.True(receipt.ReadOnly);
    }

    [Theory]
    [InlineData("/workspace", "0:49", 42UL, false, "runtime_workspace_mount_mismatch")]
    [InlineData("/workspace", "0:48", 41UL, false, "runtime_workspace_mount_mismatch")]
    [InlineData("/workspace", "0:48", 42UL, true, "runtime_workspace_mount_mismatch")]
    [InlineData("/", "0:1", 1UL, false, "runtime_workspace_mount_mismatch")]
    public void ReadReceiptRejectsUnprovenMount(
        string workspacePath, string observedDevice, ulong observedMount, bool expectedReadOnly, string code)
    {
        var mountInfo = MountLine(42, "0:48", "/workspace", "rw", "opaque-source");
        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
                mountInfo, workspacePath, expectedReadOnly, "pod-uid",
                DateTimeOffset.UtcNow, observedMount,
                uint.Parse(observedDevice.Split(':')[0]),
                uint.Parse(observedDevice.Split(':')[1]), 1234));

        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public void ReadReceiptRejectsMalformedMountInfoEscape()
    {
        var mountInfo = MountLine(42, "0:48", "/workspace\\08x", "rw", "opaque-source");

        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            LinuxRuntimeWorkspaceMountObserver.CreateReceipt(
                mountInfo, "/workspace", expectedReadOnly: false, "pod-uid",
                DateTimeOffset.UtcNow, 42, 0, 48, 1234));

        Assert.Equal("runtime_workspace_mount_info_invalid", failure.Code);
    }

    private static string MountLine(
        ulong mountId, string device, string mountPoint, string mode, string source,
        string? superblockMode = null) =>
        $"1 0 0:1 / / rw,relatime - overlay overlay rw\n" +
        $"{mountId} 1 {device} / {mountPoint} {mode},relatime - ext4 {source} {superblockMode ?? mode},relatime";
}

public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Exercises Linux open/statx and /proc/self/mountinfo; run in Linux CI.";
    }
}
