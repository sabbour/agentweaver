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

    [Fact]
    public async Task Approve_PendingApprovalOnTerminalOwningRun_Succeeds()
    {
        // #349: a genuinely-pending approval must be honored even when the owning run has
        // already transitioned out of active (e.g. it finalized while approvals were queued).
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "pending-terminal-approval";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task Approve_UnknownRequestOnTerminalRun_ReturnsConflict()
    {
        // With no pending gate to resolve, the run-active guard still rejects stale posts.
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = "no-such-request", scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_MultiplePendingApprovals_AllSucceed_WhenOwningRunFinalizes()
    {
        // #349 repro: a doc-only run raises 3 concurrent web_fetch approvals then finalizes
        // (AssembleReady) while they are still outstanding. Approving the first previously
        // flipped the run non-active and 409'd the rest; all three must now succeed.
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.AssembleReady);

        var requestIds = new[] { "web-fetch-1", "web-fetch-2", "web-fetch-3" };
        var pending = requestIds
            .Select(rid => approvalGate.WaitForApprovalAsync(
                runId.ToString(), rid, "web_fetch", $"https://example.com/{rid}",
                TimeSpan.FromMinutes(1), CancellationToken.None))
            .ToArray();

        foreach (var rid in requestIds)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/runs/{runId}/tool-approvals",
                new { request_id = rid, scope = "once" });
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"approval for {rid} should succeed");
        }

        var results = await Task.WhenAll(pending.Select(t => t.WaitAsync(TimeSpan.FromSeconds(5))));
        results.Should().OnlyContain(approved => approved);
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
    public async Task Deny_PendingApprovalOnTerminalOwningRun_Succeeds()
    {
        // #349: mirror the approval path — a genuinely-pending denial is honored even when
        // the owning run has already transitioned out of active.
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "pending-terminal-denial";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-denials",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    private static HttpClient CreateAuthenticatedClient(AgentweaverWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    private static Task InsertRunAsync(
        IRunStore runStore, RunId id, RunStatus status, string? parentRunId = null) =>
        runStore.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "tool approval endpoint test",
            SubmittingUser = AgentweaverWebApplicationFactory.TestUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = parentRunId,
            AgentName = parentRunId is null ? "Coordinator" : "Researcher",
            SubtaskId = parentRunId is null ? null : "1",
        });
}
