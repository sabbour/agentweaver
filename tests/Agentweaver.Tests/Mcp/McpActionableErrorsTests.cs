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
    public async Task GitHubStatus_Unauthorized_ThrowsAuthFirstHint()
    {
        var tools = CreateGitHubTools((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var act = () => tools.GitHubStatusAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<McpApiException>();
        ex.Which.StatusCode.Should().Be(401);
        ex.Which.Error.Should().Be("Not signed in.");
        ex.Which.Hint.Should().Be("Call github_signin then session_start before retrying.");
    }

    private static ProjectTools CreateProjectTools(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
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
