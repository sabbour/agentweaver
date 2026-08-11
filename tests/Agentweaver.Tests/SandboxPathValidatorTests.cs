using FluentAssertions;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.SandboxFs;

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

        try
        {
            var act = () => SandboxPathValidator.ValidateAndResolve("link.txt", _sandboxRoot);
            act.Should().Throw<SandboxViolationException>();
        }
        finally
        {
            try { File.Delete(symlinkPath); }
            catch { /* best effort */ }
            try { File.Delete(outsideTarget); }
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

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void DotPath_ResolvesToSandboxRoot_IsAccepted(string path)
    {
        var result = SandboxPathValidator.ValidateAndResolve(path, _sandboxRoot);

        var expectedRoot = Path.GetFullPath(_sandboxRoot).TrimEnd(Path.DirectorySeparatorChar);
        result.TrimEnd(Path.DirectorySeparatorChar).Should().Be(expectedRoot);
    }

    [Fact]
    public void DotSlashSubpath_IsAccepted()
    {
        var result = SandboxPathValidator.ValidateAndResolve("./subdir/file.txt", _sandboxRoot);

        result.Should().StartWith(_sandboxRoot);
        result.Should().EndWith("file.txt");
    }

    [Fact]
    public void RelativeOrAbsolute_AbsoluteSandboxRoot_IsAccepted()
    {
        var result = SandboxPathValidator.ValidateRelativeOrAbsoluteContained(_sandboxRoot, _sandboxRoot);

        result.Should().Be(Path.GetFullPath(_sandboxRoot));
    }

    [Fact]
    public void RelativeOrAbsolute_EmptyPath_IsRejectedBySharedDispatcher()
    {
        var act = () => SandboxPathValidator.ValidateRelativeOrAbsoluteContained("", _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }

    [Fact]
    public void RelativeOrAbsolute_AbsoluteSubdirectoryInsideSandbox_IsAccepted()
    {
        var subdirectory = Path.Combine(_sandboxRoot, "preview", "app");

        var result = SandboxPathValidator.ValidateRelativeOrAbsoluteContained(subdirectory, _sandboxRoot);

        result.Should().Be(Path.GetFullPath(subdirectory));
    }

    [Fact]
    public void RelativeOrAbsolute_AbsoluteTraversalOutsideSandbox_IsRejected()
    {
        var outsidePath = Path.Combine(_sandboxRoot, "..", "outside");

        var act = () => SandboxPathValidator.ValidateRelativeOrAbsoluteContained(outsidePath, _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }

    [Fact]
    public void RelativeOrAbsolute_AbsolutePrefixSibling_IsRejected()
    {
        var siblingPath = _sandboxRoot + "-outside";

        var act = () => SandboxPathValidator.ValidateRelativeOrAbsoluteContained(siblingPath, _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\?\C:\sandbox")]
    [InlineData(@"\\.\C:\sandbox")]
    [InlineData("C:relative-to-drive")]
    [InlineData(@"D:\outside")]
    public void RelativeOrAbsolute_WindowsAbsoluteEscapeForms_AreRejectedOnEveryPlatform(string path)
    {
        var act = () => SandboxPathValidator.ValidateRelativeOrAbsoluteContained(path, _sandboxRoot);

        act.Should().Throw<SandboxViolationException>();
    }

    [Fact]
    public void AbsoluteContained_WindowsAlternateSeparatorsInsideSandbox_AreAccepted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_sandboxRoot, "preview", "app").Replace('\\', '/');

        var result = SandboxPathValidator.ValidateAbsoluteContained(path, _sandboxRoot);

        result.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public void AbsoluteContained_PathCaseUsesHostSecuritySemantics()
    {
        var parent = Path.GetDirectoryName(_sandboxRoot)!;
        var differentlyCasedRoot = Path.Combine(
            parent,
            Path.GetFileName(_sandboxRoot).ToUpperInvariant());

        if (OperatingSystem.IsWindows())
        {
            SandboxPathValidator.ValidateAbsoluteContained(differentlyCasedRoot, _sandboxRoot)
                .Should().Be(Path.GetFullPath(differentlyCasedRoot));
        }
        else
        {
            var act = () => SandboxPathValidator.ValidateAbsoluteContained(differentlyCasedRoot, _sandboxRoot);
            act.Should().Throw<SandboxViolationException>();
        }
    }

    [Fact]
    public void RelativeOrAbsolute_DirectorySymlinkEscapingSandbox_IsRejected()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(_sandboxRoot, "linked-directory");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(symlinkPath, outsideDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var requestedPath = Path.Combine(symlinkPath, "preview");
            var act = () => SandboxPathValidator.ValidateRelativeOrAbsoluteContained(requestedPath, _sandboxRoot);

            act.Should().Throw<SandboxViolationException>();
        }
        finally
        {
            try { Directory.Delete(symlinkPath); }
            catch { /* best effort */ }
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void ValidateSandboxRoot_SymlinkOrJunctionRoot_IsRejected()
    {
        var realDirectory = Path.Combine(Path.GetTempPath(), $"real-root-{Guid.NewGuid():N}");
        var linkedRoot = Path.Combine(Path.GetTempPath(), $"linked-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realDirectory);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedRoot, realDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var act = () => SandboxPathValidator.ValidateSandboxRoot(linkedRoot);

            act.Should().Throw<SandboxViolationException>();
        }
        finally
        {
            try { Directory.Delete(linkedRoot); }
            catch { /* best effort */ }
            try { Directory.Delete(realDirectory, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
