using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;

namespace Agentweaver.Tests.Mcp;

public sealed class McpActionableErrorsTests
{
    [Fact]
    public async Task ProjectGet_NotFound_ThrowsStructuredProjectHint()
    {
        var tools = CreateProjectTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-123");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "missing" })
            });
        });

        var act = () => tools.ProjectGetAsync("proj-123", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.Error.Should().Be("Project 'proj-123' not found.");
        ex.Which.Hint.Should().Be("Call project_list to see available projects.");

        using var payload = JsonDocument.Parse(ex.Which.Message);
        payload.RootElement.GetProperty("error").GetString().Should().Be("Project 'proj-123' not found.");
        payload.RootElement.GetProperty("hint").GetString().Should().Be("Call project_list to see available projects.");
    }

    [Fact]
    public async Task RunReview_StateConflict_ThrowsStructuredReviewHint()
    {
        var tools = CreateRunTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-123/review");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { error = "Run is in status 'failed' and cannot be reviewed." })
            });
        });

        var act = () => tools.RunReviewAsync("run-123", approved: true, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Error.Should().Be("Run is not awaiting review (current state: failed).");
        ex.Which.Hint.Should().Be("Call run_status to check current state.");
    }

    [Fact]
    public async Task GitHubCapabilityConnect_Unauthorized_ThrowsAgentweaverSignInHint()
    {
        var tools = CreateGitHubTools((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var act = () => tools.GitHubRepoAppConnectAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(401);
        ex.Which.Error.Should().Be("Not signed in.");
        ex.Which.Hint.Should().Be("Sign in to Agentweaver, then retry.");
    }

    [Fact]
    public async Task TeamGet_ProjectExistsButNoTeam_ThrowsNoTeamConfiguredHint()
    {
        var tools = CreateTeamTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/projects/proj-456/team");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "workspace not found" })
            });
        });

        var act = () => tools.TeamGetAsync("proj-456", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.Error.Should().Be("No team configured for project 'proj-456'. Cast a team first with team_cast.");
        ex.Which.Hint.Should().Be("Use team_cast to initialize the team, then retry team_get.");

        using var payload = JsonDocument.Parse(ex.Which.Message);
        payload.RootElement.GetProperty("error").GetString()
            .Should().Be("No team configured for project 'proj-456'. Cast a team first with team_cast.");
    }

    [Fact]
    public async Task TeamGet_ProjectExistsButNoTeam_DoesNotSayProjectNotFound()
    {
        var tools = CreateTeamTools((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "workspace not found" })
            }));

        var ex = await ((Func<Task>)(() => tools.TeamGetAsync("proj-456", CancellationToken.None)))
            .Should().ThrowAsync<McpApiException>();

        ex.Which.Error.Should().NotContain("not found", "error should not say 'project not found' for uninitialized workspaces");
    }

    [Fact]
    public async Task TeamCast_ProjectExistsButNoTeam_ThrowsNoTeamConfiguredHint()
    {
        var tools = CreateTeamTools((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().StartWith("/api/projects/proj-456/casting");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "workspace not found" })
            });
        });

        var act = () => tools.TeamCastAsync("proj-456", goal: "Build a thing", confirm_proposal_id: null, confirm: false, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.Error.Should().Contain("No team configured");
        ex.Which.Error.Should().NotContain("Project 'proj-456' not found.");
    }


    private static ProjectTools CreateProjectTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static TeamTools CreateTeamTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static RunTools CreateRunTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static GitHubAuthTools CreateGitHubTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(CreateApiClient(handler));

    private static AgentweaverApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
