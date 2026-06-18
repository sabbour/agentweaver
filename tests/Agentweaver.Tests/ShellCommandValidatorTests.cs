using FluentAssertions;
using Agentweaver.SandboxExec;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// T028 — Unit tests for ShellCommandValidator (F4 host-side defense-in-depth).
/// ShellCommandValidator is internal; InternalsVisibleTo is declared in
/// packages/Agentweaver.SandboxExec/AssemblyInfo.cs.
/// </summary>
public sealed class ShellCommandValidatorTests : IDisposable
{
    private readonly string _sandboxRoot;

    public ShellCommandValidatorTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sb-scv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ValidDirectory_WithinSandbox_IsAllowed()
    {
        var (allowed, reason) = ShellCommandValidator.Validate(
            "echo hello", _sandboxRoot, _sandboxRoot);

        allowed.Should().BeTrue("a command in the sandbox root is a valid scenario");
        reason.Should().BeNull();
    }

    [Fact]
    public void Directory_OutsideSandbox_IsDenied()
    {
        // Parent of the sandbox root is definitively outside the sandbox boundary.
        var outside = Path.GetDirectoryName(_sandboxRoot)!;

        var (allowed, reason) = ShellCommandValidator.Validate(
            "echo hello", outside, _sandboxRoot);

        allowed.Should().BeFalse("a working directory outside the sandbox must be denied");
        reason.Should().Contain("Working directory escape");
    }

    [Fact]
    public void Directory_PathTraversal_IsDenied()
    {
        // Construct an absolute path with ".." that resolves above the sandbox root.
        var traversal = Path.Combine(_sandboxRoot, "..", "escaped-outside");

        var (allowed, reason) = ShellCommandValidator.Validate(
            "echo hello", traversal, _sandboxRoot);

        allowed.Should().BeFalse("path traversal above the sandbox root must be denied");
        reason.Should().NotBeNull();
    }

    [Fact]
    public void Command_ExceedingMaxLength_IsDenied()
    {
        var longCommand = new string('a', 65537); // one byte over the 65536 cap

        var (allowed, reason) = ShellCommandValidator.Validate(
            longCommand, _sandboxRoot, _sandboxRoot);

        allowed.Should().BeFalse("commands exceeding the max-length cap must be denied");
        reason.Should().Contain("maximum length");
    }

    [Fact]
    public void Command_WithNullByte_IsDenied()
    {
        var (allowed, reason) = ShellCommandValidator.Validate(
            "cmd\0injected", _sandboxRoot, _sandboxRoot);

        allowed.Should().BeFalse("null bytes are injection attempts and must be denied");
        reason.Should().Contain("null byte");
    }

    [Fact]
    public void Command_Normal_IsAllowed()
    {
        var (allowed, reason) = ShellCommandValidator.Validate(
            "dotnet build --no-incremental", _sandboxRoot, _sandboxRoot);

        allowed.Should().BeTrue("a normal, well-formed command must be allowed");
        reason.Should().BeNull();
    }
}
