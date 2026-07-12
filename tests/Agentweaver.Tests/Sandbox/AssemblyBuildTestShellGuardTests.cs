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

        result.Should().BeOfType<PermissionDecisionDeniedByRules>();
        errors.Should().ContainSingle().Which.Should().Contain("controlled run_command");
    }

    [Fact]
    public void Assembly_session_registers_controlled_run_command_custom_tool()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
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
            ShellSemaphore: new SemaphoreSlim(1, 1));

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
        using var semaphore = new SemaphoreSlim(1, 1);
        var context = BuildContext(executor, semaphore);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            context,
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");

        var result = await tool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = command }));

        result?.ToString().Should().Contain("rejected");
        executor.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Controlled_run_command_serializes_concurrent_invocations()
    {
        var executor = new CountingExecutor(blockFirstCall: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            BuildContext(executor, semaphore),
            includeControlledRunCommand: true).Single(t => t.Name == "run_command");
        var args = new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = "dotnet test" });

        var first = tool.InvokeAsync(args).AsTask();
        await executor.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = tool.InvokeAsync(args).AsTask();
        await Task.Delay(50);
        executor.ExecuteCalls.Should().Be(1);

        executor.ReleaseFirstCall.TrySetResult();
        await Task.WhenAll(first, second);

        executor.ExecuteCalls.Should().Be(2);
        executor.MaxConcurrent.Should().Be(1);
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

    private SandboxToolContext BuildContext(ISandboxExecutor executor, SemaphoreSlim semaphore) =>
        new(
            AgentId: "agent",
            WorkingDirectory: _root,
            SandboxRoot: _root,
            Executor: executor,
            FileTools: new SandboxedFileTools(_root),
            SearchTools: new SandboxedSearchTools(_root),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: true, DefaultTimeoutMs: 600_000)
            {
                DestructiveCommandPatterns = ["rm -rf"],
                RejectBackgroundCommands = true,
                RejectDestructiveCommands = true,
                MaximumTimeoutMs = 600_000,
            },
            Logger: NullLogger.Instance,
            ShellSemaphore: semaphore);

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
