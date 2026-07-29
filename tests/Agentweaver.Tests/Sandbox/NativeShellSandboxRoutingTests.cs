using System.Text.Json;
using System.Threading.Channels;
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

/// <summary>
/// Regression tests for the native-shell-bypass fix (findings-agent-runtime Alert 3). The Copilot
/// SDK's native shell tool executes commands in-process, bypassing the per-command
/// <see cref="ISandboxExecutor"/>/bubblewrap filesystem confinement (the permission gate validates
/// only the working directory, never the command text). Both runners now (1) reject EVERY native
/// shell request for every run purpose and (2) expose shell solely through the sandboxed
/// <c>run_command</c> tool, which runs the command through <see cref="ISandboxExecutor"/>.
/// </summary>
public sealed class NativeShellSandboxRoutingTests : IDisposable
{
    private const string RunId = "native-shell-run";
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, ".native-shell-tests", Guid.NewGuid().ToString("n"));

    public NativeShellSandboxRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static PermissionRequestShell ShellRequest(string command, string toolCallId = "shell-call") => new()
    {
        ToolCallId = toolCallId,
        FullCommandText = command,
        Intention = "run shell",
        Commands = [],
        HasWriteFileRedirection = false,
        PossiblePaths = [],
        PossibleUrls = [],
        CanOfferSessionApproval = false,
    };

    [Fact]
    public async Task CopilotAIAgent_denies_native_shell_by_default_for_every_run()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var governance = SandboxGovernance.Create(
            _root, RunId, executor, SandboxPolicy.Default(_root), NullLogger.Instance);
        var errors = new List<string>();

        // No denyNativeShell argument → the new default (true) must still deny native shell, so a
        // normal (non-AssemblyBuildTest) run cannot reach the SDK's in-process shell.
        var handler = BuildAgent(executor).BuildPermissionHandler(
            governance, RunId, _root,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, message) => errors.Add(message),
            emit: (_, _) => { },
            runCt: CancellationToken.None);

        var result = await handler(ShellRequest("cat /mnt/secrets-store/api-key"), new PermissionInvocation());

        var rejected = result.Should().BeOfType<PermissionDecisionReject>().Subject;
        rejected.Feedback.Should().Contain("run_command");
        errors.Should().ContainSingle().Which.Should().Be(rejected.Feedback);
    }

    [Fact]
    public async Task CopilotAIAgent_relabels_denied_native_shell_lifecycle_calls_to_run_command()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var governance = SandboxGovernance.Create(
            _root, RunId, executor, SandboxPolicy.Default(_root), NullLogger.Instance);
        var events = Channel.CreateUnbounded<RunEvent>();
        var agent = BuildAgent(executor);
        agent.SetTurnStreamWriter(events.Writer);

        var handler = agent.BuildPermissionHandler(
            governance, RunId, _root,
            agent.EmitToolCallOnce,
            agent.EmitToolErrorOnce,
            (_, _) => { },
            runCt: CancellationToken.None);

        agent.ObserveToolExecutionStarted(
            "shell-call-1",
            "bash",
            JsonSerializer.SerializeToElement(new { command = "pwd" }),
            DateTimeOffset.UtcNow);

        var result = await handler(ShellRequest("pwd", "shell-call-1"), new PermissionInvocation());

        result.Should().BeOfType<PermissionDecisionReject>();
        var emitted = DrainEvents(events.Reader);
        emitted.Count(e => e.Type == "tool.call").Should().Be(1);

        var toolCall = JsonSerializer.SerializeToElement(
            emitted.Single(e => e.Type == "tool.call").Payload);
        toolCall.GetProperty("toolName").GetString().Should().Be("run_command");

        var toolError = JsonSerializer.SerializeToElement(
            emitted.Single(e => e.Type == "tool.error").Payload);
        toolError.GetProperty("errorMessage").GetString().Should().Contain("Native Copilot shell is disabled");
    }

    [Fact]
    public async Task GitHubCopilotAgentRunner_denies_native_shell()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var governance = SandboxGovernance.Create(
            _root, RunId, executor, SandboxPolicy.Default(_root), NullLogger.Instance);
        var errors = new List<string>();

        var handler = BuildRunner(executor).BuildPermissionHandler(
            governance, RunId, _root,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, message) => errors.Add(message),
            emit: (_, _) => { },
            runCt: CancellationToken.None);

        var result = await handler(ShellRequest("id"), new PermissionInvocation());

        var rejected = result.Should().BeOfType<PermissionDecisionReject>().Subject;
        rejected.Feedback.Should().Contain("sandboxed run_command");
        errors.Should().ContainSingle().Which.Should().Be(rejected.Feedback);
    }

    [Fact]
    public async Task CopilotAIAgent_escalates_native_shell_denial_message_after_first_attempt()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        using var governance = SandboxGovernance.Create(
            _root, RunId, executor, SandboxPolicy.Default(_root), NullLogger.Instance);

        var handler = BuildAgent(executor).BuildPermissionHandler(
            governance, RunId, _root,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (_, _) => { },
            runCt: CancellationToken.None);

        var first = await handler(ShellRequest("pwd", "shell-call-1"), new PermissionInvocation());
        var second = await handler(ShellRequest("ls", "shell-call-2"), new PermissionInvocation());

        first.Should().BeOfType<PermissionDecisionReject>().Subject.Feedback.Should()
            .Be(CopilotAIAgent.BuildNativeShellDenyReason(1));
        second.Should().BeOfType<PermissionDecisionReject>().Subject.Feedback.Should()
            .Be(CopilotAIAgent.BuildNativeShellDenyReason(2))
            .And.Contain("attempt 2")
            .And.Contain("stop retrying");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_runners_register_run_command_when_shell_is_enabled(bool viaCopilotAiAgent)
    {
        var executor = SandboxExecutorFactory.CreatePassthrough(); // direct backend → run_command available
        var context = BuildContext(executor);

        var tools = viaCopilotAiAgent
            ? CopilotAIAgent.BuildSessionConfigTools(context, includeControlledRunCommand: true)
            : GitHubCopilotAgentRunner.BuildSessionConfigTools(context);

        tools.Select(t => t.Name).Should().Contain(
            "run_command",
            "shell must remain available, but only through the ISandboxExecutor-backed run_command tool");
    }

    [Fact]
    public async Task Sandboxed_run_command_routes_execution_through_the_sandbox_executor()
    {
        SandboxCommand? captured = null;
        var executor = new RecordingExecutor(cmd => captured = cmd);
        var context = BuildContext(executor);

        var runCommand = GitHubCopilotAgentRunner.BuildSessionConfigTools(context)
            .Single(t => t.Name == "run_command");

        var result = await runCommand.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["command"] = "echo hello" }));

        captured.Should().NotBeNull("run_command must execute via ISandboxExecutor, not in-process");
        captured!.CommandLine.Should().Be("echo hello");
        result?.ToString().Should().Contain("stdout");
    }

    private CopilotAIAgent BuildAgent(ISandboxExecutor executor)
    {
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new CopilotAIAgent(
            factory, new FixedInstallationScopeStub(), executor,
            new StubPolicyStore(), new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(), NullLogger<CopilotAIAgent>.Instance);
    }

    private GitHubCopilotAgentRunner BuildRunner(ISandboxExecutor executor)
    {
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new GitHubCopilotAgentRunner(
            factory, new FixedInstallationScopeStub(), executor,
            new StubPolicyStore(), new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(), NullLogger<GitHubCopilotAgentRunner>.Instance,
            questionGate: null, runOptions: new InMemoryRunOptionsStore());
    }

    private SandboxToolContext BuildContext(ISandboxExecutor executor) => new(
        AgentId: "agent",
        WorkingDirectory: _root,
        SandboxRoot: _root,
        Executor: executor,
        FileTools: new SandboxedFileTools(_root),
        SearchTools: new SandboxedSearchTools(_root),
        Redactor: SandboxOutputRedactor.Default,
        Options: new SandboxToolOptions(ShellEnabled: true),
        Logger: NullLogger.Instance);

    private static List<RunEvent> DrainEvents(ChannelReader<RunEvent> reader)
    {
        var events = new List<RunEvent>();
        while (reader.TryRead(out var evt))
            events.Add(evt);

        return events;
    }

    private sealed class RecordingExecutor(Action<SandboxCommand> onExecute) : ISandboxExecutor
    {
        public bool IsRealIsolation => false;
        public string BackendName => "direct";
        public string SelectionReason => "test";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;

        public Task<SandboxExecResult> ExecuteAsync(SandboxCommand command, CancellationToken ct = default)
        {
            onExecute(command);
            return Task.FromResult(new SandboxExecResult(0, "hello", "", TimedOut: false, OutputTruncated: false));
        }

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var result = await ExecuteAsync(command, ct);
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, result.Stdout);
        }
    }
}
