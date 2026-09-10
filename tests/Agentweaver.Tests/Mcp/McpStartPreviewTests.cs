using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Integration tests for the MCP <c>RunTools.start_preview</c> tool. Uses the same in-process API
/// factory seam as the sibling MCP tests (no mocks) so the MCP tool drives the REAL
/// <c>POST /api/runs/{runId}/sandbox/preview</c> endpoint — proving the MCP surface shares the same
/// approval gate + preview path as the in-sandbox tool. The preview service is disabled in-process
/// (no cluster), so the auto-approved happy path deterministically reaches the preview-start step
/// and fails with 409 "no sandbox pod" — which still proves the run_id/port forward, owner auth and
/// the approval gate were all traversed.
/// </summary>
public sealed class McpStartPreviewTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public McpStartPreviewTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunTools CreateTools()
    {
        var httpClient = _factory.CreateClient();
        var config = new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey);
        var apiClient = new AgentweaverApiClient(httpClient, config);
        return new RunTools(apiClient);
    }

    [Fact]
    public async Task StartPreview_UnknownRunId_ThrowsMcpApiException404()
    {
        var tools = CreateTools();

        var act = () => tools.StartPreviewAsync(Guid.NewGuid().ToString("N"), 3000, ct: CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 404);
    }

    [Fact]
    public async Task StartPreview_InvalidPort_ThrowsMcpApiException400()
    {
        var tools = CreateTools();

        var act = () => tools.StartPreviewAsync(Guid.NewGuid().ToString("N"), 70000, ct: CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 400);
    }

    [Fact]
    public async Task StartPreview_AutoApproved_NoBoundPod_ThrowsMcpApiException409()
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();

        await runStore.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = Path.Combine(Path.GetTempPath(), "agentweaver-mcp-preview-norepo"),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "preview test task",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        });

        // Auto-approve at the HITL gate so the request reaches the preview-start path instead of
        // suspending for an operator. With the preview service disabled and no registered pod, the
        // legacy port-forward path then fails deterministically with 409.
        _factory.Services.GetRequiredService<IRunOptionsStore>()
            .SetAutoApproveTools(runId.ToString(), true);

        var tools = CreateTools();
        var act = () => tools.StartPreviewAsync(runId.ToString(), 3000, ct: CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 409);
    }

    [Fact]
    public async Task StartPreview_TransportTimeout_ReturnsActionableMcpError()
    {
        using var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("preview request timed out")));
        var api = new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey));
        var tools = new RunTools(api);

        var act = () => tools.StartPreviewAsync(
            "run-preview-timeout",
            8080,
            session_id: null,
            ct: CancellationToken.None);

        var error = await act.Should().ThrowAsync<McpApiException>();
        error.Which.StatusCode.Should().Be(-32001);
        error.Which.Error.Should().Contain("Preview registration did not complete");
        error.Which.Hint.Should().Contain("run_status");
    }

    [Fact]
    public async Task StartPreview_InternalDeadline_StopsHungRequest()
    {
        using var http = new HttpClient(new HangingHandler());
        var api = new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey));
        var tools = new RunTools(api, TimeSpan.FromMilliseconds(50));

        var act = () => tools.StartPreviewAsync(
            "run-preview-hung",
            8080,
            session_id: null,
            ct: CancellationToken.None);

        var error = await act.Should().ThrowAsync<McpApiException>();
        error.Which.ApiErrorCode.Should().Be("preview_registration_timeout");
        error.Which.Error.Should().Contain("50 milliseconds");
        error.Which.Hint.Should().Contain("review it");
    }

    [Fact]
    public async Task StartPreview_ApprovalDecision_CompletesOriginalRequest()
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.Combine(Path.GetTempPath(), "agentweaver-mcp-preview-approval"),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "approval completion test",
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var streams = _factory.Services.GetRequiredService<RunStreamStore>();
        streams.Create(runId.ToString(), ProjectsWebApplicationFactory.TestUser);

        var tools = CreateTools();
        var preview = tools.StartPreviewAsync(
            runId.ToString(),
            3000,
            session_id: null,
            ct: CancellationToken.None);
        var requestId = await WaitForApprovalRequestIdAsync(streams, runId.ToString());

        using var client = _factory.CreateAuthenticatedClient();
        var approval = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        approval.StatusCode.Should().Be(HttpStatusCode.OK);
        Func<Task> previewResult = async () => _ = await preview;
        await previewResult.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 409);
        streams.Get(runId.ToString())!.GetSnapshotSince(0).Events
            .Should().Contain(evt => evt.Type == EventTypes.ToolApprovalResolved);
    }

    [Fact]
    public async Task StartPreview_CallerDeadline_ReturnsActionableMcpError()
    {
        using var http = new HttpClient(new CancelledHandler());
        var api = new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey));
        var tools = new RunTools(api);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        var act = () => tools.StartPreviewAsync(
            "run-preview-deadline",
            8080,
            session_id: "preview-session",
            ct: deadline.Token);

        var error = await act.Should().ThrowAsync<McpApiException>();
        error.Which.StatusCode.Should().Be(-32001);
        error.Which.ApiErrorCode.Should().Be("preview_registration_timeout");
        error.Which.Hint.Should().Contain("run_status");
    }

    [Fact]
    public async Task StartPreview_ForwardsObservedSessionId()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var api = new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey));
        var tools = new RunTools(api);

        await tools.StartPreviewAsync(
            "run-with-observed-process",
            8080,
            session_id: "preview-session-123",
            ct: CancellationToken.None);

        handler.LastPath.Should().Be("/api/runs/run-with-observed-process/sandbox/preview");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("target_port").GetInt32().Should().Be(8080);
        body.RootElement.GetProperty("preview_runner_session_id").GetString()
            .Should().Be("preview-session-123");
    }

    [Fact]
    public void PreviewProcessExitedConflict_ReturnsActionableMcpError()
    {
        var error = new McpApiException(
            409,
            "Preview session has exited or is unreachable; a preview URL cannot be published.",
            "/api/runs/run-ended/sandbox/preview");

        error.Error.Should().Be("The preview process or its run is no longer active.");
        error.Hint.Should().Contain("session_id");
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CancelledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<string> WaitForApprovalRequestIdAsync(
        RunStreamStore streams,
        string runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var approval = streams.Get(runId)!.GetSnapshotSince(0).Events
                .FirstOrDefault(evt => evt.Type == EventTypes.ToolApprovalRequired);
            if (approval is not null)
            {
                var payload = JsonSerializer.SerializeToElement(approval.Payload);
                return payload.GetProperty("requestId").GetString()!;
            }
            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Approval request was not emitted.");
    }
}
