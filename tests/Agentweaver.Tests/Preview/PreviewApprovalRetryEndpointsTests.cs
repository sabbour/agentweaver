using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Preview;

public sealed class PreviewApprovalRetryEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PreviewApprovalRetryEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task RetryExpiredApproval_CreatesFreshPendingAttempt()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var retryRequestId = body.GetProperty("request_id").GetString();
        retryRequestId.Should().NotBeNullOrWhiteSpace().And.NotBe(requestId);
        body.GetProperty("retry_of_request_id").GetString().Should().Be(requestId);

        var streams = _factory.Services.GetRequiredService<RunStreamStore>();
        var pending = streams.Get(runId)!.GetSnapshotSince(0).Events
            .Last(e => e.Type == EventTypes.SandboxPreviewPending);
        ReadString(pending.Payload, "request_id").Should().Be(retryRequestId);
        ReadString(pending.Payload, "retry_of_request_id").Should().Be(requestId);

        _factory.Services.GetRequiredService<IToolApprovalGate>()
            .Deny(runId, retryRequestId!)
            .Should().BeTrue();
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsNonOwner()
    {
        var (runId, requestId) = await CreateRetryableRunAsync(owner: "another-user");

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RetryApproval_RejectsRequestThatHasNotExpired()
    {
        var (runId, requestId) = await CreateRunAsync(RunStatus.InProgress);
        var gate = _factory.Services.GetRequiredService<IToolApprovalGate>();
        _ = gate.WaitForApprovalAsync(
            runId,
            requestId,
            "start_preview",
            "sandbox-preview:5173",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        EmitRetryableFailure(runId, requestId);

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        gate.Deny(runId, requestId).Should().BeTrue();
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsTerminalRun()
    {
        var (runId, requestId) = await CreateRetryableRunAsync(status: RunStatus.Completed);

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RetryExpiredApproval_RejectsSupersededPreviewState()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();
        _factory.Services.GetRequiredService<RunStreamStore>().Get(runId)!.RecordNext(
            EventTypes.SandboxPreviewReady,
            new { run_id = runId, target_port = 5173 });

        var response = await _client.PostAsync(
            $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RetryExpiredApproval_ConcurrentDuplicatesCreateOneAttempt()
    {
        var (runId, requestId) = await CreateRetryableRunAsync();
        var path = $"/api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry";

        var responses = await Task.WhenAll(
            _client.PostAsync(path, content: null),
            _client.PostAsync(path, content: null));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var accepted = responses.Single(r => r.StatusCode == HttpStatusCode.Accepted);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        _factory.Services.GetRequiredService<IToolApprovalGate>()
            .Deny(runId, body.GetProperty("request_id").GetString()!)
            .Should().BeTrue();
    }

    private async Task<(string RunId, string RequestId)> CreateRetryableRunAsync(
        string owner = ProjectsWebApplicationFactory.TestUser,
        RunStatus status = RunStatus.InProgress)
    {
        var (runId, requestId) = await CreateRunAsync(status, owner);
        var gate = _factory.Services.GetRequiredService<IToolApprovalGate>();
        await gate.WaitForApprovalAsync(
            runId,
            requestId,
            "start_preview",
            "sandbox-preview:5173",
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        gate.GetRequestState(runId, requestId).Should().Be(ToolApprovalRequestState.Expired);
        EmitRetryableFailure(runId, requestId);
        return (runId, requestId);
    }

    private async Task<(string RunId, string RequestId)> CreateRunAsync(
        RunStatus status,
        string owner = ProjectsWebApplicationFactory.TestUser)
    {
        var runId = RunId.New();
        await _factory.Services.GetRequiredService<SqliteRunStore>().InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "preview retry endpoint test",
            SubmittingUser = owner,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
        });

        var id = runId.ToString();
        _factory.Services.GetRequiredService<RunStreamStore>().Create(id, owner);
        return (id, Guid.NewGuid().ToString("n"));
    }

    private void EmitRetryableFailure(string runId, string requestId) =>
        _factory.Services.GetRequiredService<RunStreamStore>().Get(runId)!.RecordNext(
            EventTypes.SandboxPreviewFailed,
            new
            {
                run_id = runId,
                target_port = 5173,
                reason = "approval_timed_out",
                approval_request_id = requestId,
                retry_available = true,
            });

    private static string ReadString(object payload, string property) =>
        payload.GetType().GetProperty(property)!.GetValue(payload)!.ToString()!;
}
