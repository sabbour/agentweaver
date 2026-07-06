using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Agentweaver.Api.Contracts;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

[Collection("WorkflowIntegration")]
public sealed class WorkflowIntegrationTests : IDisposable
{
    private readonly WorkflowWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public WorkflowIntegrationTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task PostRuns_ReturnsGone_BecauseCoordinatorIsTheOnlyStartWorkPath()
    {
        var response = await _client.PostAsJsonAsync("/api/runs", new CreateRunRequest
        {
            Task = "do work",
            RepositoryPath = Environment.CurrentDirectory,
            OriginatingBranch = "main",
            ModelSource = "github-copilot",
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Gone);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
