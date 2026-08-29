using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Tests for the tool-approval heartbeat (#212). When the permission handler blocks on a
/// tool-approval gate, it must emit <see cref="EventTypes.ToolApprovalRequired"/> FIRST and then
/// punctuate the wait with <see cref="EventTypes.ToolApprovalPending"/> heartbeats at the configured
/// cadence, so the pod's outbound event stream keeps flowing (the buffered approval frame is
/// delivered + durably persisted promptly) and the parent coordinator's stall timer is reset while
/// the operator decides. Fast approvals must NOT emit any spurious heartbeats.
/// </summary>
public sealed class ToolApprovalHeartbeatTests : IDisposable
{
    private const string RunId = "approval-heartbeat-run";
    private readonly string _tempDir;

    public ToolApprovalHeartbeatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"approval-hb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BlockedApproval_EmitsRequiredFirstThenPendingHeartbeats_UntilResolved()
    {
        using var governance = BuildGovernance();
        var gate = new InMemoryToolApprovalGate();
        var runner = BuildRunner(gate);
        // Shrink the cadence so several heartbeats elapse within the test window.
        runner.ApprovalHeartbeatInterval = TimeSpan.FromMilliseconds(40);

        var emitted = new List<(string Type, object Payload)>();
        var emitLock = new object();
        var handler = runner.BuildPermissionHandler(
            governance,
            runId: RunId,
            workingDirectory: _tempDir,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (type, payload) => { lock (emitLock) emitted.Add((type, payload)); },
            runCt: CancellationToken.None);

        const string toolCallId = "call-url-hb";
        var request = new PermissionRequestUrl
        {
            ToolCallId = toolCallId,
            Url = "https://example.com/data",
            Intention = "fetch reference data",
        };

        // The handler is SYNCHRONOUS and blocks on the gate — run it on a background task so the
        // test thread can grant the gate from the outside.
        var handlerTask = Task.Run(() => handler(request, new PermissionInvocation()).GetAwaiter().GetResult());

        // Wait until the request is registered (Pending) to avoid a grant/registration race.
        await WaitUntilAsync(() => gate.GetRequestState(RunId, toolCallId) == ToolApprovalRequestState.Pending,
            TimeSpan.FromSeconds(5));

        // Let several heartbeat intervals elapse before granting.
        await Task.Delay(250);
        (await gate.GrantAsync(RunId, toolCallId, ApprovalScope.Once)).Should().BeTrue();

        var result = await handlerTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeOfType<PermissionDecisionApproveOnce>("granting the gate approves the fetch");

        List<(string Type, object Payload)> snapshot;
        lock (emitLock) snapshot = emitted.ToList();

        snapshot.Should().NotBeEmpty();
        snapshot[0].Type.Should().Be(EventTypes.ToolApprovalRequired,
            "the approval_required frame must be emitted BEFORE the handler blocks on the gate");

        var requiredIdx = snapshot.FindIndex(e => e.Type == EventTypes.ToolApprovalRequired);
        var firstHeartbeatIdx = snapshot.FindIndex(e => e.Type == EventTypes.ToolApprovalPending);
        firstHeartbeatIdx.Should().BeGreaterThan(requiredIdx,
            "heartbeats only start AFTER the approval_required frame");

        var heartbeats = snapshot.Where(e => e.Type == EventTypes.ToolApprovalPending).ToList();
        heartbeats.Count.Should().BeGreaterThanOrEqualTo(2,
            "a ~250ms wait with a 40ms cadence must emit at least two tool.approval_pending heartbeats");

        heartbeats.Should().OnlyContain(e => ReadProp(e.Payload, "requestId") == toolCallId,
            "each heartbeat must carry the requestId for correlation (#212)");
    }

    [Fact]
    public async Task FastApproval_EmitsSingleRequiredAndNoHeartbeats()
    {
        using var governance = BuildGovernance();
        var gate = new InMemoryToolApprovalGate();
        var runner = BuildRunner(gate);
        // Production-like cadence (20s): a prompt approval must never trip a heartbeat.
        runner.ApprovalHeartbeatInterval = TimeSpan.FromSeconds(20);

        var emitted = new List<(string Type, object Payload)>();
        var emitLock = new object();
        var handler = runner.BuildPermissionHandler(
            governance,
            runId: RunId,
            workingDirectory: _tempDir,
            emitToolCallOnce: (_, _, _) => { },
            emitToolErrorOnce: (_, _) => { },
            emit: (type, payload) => { lock (emitLock) emitted.Add((type, payload)); },
            runCt: CancellationToken.None);

        const string toolCallId = "call-url-fast";
        var request = new PermissionRequestUrl
        {
            ToolCallId = toolCallId,
            Url = "https://example.com/fast",
            Intention = "fetch reference data",
        };

        var handlerTask = Task.Run(() => handler(request, new PermissionInvocation()).GetAwaiter().GetResult());

        await WaitUntilAsync(() => gate.GetRequestState(RunId, toolCallId) == ToolApprovalRequestState.Pending,
            TimeSpan.FromSeconds(5));

        // Resolve promptly — well under the 20s heartbeat interval.
        (await gate.GrantAsync(RunId, toolCallId, ApprovalScope.Once)).Should().BeTrue();

        var result = await handlerTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeOfType<PermissionDecisionApproveOnce>();

        List<(string Type, object Payload)> snapshot;
        lock (emitLock) snapshot = emitted.ToList();

        snapshot.Count(e => e.Type == EventTypes.ToolApprovalRequired).Should().Be(1,
            "exactly one approval_required frame is emitted per gate");
        snapshot.Should().NotContain(e => e.Type == EventTypes.ToolApprovalPending,
            "a fast approval must not emit any tool.approval_pending heartbeats");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static string? ReadProp(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload)?.ToString();

    private GitHubCopilotAgentRunner BuildRunner(IToolApprovalGate gate)
    {
        var config = new ConfigurationBuilder().Build();
        var factory = new GitHubCopilotClientFactory(config, new FixedGitHubCopilotCapabilityCredentialProvider());
        return new GitHubCopilotAgentRunner(
            factory,
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            gate,
            NullLogger<GitHubCopilotAgentRunner>.Instance,
            questionGate: null,
            runOptions: new InMemoryRunOptionsStore());
    }

    private SandboxGovernance BuildGovernance()
        => SandboxGovernance.Create(
            _tempDir, RunId,
            SandboxExecutorFactory.CreatePassthrough(),
            SandboxPolicy.Default(_tempDir),
            NullLogger.Instance);
}
