using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests;

public sealed class ToolApprovalGateTests
{
    private static InMemoryToolApprovalGate CreateGate() => new();

    // Helper: registers a pending approval with context atomically, returns the awaitable task.
    private static Task<bool> Register(
        InMemoryToolApprovalGate gate,
        string runId,
        string requestId,
        string toolName = "web_fetch",
        string url = "https://example.com")
    {
        return gate.WaitForApprovalAsync(runId, requestId, toolName, url, TimeSpan.FromMinutes(5), CancellationToken.None);
    }

    // ── Once scope ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Once_Scope_SecondIdenticalRequest_StillRequiresApproval()
    {
        var gate = CreateGate();
        const string runId = "run-1";
        const string url = "https://example.com";

        // First request: grant with Once scope.
        var firstTask = Register(gate, runId, "req-1", url: url);
        await gate.GrantAsync(runId, "req-1", ApprovalScope.Once);
        (await firstTask).Should().BeTrue();

        // Second request for the same tool+URL: should NOT be auto-approved.
        gate.IsAutoApproved(runId, "web_fetch", url).Should().BeFalse();
    }

    // ── Run scope ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Scope_SecondIdenticalRequest_SameRun_IsAutoApproved()
    {
        var gate = CreateGate();
        const string runId = "run-2";
        const string url = "https://example.com";

        // First request: grant with Run scope.
        var firstTask = Register(gate, runId, "req-1", url: url);
        await gate.GrantAsync(runId, "req-1", ApprovalScope.Run);
        (await firstTask).Should().BeTrue();

        // Second request for the same tool+URL in the same run: auto-approved.
        gate.IsAutoApproved(runId, "web_fetch", url).Should().BeTrue();
    }

    [Fact]
    public async Task Run_Scope_DifferentUrl_IsNotAutoApproved()
    {
        var gate = CreateGate();
        const string runId = "run-3";

        var firstTask = Register(gate, runId, "req-1", url: "https://example.com");
        await gate.GrantAsync(runId, "req-1", ApprovalScope.Run);
        (await firstTask).Should().BeTrue();

        // A different URL is not covered by the run-scoped policy.
        gate.IsAutoApproved(runId, "web_fetch", "https://other.com").Should().BeFalse();
    }

    // ── Always scope ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Always_Scope_SecondRequest_DifferentRun_IsAutoApproved()
    {
        var gate = CreateGate();
        const string url = "https://example.com";

        // Grant Always on run-A.
        var firstTask = Register(gate, "run-A", "req-1", url: url);
        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);
        (await firstTask).Should().BeTrue();

        // A completely different run should see the always-allowed policy.
        gate.IsAutoApproved("run-B", "web_fetch", url).Should().BeTrue();
    }

    [Fact]
    public async Task Always_Scope_SameRun_IsAlsoAutoApproved()
    {
        var gate = CreateGate();
        const string runId = "run-4";
        const string url = "https://example.com";

        var firstTask = Register(gate, runId, "req-1", url: url);
        await gate.GrantAsync(runId, "req-1", ApprovalScope.Always);
        (await firstTask).Should().BeTrue();

        gate.IsAutoApproved(runId, "web_fetch", url).Should().BeTrue();
    }

    // ── Clear ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesRunScopedEntries_ButNotAlwaysAllowed()
    {
        var gate = CreateGate();
        const string url = "https://example.com";

        // Run-scoped policy on run-A.
        var runTask = Register(gate, "run-A", "req-run", url: url);
        await gate.GrantAsync("run-A", "req-run", ApprovalScope.Run);
        (await runTask).Should().BeTrue();

        // Always-allowed policy also on run-A.
        var alwaysTask = Register(gate, "run-A", "req-always", url: "https://always.com");
        await gate.GrantAsync("run-A", "req-always", ApprovalScope.Always);
        (await alwaysTask).Should().BeTrue();

        // Sanity: both are active before Clear.
        gate.IsAutoApproved("run-A", "web_fetch", url).Should().BeTrue();
        gate.IsAutoApproved("run-A", "web_fetch", "https://always.com").Should().BeTrue();

        // Clear the run.
        gate.Clear("run-A");

        // Run-scoped entry gone.
        gate.IsAutoApproved("run-A", "web_fetch", url).Should().BeFalse();

        // Always-allowed entry survives.
        gate.IsAutoApproved("run-A", "web_fetch", "https://always.com").Should().BeTrue();
        gate.IsAutoApproved("run-B", "web_fetch", "https://always.com").Should().BeTrue();
    }

    [Fact]
    public async Task Clear_DeniesAllPendingRequests()
    {
        var gate = CreateGate();
        const string runId = "run-5";

        var pendingTask = Register(gate, runId, "req-pending");

        gate.Clear(runId);

        // The pending TCS is resolved as false (denied); await with a generous timeout.
        var result = await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeFalse();
    }

    // ── Sibling propagation (RegisterParentRun, commit cb7fbbf) ───────────────────

    [Fact]
    public async Task ToolScope_GrantInChildA_PropagatesToSiblingChildB()
    {
        var gate = CreateGate();
        const string parent = "coord-1";
        const string childA = "child-A";
        const string childB = "child-B";

        gate.RegisterParentRun(childA, parent);
        gate.RegisterParentRun(childB, parent);

        // Grant Tool scope in child A.
        var task = Register(gate, childA, "req-1", url: "https://example.com");
        await gate.GrantAsync(childA, "req-1", ApprovalScope.Tool);
        (await task).Should().BeTrue();

        // Sibling child B sees the policy for the same tool (any URL).
        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeTrue();
    }

    [Fact]
    public async Task ToolScope_PropagatesAcrossUrls_RunScope_DoesNot()
    {
        var gate = CreateGate();
        const string parent = "coord-2";
        const string childA = "child-A2";
        const string childB = "child-B2";

        gate.RegisterParentRun(childA, parent);
        gate.RegisterParentRun(childB, parent);

        // Run scope is URL-specific: grant for example.com in child A.
        var runTask = Register(gate, childA, "req-run", toolName: "web_fetch", url: "https://example.com");
        await gate.GrantAsync(childA, "req-run", ApprovalScope.Run);
        (await runTask).Should().BeTrue();

        // Run-scoped grant does NOT propagate to a sibling for a DIFFERENT URL.
        gate.IsAutoApproved(childB, "web_fetch", "https://other.com").Should().BeFalse();
        // It does cover the sibling for the SAME URL (stored under the parent).
        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeTrue();

        // Tool scope is URL-agnostic: grant for a different tool in child A.
        var toolTask = Register(gate, childA, "req-tool", toolName: "shell", url: "https://anything.com");
        await gate.GrantAsync(childA, "req-tool", ApprovalScope.Tool);
        (await toolTask).Should().BeTrue();

        // Tool-scoped grant propagates to the sibling for ANY URL of that tool.
        gate.IsAutoApproved(childB, "shell", "https://different.com").Should().BeTrue();
    }

    [Fact]
    public async Task Clear_RemovesParentEntry_SiblingNoLongerSeesPolicy()
    {
        var gate = CreateGate();
        const string parent = "coord-3";
        const string childA = "child-A3";
        const string childB = "child-B3";

        gate.RegisterParentRun(childA, parent);
        gate.RegisterParentRun(childB, parent);

        var task = Register(gate, childA, "req-1", url: "https://example.com");
        await gate.GrantAsync(childA, "req-1", ApprovalScope.Tool);
        (await task).Should().BeTrue();

        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeTrue();

        // Clearing the parent run removes the propagated policy.
        gate.Clear(parent);

        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeFalse();
    }

    [Fact]
    public async Task NoRegistration_GrantInChildA_DoesNotPropagateToChildB()
    {
        var gate = CreateGate();
        const string childA = "child-A4";
        const string childB = "child-B4";

        // Note: RegisterParentRun is NOT called.
        var task = Register(gate, childA, "req-1", url: "https://example.com");
        await gate.GrantAsync(childA, "req-1", ApprovalScope.Tool);
        (await task).Should().BeTrue();

        // Without a registered parent relationship, child B sees nothing.
        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeFalse();
    }

    // ── 409 / resolution guard (regression for issue #174) ─────────────────

    [Fact]
    public async Task GrantAsync_AfterAlreadyGranted_ReturnsFalse()
    {
        var gate = CreateGate();
        var task = Register(gate, "run-r1", "req-r1");
        await gate.GrantAsync("run-r1", "req-r1", ApprovalScope.Once);
        (await task).Should().BeTrue();

        // A second grant on the same resolved request must fail — this is the 409 path.
        (await gate.GrantAsync("run-r1", "req-r1", ApprovalScope.Once)).Should().BeFalse();
    }

    [Fact]
    public async Task GrantAsync_AfterTimeout_ReturnsFalse()
    {
        var gate = CreateGate();
        // Use a tiny timeout so the gate expires before we try to grant.
        var result = await gate.WaitForApprovalAsync(
            "run-r2", "req-r2", "web_fetch", null, TimeSpan.FromMilliseconds(30), CancellationToken.None);

        result.Should().BeFalse("timed out");

        // After timeout the request is gone from the gate — grant must also fail.
        (await gate.GrantAsync("run-r2", "req-r2", ApprovalScope.Once)).Should().BeFalse();
    }

    [Fact]
    public async Task Deny_AfterAlreadyDenied_ReturnsFalse()
    {
        var gate = CreateGate();
        var task = Register(gate, "run-r3", "req-r3");
        gate.Deny("run-r3", "req-r3").Should().BeTrue();
        (await task).Should().BeFalse();

        gate.Deny("run-r3", "req-r3").Should().BeFalse();
    }

    [Fact]
    public void IsKnownRequest_UnregisteredRequest_ReturnsFalse()
    {
        var gate = CreateGate();
        gate.IsKnownRequest("run-r4", "never-registered").Should().BeFalse();
    }

    [Fact]
    public async Task IsKnownRequest_AfterResolution_StillReturnsTrue()
    {
        var gate = CreateGate();
        var task = Register(gate, "run-r5", "req-r5");
        gate.IsKnownRequest("run-r5", "req-r5").Should().BeTrue();
        await gate.GrantAsync("run-r5", "req-r5", ApprovalScope.Once);
        (await task).Should().BeTrue();
        gate.IsKnownRequest("run-r5", "req-r5").Should().BeTrue();
    }

    [Fact]
    public async Task IsKnownRequest_AfterClear_ReturnsFalse()
    {
        var gate = CreateGate();
        var task = Register(gate, "run-r6", "req-r6");
        gate.IsKnownRequest("run-r6", "req-r6").Should().BeTrue();
        gate.Clear("run-r6");
        (await task).Should().BeFalse();
        gate.IsKnownRequest("run-r6", "req-r6").Should().BeFalse();
    }

    [Fact]
    public async Task GetRequestState_TracksPendingResolvedExpiredAndUnknown()
    {
        var gate = CreateGate();

        var pendingTask = Register(gate, "run-s1", "req-s1");
        gate.GetRequestState("run-s1", "req-s1").Should().Be(ToolApprovalRequestState.Pending);

        await gate.GrantAsync("run-s1", "req-s1", ApprovalScope.Once);
        (await pendingTask).Should().BeTrue();
        gate.GetRequestState("run-s1", "req-s1").Should().Be(ToolApprovalRequestState.Approved);

        await gate.WaitForApprovalAsync(
            "run-s2", "req-s2", "web_fetch", null, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        gate.GetRequestState("run-s2", "req-s2").Should().Be(ToolApprovalRequestState.Expired);

        gate.GetRequestState("run-s3", "req-never").Should().Be(ToolApprovalRequestState.Unknown);
    }
}

/// <summary>
/// Regression tests for <see cref="DurableToolApprovalGate"/> event emission (issue #174).
/// Verifies that tool.approval_resolved is emitted on the run event stream on timeout and on
/// explicit grant/deny so the frontend can always disable stale HITL cards.
/// </summary>
public sealed class DurableToolApprovalGateEventTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _providers = [];

    public DurableToolApprovalGateEventTests()
    {
        _connectionString = $"DataSource=file:tool-approval-events-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var scope = NewProvider().CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Timeout_EmitsToolApprovalResolvedEvent_WithExpiredTrue()
    {
        var streams = new RunStreamStore();
        var entry = streams.Create("run-e1", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        var result = await gate.WaitForApprovalAsync(
            "run-e1", "req-e1", "web_fetch", null, TimeSpan.FromMilliseconds(40), CancellationToken.None);

        result.Should().BeFalse();

        var evt = entry.GetSnapshotSince(0).Events
            .FirstOrDefault(e => e.Type == EventTypes.ToolApprovalResolved);

        evt.Should().NotBeNull("tool.approval_resolved must be emitted on timeout");

        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(evt!.Payload));
        payload.GetProperty("expired").GetBoolean().Should().BeTrue();
        payload.GetProperty("approved").GetBoolean().Should().BeFalse();
        payload.GetProperty("requestId").GetString().Should().Be("req-e1");
    }

    [Fact]
    public async Task Grant_EmitsToolApprovalResolvedEvent_WithApprovedTrue()
    {
        var streams = new RunStreamStore();
        var entry = streams.Create("run-e2", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        var waitTask = gate.WaitForApprovalAsync(
            "run-e2", "req-e2", "web_fetch", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return await gate.GrantAsync("run-e2", "req-e2", ApprovalScope.Once);
        });

        (await waitTask).Should().BeTrue();

        var evt = entry.GetSnapshotSince(0).Events
            .FirstOrDefault(e => e.Type == EventTypes.ToolApprovalResolved);

        evt.Should().NotBeNull("tool.approval_resolved must be emitted on grant");

        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(evt!.Payload));
        payload.GetProperty("approved").GetBoolean().Should().BeTrue();
        payload.GetProperty("expired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Deny_EmitsToolApprovalResolvedEvent_WithApprovedFalseAndExpiredFalse()
    {
        var streams = new RunStreamStore();
        var entry = streams.Create("run-e3", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        var waitTask = gate.WaitForApprovalAsync(
            "run-e3", "req-e3", "web_fetch", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return gate.Deny("run-e3", "req-e3");
        });

        (await waitTask).Should().BeFalse();

        var evt = entry.GetSnapshotSince(0).Events
            .FirstOrDefault(e => e.Type == EventTypes.ToolApprovalResolved);

        evt.Should().NotBeNull("tool.approval_resolved must be emitted on deny");

        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(evt!.Payload));
        payload.GetProperty("approved").GetBoolean().Should().BeFalse();
        payload.GetProperty("expired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GrantAfterTimeout_DoesNotEmitSecondEvent()
    {
        var streams = new RunStreamStore();
        var entry = streams.Create("run-e4", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        await gate.WaitForApprovalAsync(
            "run-e4", "req-e4", "web_fetch", null, TimeSpan.FromMilliseconds(40), CancellationToken.None);

        // Late grant after timeout — must return false.
        (await gate.GrantAsync("run-e4", "req-e4", ApprovalScope.Once)).Should().BeFalse();

        // Only one tool.approval_resolved event should exist (from the timeout).
        var resolvedEvents = entry.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.ToolApprovalResolved)
            .ToList();

        resolvedEvents.Should().HaveCount(1, "only the timeout resolution fires; the late grant must not duplicate it");

        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(resolvedEvents[0].Payload));
        payload.GetProperty("expired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DurableGetRequestState_DistinguishesExpiredFromWrongRun()
    {
        var streams = new RunStreamStore();
        streams.Create("run-e5", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        var waitTask = gate.WaitForApprovalAsync(
            "run-e5", "req-e5", "web_fetch", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return gate.GetRequestState("run-e5", "req-e5") == ToolApprovalRequestState.Pending;
        });

        gate.GetRequestState("other-run", "req-e5").Should().Be(ToolApprovalRequestState.Unknown);

        gate.Deny("run-e5", "req-e5").Should().BeTrue();
        (await waitTask).Should().BeFalse();
        gate.GetRequestState("run-e5", "req-e5").Should().Be(ToolApprovalRequestState.Denied);

        await gate.WaitForApprovalAsync(
            "run-e5", "req-expired", "web_fetch", null, TimeSpan.FromMilliseconds(40), CancellationToken.None);
        gate.GetRequestState("run-e5", "req-expired").Should().Be(ToolApprovalRequestState.Expired);
    }

    private DurableRunControlState NewState() =>
        new(NewProvider().GetRequiredService<IServiceScopeFactory>());

    private ServiceProvider NewProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> action)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await action()) return;
            await Task.Delay(50);
        }
        false.Should().BeTrue("the pending approval context should become visible within 2s");
    }

    public void Dispose()
    {
        foreach (var p in _providers) p.Dispose();
        _keepAlive.Dispose();
    }
}
