using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.Sandbox;

public sealed class RunCommandHeartbeatTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "run-command-heartbeat-workspaces",
        Guid.NewGuid().ToString("n"));

    public RunCommandHeartbeatTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for test workspaces under the build output directory.
        }
    }

    [Fact]
    public async Task RunCommand_UsesInstrumentedToolCallId_ForActiveHeartbeatCorrelation()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new SnapshotCapturingExecutor(tracker);
        var callIds = new List<string>();
        var tool = BuildInstrumentedRunCommandTool(executor, tracker, callIds);

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "dotnet test",
        }));

        callIds.Should().ContainSingle();
        executor.ObservedSnapshot.Should().NotBeNull();
        executor.ObservedSnapshot!.ToolCallId.Should().Be(callIds[0],
            "run_command heartbeats must correlate to the same tool.call id the trace UI sees");
        executor.ObservedSnapshot.CommandHash.Should().NotBe(callIds[0],
            "the command hash remains internal timing metadata, not the UI correlation id");
    }

    [Fact]
    public void ToolExecutionPendingPayload_IsCorrelatedAndOutputFree()
    {
        var started = DateTimeOffset.Parse("2026-09-14T09:00:00Z");
        var snapshot = new ShellExecutionSnapshot(
            "call-run-command-1",
            "hash-derived-from-command-and-not-emitted",
            started,
            started.AddMinutes(10));

        var payload = CopilotAIAgent.CreateShellExecutionPendingPayload(
            "run-heartbeat-1",
            snapshot,
            started.AddSeconds(7.25));
        var json = JsonSerializer.SerializeToElement(payload);

        json.EnumerateObject().Select(prop => prop.Name).Should().BeEquivalentTo(
            "runId",
            "toolCallId",
            "toolName",
            "startedAtUtc",
            "deadlineUtc",
            "elapsedSeconds");
        json.GetProperty("runId").GetString().Should().Be("run-heartbeat-1");
        json.GetProperty("toolCallId").GetString().Should().Be("call-run-command-1");
        json.GetProperty("toolName").GetString().Should().Be("run_command");
        json.GetProperty("elapsedSeconds").GetDouble().Should().BeApproximately(7.25, 0.01);

        var serialized = json.GetRawText();
        serialized.Should().NotContain("hash-derived-from-command-and-not-emitted");
        serialized.Should().NotContain("npm test -- --secret");
        serialized.Should().NotContain("commandText");
        serialized.Should().NotContain("commandHash");
        serialized.Should().NotContain("stdout");
        serialized.Should().NotContain("stderr");
        serialized.Should().NotContain("exit");
        serialized.Should().NotContain("environment");
        serialized.Should().NotContain("cwd");
    }

    [Fact]
    public async Task RunCommand_Cancellation_ReleasesActiveHeartbeatSlot()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new BlockingExecutor();
        var tool = BuildRunCommandTool(executor, tracker);
        using var cts = new CancellationTokenSource();

        var invocation = tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "sleep 30" }),
            cts.Token).AsTask();

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tracker.ActiveExecution.Should().NotBeNull();

        cts.Cancel();

        Func<Task> act = async () => await invocation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        tracker.ActiveExecution.Should().BeNull(
            "run_command must stop heartbeats when the executor observes cancellation");
    }

    [Fact]
    public async Task RunCommand_Exception_ReleasesActiveHeartbeatSlot()
    {
        using var tracker = new ShellExecutionTracker();
        var executor = new ThrowingExecutor();
        var tool = BuildRunCommandTool(executor, tracker);

        Func<Task> act = async () => await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["command"] = "dotnet build",
            }));

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("executor failed");

        tracker.ActiveExecution.Should().BeNull(
            "run_command must stop heartbeats when the executor throws before producing a result");
    }

    private AIFunction BuildRunCommandTool(ISandboxExecutor executor, ShellExecutionTracker tracker) =>
        CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker),
            includeControlledRunCommand: true)
        .Single(tool => tool.Name == "run_command");

    private AIFunction BuildInstrumentedRunCommandTool(
        ISandboxExecutor executor,
        ShellExecutionTracker tracker,
        List<string> callIds) =>
        CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker),
            includeControlledRunCommand: true,
            instrumentCustomTool: tool => new CopilotAIAgent.InstrumentedCustomAIFunction(
                tool,
                emitToolCallOnce: (callId, _, _) => callIds.Add(callId),
                emitToolResultOnce: (_, _) => { },
                emitToolErrorOnce: (_, _) => { },
                startToolSpan: (_, _, _, _) => { },
                completeToolSpan: (_, _, _, _, _) => { }))
        .Single(tool => tool.Name == "run_command");

    private SandboxToolContext BuildContext(ISandboxExecutor executor, ShellExecutionTracker tracker) => new(
        AgentId: "run-command-heartbeat-test",
        WorkingDirectory: _root,
        SandboxRoot: _root,
        Executor: executor,
        FileTools: new SandboxedFileTools(_root),
        SearchTools: new SandboxedSearchTools(_root),
        Redactor: SandboxOutputRedactor.Default,
        Options: new SandboxToolOptions(ShellEnabled: true),
        Logger: NullLogger.Instance,
        RunId: "run-command-heartbeat-test",
        ShellExecutionTracker: tracker,
        CurrentToolCallId: () => SandboxToolInvocation.CurrentToolCallId);

    private sealed class SnapshotCapturingExecutor(ShellExecutionTracker tracker) : TestExecutor
    {
        public ShellExecutionSnapshot? ObservedSnapshot { get; private set; }

        public override Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            ObservedSnapshot = tracker.ActiveExecution;
            return Task.FromResult(new SandboxExecResult(0, "ok", "", TimedOut: false, OutputTruncated: false));
        }
    }

    private sealed class BlockingExecutor : TestExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new SandboxExecResult(0, "", "", TimedOut: false, OutputTruncated: false);
        }
    }

    private sealed class ThrowingExecutor : TestExecutor
    {
        public override Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("executor failed");
    }

    private abstract class TestExecutor : ISandboxExecutor
    {
        public bool IsRealIsolation => false;
        public string BackendName => "direct";
        public string SelectionReason => "run_command heartbeat test";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;

        public abstract Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default);

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await ExecuteAsync(command, ct);
            if (!string.IsNullOrEmpty(result.Stdout))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, result.Stdout);
            if (!string.IsNullOrEmpty(result.Stderr))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, result.Stderr);
        }
    }
}
