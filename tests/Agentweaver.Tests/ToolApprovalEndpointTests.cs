using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Api;

public sealed class ToolApprovalEndpointTests
{
    [Fact]
    public async Task Approve_PendingChildApproval_Succeeds_WhenCoordinatorIsTerminal()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.Failed);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        const string requestId = "pending-child-approval";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    // #349 — exact repro: an agent issues 3 concurrent approval-gated web_fetch tool.call events,
    // but the SDK invokes the permission callback sequentially so only the FIRST registers a real
    // backend approval gate (+ tool.approval_required). The frontend optimistically renders a card
    // per tool.call, so #2/#3 are "phantom" cards whose request_ids are Unknown to the backend.
    // Approving the first (posted to the coordinator, resolved to the active child) must succeed;
    // approving the phantom #2/#3 must NOT be mislabeled "Run is not active" — the coordinator the
    // card posted to may already be AssembleReady while the owning child is still active.
    [Fact]
    public async Task Approve_ThreeConcurrentWebFetch_FirstSucceeds_PhantomsReportUnknownNotRunNotActive()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        // Coordinator has already moved on to assembly while the child research subtask keeps
        // fetching — this is what makes the phantom-card fallback hit a non-active run.
        await InsertRunAsync(runStore, coordinatorId, RunStatus.AssembleReady);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        // Only the FIRST web_fetch reached the permission gate and registered a real request.
        const string firstRequestId = "toolu_first_web_fetch";
        var firstApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), firstRequestId, "web_fetch", "https://anthropic.com/a",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        // First card: posted to the coordinator, resolves to the active child, approves cleanly.
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = firstRequestId, scope = "once" });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await firstApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        // Phantom cards #2 and #3: never backend-registered. Must not 409 "Run is not active".
        foreach (var phantomRequestId in new[] { "toolu_second_web_fetch", "toolu_third_web_fetch" })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/runs/{coordinatorId}/tool-approvals",
                new { request_id = phantomRequestId, scope = "once" });

            response.StatusCode.Should().NotBe(
                HttpStatusCode.Conflict, $"phantom {phantomRequestId} must not be mislabeled Run-not-active");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("Run is not active");
            body.Should().Contain("unknown", "an unregistered request must report an accurate unknown-request error");
        }
    }

    [Fact]
    public async Task Deny_PhantomCard_UnknownRequestOnNonActiveCoordinator_DoesNotReturnRunNotActive()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.AssembleReady);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-denials",
            new { request_id = "toolu_never_registered", scope = "once" });

        response.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Run is not active");
        body.Should().Contain("unknown");
    }

    [Fact]
    public async Task Approve_PendingApprovalOnTerminalOwningRun_ReturnsConflict()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "stale-terminal-approval";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        approvalGate.Deny(runId.ToString(), requestId).Should().BeTrue();
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Deny_PendingChildApproval_Succeeds_WhenCoordinatorIsTerminal()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.Failed);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        const string requestId = "pending-child-denial";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-denials",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Deny_PendingApprovalOnTerminalOwningRun_ReturnsConflict()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "stale-terminal-denial";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-denials",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        approvalGate.Deny(runId.ToString(), requestId).Should().BeTrue();
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task ApproveAlways_AffectsOnlyPersistedInitiatingOwner()
    {
        using var factory = new CoordinatorWebApplicationFactory();
        using var ownerClient = factory.CreateOwnerClient();
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var source = RunId.New();
        var ownerFuture = RunId.New();
        var otherFuture = RunId.New();
        await InsertRunAsync(
            runStore, source, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await InsertRunAsync(
            runStore, ownerFuture, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await InsertRunAsync(
            runStore, otherFuture, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OtherUser);
        var pending = approvalGate.WaitForApprovalAsync(
            source.ToString(), "owner-always", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = "owner-always", scope = "always" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        approvalGate.IsAutoApproved(ownerFuture.ToString(), "web_fetch", "https://owner.test")
            .Should().BeTrue();
        approvalGate.IsAutoApproved(otherFuture.ToString(), "web_fetch", "https://other.test")
            .Should().BeFalse();
    }

    [Fact]
    public async Task Approve_ParentOwnerCannotGrantApprovalOwnedByDifferentPersistedChildOwner()
    {
        using var factory = new CoordinatorWebApplicationFactory();
        using var ownerClient = factory.CreateOwnerClient();
        using var otherClient = factory.CreateOtherClient();
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(
            runStore, coordinatorId, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await InsertRunAsync(
            runStore, childId, RunStatus.InProgress, coordinatorId.ToString(),
            CoordinatorWebApplicationFactory.OtherUser);
        var pending = approvalGate.WaitForApprovalAsync(
            childId.ToString(), "cross-owner-child", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var unauthorized = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = "cross-owner-child", scope = "always" });

        unauthorized.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        approvalGate.GetRequestState(childId.ToString(), "cross-owner-child")
            .Should().Be(ToolApprovalRequestState.Pending);

        var authorized = await otherClient.PostAsJsonAsync(
            $"/api/runs/{childId}/tool-approvals",
            new { request_id = "cross-owner-child", scope = "once" });

        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    private static HttpClient CreateAuthenticatedClient(AgentweaverWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    private static Task InsertRunAsync(
        IRunStore runStore,
        RunId id,
        RunStatus status,
        string? parentRunId = null,
        string? submittingUser = null) =>
        runStore.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "tool approval endpoint test",
            SubmittingUser = submittingUser ?? AgentweaverWebApplicationFactory.TestUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = parentRunId,
            AgentName = parentRunId is null ? "Coordinator" : "Researcher",
            SubtaskId = parentRunId is null ? null : "1",
        });
}
