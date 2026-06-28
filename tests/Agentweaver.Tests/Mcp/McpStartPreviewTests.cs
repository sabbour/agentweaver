using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

        var act = () => tools.StartPreviewAsync(Guid.NewGuid().ToString("N"), 3000, CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 404);
    }

    [Fact]
    public async Task StartPreview_InvalidPort_ThrowsMcpApiException400()
    {
        var tools = CreateTools();

        var act = () => tools.StartPreviewAsync(Guid.NewGuid().ToString("N"), 70000, CancellationToken.None);

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
        var act = () => tools.StartPreviewAsync(runId.ToString(), 3000, CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 409);
    }
}
