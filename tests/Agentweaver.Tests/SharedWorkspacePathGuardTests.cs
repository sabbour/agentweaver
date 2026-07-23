using FluentAssertions;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// #476 — Unit tests for <see cref="SharedWorkspacePathGuard"/>, the command-text
/// absolute-path guard that blocks cross-run/cross-project access into the shared
/// <c>/workspace</c> PVC that every Kata AgentHost pod mounts.
/// </summary>
public sealed class SharedWorkspacePathGuardTests
{
    // A run whose own tree lives in the pod-local scratch (Local execution mode): ANY
    // absolute reference into the shared /workspace PVC is cross-run.
    private static readonly string[] LocalModeRoots = { "/local-workspace/run-abc/tree" };

    // A run whose own tree is a worktree under the shared PVC (Shared execution mode):
    // its OWN subtree is allowed, sibling projects are not.
    private static readonly string[] SharedModeRoots = { "/workspace/my-project" };

    [Theory]
    [InlineData("cat /workspace/other-project/secrets.txt")]
    [InlineData("git -C /workspace/other-project status")]
    [InlineData("cp /workspace/victim/.env /local-workspace/run-abc/tree/leak")]
    [InlineData("cat \"/workspace/other-project/secrets.txt\"")]
    [InlineData("git --git-dir=/workspace/other/.git log")]
    [InlineData("cat /local-workspace/run-abc/tree/../../../workspace/other/secrets")]
    public void CrossRunAbsolutePath_IntoSharedPvc_IsRejected(string command)
    {
        var (allowed, reason) = SharedWorkspacePathGuard.Inspect(command, LocalModeRoots);

        allowed.Should().BeFalse("absolute paths into the shared /workspace PVC across runs must be blocked");
        reason.Should().NotBeNull();
        reason.Should().Contain("/workspace");
    }

    [Theory]
    [InlineData("echo hello")]
    [InlineData("dotnet build --no-incremental")]
    [InlineData("cat /usr/bin/git")]
    [InlineData("ls /etc/hosts")]
    [InlineData("node /home/appuser/.npm/index.js")]
    [InlineData("cat /local-workspace/run-abc/tree/README.md")]
    [InlineData("git -C /local-workspace/run-abc/tree status")]
    public void SystemAndOwnRootPaths_AreAllowed(string command)
    {
        var (allowed, reason) = SharedWorkspacePathGuard.Inspect(command, LocalModeRoots);

        allowed.Should().BeTrue("system paths and the run's own tree must not be flagged");
        reason.Should().BeNull();
    }

    [Fact]
    public void SharedMode_OwnWorktree_IsAllowed()
    {
        var (allowed, _) = SharedWorkspacePathGuard.Inspect(
            "git -C /workspace/my-project status", SharedModeRoots);

        allowed.Should().BeTrue("the run's own worktree under the shared PVC is its allowed root");
    }

    [Fact]
    public void SharedMode_SiblingWorktree_IsRejected()
    {
        var (allowed, reason) = SharedWorkspacePathGuard.Inspect(
            "cat /workspace/other-project/secrets.txt", SharedModeRoots);

        allowed.Should().BeFalse("a sibling project under the shared PVC is outside the run's own root");
        reason.Should().Contain("other-project");
    }

    [Fact]
    public void PathTraversalIntoSibling_IsRejected()
    {
        // /workspace/my-project/../other-project normalizes to /workspace/other-project.
        var (allowed, _) = SharedWorkspacePathGuard.Inspect(
            "cat /workspace/my-project/../other-project/x", SharedModeRoots);

        allowed.Should().BeFalse("traversal out of the own worktree into a sibling must be caught");
    }

    [Fact]
    public void EmptyCommand_IsAllowed()
    {
        SharedWorkspacePathGuard.Inspect("", LocalModeRoots).Allowed.Should().BeTrue();
    }

    [Fact]
    public void CustomProtectedRoots_AreHonored()
    {
        var (allowed, _) = SharedWorkspacePathGuard.Inspect(
            "cat /shared/other/secret",
            allowedRoots: new[] { "/shared/mine" },
            protectedRoots: new[] { "/shared" });

        allowed.Should().BeFalse("a caller-supplied protected root must be enforced");
    }

    [Fact]
    public void PathListWithColon_SurfacesEmbeddedSharedPath()
    {
        var (allowed, _) = SharedWorkspacePathGuard.Inspect(
            "PATH=/usr/bin:/workspace/other/bin ls", LocalModeRoots);

        allowed.Should().BeFalse("a shared-mount path embedded in a colon list must still be caught");
    }
}
