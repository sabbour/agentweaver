using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scaffolder.Api.Tests.Contracts;

/// <summary>
/// T067: Contract tests for POST /runs and GET /runs/{runId}.
/// Validates responses against the run-api.yaml contract shapes.
/// </summary>
public sealed class RunEndpointsContractTests(ScaffolderWebAppFactory factory)
    : IClassFixture<ScaffolderWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task PostRuns_ValidRequest_Returns201WithRunSchema()
    {
        var request = new
        {
            originatingBranch = "main",
            modelSource = "CopilotSdk",
            taskPrompt = "Write a README file",
            maxSteps = 10,
            maxDurationSeconds = 60
        };

        var response = await _client.PostAsJsonAsync("/runs", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.TryGetProperty("id", out _).Should().BeTrue("id field is required");
        root.TryGetProperty("status", out var status).Should().BeTrue("status field is required");
        root.TryGetProperty("originatingBranch", out _).Should().BeTrue();
        root.TryGetProperty("modelSource", out _).Should().BeTrue();
        root.TryGetProperty("taskPrompt", out _).Should().BeTrue();

        status.GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task PostRuns_InvalidModelSource_Returns400()
    {
        var request = new
        {
            originatingBranch = "main",
            modelSource = "InvalidProvider",
            taskPrompt = "Test"
        };

        var response = await _client.PostAsJsonAsync("/runs", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostRuns_MissingTaskPrompt_Returns400()
    {
        var request = new
        {
            originatingBranch = "main",
            modelSource = "CopilotSdk",
            taskPrompt = ""
        };

        var response = await _client.PostAsJsonAsync("/runs", request);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task GetRun_ExistingRun_Returns200WithSchema()
    {
        // Create a run first
        var createRequest = new
        {
            originatingBranch = "main",
            modelSource = "CopilotSdk",
            taskPrompt = "Test status endpoint"
        };
        var createResponse = await _client.PostAsJsonAsync("/runs", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var runId = createDoc.RootElement.GetProperty("id").GetString()!;

        // Get the run
        var getResponse = await _client.GetAsync($"/runs/{runId}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be(runId);
        root.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetRun_NonExistentRun_Returns404()
    {
        var runId = Guid.NewGuid();
        var response = await _client.GetAsync($"/runs/{runId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
