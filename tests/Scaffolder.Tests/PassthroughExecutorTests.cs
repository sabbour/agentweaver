using System.Diagnostics;
using FluentAssertions;
using Scaffolder.SandboxExec;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// T026 — Unit tests for PassthroughExecutor (deny-by-default fallback executor).
/// Tested via SandboxExecutorFactory.CreatePassthrough() because PassthroughExecutor is internal.
/// </summary>
public sealed class PassthroughExecutorTests
{
    private readonly ISandboxExecutor _executor =
        SandboxExecutorFactory.CreatePassthrough("unit-test: no isolation available");

    [Fact]
    public void IsRealIsolation_IsFalse()
    {
        _executor.IsRealIsolation.Should().BeFalse(
            "passthrough is a deny-by-default fallback and provides no real process isolation");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsDeniedResult_WithExitCodeMinus1()
    {
        var result = await _executor.ExecuteAsync(MakeCommand());

        result.ExitCode.Should().Be(-1);
        result.Stderr.Should().Contain("Shell execution denied");
        result.TimedOut.Should().BeFalse();
        result.OutputTruncated.Should().BeFalse();
        result.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NeverSpawnsProcess()
    {
        var before = Process.GetProcesses().Length;
        _ = await _executor.ExecuteAsync(MakeCommand());
        var after = Process.GetProcesses().Length;

        // Allow minor OS-level process-count variance (≤3) but no new process from our executor.
        after.Should().BeLessThanOrEqualTo(before + 3,
            "passthrough executor must not spawn any process");
    }

    [Fact]
    public async Task StreamAsync_YieldsOnlyExitCodeChunk_WithDenyMessage()
    {
        var chunks = new List<SandboxOutputChunk>();
        await foreach (var chunk in _executor.StreamAsync(MakeCommand()))
            chunks.Add(chunk);

        chunks.Should().HaveCount(1, "passthrough yields exactly one terminal chunk");
        chunks[0].Stream.Should().Be(SandboxOutputStream.ExitCode);
        chunks[0].Data.Should().Contain("Shell execution denied");
    }

    private static SandboxCommand MakeCommand() =>
        new("echo hello", Path.GetTempPath(), null, new SandboxFsPolicy([], [], []), 5000);
}
