using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Unit tests for <see cref="AgentPreviewGate"/> — the human-in-the-loop approval seam behind the
/// agent-initiated <c>start_preview</c> tool. Verifies the auto-approve sources (global config,
/// per-run option, scoped policy) grant unattended, that an operator grant resolves the gate, and
/// that deny / timeout produce <see cref="PreviewApprovalOutcome.DeniedOrTimedOut"/>.
/// </summary>
public sealed class AgentPreviewGateTests
{
    private const string RunId = "run-preview-1";

    private static AgentPreviewGate CreateGate(
        bool autoApproveConfigured,
        out InMemoryToolApprovalGate approvalGate,
        out InMemoryRunOptionsStore runOptions,
        out RunStreamStore streams,
        TimeSpan? timeout = null)
    {
        approvalGate = new InMemoryToolApprovalGate();
        runOptions = new InMemoryRunOptionsStore();
        streams = new RunStreamStore();
        streams.Create(RunId, "owner");
        return new AgentPreviewGate(
            approvalGate, runOptions, streams, autoApproveConfigured,
            NullLogger<AgentPreviewGate>.Instance, timeout);
    }

    [Fact]
    public async Task RequestApproval_GlobalAutoApprove_GrantsImmediately()
    {
        var gate = CreateGate(autoApproveConfigured: true, out _, out _, out _);

        var outcome = await gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);

        outcome.Should().Be(PreviewApprovalOutcome.Approved);
    }

    [Fact]
    public async Task RequestApproval_PerRunAutoApproveTools_GrantsImmediately()
    {
        var gate = CreateGate(autoApproveConfigured: false, out _, out var runOptions, out _);
        runOptions.SetAutoApproveTools(RunId, true);

        var outcome = await gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);

        outcome.Should().Be(PreviewApprovalOutcome.Approved);
    }

    [Fact]
    public async Task RequestApproval_OperatorGrant_Resolves()
    {
        var gate = CreateGate(autoApproveConfigured: false, out var approvalGate, out _, out var streams,
            timeout: TimeSpan.FromSeconds(5));

        var task = gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);
        var requestId = await WaitForRequestIdAsync(streams);

        var resolved = await approvalGate.GrantAsync(RunId, requestId, ApprovalScope.Once);
        resolved.Should().BeTrue();

        (await task).Should().Be(PreviewApprovalOutcome.Approved);
    }

    [Fact]
    public async Task RequestApproval_EmitsHitlCard()
    {
        var gate = CreateGate(autoApproveConfigured: false, out var approvalGate, out _, out var streams,
            timeout: TimeSpan.FromSeconds(5));

        var task = gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);
        var requestId = await WaitForRequestIdAsync(streams);

        var card = streams.Get(RunId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.ToolApprovalRequired);
        ReadString(card.Payload, "toolName").Should().Be("start_preview");
        ReadString(card.Payload, "url").Should().Contain("3000");

        // Resolve so the awaiting task completes deterministically.
        approvalGate.Deny(RunId, requestId);
        (await task).Should().Be(PreviewApprovalOutcome.DeniedOrTimedOut);
    }

    [Fact]
    public async Task RequestApproval_OperatorDeny_ReturnsDenied()
    {
        var gate = CreateGate(autoApproveConfigured: false, out var approvalGate, out _, out var streams,
            timeout: TimeSpan.FromSeconds(5));

        var task = gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);
        var requestId = await WaitForRequestIdAsync(streams);

        approvalGate.Deny(RunId, requestId).Should().BeTrue();

        (await task).Should().Be(PreviewApprovalOutcome.DeniedOrTimedOut);
    }

    [Fact]
    public async Task RequestApproval_Timeout_ReturnsDenied()
    {
        var gate = CreateGate(autoApproveConfigured: false, out _, out _, out _,
            timeout: TimeSpan.FromMilliseconds(50));

        var outcome = await gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);

        outcome.Should().Be(PreviewApprovalOutcome.DeniedOrTimedOut);
    }

    private static async Task<string> WaitForRequestIdAsync(RunStreamStore streams)
    {
        for (var i = 0; i < 200; i++)
        {
            var card = streams.Get(RunId)!.GetSnapshotSince(0).Events
                .FirstOrDefault(e => e.Type == EventTypes.ToolApprovalRequired);
            if (card is not null)
                return ReadString(card.Payload, "requestId");
            await Task.Delay(10);
        }

        throw new InvalidOperationException("ToolApprovalRequired card was not emitted in time.");
    }

    private static string ReadString(object payload, string property) =>
        payload.GetType().GetProperty(property)!.GetValue(payload)!.ToString()!;
}
