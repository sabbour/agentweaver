using Agentweaver.SandboxFs;

namespace Agentweaver.Tests;

public class RealPathTests
{
    /// <summary>
    /// Regression test: RealPath.Resolve must handle directories (not just files).
    /// Before the fix, ResolveUnix used File.Open which throws UnauthorizedAccessException
    /// on directories under Linux/macOS, making every run submission return 400.
    /// This test exercises the Windows branch on Windows hosts and would catch the
    /// directory-open bug on Linux/macOS CI.
    /// </summary>
    [Fact]
    public void Resolve_directory_returns_valid_rooted_path()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"realpath-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var resolved = RealPath.Resolve(tempDir);

            Assert.False(string.IsNullOrWhiteSpace(resolved));
            Assert.True(Path.IsPathRooted(resolved));

            // The resolved path must point at the same directory.
            // On macOS, /var -> /private/var, so we can't string-equal the full path.
            // Instead, verify a marker file created inside is reachable via the resolved path.
            var markerName = $"marker-{Guid.NewGuid():N}.txt";
            var markerViaOriginal = Path.Combine(tempDir, markerName);
            File.WriteAllText(markerViaOriginal, "test");

            var markerViaResolved = Path.Combine(resolved, markerName);
            Assert.True(File.Exists(markerViaResolved),
                $"Marker file not found at resolved path. Original: {tempDir}, Resolved: {resolved}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_throws_for_nonexistent_path()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");
        Assert.Throws<IOException>(() => RealPath.Resolve(bogus));
    }

    [Fact]
    public void Resolve_throws_for_empty_path()
    {
        Assert.Throws<IOException>(() => RealPath.Resolve(""));
        Assert.Throws<IOException>(() => RealPath.Resolve("   "));
    }
}
