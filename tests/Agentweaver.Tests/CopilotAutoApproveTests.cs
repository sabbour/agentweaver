using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Tests for the per-run auto-approve-tools option (Feature 008, Feature A). Exercises the real
/// <see cref="GitHubCopilotAgentRunner.BuildPermissionHandler"/> permission handler with the option
/// ON, proving:
/// <list type="bullet">
/// <item>an allow-with-approval request (<c>web_fetch</c> / <see cref="PermissionRequestUrl"/>) is
/// auto-granted without stalling and emits a <c>tool.auto_approved</c> audit event;</item>
/// <item>SAFETY: a policy-DENIED tool (an out-of-sandbox read) is STILL rejected — auto-approve only
/// short-circuits the HITL wait for not-denied tools and never bypasses governance.</item>
/// </list>
/// </summary>
public sealed class CopilotAutoApproveTests : IDisposable
{
    private const string RunId = "auto-approve-run";
    private readonly string _tempDir;

    public CopilotAutoApproveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"auto-approve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AutoApproveOn_WebFetchUrl_AutoGrantedAndEmitsAuditEvent()
    {
        using var governance = BuildGovernance();
        var options = new InMemoryRunOptionsStore();
        options.Set(RunId, new RunOptions(AutoApproveTools: true));
        var runner = BuildRunner(options);

        var emitted = new List<(string Type, object Payload)>();
        var handler = runner.BuildPermissionHandler(
            governance,
            runId: RunId,
            workingDirectory: _tempDir,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (type, payload) => emitted.Add((type, payload)),
            runCt: CancellationToken.None);

        var request = new PermissionRequestUrl
        {
            ToolCallId = "call-url-1",
            Url = "https://example.com/data",
            Intention = "fetch reference data",
        };

        // If auto-approve did NOT short-circuit, this would block on the gate for 5 minutes.
        var result = await handler(request, new PermissionInvocation())
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.Kind.Should().Be(PermissionRequestResultKind.Approved,
            "auto-approve-tools must grant the allow-with-approval web_fetch request without an operator");
        emitted.Should().ContainSingle(e => e.Type == EventTypes.ToolAutoApproved,
            "every auto-grant must be logged as a tool.auto_approved audit event");
    }

    [Fact]
    public async Task AutoApproveOn_PolicyDeniedTool_StillRejected()
    {
        // SAFETY (mandatory): auto-approve must NEVER override a policy deny. An out-of-sandbox read
        // is denied by SandboxGovernance regardless of the run option.
        using var governance = BuildGovernance();
        var options = new InMemoryRunOptionsStore();
        options.Set(RunId, new RunOptions(AutoApproveTools: true));
        var runner = BuildRunner(options);

        var handler = runner.BuildPermissionHandler(
            governance,
            runId: RunId,
            workingDirectory: _tempDir,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (_, _) => { },
            runCt: CancellationToken.None);

        var outOfSandbox = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config\SAM"
            : "/etc/shadow";
        var argsEl = System.Text.Json.JsonSerializer.SerializeToElement(new { path = outOfSandbox });
        var request = new PermissionRequestCustomTool
        {
            ToolName = "read_file",
            ToolCallId = "call-deny-1",
            ToolDescription = "Reads a file.",
            Args = argsEl,
        };

        var result = await handler(request, new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Rejected,
            "a policy-denied tool stays denied even when auto-approve-tools is ON");
    }

    [Fact]
    public async Task AutoApproveOff_PolicyDeniedTool_StillRejected()
    {
        using var governance = BuildGovernance();
        var runner = BuildRunner(new InMemoryRunOptionsStore()); // option OFF (default)

        var handler = runner.BuildPermissionHandler(
            governance,
            runId: RunId,
            workingDirectory: _tempDir,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (_, _) => { },
            runCt: CancellationToken.None);

        var outOfSandbox = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config\SAM"
            : "/etc/shadow";
        var argsEl = System.Text.Json.JsonSerializer.SerializeToElement(new { path = outOfSandbox });
        var request = new PermissionRequestCustomTool
        {
            ToolName = "read_file",
            ToolCallId = "call-deny-2",
            ToolDescription = "Reads a file.",
            Args = argsEl,
        };

        var result = await handler(request, new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Rejected);
    }

    private GitHubCopilotAgentRunner BuildRunner(IRunOptionsStore options)
    {
        var config = new ConfigurationBuilder().Build();
        var factory = new GitHubCopilotClientFactory(config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new GitHubCopilotAgentRunner(
            factory,
            new FixedInstallationScopeStub(),
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<GitHubCopilotAgentRunner>.Instance,
            questionGate: null,
            runOptions: options);
    }

    private SandboxGovernance BuildGovernance()
        => SandboxGovernance.Create(
            _tempDir, RunId,
            SandboxExecutorFactory.CreatePassthrough(),
            SandboxPolicy.Default(_tempDir),
            NullLogger.Instance);
}
