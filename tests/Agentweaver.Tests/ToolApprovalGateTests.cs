using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

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
}
