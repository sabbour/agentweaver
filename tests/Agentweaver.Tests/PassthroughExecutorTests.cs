using FluentAssertions;
using Agentweaver.SandboxExec;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// T026 — Unit tests for PassthroughExecutor (direct execution fallback).
/// </summary>
public sealed class PassthroughExecutorTests
{
    private readonly ISandboxExecutor _executor =
        SandboxExecutorFactory.CreatePassthrough("unit-test: direct execution");

    [Fact]
    public void IsRealIsolation_IsFalse()
    {
        _executor.IsRealIsolation.Should().BeFalse(
            "passthrough provides no process isolation — relies on deployment environment");
    }

    [Fact]
    public void BackendName_IsDirect()
    {
        _executor.BackendName.Should().Be("direct");
    }

    [Fact]
    public async Task ExecuteAsync_RunsCommand_ReturnsOutput()
    {
        // Use a command that works on both Windows and Linux
        var command = OperatingSystem.IsWindows() ? "echo hello" : "echo hello";
        var result = await _executor.ExecuteAsync(
            new SandboxCommand(command, Path.GetTempPath(), null, new SandboxFsPolicy([], [], []), 5000));

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("hello");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_YieldsOutputAndExitCode()
    {
        var command = OperatingSystem.IsWindows() ? "echo hi" : "echo hi";
        var chunks = new List<SandboxOutputChunk>();
        await foreach (var chunk in _executor.StreamAsync(
            new SandboxCommand(command, Path.GetTempPath(), null, new SandboxFsPolicy([], [], []), 5000)))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.Stream == SandboxOutputStream.ExitCode && c.Data == "0");
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCrossRunSharedPath_WithoutRunning()
    {
        // #476: PassthroughExecutor is the Kata-mode executor and enforces the per-run
        // filesystem policy itself. A command referencing the shared /workspace PVC outside
        // the run's own roots must be rejected before any process starts.
        var policy = new SandboxFsPolicy(
            ReadWritePaths: new[] { "/local-workspace/run-1/tree" },
            ReadOnlyPaths: Array.Empty<string>(),
            DeniedPaths: Array.Empty<string>());
        var result = await _executor.ExecuteAsync(new SandboxCommand(
            "cat /workspace/other-project/secrets.txt",
            "/local-workspace/run-1/tree", null, policy, 5000));

        result.ExitCode.Should().Be(126);
        result.Stderr.Should().Contain("rejected");
        result.Stderr.Should().Contain("/workspace");
    }

    [Fact]
    public async Task ExecuteAsync_AllowsOwnSharedWorktreePath()
    {
        // A run in shared-execution mode legitimately owns a /workspace/<worktree> subtree.
        var policy = new SandboxFsPolicy(
            ReadWritePaths: new[] { "/workspace/my-project" },
            ReadOnlyPaths: Array.Empty<string>(),
            DeniedPaths: Array.Empty<string>());
        var command = OperatingSystem.IsWindows()
            ? "echo /workspace/my-project/file"
            : "echo /workspace/my-project/file";
        var result = await _executor.ExecuteAsync(new SandboxCommand(
            command, Path.GetTempPath(), null, policy, 5000));

        result.ExitCode.Should().Be(0, "the run's own worktree path must not be blocked");
    }
}
