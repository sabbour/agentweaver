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
