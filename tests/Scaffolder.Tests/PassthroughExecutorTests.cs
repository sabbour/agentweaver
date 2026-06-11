using FluentAssertions;
using Scaffolder.SandboxExec;

namespace Scaffolder.Tests.Sandbox;

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
}
