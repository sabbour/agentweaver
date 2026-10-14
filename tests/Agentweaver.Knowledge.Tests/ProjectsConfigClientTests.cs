using System.Net;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

public sealed class ProjectsConfigClientTests
{
    [Fact]
    public async Task RunSelectionMayOmitPromptTokenLimit()
    {
        var (client, httpClient) = CreateClient("""
            {
              "projectId": "project-a",
              "runId": "run-a",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "context-v1",
              "providers": [],
              "runLimits": {}
            }
            """);
        using (httpClient)
        {
            var selection = await client.GetRunSelectionAsync("project-a", "run-a", CancellationToken.None);

            Assert.Null(selection.RunLimits.MaxPromptTokens);
        }
    }

    [Fact]
    public async Task RunSelectionPreservesConfiguredPromptTokenLimit()
    {
        var (client, httpClient) = CreateClient("""
            {
              "projectId": "project-a",
              "runId": "run-a",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "context-v1",
              "providers": [],
              "runLimits": { "maxPromptTokens": 20000 }
            }
            """);
        using (httpClient)
        {
            var selection = await client.GetRunSelectionAsync("project-a", "run-a", CancellationToken.None);

            Assert.Equal(20000, selection.RunLimits.MaxPromptTokens);
        }
    }

    [Fact]
    public async Task RunSelectionRejectsExplicitZeroPromptTokenLimit()
    {
        var (client, httpClient) = CreateClient("""
            {
              "projectId": "project-a",
              "runId": "run-a",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "platformRuntimeRevision": 1,
              "contextRevision": "context-v1",
              "providers": [],
              "runLimits": { "maxPromptTokens": 0 }
            }
            """);
        using (httpClient)
        {
            var error = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
                client.GetRunSelectionAsync("project-a", "run-a", CancellationToken.None));

            Assert.Equal("invalid_run_selection", error.Code);
            Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
        }
    }

    private static (ProjectsConfigClient Client, HttpClient HttpClient) CreateClient(string responseBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer caller-token";
        var options = new KnowledgeRuntimeOptions(
            new Uri("https://identity.test/"),
            "knowledge-tests",
            new Uri("https://projects.test/"),
            new Uri("https://events.test/"),
            "events-tests",
            new NativePostgresMemoryOptions(
                "native-memory", "knowledge", 1, "knowledge", "test-options", 1),
            100,
            20,
            1000);
        var client = new HttpClient(new StaticResponseHandler(responseBody))
        {
            BaseAddress = options.ProjectsConfigBaseAddress
        };
        return (new ProjectsConfigClient(client, new HttpContextAccessor { HttpContext = context }, options), client);
    }

    private sealed class StaticResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                "/api/projects/project-a/runs/run-a/selection",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("caller-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
