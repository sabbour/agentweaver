using Agentweaver.AgentRuntime;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Regression guard for the "wasted first shell attempt" problem: the model would call the SDK's
/// native bash/shell tool, get rejected with "Native Copilot shell is disabled; use the sandboxed
/// run_command tool", then retry the identical command via <c>run_command</c> — burning a whole
/// tool-calling round-trip every time. The SDK always advertises its native shell tool and there is
/// no supported switch to unregister it, so the runtime denies it in the permission handler
/// (CopilotAIAgent / GitHubCopilotAgentRunner) and instead exposes the sandboxed
/// <c>run_command</c> custom tool.
///
/// These tests lock the base system prompt to proactively steer the model to <c>run_command</c>
/// from the start, so the native-shell denial path is not exercised as a matter of course.
/// </summary>
public sealed class ShellRoutingPromptGuidanceTests
{
    [Fact]
    public void BasePrompt_InstructsModelToUseRunCommandForShell()
    {
        AgentBasePrompt.Base.Should().Contain("run_command",
            "the base prompt must name the sandboxed shell tool so the model prefers it from the first turn");
        AgentBasePrompt.Base.Should().Contain("native",
            "the base prompt must tell the model the native shell tool is unavailable");
    }

    [Fact]
    public void BasePrompt_WarnsThatNativeShellIsDisabledToAvoidWastedRetry()
    {
        // The guidance must make clear the native shell is a dead end, not a fallback to try first —
        // this is what eliminates the reject-then-retry round-trip.
        AgentBasePrompt.Base.Should().Contain("disabled",
            "the prompt must state the native shell is disabled");
        AgentBasePrompt.Base.Should().MatchRegex("(?i)go straight to run_command|do not attempt the native shell first",
            "the prompt must tell the model not to try the native shell first and wait for it to fail");
    }

    [Fact]
    public void BasePrompt_ShellGuidanceMatchesTheRuntimeDenialMessage()
    {
        // Keep the prompt's phrasing anchored to the exact denial string emitted by the permission
        // handlers, so the two never drift and the model learns the precise failure it must avoid.
        const string denial =
            "Native Copilot shell is disabled; use the sandboxed run_command tool";
        AgentBasePrompt.Base.Should().Contain(denial,
            "the prompt should quote the runtime's native-shell denial so the model recognizes and preempts it");
    }
}
