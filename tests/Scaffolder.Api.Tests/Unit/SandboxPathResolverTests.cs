using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Scaffolder.Api.Agent.Tools;
using Xunit;

namespace Scaffolder.Api.Tests.Unit;

/// <summary>
/// T070: Unit tests for SandboxPathResolver.
/// Validates all security invariants per FR-007 and SC-002.
/// </summary>
public sealed class SandboxPathResolverTests
{
    private readonly SandboxPathResolver _resolver = new();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sandbox-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ValidRelativePath_ReturnsResolvedPath()
    {
        var artifactDir = TempDir();
        try
        {
            var result = _resolver.Resolve("subdir/file.txt", artifactDir);

            result.IsSuccess.Should().BeTrue();
            result.ResolvedPath.Should().StartWith(artifactDir);
            result.ResolvedPath.Should().EndWith("file.txt");
        }
        finally
        {
            Directory.Delete(artifactDir, recursive: true);
        }
    }

    [Fact]
    public void AbsolutePath_IsRejectedWithPathEscape()
    {
        var artifactDir = TempDir();
        try
        {
            var absolutePath = Path.GetTempPath(); // e.g. /tmp or C:\Temp

            var result = _resolver.Resolve(absolutePath, artifactDir);

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be(SandboxErrorCode.PathEscape);
        }
        finally
        {
            Directory.Delete(artifactDir, recursive: true);
        }
    }

    [Fact]
    public void SingleDotDotSegment_IsRejectedWithPathEscape()
    {
        var artifactDir = TempDir();
        try
        {
            var result = _resolver.Resolve("../outside.txt", artifactDir);

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be(SandboxErrorCode.PathEscape);
        }
        finally
        {
            Directory.Delete(artifactDir, recursive: true);
        }
    }

    [Fact]
    public void MultiHopDotDot_IsRejectedWithPathEscape()
    {
        var artifactDir = TempDir();
        try
        {
            var result = _resolver.Resolve("a/../../outside.txt", artifactDir);

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be(SandboxErrorCode.PathEscape);
        }
        finally
        {
            Directory.Delete(artifactDir, recursive: true);
        }
    }

    [Fact]
    public void NestedValidPath_IsAllowed()
    {
        var artifactDir = TempDir();
        try
        {
            var result = _resolver.Resolve("a/b/c/file.txt", artifactDir);

            result.IsSuccess.Should().BeTrue();
            result.ResolvedPath.Should().Contain("a");
            result.ResolvedPath.Should().StartWith(artifactDir);
        }
        finally
        {
            Directory.Delete(artifactDir, recursive: true);
        }
    }
}
