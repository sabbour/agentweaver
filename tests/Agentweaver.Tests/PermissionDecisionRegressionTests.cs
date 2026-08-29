using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Agentweaver.Tests.Runtime;

public sealed class PermissionDecisionRegressionTests : IDisposable
{
    private const string RunId = "permission-decision-run";
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".permission-decision-tests",
        Guid.NewGuid().ToString("n"));

    public PermissionDecisionRegressionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public async Task Custom_tool_policy_denial_returns_reject_with_audit_reason(string implementation)
    {
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var handler = BuildHandler(implementation, governance, new ImmediateDenyGate(), (_, _, _) => { }, errors);
        var outsidePath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config\SAM"
            : "/etc/shadow";

        var result = await handler(
            new PermissionRequestCustomTool
            {
                ToolName = "read_file",
                ToolCallId = "custom-policy-deny",
                ToolDescription = "Reads a file.",
                Args = System.Text.Json.JsonSerializer.SerializeToElement(new { path = outsidePath }),
            },
            new PermissionInvocation());

        AssertRejectedWithEmittedReason(result, errors);
    }

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public async Task Custom_tool_fail_closed_returns_reject_with_internal_error(string implementation)
    {
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var calls = 0;
        var handler = BuildHandler(
            implementation,
            governance,
            new ImmediateDenyGate(),
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("injected callback failure");
            },
            errors);

        var result = await handler(
            new PermissionRequestCustomTool
            {
                ToolName = "read_file",
                ToolCallId = "custom-fail-closed",
                ToolDescription = "Reads a file.",
                Args = System.Text.Json.JsonSerializer.SerializeToElement(new { path = _root }),
            },
            new PermissionInvocation());

        AssertRejected(result, "Operation denied: internal error evaluating sandbox policy.");
        errors.Should().ContainSingle().Which.Should().Be(
            "Operation denied: internal error evaluating sandbox policy.");
    }

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public async Task Native_request_policy_denial_returns_reject_with_audit_reason(string implementation)
    {
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var handler = BuildHandler(implementation, governance, new ImmediateDenyGate(), (_, _, _) => { }, errors);
        var outsidePath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config\SAM"
            : "/etc/shadow";

        var result = await handler(
            new PermissionRequestRead
            {
                Path = outsidePath,
                Intention = "read protected file",
                ToolCallId = "native-policy-deny",
            },
            new PermissionInvocation());

        AssertRejectedWithEmittedReason(result, errors);
    }

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public async Task Native_request_fail_closed_returns_reject_with_internal_error(string implementation)
    {
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var calls = 0;
        var handler = BuildHandler(
            implementation,
            governance,
            new ImmediateDenyGate(),
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("injected callback failure");
            },
            errors);

        var result = await handler(
            new PermissionRequestRead
            {
                Path = Path.Combine(_root, "file.txt"),
                Intention = "read file",
                ToolCallId = "native-fail-closed",
            },
            new PermissionInvocation());

        AssertRejected(result, "Operation denied: internal error evaluating sandbox policy.");
        errors.Should().ContainSingle().Which.Should().Be(
            "Operation denied: internal error evaluating sandbox policy.");
    }

    [Theory]
    [InlineData("CopilotAIAgent")]
    [InlineData("GitHubCopilotAgentRunner")]
    public async Task Operator_url_denial_returns_reject_with_operator_feedback(string implementation)
    {
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var handler = BuildHandler(implementation, governance, new ImmediateDenyGate(), (_, _, _) => { }, errors);

        var result = await handler(
            new PermissionRequestUrl
            {
                ToolCallId = "url-deny",
                Url = "https://example.com",
                Intention = "fetch data",
            },
            new PermissionInvocation());

        AssertRejected(result, "URL fetch was denied by the operator.");
        errors.Should().ContainSingle().Which.Should().Be(
            "URL fetch was denied by the operator.");
    }

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildHandler(
        string implementation,
        SandboxGovernance governance,
        IToolApprovalGate approvalGate,
        Action<string, string, object?> emitToolCall,
        List<string> errors)
    {
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(), new FixedGitHubCopilotCapabilityCredentialProvider());

        return implementation switch
        {
            "CopilotAIAgent" => new CopilotAIAgent(
                factory,
                SandboxExecutorFactory.CreatePassthrough(),
                new StubPolicyStore(),
                new InMemoryShellApprovalStore(),
                approvalGate,
                NullLogger<CopilotAIAgent>.Instance)
                .BuildPermissionHandler(
                    governance, RunId, _root, emitToolCall,
                    (_, reason) => errors.Add(reason), (_, _) => { }, CancellationToken.None),
            "GitHubCopilotAgentRunner" => new GitHubCopilotAgentRunner(
                factory,
                SandboxExecutorFactory.CreatePassthrough(),
                new StubPolicyStore(),
                new InMemoryShellApprovalStore(),
                approvalGate,
                NullLogger<GitHubCopilotAgentRunner>.Instance)
                .BuildPermissionHandler(
                    governance, RunId, _root, emitToolCall,
                    (_, reason) => errors.Add(reason), (_, _) => { }, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(implementation)),
        };
    }

    private SandboxGovernance BuildGovernance() =>
        SandboxGovernance.Create(
            _root,
            RunId,
            SandboxExecutorFactory.CreatePassthrough(),
            SandboxPolicy.Default(_root),
            NullLogger.Instance);

    private static void AssertRejectedWithEmittedReason(
        PermissionDecision result,
        List<string> errors)
    {
        errors.Should().ContainSingle();
        errors[0].Should().NotBeNullOrWhiteSpace();
        AssertRejected(result, errors[0]);
    }

    private static void AssertRejected(PermissionDecision result, string feedback)
    {
        var rejected = result.Should().BeOfType<PermissionDecisionReject>().Subject;
        rejected.Kind.Should().Be("reject");
        rejected.Feedback.Should().Be(feedback);

        var payload = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        root.GetProperty("kind").GetString().Should().Be("reject");
        root.GetProperty("feedback").GetString().Should().Be(feedback);
        root.GetProperty("feedback").GetString().Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ImmediateDenyGate : IToolApprovalGate
    {
        public Task<bool> WaitForApprovalAsync(
            string runId,
            string requestId,
            string toolName,
            string? url,
            TimeSpan timeout,
            CancellationToken ct) => Task.FromResult(false);

        public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope) =>
            Task.FromResult(false);

        public bool Deny(string runId, string requestId) => false;
        public bool IsAutoApproved(string runId, string toolName, string? url) => false;
        public void Clear(string runId) { }
        public void RegisterParentRun(string childRunId, string parentRunId) { }
    }
}
