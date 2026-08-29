using Agentweaver.SandboxExec.PodExec;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Regression guard for #1008.
///
/// <para>The AgentHost↔executor Unix socket worked from #757 until the AKS katapool node image was
/// upgraded on 2026-08-27T17:41:48Z, which brought Kata Containers 3.32.0. Upstream had flipped
/// <c>disable_guest_empty_dir</c> from <c>false</c> to <c>true</c>, so the pod's default
/// <c>emptyDir</c> stopped being a directory the guest agent creates and became a host directory
/// re-exported over virtio-fs with a per-container share path. AgentHost then saw the socket file
/// with <c>S_ISSOCK</c> set and got <c>ECONNREFUSED</c> on every connect.</para>
///
/// <para>The repair is one manifest line (<c>medium: Memory</c>). These tests pin the detector that
/// turns a recurrence into a named startup error instead of another silent crash-loop.</para>
/// </summary>
public sealed class PodExecRendezvousFilesystemTests
{
    /// <summary>
    /// The executor sidecar's real mount table on the katapool node, captured after the upgrade.
    /// </summary>
    private const string BrokenKataMountInfo = """
        166 165 0:47 / / rw,relatime - overlay overlay rw,lowerdir=/x,upperdir=/y,workdir=/z
        175 166 0:51 / /workspace rw,relatime - virtiofs none rw
        189 166 0:59 / /run/agentweaver-exec rw,relatime - virtiofs none rw
        190 166 0:38 / /tmp rw,relatime - ext4 /dev/vda rw
        """;

    /// <summary>The same node with <c>medium: Memory</c> on the volume.</summary>
    private const string FixedKataMountInfo = """
        166 165 0:47 / / rw,relatime - overlay overlay rw,lowerdir=/x,upperdir=/y,workdir=/z
        175 166 0:51 / /workspace rw,relatime - virtiofs none rw
        189 166 0:38 / /run/agentweaver-exec rw,relatime - tmpfs tmpfs rw
        190 166 0:38 / /tmp rw,relatime - ext4 /dev/vda rw
        """;

    /// <summary>
    /// Longest-prefix matching matters: <c>/run/agentweaver-exec</c> must not be answered by the
    /// root overlay entry, or the detector would never fire.
    /// </summary>
    [Fact]
    public void FilesystemType_IsResolvedFromTheRealKataMountTables()
    {
        PodExecEndpoint.ResolveFilesystemType(BrokenKataMountInfo, "/run/agentweaver-exec")
            .Should().Be("virtiofs");
        PodExecEndpoint.ResolveFilesystemType(FixedKataMountInfo, "/run/agentweaver-exec")
            .Should().Be("tmpfs");
        PodExecEndpoint.ResolveFilesystemType(BrokenKataMountInfo, "/run/agentweaver-exec/exec.sock")
            .Should().Be("virtiofs");
        PodExecEndpoint.ResolveFilesystemType(BrokenKataMountInfo, "/run/agentweaver-exec-other")
            .Should().Be("overlay");
        PodExecEndpoint.ResolveFilesystemType(BrokenKataMountInfo, "/tmp/x").Should().Be("ext4");
    }

    /// <summary>
    /// mountinfo carries optional fields between the mount point and the separator, and
    /// octal-escapes whitespace. Parsing positionally from the end of the line would misread both.
    /// </summary>
    [Fact]
    public void FilesystemType_ParsesOptionalFieldsAndEscapedMountPoints()
    {
        const string mountInfo = """
            36 35 0:59 / /run/agent\040exec rw,relatime shared:1 master:2 - virtiofs none rw
            """;

        PodExecEndpoint.ResolveFilesystemType(mountInfo, "/run/agent exec").Should().Be("virtiofs");
    }

    /// <summary>
    /// The guard exists to catch one known-broken pod configuration. An indeterminate answer must
    /// never become a new reason to refuse service on a developer machine, in CI, or on Windows.
    /// </summary>
    [Fact]
    public void RendezvousCheck_IsPermissiveWhenTheMountTableCannotBeRead()
    {
        PodExecEndpoint.ResolveFilesystemType(string.Empty, "/run/agentweaver-exec").Should().BeNull();
        PodExecEndpoint
            .CanHostCrossContainerRendezvous(
                Path.Combine(AppContext.BaseDirectory, $"exec-{Guid.NewGuid():N}", "exec.sock"),
                out _)
            .Should().BeTrue();
    }
}
