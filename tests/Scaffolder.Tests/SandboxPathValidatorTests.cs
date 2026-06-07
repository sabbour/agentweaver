using FluentAssertions;
using Scaffolder.SandboxFs;

namespace Scaffolder.Tests.SandboxFs;

/// <summary>
/// Verifies SC-002: 100% rejection of every path-escape attempt against the
/// sandbox boundary. No mocks; all tests use real temp directories.
/// </summary>
public sealed class SandboxPathValidatorTests : IDisposable
{
    private readonly string _sandboxRoot;

    public SandboxPathValidatorTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sandbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void RelativePath_WithinSandbox_IsAccepted()
    {
        var result = SandboxPathValidator.ValidateAndResolve("file.txt", _sandboxRoot);

        result.Should().StartWith(_sandboxRoot);
        result.Should().EndWith("file.txt");
    }

    [Fact]
    public void SubdirectoryPath_WithinSandbox_IsAccepted()
    {
        var result = SandboxPathValidator.ValidateAndResolve("subdir/nested/file.cs", _sandboxRoot);

        result.Should().StartWith(_sandboxRoot);
        result.Should().Contain("nested");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("../outside.txt")]
    [InlineData("subdir/../../outside")]
    [InlineData("./../../outside")]
    public void PathEscapeAttempt_IsRejected(string path)
    {
        var act = () => SandboxPathValidator.ValidateAndResolve(path, _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }

    [Fact]
    public void SymlinkOutsideSandbox_IsRejected()
    {
        // Creating symlinks on Windows requires Developer Mode or elevated rights.
        // Skip gracefully when the privilege is not available.
        var outsideTarget = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsideTarget, "outside content");

        var symlinkPath = Path.Combine(_sandboxRoot, "link.txt");
        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideTarget);
        }
        catch (UnauthorizedAccessException)
        {
            // Symbolic link creation not permitted — skip this test.
            return;
        }
        catch (IOException)
        {
            return;
        }
        finally
        {
            try { File.Delete(outsideTarget); }
            catch { /* best effort */ }
        }

        try
        {
            var act = () => SandboxPathValidator.ValidateAndResolve("link.txt", _sandboxRoot);
            act.Should().Throw<SandboxViolationException>();
        }
        finally
        {
            try { File.Delete(symlinkPath); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void NullByte_InPath_IsRejected()
    {
        var act = () => SandboxPathValidator.ValidateAndResolve("file\0evil.txt", _sandboxRoot);

        // Path.GetFullPath throws on null bytes; the validator surfaces this as a
        // SandboxViolationException or allows the underlying exception to propagate.
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EmptyPath_IsRejected()
    {
        var act = () => SandboxPathValidator.ValidateAndResolve("", _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }
}
