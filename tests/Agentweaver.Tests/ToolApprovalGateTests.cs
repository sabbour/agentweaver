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
    private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromMilliseconds(250);

    private static InMemoryToolApprovalGate CreateGate(Func<string, string?>? ownerForRun = null) =>
        new(new DelegateOwnerResolver(ownerForRun ?? (_ => "test-owner")));

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
    public async Task Run_Scope_DifferentUrl_IsAutoApproved()
    {
        var gate = CreateGate();
        const string runId = "run-3";

        var firstTask = Register(gate, runId, "req-1", url: "https://example.com");
        await gate.GrantAsync(runId, "req-1", ApprovalScope.Run);
        (await firstTask).Should().BeTrue();

        // Run scope applies to this tool for the run, regardless of URL.
        gate.IsAutoApproved(runId, "web_fetch", "https://other.com").Should().BeTrue();
    }

    // ── Always scope ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Always_Scope_DifferentUrlAndRun_IsAutoApproved()
    {
        var gate = CreateGate();
        const string url = "https://example.com";

        // Grant Always on run-A.
        var firstTask = Register(gate, "run-A", "req-1", url: url);
        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);
        (await firstTask).Should().BeTrue();

        // A completely different run and URL should see the tool-wide policy.
        gate.IsAutoApproved("run-B", "web_fetch", "https://other.com?next=1").Should().BeTrue();
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

    [Fact]
    public async Task Always_Scope_DifferentOwner_IsNotAutoApproved()
    {
        var gate = CreateGate(runId => runId == "run-A" ? "alice" : "bob");
        var firstTask = Register(gate, "run-A", "req-1");

        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);

        (await firstTask).Should().BeTrue();
        gate.IsAutoApproved("run-A", "web_fetch", "https://same-owner.test").Should().BeTrue();
        gate.IsAutoApproved("run-B", "web_fetch", "https://different-owner.test").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Always_Scope_MissingOwner_FailsClosed(string? owner)
    {
        var gate = CreateGate(_ => owner);
        var firstTask = Register(gate, "run-A", "req-1");

        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);

        (await firstTask).Should().BeTrue();
        gate.IsAutoApproved("run-A", "web_fetch", "https://example.test").Should().BeFalse();
    }

    [Fact]
    public async Task Always_Scope_OwnerResolverFailure_FailsClosed()
    {
        var gate = CreateGate(_ => throw new InvalidOperationException("owner store unavailable"));
        var firstTask = Register(gate, "run-A", "req-1");

        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);

        (await firstTask).Should().BeTrue();
        gate.IsAutoApproved("run-A", "web_fetch", "https://example.test").Should().BeFalse();
    }

    [Theory]
    [InlineData("start_preview")]
    [InlineData("write_file")]
    [InlineData("unknown_tool")]
    [InlineData("Web_Fetch")]
    public async Task Always_Scope_NonEligibleTool_RemainsGated(string toolName)
    {
        var gate = CreateGate();
        var firstTask = Register(gate, "run-A", "req-1", toolName);

        await gate.GrantAsync("run-A", "req-1", ApprovalScope.Always);

        (await firstTask).Should().BeTrue();
        gate.IsAutoApproved("run-A", toolName, null).Should().BeFalse();
        gate.IsAutoApproved("run-B", toolName, null).Should().BeFalse();
    }

    // ── Clear ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesRunScopedEntries_ButNotAlwaysAllowed()
    {
        var gate = CreateGate();
        const string url = "https://example.com";

        // Run-scoped policy on run-A.
        var runTask = Register(gate, "run-A", "req-run", toolName: "shell", url: url);
        await gate.GrantAsync("run-A", "req-run", ApprovalScope.Run);
        (await runTask).Should().BeTrue();

        // Always-allowed policy also on run-A.
        var alwaysTask = Register(gate, "run-A", "req-always", url: "https://always.com");
        await gate.GrantAsync("run-A", "req-always", ApprovalScope.Always);
        (await alwaysTask).Should().BeTrue();

        // Sanity: both are active before Clear.
        gate.IsAutoApproved("run-A", "shell", url).Should().BeTrue();
        gate.IsAutoApproved("run-A", "web_fetch", "https://always.com/another-path").Should().BeTrue();

        // Clear the run.
        gate.Clear("run-A");

        // Run-scoped entry gone.
        gate.IsAutoApproved("run-A", "shell", url).Should().BeFalse();

        // Always-allowed entry survives.
        gate.IsAutoApproved("run-A", "web_fetch", "https://always.com/another-path").Should().BeTrue();
        gate.IsAutoApproved("run-B", "web_fetch", "https://always.com/another-path").Should().BeTrue();
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

    [Theory]
    [InlineData(ApprovalScope.Run)]
    [InlineData(ApprovalScope.Tool)]
    [InlineData(ApprovalScope.Always)]
    public async Task Clear_RevokesFinalizedLocalScopePolicies(ApprovalScope scope)
    {
        var gate = CreateGate();
        const string runId = "run-finalized-clear";
        const string requestId = "req-finalized-clear";
        const string scopeGrantId = "scope-finalized-clear";

        var pending = Register(gate, runId, requestId);
        (await gate.GrantProvisionalScopeAsync(
            runId,
            requestId,
            scope,
            scopeGrantId,
            DateTimeOffset.UtcNow.AddMinutes(1))).Should().BeTrue();
        (await pending).Should().BeTrue();
        gate.FinalizeScopeGrant(runId, requestId, scopeGrantId).Should().BeTrue();
        gate.IsAutoApproved(runId, "web_fetch", "https://before-clear.test").Should().BeTrue();

        gate.Clear(runId);

        gate.IsAutoApproved(runId, "web_fetch", "https://after-clear.test").Should().BeFalse(
            "finalized pod-local scopes remain lifecycle-bound and must be withdrawn with their run");
    }

    // ── Sibling propagation (RegisterParentRun, commit cb7fbbf) ───────────────────

    [Fact]
    public async Task ToolScope_GrantInChildA_RemainsConfinedToChildA()
    {
        var gate = CreateGate();
        const string parent = "coord-1";
        const string childA = "child-A";
        const string childB = "child-B";
        const string childC = "child-C";

        gate.RegisterParentRun(childA, parent);
        gate.RegisterParentRun(childB, parent);

        // Grant Tool scope in child A.
        var task = Register(gate, childA, "req-1", url: "https://example.com");
        await gate.GrantAsync(childA, "req-1", ApprovalScope.Tool);
        (await task).Should().BeTrue();

        gate.IsAutoApproved(childA, "web_fetch", "https://another-url.test").Should().BeTrue(
            "Tool scope remains URL-agnostic within the approving child run");
        gate.IsAutoApproved(childB, "web_fetch", "https://example.com").Should().BeFalse(
            "Tool scope must not authorize a sibling child");

        gate.RegisterParentRun(childC, parent);
        gate.IsAutoApproved(childC, "web_fetch", "https://future-child.test").Should().BeFalse(
            "Tool scope must not authorize a future child");
    }

    [Fact]
    public async Task RunScope_PropagatesAcrossChildren_WhileToolScopeDoesNot()
    {
        var gate = CreateGate();
        const string parent = "coord-2";
        const string childA = "child-A2";
        const string childB = "child-B2";

        gate.RegisterParentRun(childA, parent);
        gate.RegisterParentRun(childB, parent);

        // Run scope applies to this tool across URLs: grant in child A.
        var runTask = Register(gate, childA, "req-run", toolName: "web_fetch", url: "https://example.com");
        await gate.GrantAsync(childA, "req-run", ApprovalScope.Run);
        (await runTask).Should().BeTrue();

        // The propagated run-scoped grant covers the tool for every URL.
        gate.IsAutoApproved(childB, "web_fetch", "https://other.com").Should().BeTrue();

        // Tool scope is URL-agnostic, but only within the approving child.
        var toolTask = Register(gate, childA, "req-tool", toolName: "shell", url: "https://anything.com");
        await gate.GrantAsync(childA, "req-tool", ApprovalScope.Tool);
        (await toolTask).Should().BeTrue();

        gate.IsAutoApproved(childA, "shell", "https://different.com").Should().BeTrue();
        gate.IsAutoApproved(childB, "shell", "https://different.com").Should().BeFalse();
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
        await gate.GrantAsync(childA, "req-1", ApprovalScope.Run);
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
        // Keep the timeout short enough to exercise expiry without depending on sub-50ms scheduling.
        var result = await gate.WaitForApprovalAsync(
            "run-r2", "req-r2", "web_fetch", null, ExpirationTimeout, CancellationToken.None);

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
    public async Task DuplicateRequest_DeniesOriginalWithoutRemovingReplacement()
    {
        var gate = CreateGate();
        var original = Register(gate, "run-r3b", "req-r3b");
        var replacement = Register(gate, "run-r3b", "req-r3b");

        (await original).Should().BeFalse("a duplicate request replaces and denies the original waiter");
        (await gate.GrantAsync("run-r3b", "req-r3b", ApprovalScope.Once)).Should().BeTrue();
        (await replacement).Should().BeTrue();
        gate.GetRequestState("run-r3b", "req-r3b").Should().Be(ToolApprovalRequestState.Approved);
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
            "run-s2", "req-s2", "web_fetch", null, ExpirationTimeout, CancellationToken.None);
        gate.GetRequestState("run-s2", "req-s2").Should().Be(ToolApprovalRequestState.Expired);

        gate.GetRequestState("run-s3", "req-never").Should().Be(ToolApprovalRequestState.Unknown);
    }

    // ── HasArmedApproval (idle-close protection for HITL waits) ──────────────────

    [Fact]
    public async Task HasArmedApproval_TrueWhilePending_FalseOnceGranted()
    {
        var gate = CreateGate();
        gate.HasArmedApproval("run-a1").Should().BeFalse("no request has been registered yet");

        var task = Register(gate, "run-a1", "req-a1");
        gate.HasArmedApproval("run-a1").Should().BeTrue(
            "a registered, unresolved request is armed and awaiting the operator");

        await gate.GrantAsync("run-a1", "req-a1", ApprovalScope.Once);
        (await task).Should().BeTrue();
        gate.HasArmedApproval("run-a1").Should().BeFalse("a granted request is no longer armed");
    }

    [Fact]
    public async Task HasArmedApproval_FalseAfterDenyAndAfterClear()
    {
        var gate = CreateGate();

        var denyTask = Register(gate, "run-a2", "req-a2");
        gate.HasArmedApproval("run-a2").Should().BeTrue();
        gate.Deny("run-a2", "req-a2").Should().BeTrue();
        (await denyTask).Should().BeFalse();
        gate.HasArmedApproval("run-a2").Should().BeFalse("a denied request is no longer armed");

        var clearTask = Register(gate, "run-a3", "req-a3");
        gate.HasArmedApproval("run-a3").Should().BeTrue();
        gate.Clear("run-a3");
        (await clearTask).Should().BeFalse();
        gate.HasArmedApproval("run-a3").Should().BeFalse("clearing a run drops its armed approvals");
    }

    private sealed class DelegateOwnerResolver(Func<string, string?> resolve) : IToolApprovalOwnerResolver
    {
        public string? GetCanonicalOwner(string runId) => resolve(runId);
    }
}

/// <summary>
/// Regression tests for <see cref="DurableToolApprovalGate"/> event emission (issue #174).
/// Verifies that tool.approval_resolved is emitted on the run event stream on timeout and on
/// explicit grant/deny so the frontend can always disable stale HITL cards.
/// </summary>
public sealed class DurableToolApprovalGateEventTests : IDisposable
{
    private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromMilliseconds(250);
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
            "run-e1", "req-e1", "web_fetch", null, ExpirationTimeout, CancellationToken.None);

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
            "run-e4", "req-e4", "web_fetch", null, ExpirationTimeout, CancellationToken.None);

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
            "run-e5", "req-expired", "web_fetch", null, ExpirationTimeout, CancellationToken.None);
        gate.GetRequestState("run-e5", "req-expired").Should().Be(ToolApprovalRequestState.Expired);
    }

    // ── Coordinator child-subtask routing (regression for issue #196) ──────────
    // A child subtask registers its tool approval under the CHILD run id. The web console must
    // POST approve/deny to that child run id. Posting to the parent/coordinator run id finds no
    // matching pending approval, which the endpoint surfaces as 404 state=unknown. These tests
    // pin the run-id keying that the /tool-approvals and /tool-denials endpoints rely on.
    [Fact]
    public async Task ChildSubtaskApproval_GrantOnChildRunId_Succeeds_ButCoordinatorRunId_IsUnknown()
    {
        var streams = new RunStreamStore();
        streams.Create("child-run", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);
        gate.RegisterParentRun("child-run", "coordinator-run");

        // Child subtask raises the gate under its own run id.
        var waitTask = gate.WaitForApprovalAsync(
            "child-run", "toolu_01abc", "web_fetch", "https://api.github.com", TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return gate.GetRequestState("child-run", "toolu_01abc") == ToolApprovalRequestState.Pending;
        });

        // Posting to the coordinator/parent run id must NOT resolve it, and reports Unknown → 404.
        (await gate.GrantAsync("coordinator-run", "toolu_01abc", ApprovalScope.Once)).Should().BeFalse();
        gate.GetRequestState("coordinator-run", "toolu_01abc").Should().Be(ToolApprovalRequestState.Unknown);

        // Posting to the child subtask run id resolves it → 200.
        (await gate.GrantAsync("child-run", "toolu_01abc", ApprovalScope.Once)).Should().BeTrue();
        (await waitTask).Should().BeTrue();
        gate.GetRequestState("child-run", "toolu_01abc").Should().Be(ToolApprovalRequestState.Approved);
    }

    [Fact]
    public async Task ChildSubtaskApproval_DenyOnChildRunId_Succeeds_ButCoordinatorRunId_IsUnknown()
    {
        var streams = new RunStreamStore();
        streams.Create("child-run-d", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);
        gate.RegisterParentRun("child-run-d", "coordinator-run-d");

        var waitTask = gate.WaitForApprovalAsync(
            "child-run-d", "toolu_01def", "web_fetch", "https://api.github.com", TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return gate.GetRequestState("child-run-d", "toolu_01def") == ToolApprovalRequestState.Pending;
        });

        // Deny against the coordinator run id finds nothing → 404 unknown.
        gate.Deny("coordinator-run-d", "toolu_01def").Should().BeFalse();
        gate.GetRequestState("coordinator-run-d", "toolu_01def").Should().Be(ToolApprovalRequestState.Unknown);

        // Deny against the owning child run id resolves it → 200.
        gate.Deny("child-run-d", "toolu_01def").Should().BeTrue();
        (await waitTask).Should().BeFalse();
        gate.GetRequestState("child-run-d", "toolu_01def").Should().Be(ToolApprovalRequestState.Denied);
    }

    // ── HasArmedApproval on the durable gate (idle-close protection for HITL waits) ──

    [Fact]
    public async Task HasArmedApproval_TrueWhilePending_FalseOnceResolved()
    {
        var streams = new RunStreamStore();
        streams.Create("run-arm1", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        gate.HasArmedApproval("run-arm1").Should().BeFalse("no request registered yet");

        var waitTask = gate.WaitForApprovalAsync(
            "run-arm1", "req-arm1", "coordinator_start", null, TimeSpan.FromSeconds(10), CancellationToken.None);

        await WaitUntilAsync(async () => { await Task.CompletedTask; return gate.HasArmedApproval("run-arm1"); });
        gate.HasArmedApproval("run-arm1").Should().BeTrue(
            "a registered, unresolved request is armed and awaiting the operator");

        gate.Deny("run-arm1", "req-arm1").Should().BeTrue();
        (await waitTask).Should().BeFalse();
        gate.HasArmedApproval("run-arm1").Should().BeFalse("a resolved request is no longer armed");
    }

    [Fact]
    public async Task HasArmedApproval_FalseAfterExpiry()
    {
        var streams = new RunStreamStore();
        streams.Create("run-arm2", "owner");
        var gate = new DurableToolApprovalGate(NewState(), streams);

        await gate.WaitForApprovalAsync(
            "run-arm2", "req-arm2", "coordinator_start", null, ExpirationTimeout, CancellationToken.None);

        gate.HasArmedApproval("run-arm2").Should().BeFalse("an expired request must not count as armed");
    }

    // ── PR #972 findings #2 and #3: active-run claim for every non-once scope/caller ──

    [Fact]
    public async Task GrantAsync_StandardApiPath_FailsClosed_WhenRunAlreadyTerminalized()
    {
        var streams = new RunStreamStore();
        var runIdValue = RunId.New();
        var runId = runIdValue.ToString();
        streams.Create(runId, "owner");
        var runStore = new FixedStatusRunStore(RunStatus.Failed);
        var gate = new DurableToolApprovalGate(NewState(), streams, runStore: runStore);

        // Register a live pending context via the standard (non-AgentHost-context) path. Calling
        // WaitForApprovalAsync synchronously appends the context before this expression returns
        // control, mirroring the endpoint's own "register pending, then later grant" flow.
        var pending = gate.WaitForApprovalAsync(
            runId, "req-standard-1", "web_fetch", "https://example.test",
            TimeSpan.FromMilliseconds(300), CancellationToken.None);

        var granted = await gate.GrantAsync(runId, "req-standard-1", ApprovalScope.Run);

        granted.Should().BeFalse(
            "the standard API GrantAsync path (context: null) must also require an active run " +
            "before persisting a non-once scope -- there is no context-based carve-out");
        gate.IsAutoApproved(runId, "web_fetch", "https://other.test").Should().BeFalse();

        (await pending).Should().BeFalse("the request was never granted, so it must expire");
    }

    [Fact]
    public async Task PersistAgentHostApprovalAsync_HoldsActiveClaimAcrossReadAndCommit_ExpiresAfterConcurrentReviewReady()
    {
        var streams = new RunStreamStore();
        var runIdValue = RunId.New();
        var runId = runIdValue.ToString();
        streams.Create(runId, "owner");
        var guard = new RunActiveClaimGuard();
        var inner = new PausableActiveRunStore();
        var guardedStore = new RunActiveClaimGuardedRunStore(inner, guard);
        var gate = new DurableToolApprovalGate(
            NewState(), streams, runStore: guardedStore, runActiveClaimGuard: guard);

        var grantTask = gate.PersistAgentHostApprovalAsync(
            runId, "req-race-1", "web_fetch", "https://example.test", ApprovalScope.Run);

        await inner.EnteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the grant's active-run read is paused mid-flight -- still holding the claim
        // acquired around the whole read-then-commit critical section -- marking the same run
        // review-ready must be unable to proceed. This is the atomicity finding #3 requires on
        // SQLite, where the run store and the RunEvents/policy store are separate database files
        // that cannot share one ACID transaction.
        var reviewReadyTask = guardedStore.UpdateReviewReadyAsync(
            runIdValue, "tree", "diff", 1, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        reviewReadyTask.IsCompleted.Should().BeFalse(
            "marking review ready must wait for the in-flight durable approval-scope grant");
        inner.ReviewReadyCalls.Should().Be(0);

        inner.ReleaseRead.SetResult();

        (await grantTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the run was InProgress for the entire atomic claim, so the grant must succeed");
        await reviewReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        inner.ReviewReadyCalls.Should().Be(1);

        gate.IsAutoApproved(runId, "web_fetch", "https://other.test").Should().BeFalse(
            "the durable run-scope policy committed before terminalization, but the run is no longer active");
    }

    private sealed class FixedStatusRunStore(RunStatus status) : IRunStore
    {
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) =>
            Task.FromResult<Run?>(new Run
            {
                Id = runId,
                RepositoryPath = "dummy-repo-path",
                OriginatingBranch = "main",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "tool approval gate test",
                SubmittingUser = "owner",
                Status = status,
                StartedAt = DateTimeOffset.UtcNow,
            });

        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus s, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus s, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus s, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus s, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class PausableActiveRunStore : IRunStore
    {
        private RunStatus _status = RunStatus.InProgress;
        private int _getCalls;

        public readonly TaskCompletionSource EnteredRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TerminalizeCalls;
        public int ReviewReadyCalls;

        public async Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
        {
            // DurableToolApprovalGate.ResolveAndPersistAsync reads the run store twice for a
            // non-once scope: once in SubjectOfAsync (subject resolution, BEFORE the active-run
            // claim is acquired) and once inside LockAndRequireActiveRunAsync (the active-run
            // check, AFTER the claim is held). Only the second read is the one the claim is meant
            // to bracket, so only it pauses here; the first must return promptly so the gate can
            // reach the guarded section at all.
            if (Interlocked.Increment(ref _getCalls) == 2)
            {
                EnteredRead.TrySetResult();
                await ReleaseRead.Task.ConfigureAwait(false);
            }

            return new Run
            {
                Id = runId,
                RepositoryPath = "dummy-repo-path",
                OriginatingBranch = "main",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "tool approval gate test",
                SubmittingUser = "owner",
                Status = _status,
                StartedAt = DateTimeOffset.UtcNow,
            };
        }

        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref TerminalizeCalls);
            _status = toStatus;
            return Task.FromResult(true);
        }

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus s, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus s, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null)
        {
            Interlocked.Increment(ref ReviewReadyCalls);
            _status = RunStatus.AwaitingReview;
            return Task.CompletedTask;
        }
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus s, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus s, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private DurableRunControlState NewState()
    {
        var provider = NewProvider();
        return new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IRunEventStream>());
    }

    private ServiceProvider NewProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        services.AddDbContextFactory<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        services.AddSingleton<IRunEventStream, EfRunEventStream>();
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
