using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agentweaver.Tests.Sandbox;

public sealed class AssemblyBuildTestShellGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".assembly-shell-tests",
        Guid.NewGuid().ToString("n"));

    public AssemblyBuildTestShellGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Native_shell_is_denied_for_assembly_build_test()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var governance = SandboxGovernance.Create(
            _root,
            "run-1",
            executor,
            SandboxPolicy.Default(_root),
            NullLogger.Instance);
        var agent = BuildAgent(executor);
        var errors = new List<string>();
        var handler = agent.BuildPermissionHandler(
            governance,
            runId: "run-1",
            workingDirectory: _root,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, message) => errors.Add(message),
            emit: (_, _) => { },
            runCt: CancellationToken.None,
            denyNativeShell: true);

        var result = await handler(
            new PermissionRequestShell
            {
                FullCommandText = "npm ci",
                Intention = "build",
                Commands = [],
                HasWriteFileRedirection = false,
                PossiblePaths = [],
                PossibleUrls = [],
                CanOfferSessionApproval = false,
            },
            new PermissionInvocation());

        var rejected = result.Should().BeOfType<PermissionDecisionReject>().Subject;
        rejected.Feedback.Should().Be(
            "Native Copilot shell is disabled; use the sandboxed run_command tool (routed through the sandbox executor).");
        errors.Should().ContainSingle().Which.Should().Be(rejected.Feedback);
    }

    [Fact]
    public void Assembly_session_registers_controlled_run_command_custom_tool()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var tracker = new ShellExecutionTracker();
        var context = new SandboxToolContext(
            AgentId: "agent",
            WorkingDirectory: _root,
            SandboxRoot: _root,
            Executor: executor,
            FileTools: new SandboxedFileTools(_root),
            SearchTools: new SandboxedSearchTools(_root),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: true)
            {
                RejectBackgroundCommands = true,
                RejectDestructiveCommands = true,
                MaximumTimeoutMs = 600_000,
            },
            Logger: NullLogger.Instance,
            ShellExecutionTracker: tracker);

        var tools = CopilotAIAgent.BuildSessionConfigTools(
            context,
            includeControlledRunCommand: true);

        tools.Select(t => t.Name).Should().Contain("run_command");
    }

    [Theory]
    [InlineData("npm test &")]
    [InlineData("nohup npm test")]
    [InlineData("setsid npm test")]
    [InlineData("rm -rf node_modules")]
    public async Task Controlled_run_command_rejects_backgrounding_and_destructive_commands(string command)
    {
        var executor = new CountingExecutor();
        using var tracker = new ShellExecutionTracker();
        var context = BuildContext(executor, tracker);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            context,
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        var result = await tool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = command }));

        result?.ToString().Should().Contain("rejected");
        executor.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Controlled_run_command_arms_watchdog_deadline_above_executor_timeout_by_grace()
    {
        // #313: the streaming watchdog's shell hard-deadline must be armed strictly LATER than the
        // executor's own command timeout so PassthroughExecutor.CancelAfter fires first and returns
        // a graceful timed_out:true; the watchdog only backstops a genuinely hung process. Arming
        // both at the same value made the watchdog win the race and fatally abort the build/test turn.
        using var tracker = new ShellExecutionTracker();
        int observedCommandTimeoutMs = 0;
        ShellExecutionSnapshot? observedSnapshot = null;
        var executor = new CapturingExecutor(cmd =>
        {
            observedCommandTimeoutMs = cmd.TimeoutMs;
            observedSnapshot = tracker.ActiveExecution;
        });
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker),
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        const int requestedMs = 120_000; // 2 min — below the 10-min cap, no floor in this context
        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "dotnet test",
            ["timeout_ms"] = requestedMs,
        }));

        observedCommandTimeoutMs.Should().Be(requestedMs,
            "the executor's own timeout stays exactly what the caller asked for");
        observedSnapshot.Should().NotBeNull();
        (observedSnapshot!.Deadline - observedSnapshot.StartedAt).Should().Be(
            TimeSpan.FromMilliseconds(requestedMs) + SandboxToolOptions.WatchdogTimeoutGrace,
            "the watchdog deadline must exceed the executor timeout by exactly the grace period");
    }

    [Fact]
    public async Task Controlled_run_command_lets_executor_timeout_win_before_watchdog_grace()
    {
        // #313 end-to-end: a command that runs LONGER than the caller-supplied timeout but well
        // within the watchdog grace must surface the executor's recoverable timed_out:true, never a
        // fatal shell_execution_timeout. (Direct tool invocation returns the executor result; the
        // watchdog deadline is proven separately to sit past this point.)
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var tracker = new ShellExecutionTracker();
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker),
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        var sleepLongerThanTimeout = OperatingSystem.IsWindows()
            ? "ping -n 4 127.0.0.1 >NUL"   // ~3s, exceeds the 300ms timeout
            : "sleep 3";
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = sleepLongerThanTimeout,
            ["timeout_ms"] = 300, // executor expires at 300ms; watchdog grace is 60s away
        }));

        result?.ToString().Should().Contain("timed_out: true",
            "the executor's own timeout must win and return a recoverable result, not throw");
        tracker.ActiveExecution.Should().BeNull("the lease is released after the command returns");
    }

    [Fact]
    public async Task Controlled_run_command_floors_caller_timeout_to_the_build_test_minimum()
    {
        // #313 floor: the model's optimistic 3-min timeout_ms is the observed trigger. In the
        // Build/Test context (MinimumTimeoutMs = 10 min) a sub-floor caller timeout must be clamped
        // up so a legitimate build isn't killed at 3 minutes.
        using var tracker = new ShellExecutionTracker();
        int observedCommandTimeoutMs = 0;
        var executor = new CapturingExecutor(cmd => observedCommandTimeoutMs = cmd.TimeoutMs);
        var context = new SandboxToolContext(
            AgentId: "agent",
            WorkingDirectory: _root,
            SandboxRoot: _root,
            Executor: executor,
            FileTools: new SandboxedFileTools(_root),
            SearchTools: new SandboxedSearchTools(_root),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: true, DefaultTimeoutMs: 600_000)
            {
                MinimumTimeoutMs = 600_000,
                MaximumTimeoutMs = 600_000,
            },
            Logger: NullLogger.Instance,
            ShellExecutionTracker: tracker);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            context, includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "dotnet test",
            ["timeout_ms"] = 180_000, // the #313 trigger value (3 min)
        }));

        observedCommandTimeoutMs.Should().Be(600_000,
            "a sub-floor model timeout must be clamped up to the Build/Test minimum (10 min)");
    }

    [Fact]
    public async Task Controlled_run_command_serializes_concurrent_invocations()
    {
        var executor = new CountingExecutor(blockFirstCall: true);
        using var tracker = new ShellExecutionTracker();
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker),
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");
        var args = new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = "dotnet test" });

        var first = tool.InvokeAsync(args).AsTask();
        await executor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        tracker.ActiveExecution.Should().NotBeNull();
        tracker.ActiveExecution!.Deadline.Should().BeAfter(tracker.ActiveExecution.StartedAt);
        var second = tool.InvokeAsync(args).AsTask();
        await Task.Delay(50);
        executor.ExecuteCalls.Should().Be(1);

        executor.ReleaseFirstCall.TrySetResult();
        await Task.WhenAll(first, second);

        executor.ExecuteCalls.Should().Be(2);
        executor.MaxConcurrent.Should().Be(1);
        tracker.ActiveExecution.Should().BeNull();
    }

    [Fact]
    public async Task Controlled_run_command_allows_run_scoped_scratch_outside_workspace()
    {
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratch);
        SandboxCommand? observed = null;
        var executor = new CapturingExecutor(cmd => observed = cmd);
        using var tracker = new ShellExecutionTracker();
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, tracker, scratchDirectory: scratch),
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        var result = await tool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = "echo scratch" }));

        result?.ToString().Should().Contain("exit_code: 0");
        observed.Should().NotBeNull();
        observed!.FilesystemPolicy.ReadWritePaths.Should().Contain(scratch);
        observed.Environment.Should().NotBeNull();
        observed.Environment!["AGENTWEAVER_SCRATCH"].Should().Be(scratch);
        observed.Environment["TMPDIR"].Should().Be(scratch);
    }

    [Fact(Skip = "Flaky in CI: bwrap /bin/bash intermittently not visible in LinuxBwrapExecutor sandbox; tracked for fix separately")]
    public async Task Controlled_run_command_runs_npm_install_with_sandbox_local_home_in_real_linux_sandbox()
    {
        ISandboxExecutor realExecutor;
        if (OperatingSystem.IsLinux())
        {
            if (!LinuxBwrapExecutor.IsBwrapAvailable())
                return;
            realExecutor = new LinuxBwrapExecutor(NullLogger.Instance);
        }
        else if (OperatingSystem.IsWindows())
        {
            var wslExecutor = WslMxcSandboxExecutor.TryCreate(NullLogger.Instance);
            if (wslExecutor is null)
                return;
            realExecutor = wslExecutor;
            realExecutor.BackendName.Should().Be(
                "wsl-bwrap",
                "the cache test must exercise filesystem-confined Linux execution, not passthrough/unshare");
        }
        else
        {
            return;
        }

        var workspace = Path.Combine(_root, "real-linux-install");
        var fixturePackage = Path.Combine(workspace, "fixture-package");
        Directory.CreateDirectory(fixturePackage);
        File.WriteAllText(
            Path.Combine(fixturePackage, "package.json"),
            """{"name":"agentweaver-cache-fixture","version":"1.0.0","main":"index.js"}""");
        File.WriteAllText(Path.Combine(fixturePackage, "index.js"), "module.exports = 'installed';");
        File.WriteAllText(
            Path.Combine(workspace, "package.json"),
            """
            {
              "name": "controlled-cache-test",
              "version": "1.0.0",
              "private": true,
              "dependencies": {
                "agentweaver-cache-fixture": "file:./fixture-package"
              }
            }
            """);
        var executor = new RecordingExecutor(realExecutor);
        using var tracker = new ShellExecutionTracker();
        var context = BuildContext(executor, tracker, workspace);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            context,
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        var result = await tool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["command"] =
                    "unset npm_config_cache NPM_CONFIG_CACHE && " +
                    "npm install --ignore-scripts --no-audit --no-fund && " +
                    "test -f node_modules/agentweaver-cache-fixture/index.js && " +
                    "test -d \"$HOME/.npm\" && " +
                    "find \"$HOME/.npm\" -type f -print -quit | grep -q .",
            }));

        result?.ToString().Should().Contain("exit_code: 0");
        File.ReadAllText(Path.Combine(
            workspace, "node_modules", "agentweaver-cache-fixture", "index.js"))
            .Should().Be("module.exports = 'installed';");
        Directory.EnumerateFiles(
                Path.Combine(workspace, ".agentweaver-home", ".npm"),
                "*",
                SearchOption.AllDirectories)
            .Should().NotBeEmpty("npm must write its cache beneath the sandbox-local HOME");
        executor.LastCommand.Should().NotBeNull();
        executor.LastCommand!.Environment.Should().Contain(
            new KeyValuePair<string, string>("HOME", ".agentweaver-home"));
        var readWritePaths = executor.LastCommand.FilesystemPolicy.ReadWritePaths;
        readWritePaths.Should().Contain(workspace);
        readWritePaths.Should().NotContain(
            path => path.Contains(".agentweaver-home", StringComparison.OrdinalIgnoreCase),
            "the sandbox-local HOME should remain covered by the writable workspace root rather than being added as an extra writable mount");
    }

    private static CopilotAIAgent BuildAgent(ISandboxExecutor executor)
    {
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(),
            new NullGitHubTokenStore(),
            new FixedInstallationScopeStub());
        return new CopilotAIAgent(
            factory,
            new FixedInstallationScopeStub(),
            executor,
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance);
    }

    private SandboxToolContext BuildContext(
        ISandboxExecutor executor,
        ShellExecutionTracker tracker,
        string? workspace = null,
        string? scratchDirectory = null) =>
        new(
            AgentId: "agent",
            WorkingDirectory: workspace ?? _root,
            SandboxRoot: workspace ?? _root,
            Executor: executor,
            FileTools: new SandboxedFileTools(workspace ?? _root),
            SearchTools: new SandboxedSearchTools(workspace ?? _root),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: true, DefaultTimeoutMs: 600_000)
            {
                DestructiveCommandPatterns = ["rm -rf"],
                RejectBackgroundCommands = true,
                RejectDestructiveCommands = true,
                MaximumTimeoutMs = 600_000,
            },
            Logger: NullLogger.Instance,
            ShellExecutionTracker: tracker,
            ScratchDirectory: scratchDirectory);

    private sealed class RecordingExecutor(ISandboxExecutor inner) : ISandboxExecutor
    {
        public SandboxCommand? LastCommand { get; private set; }
        public bool IsRealIsolation => inner.IsRealIsolation;
        public string BackendName => inner.BackendName;
        public string SelectionReason => inner.SelectionReason;
        public bool HasNetworkWarning => inner.HasNetworkWarning;
        public string? NetworkWarningMessage => inner.NetworkWarningMessage;

        public Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return inner.ExecuteAsync(command, ct);
        }

        public IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return inner.StreamAsync(command, ct);
        }
    }

    private sealed class CapturingExecutor(Action<SandboxCommand> onExecute) : ISandboxExecutor
    {
        public bool IsRealIsolation => false;
        public string BackendName => "direct";
        public string SelectionReason => "test";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;

        public Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            onExecute(command);
            return Task.FromResult(
                new SandboxExecResult(0, "ok", "", TimedOut: false, OutputTruncated: false));
        }

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await ExecuteAsync(command, ct);
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, result.Stdout);
        }
    }

    private sealed class CountingExecutor(bool blockFirstCall = false) : ISandboxExecutor
    {
        private int _active;
        private int _executeCalls;
        private int _maxConcurrent;

        public bool IsRealIsolation => false;
        public string BackendName => "direct";
        public string SelectionReason => "test";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;
        public int ExecuteCalls => Volatile.Read(ref _executeCalls);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _executeCalls);
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maxConcurrent, active);
            try
            {
                if (call == 1)
                {
                    FirstCallStarted.TrySetResult();
                    if (blockFirstCall)
                        await ReleaseFirstCall.Task.WaitAsync(ct);
                }

                return new SandboxExecResult(0, "ok", "", TimedOut: false, OutputTruncated: false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await ExecuteAsync(command, ct);
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, result.Stdout);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
