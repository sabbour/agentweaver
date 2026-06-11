using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.AgentRuntime;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentTools;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Runtime;

/// <summary>
/// Unit tests for B2: report_intent handling in GitHubCopilotAgentRunner.
/// Verifies:
///   - The session config tool list for the Copilot runner contains EXACTLY report_intent.
///   - SanitizeIntent produces safe, bounded output.
///   - The BuildSessionConfigTools method returns one function named report_intent.
/// </summary>
public sealed class CopilotReportIntentTests : IDisposable
{
    private readonly string _tempDir;

    public CopilotReportIntentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ri-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // Test 1 (B2): Session config tool list contains EXACTLY report_intent.
    // =========================================================================
    [Fact]
    public void BuildSessionConfigTools_ContainsExactlyReportIntent()
    {
        var context = BuildMinimalContext();
        var tools = GitHubCopilotAgentRunner.BuildSessionConfigTools(context);

        tools.Should().ContainSingle("only report_intent must be registered in SessionConfig.Tools");
        tools[0].Name.Should().Be("report_intent",
            "the single registered tool must be report_intent so the agent can call it");
    }

    // =========================================================================
    // Test 2 (B2): SanitizeIntent — strips control characters except \t and \n.
    // =========================================================================
    [Fact]
    public void SanitizeIntent_StripsControlCharsKeepsText()
    {
        // NUL (0x00), BEL (0x07), ESC (0x1B), DEL (0x7F) should be stripped.
        // Tab (0x09) and newline (0x0A) should be preserved.
        var raw = "Hello\x00World\x07!\t\nEnd\x1B\x7F";
        var result = GitHubCopilotAgentRunner.SanitizeIntent(raw);

        result.Should().Be("HelloWorld!\t\nEnd", "NUL, BEL, ESC, and DEL must be stripped; tab and newline kept");
    }

    // =========================================================================
    // Test 3 (B2): SanitizeIntent — caps at 2000 characters.
    // =========================================================================
    [Fact]
    public void SanitizeIntent_TruncatesAt2000Chars()
    {
        var raw = new string('a', 3000);
        var result = GitHubCopilotAgentRunner.SanitizeIntent(raw);

        result.Length.Should().Be(2000, "intent must be capped at 2000 characters");
    }

    // =========================================================================
    // Test 4 (B2): SanitizeIntent — normalizes CR LF and bare CR to LF.
    // =========================================================================
    [Fact]
    public void SanitizeIntent_NormalizesLineEndings()
    {
        var raw = "line1\r\nline2\rline3\nline4";
        var result = GitHubCopilotAgentRunner.SanitizeIntent(raw);

        result.Should().Be("line1\nline2\nline3\nline4",
            "CR LF and bare CR must be normalized to LF");
    }

    // =========================================================================
    // Test 5 (B2): SanitizeIntent — strips C1 control characters (0x80–0x9F).
    // =========================================================================
    [Fact]
    public void SanitizeIntent_StripsC1ControlChars()
    {
        // U+0080 (PAD) and U+009F (APC) should be stripped.
        // Use explicit \uNNNN escapes to avoid the C# \xNN greedy-hex-digit ambiguity.
        var raw = "a\u0080b\u009Fc";
        var result = GitHubCopilotAgentRunner.SanitizeIntent(raw);

        result.Should().Be("abc", "C1 control chars (U+0080–U+009F) must be stripped");
    }

    // =========================================================================
    // Test 6 (B2): SanitizeIntent — null/empty input returns empty string.
    // =========================================================================
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeIntent_NullOrEmpty_ReturnsEmpty(string? input)
    {
        var result = GitHubCopilotAgentRunner.SanitizeIntent(input);
        result.Should().BeEmpty();
    }

    // =========================================================================
    // Test 7 (B2/FIX 4): report_intent handler auto-approves, emits ONE
    // agent.intent with sanitized text, and emits NO tool.call / tool.error.
    // =========================================================================
    [Fact]
    public async Task BuildPermissionHandler_ReportIntent_AutoApprovedAndEmitsAgentIntent()
    {
        // Arrange
        using var governance = BuildGovernance();
        var runner = BuildRunner();
        var emittedEvents = new List<(string Type, object Payload)>();
        var toolCalls = new List<(string CallId, string ToolName, object? Args)>();
        var toolErrors = new List<(string CallId, string Message)>();

        var handler = runner.BuildPermissionHandler(
            governance,
            runId: "test-run",
            workingDirectory: _tempDir,
            emitToolCallOnce: (callId, toolName, args) => toolCalls.Add((callId, toolName, args)),
            emitToolErrorOnce: (callId, msg) => toolErrors.Add((callId, msg)),
            emit: (type, payload) => emittedEvents.Add((type, payload)),
            runCt: CancellationToken.None);

        // intent contains a NUL (0x00) control character that must be stripped
        var argsEl = System.Text.Json.JsonSerializer.SerializeToElement(new { intent = "Hello\x00World" });
        var request = new PermissionRequestCustomTool
        {
            ToolName = "report_intent",
            ToolCallId = "call-ri-1",
            ToolDescription = "Reports the agent's intent for the next action.",
            Args = argsEl,
        };

        // Act
        var result = await handler(request, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved,
            "report_intent must always be auto-approved without governance consultation");

        emittedEvents.Should().ContainSingle(e => e.Type == EventTypes.AgentIntent,
            "exactly one agent.intent event must be emitted for report_intent");

        toolCalls.Should().BeEmpty("report_intent must NOT emit a tool.call event");
        toolErrors.Should().BeEmpty("report_intent must NOT emit a tool.error event");

        // Verify the NUL char is stripped from the emitted intent payload.
        var intentEvent = emittedEvents.Single(e => e.Type == EventTypes.AgentIntent);
        var intentJson = System.Text.Json.JsonSerializer.Serialize(intentEvent.Payload);
        intentJson.Should().Contain("HelloWorld", "NUL must be stripped leaving letters adjacent");
        intentJson.Should().NotContain("\\u0000", "NUL must not appear in the sanitized payload");
    }

    // =========================================================================
    // Test 8 (B2/FIX 4): a non-report_intent custom tool is routed through
    // governance (NOT auto-approved) — observable via tool.call emission.
    // =========================================================================
    [Fact]
    public async Task BuildPermissionHandler_UnknownCustomTool_RoutedThroughGovernance()
    {
        // Arrange
        using var governance = BuildGovernance();
        var runner = BuildRunner();
        var emittedEvents = new List<(string Type, object Payload)>();
        var toolCalls = new List<(string CallId, string ToolName, object? Args)>();
        var toolErrors = new List<(string CallId, string Message)>();

        var handler = runner.BuildPermissionHandler(
            governance,
            runId: "test-run",
            workingDirectory: _tempDir,
            emitToolCallOnce: (callId, toolName, args) => toolCalls.Add((callId, toolName, args)),
            emitToolErrorOnce: (callId, msg) => toolErrors.Add((callId, msg)),
            emit: (type, payload) => emittedEvents.Add((type, payload)),
            runCt: CancellationToken.None);

        var argsEl = System.Text.Json.JsonSerializer.SerializeToElement(new { operation = "wipe" });
        var request = new PermissionRequestCustomTool
        {
            ToolName = "delete_everything",
            ToolCallId = "call-de-1",
            ToolDescription = "Deletes all files.",
            Args = argsEl,
        };

        // Act
        var result = await handler(request, new PermissionInvocation());

        // Assert: NOT auto-approved — governance was consulted. Observable via tool.call
        // being emitted (report_intent suppresses tool.call entirely; other tools do not).
        toolCalls.Should().NotBeEmpty(
            "a non-report_intent custom tool must emit tool.call, proving governance was consulted");

        emittedEvents.Should().NotContain(e => e.Type == EventTypes.AgentIntent,
            "agent.intent must only be emitted for report_intent, not for other custom tools");

        // The result is Approved or Rejected depending on the governance policy.
        // What matters is the handler reached governance instead of short-circuiting.
        result.Kind.Should().BeOneOf(
            PermissionRequestResultKind.Approved, PermissionRequestResultKind.Rejected,
            "governance must produce a definitive allow/deny decision for non-report_intent tools");
    }

    // =========================================================================
    // Helpers for BuildPermissionHandler tests
    // =========================================================================

    private GitHubCopilotAgentRunner BuildRunner()
    {
        var config = new ConfigurationBuilder().Build();
        var factory = new GitHubCopilotClientFactory(config);
        return new GitHubCopilotAgentRunner(
            factory,
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<GitHubCopilotAgentRunner>.Instance);
    }

    private SandboxGovernance BuildGovernance()
        => SandboxGovernance.Create(
            _tempDir, "test-run",
            SandboxExecutorFactory.CreatePassthrough(),
            SandboxPolicy.Default(_tempDir),
            NullLogger.Instance);

    // =========================================================================
    // Helpers
    // =========================================================================

    private SandboxToolContext BuildMinimalContext()
    {
        var logger = NullLogger.Instance;
        return new SandboxToolContext(
            AgentId: "test-agent",
            WorkingDirectory: _tempDir,
            SandboxRoot: _tempDir,
            Executor: SandboxExecutorFactory.CreatePassthrough(),
            FileTools: new SandboxedFileTools(_tempDir),
            SearchTools: new SandboxedSearchTools(_tempDir),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: false),
            Logger: logger);
    }
}
