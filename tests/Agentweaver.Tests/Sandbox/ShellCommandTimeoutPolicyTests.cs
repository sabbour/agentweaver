using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Sandbox;

public sealed class ShellCommandTimeoutPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".shell-command-timeout-tests",
        Guid.NewGuid().ToString("n"));

    public ShellCommandTimeoutPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DefaultTimeout_IsFiveMinutes()
    {
        var options = new SandboxToolOptions(ShellEnabled: true);

        options.DefaultTimeoutMs.Should().Be((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
    }

    [Fact]
    public async Task RunCommand_UsesDefaultAndAddsFiveMinuteWatchdogGrace()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new CapturingExecutor(tracker);
        var tool = BuildRunCommandTool(executor, tracker, new SandboxToolOptions(ShellEnabled: true));

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "echo default-timeout",
        }));

        executor.Command.Should().NotBeNull();
        executor.Command!.TimeoutMs.Should().Be((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        var observedDeadline = executor.ObservedDeadline;
        observedDeadline.Should().NotBeNull();
        (observedDeadline!.Deadline - observedDeadline.StartedAt)
            .Should().Be(TimeSpan.FromMinutes(10),
                "the executor receives five minutes and the watchdog allows five more minutes for Kata teardown");
    }

    [Fact]
    public async Task RunCommand_PreservesExplicitCallerTimeoutWithoutPolicyCap()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new CapturingExecutor(tracker);
        var tool = BuildRunCommandTool(executor, tracker, new SandboxToolOptions(ShellEnabled: true));

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "echo caller-timeout",
            ["timeout_ms"] = 45_000,
        }));

        executor.Command.Should().NotBeNull();
        executor.Command!.TimeoutMs.Should().Be(45_000,
            "a caller timeout remains in effect when this runtime context has no safety cap");
    }

    [Fact]
    public async Task RunCommand_ClampsExplicitCallerTimeoutToHardSafetyCap()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new CapturingExecutor(tracker);
        var tool = BuildRunCommandTool(
            executor,
            tracker,
            new SandboxToolOptions(ShellEnabled: true)
            {
                MaximumTimeoutMs = 600_000,
            });

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "echo capped-timeout",
            ["timeout_ms"] = 900_000,
        }));

        executor.Command.Should().NotBeNull();
        executor.Command!.TimeoutMs.Should().Be(600_000);
    }

    [Fact]
    public async Task RunCommand_AllowsKataStyleSlowTeardownInsideWatchdogGrace()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new CapturingExecutor(tracker, teardownDelay: TimeSpan.FromMilliseconds(150));
        var tool = BuildRunCommandTool(executor, tracker, new SandboxToolOptions(ShellEnabled: true));

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "echo kata-teardown",
        }));

        var observedDeadline = executor.ObservedDeadline;
        observedDeadline.Should().NotBeNull(
            "the shell remains watchdog-observed while the executor waits for Kata process teardown");
        (observedDeadline!.Deadline - observedDeadline.StartedAt)
            .Should().Be(TimeSpan.FromMinutes(10));
        result?.ToString().Should().Contain("timed_out: true",
            "the executor timeout remains a recoverable tool result while teardown completes");
        tracker.ActiveExecution.Should().BeNull("the tool releases its watchdog lease after teardown");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AIFunction BuildRunCommandTool(
        CapturingExecutor executor,
        ShellExecutionTracker tracker,
        SandboxToolOptions options) =>
        CopilotAIAgent.BuildSessionConfigTools(
            new SandboxToolContext(
                AgentId: "timeout-policy-test",
                WorkingDirectory: _root,
                SandboxRoot: _root,
                Executor: executor,
                FileTools: new SandboxedFileTools(_root),
                SearchTools: new SandboxedSearchTools(_root),
                Redactor: SandboxOutputRedactor.Default,
                Options: options,
                Logger: NullLogger.Instance,
                ShellExecutionTracker: tracker),
            includeControlledRunCommand: true)
        .Single(tool => tool.Name == "run_command");

    private sealed class CapturingExecutor(
        ShellExecutionTracker tracker,
        TimeSpan? teardownDelay = null) : ISandboxExecutor
    {
        public SandboxCommand? Command { get; private set; }
        public ShellExecutionSnapshot? ObservedDeadline { get; private set; }

        public bool IsRealIsolation => false;
        public string BackendName => "direct";
        public string SelectionReason => "test executor";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;

        public async Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            Command = command;
            ObservedDeadline = tracker.ActiveExecution;
            if (teardownDelay is { } delay)
                await Task.Delay(delay, ct);
            return new SandboxExecResult(-1, "", "Timed out.", TimedOut: teardownDelay is not null, OutputTruncated: false);
        }

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await ExecuteAsync(command, ct);
            yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
        }
    }
}
