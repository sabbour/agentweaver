using FluentAssertions;
using Scaffolder.AgentRuntime;
using Scaffolder.SandboxFs;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// G. Foundry regression: asserts that the removed ResolveSandboxedPath method
/// does NOT exist on FoundryAgentRunner. This was the source of the escape —
/// Foundry had its own path-resolution logic that bypassed the shared governance gate.
/// </summary>
public sealed class FoundryRegressionTests
{
    [Fact]
    public void FoundryAgentRunner_DoesNotHave_ResolveSandboxedPath()
    {
        var type = typeof(FoundryAgentRunner);

        var method = type.GetMethod("ResolveSandboxedPath",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance);

        method.Should().BeNull(
            "ResolveSandboxedPath was DELETED as part of the containment fix — " +
            "Foundry must use the shared SandboxGovernance gate, not its own resolver");
    }
}

/// <summary>
/// F. SandboxedFileTools defense-in-depth: in-tool validation provides an
/// additional layer even if governance is somehow bypassed.
/// </summary>
public sealed class SandboxedFileToolsDefenseTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxedFileTools _tools;

    public SandboxedFileToolsDefenseTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sandbox-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _tools = new SandboxedFileTools(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData(@"..\..\..\etc\passwd")]
    [InlineData(@"..\..\Windows\system32\cmd.exe")]
    public async Task ReadFileAsync_TraversalAttack_ReturnsRejected(string path)
    {
        var (content, failure) = await _tools.ReadFileAsync(path);

        content.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData(@"D:\evil\x.txt")]
    public async Task ReadFileAsync_AbsoluteOutOfSandbox_ReturnsRejected(string path)
    {
        var (content, failure) = await _tools.ReadFileAsync(path);

        content.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Fact]
    public async Task ReadFileAsync_InSandbox_ReturnsContent()
    {
        var filePath = Path.Combine(_sandboxRoot, "hello.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var (content, failure) = await _tools.ReadFileAsync("hello.txt");

        failure.Should().BeNull();
        content.Should().Be("hello world");
    }

    [Theory]
    [InlineData(@"..\..\..\etc\shadow")]
    [InlineData(@"C:\Windows\temp\evil.txt")]
    public async Task WriteFileAsync_EscapeAttempt_ReturnsRejected(string path)
    {
        var (bytes, failure) = await _tools.WriteFileAsync(path, "evil content");

        bytes.Should().Be(0);
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Fact]
    public async Task WriteFileAsync_InSandbox_Succeeds()
    {
        var (bytes, failure) = await _tools.WriteFileAsync("output.txt", "safe content");

        failure.Should().BeNull();
        bytes.Should().BeGreaterThan(0);
        File.ReadAllText(Path.Combine(_sandboxRoot, "output.txt")).Should().Be("safe content");
    }
}
