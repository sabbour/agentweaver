using FluentAssertions;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// T027 — Unit tests for SandboxFsPolicyBuilder (F2 hardened).
/// Verifies that the policy builder correctly populates read-write, read-only,
/// and denied path lists, and rejects symlink sandbox roots.
/// </summary>
public sealed class SandboxFsPolicyBuilderTests : IDisposable
{
    private readonly string _sandboxRoot;

    public SandboxFsPolicyBuilderTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sb-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void SandboxRoot_IsAddedToReadWritePaths()
    {
        var policy = SandboxFsPolicyBuilder.Build(_sandboxRoot, []);

        policy.ReadWritePaths.Should().ContainSingle(
            "the sandbox root must be the sole read-write path when no extras are given");
        policy.ReadWritePaths[0].Should().BeEquivalentTo(Path.GetFullPath(_sandboxRoot));
    }

    [Fact]
    public void AllowedRoot_IsAddedToReadOnlyPaths()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"allowed-ro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);
        try
        {
            var policy = SandboxFsPolicyBuilder.Build(_sandboxRoot, [allowedRoot]);

            policy.ReadOnlyPaths.Should().ContainSingle(
                "the allowed repository root must appear in read-only paths");
            policy.ReadOnlyPaths[0].Should().BeEquivalentTo(Path.GetFullPath(allowedRoot));
        }
        finally
        {
            try { Directory.Delete(allowedRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void AllowedRoot_EqualToSandboxRoot_IsNotDuplicated()
    {
        var policy = SandboxFsPolicyBuilder.Build(_sandboxRoot, [_sandboxRoot]);

        policy.ReadWritePaths.Should().ContainSingle(
            "the sandbox root must appear once in read-write paths");
        policy.ReadOnlyPaths.Should().BeEmpty(
            "a root equal to the sandbox root must not be duplicated into read-only paths");
    }

    [Fact]
    public void DeniedPaths_IsAlwaysEmpty()
    {
        var policy = SandboxFsPolicyBuilder.Build(_sandboxRoot, []);

        // The sandbox allow-list (ReadWritePaths/ReadOnlyPaths) provides containment.
        // An explicit deny list is redundant — on Windows it causes a deny-all fallback
        // that blocks the workspace itself, and on all platforms the allow-list already
        // restricts access to only what is explicitly granted.
        policy.DeniedPaths.Should().BeEmpty(
            "denied paths are not set; containment is enforced via the allow-list alone");
    }

    [Fact]
    public void SymlinkRoot_ThrowsSandboxViolationException()
    {
        var realTarget = Path.Combine(Path.GetTempPath(), $"real-target-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(Path.GetTempPath(), $"symlink-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realTarget);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(symlinkPath, realTarget);
            }
            catch (UnauthorizedAccessException)
            {
                return; // Symlink creation requires elevation on this OS — skip gracefully.
            }
            catch (IOException)
            {
                return;
            }

            var act = () => SandboxFsPolicyBuilder.Build(symlinkPath, []);
            act.Should().Throw<SandboxViolationException>(
                "a symlink sandbox root is a security violation and must be rejected at policy-build time");
        }
        finally
        {
            try { Directory.Delete(realTarget, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(symlinkPath); } catch { /* best effort */ }
        }
    }
}
