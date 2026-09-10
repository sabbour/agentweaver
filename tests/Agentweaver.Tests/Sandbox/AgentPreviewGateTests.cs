using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Unit tests for <see cref="AgentPreviewGate"/> — the human-in-the-loop approval seam behind the
/// agent-initiated <c>start_preview</c> tool. Verifies the auto-approve sources (global config,
/// scoped policy) grant unattended, that the safe-tool run option does not bypass preview, that an operator grant resolves the gate, and
/// that deny / timeout produce distinct final outcomes.
/// </summary>
[Trait("Category", "ProcessEnvironment")]
public sealed class AgentPreviewGateTests
{
    private const string RunId = "run-preview-1";
    private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromMilliseconds(250);
    private const string ApprovalTimeoutEnvVar = "SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES";
    private static readonly object ApprovalTimeoutEnvironmentLock = new();

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

        outcome.Outcome.Should().Be(PreviewApprovalOutcome.Approved);
    }

    [Fact]
    public async Task BeginApproval_AutoApprovedRetryCreatesFreshPendingIdentity()
    {
        var gate = CreateGate(autoApproveConfigured: true, out _, out _, out var streams);

        var retry = await gate.BeginApprovalAsync(
            RunId,
            3000,
            CancellationToken.None,
            retryOfRequestId: "expired-request");

        retry.RequestId.Should().NotBeNullOrWhiteSpace().And.NotBe("expired-request");
        (await retry.Completion).Outcome.Should().Be(PreviewApprovalOutcome.Approved);
        var pending = streams.Get(RunId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.SandboxPreviewPending);
        ReadString(pending.Payload, "request_id").Should().Be(retry.RequestId);
        ReadString(pending.Payload, "retry_of_request_id").Should().Be("expired-request");
    }

    [Fact]
    public async Task RequestApproval_PerRunSafeToolPolicy_DoesNotBypassPreview()
    {
        var gate = CreateGate(
            autoApproveConfigured: false,
            out var approvalGate,
            out var runOptions,
            out var streams,
            timeout: TimeSpan.FromSeconds(5));
        runOptions.SetAutoApproveTools(RunId, true);

        var pending = gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);
        var requestId = await WaitForRequestIdAsync(streams);

        approvalGate.Deny(RunId, requestId).Should().BeTrue();
        (await pending).Outcome.Should().Be(PreviewApprovalOutcome.Denied,
            "safe-tool auto-approval must not bypass the separate preview approval boundary");
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

        (await task).Outcome.Should().Be(PreviewApprovalOutcome.Approved);
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
        (await task).Outcome.Should().Be(PreviewApprovalOutcome.Denied);
    }

    [Fact]
    public async Task RequestApproval_OperatorDeny_ReturnsDenied()
    {
        var gate = CreateGate(autoApproveConfigured: false, out var approvalGate, out _, out var streams,
            timeout: TimeSpan.FromSeconds(5));

        var task = gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);
        var requestId = await WaitForRequestIdAsync(streams);

        approvalGate.Deny(RunId, requestId).Should().BeTrue();

        (await task).Outcome.Should().Be(PreviewApprovalOutcome.Denied);
    }

    [Fact]
    public async Task RequestApproval_Timeout_ReturnsTimedOut()
    {
        var gate = CreateGate(autoApproveConfigured: false, out _, out _, out _,
            timeout: ExpirationTimeout);

        var outcome = await gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);

        outcome.Outcome.Should().Be(PreviewApprovalOutcome.TimedOut);
        outcome.RequestId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RequestApproval_BrokenWaiter_HitsCompletionBackstop()
    {
        var approvalGate = new NeverCompletingApprovalGate();
        var streams = new RunStreamStore();
        streams.Create(RunId, "owner");
        var gate = new AgentPreviewGate(
            approvalGate,
            new InMemoryRunOptionsStore(),
            streams,
            autoApproveConfigured: false,
            NullLogger<AgentPreviewGate>.Instance,
            approvalTimeout: TimeSpan.FromMilliseconds(20),
            completionGrace: TimeSpan.FromMilliseconds(20));

        var outcome = await gate.RequestApprovalAsync(RunId, 3000, CancellationToken.None);

        outcome.Outcome.Should().Be(PreviewApprovalOutcome.TimedOut);
        streams.Get(RunId)!.GetSnapshotSince(0).Events
            .Should().ContainSingle(evt => evt.Type == EventTypes.ToolApprovalResolved);
    }

    [Fact]
    public async Task BeginApproval_RetryCreatesFreshLinkedRequest()
    {
        var gate = CreateGate(
            autoApproveConfigured: false,
            out var approvalGate,
            out _,
            out var streams,
            timeout: ExpirationTimeout);

        var first = await gate.BeginApprovalAsync(RunId, 3000, CancellationToken.None);
        (await first.Completion).Outcome.Should().Be(PreviewApprovalOutcome.TimedOut);

        var retry = await gate.BeginApprovalAsync(
            RunId,
            3000,
            CancellationToken.None,
            retryOfRequestId: first.RequestId);

        retry.RequestId.Should().NotBeNullOrWhiteSpace().And.NotBe(first.RequestId);
        var card = streams.Get(RunId)!.GetSnapshotSince(0).Events
            .Last(e => e.Type == EventTypes.ToolApprovalRequired);
        ReadString(card.Payload, "requestId").Should().Be(retry.RequestId);
        ReadString(card.Payload, "retryOfRequestId").Should().Be(first.RequestId);

        approvalGate.Deny(RunId, retry.RequestId!).Should().BeTrue();
        (await retry.Completion).Outcome.Should().Be(PreviewApprovalOutcome.Denied);
    }

    [Fact]
    public void ResolveApprovalTimeout_ConfigMinutes_UsesConfiguredValue()
    {
        WithApprovalTimeoutEnvironmentVariable(null, () =>
        {
            var configuration = BuildConfiguration(("Sandbox:Preview:ApprovalTimeoutMinutes", "20"));

            AgentPreviewGate.ResolveApprovalTimeout(configuration).Should().Be(TimeSpan.FromMinutes(20));
        });
    }

    [Fact]
    public void ResolveApprovalTimeout_ConfigInvalid_FallsBackToNamedEnvironmentVariable()
    {
        WithApprovalTimeoutEnvironmentVariable("18", () =>
        {
            var configuration = BuildConfiguration(("Sandbox:Preview:ApprovalTimeoutMinutes", "not-a-number"));

            AgentPreviewGate.ResolveApprovalTimeout(configuration).Should().Be(TimeSpan.FromMinutes(18));
        });
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("wat", 30)]
    [InlineData("0", 1)]
    [InlineData("-4", 1)]
    [InlineData("2000", 1440)]
    public void ResolveApprovalTimeout_UsesDefaultOrMinimum(string? configuredMinutes, int expectedMinutes)
    {
        WithApprovalTimeoutEnvironmentVariable(null, () =>
        {
            var values = new Dictionary<string, string?>();
            if (configuredMinutes is not null)
                values["Sandbox:Preview:ApprovalTimeoutMinutes"] = configuredMinutes;

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            AgentPreviewGate.ResolveApprovalTimeout(configuration).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
        });
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

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value))
            .Build();

    private static void WithApprovalTimeoutEnvironmentVariable(string? value, Action assertion)
    {
        lock (ApprovalTimeoutEnvironmentLock)
        {
            var previousValue = Environment.GetEnvironmentVariable(ApprovalTimeoutEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(ApprovalTimeoutEnvVar, value);
                assertion();
            }
            finally
            {
                Environment.SetEnvironmentVariable(ApprovalTimeoutEnvVar, previousValue);
            }
        }
    }

    private sealed class NeverCompletingApprovalGate : IToolApprovalGate
    {
        public Task<bool> WaitForApprovalAsync(
            string runId,
            string requestId,
            string toolName,
            string? url,
            TimeSpan timeout,
            CancellationToken ct) =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public Task<bool> GrantAsync(string runId, string requestId, ApprovalScope scope) =>
            Task.FromResult(false);

        public bool Deny(string runId, string requestId) => false;
        public bool IsAutoApproved(string runId, string toolName, string? url) => false;
        public ToolApprovalRequestState GetRequestState(string runId, string requestId) =>
            ToolApprovalRequestState.Pending;
        public void Clear(string runId) { }
        public void RegisterParentRun(string childRunId, string parentRunId) { }
    }
}
