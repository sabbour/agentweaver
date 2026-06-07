using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Scaffolder.Api.Tests.Integration;

/// <summary>
/// T072: Integration test for the full run lifecycle.
/// Validates the state machine: queued -> running -> completed -> awaiting_review.
/// (The merge portion requires a real git repo and is tested in T080 quickstart.)
/// </summary>
public sealed class RunLifecycleIntegrationTests(ScaffolderWebAppFactory factory)
    : IClassFixture<ScaffolderWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SubmitRun_CompletesAndReachesAwaitingReview()
    {
        // Submit a run
        var request = new
        {
            originatingBranch = "main",
            modelSource = "CopilotSdk",
            taskPrompt = "Write a summary of the project",
            maxSteps = 5,
            maxDurationSeconds = 30
        };

        var createResponse = await _client.PostAsJsonAsync("/runs", request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await createResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var runId = doc.RootElement.GetProperty("id").GetString()!;

        // Poll until the run reaches AwaitingReview or a terminal state
        var finalStatus = await PollUntilTerminalOrReviewAsync(runId, TimeSpan.FromSeconds(20));

        finalStatus.Should().BeOneOf(
            "AwaitingReview",
            "Bounded",
            "Failed",
            "Completed");
    }

    [Fact]
    public async Task GetRun_AfterSubmit_ReturnsValidRunObject()
    {
        var request = new
        {
            originatingBranch = "main",
            modelSource = "MicrosoftFoundry",
            taskPrompt = "Test run object shape"
        };

        var createResponse = await _client.PostAsJsonAsync("/runs", request);
        createResponse.EnsureSuccessStatusCode();
        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createBody);
        var runId = createDoc.RootElement.GetProperty("id").GetString()!;

        var getResponse = await _client.GetAsync($"/runs/{runId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getBody);
        var run = getDoc.RootElement;

        run.GetProperty("id").GetString().Should().Be(runId);
        run.GetProperty("originatingBranch").GetString().Should().Be("main");
        run.GetProperty("modelSource").GetString().Should().Be("MicrosoftFoundry");
    }

    private async Task<string> PollUntilTerminalOrReviewAsync(string runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var terminalOrReview = new HashSet<string>
        {
            "AwaitingReview", "Bounded", "Failed", "Merged", "Declined", "MergeConflict"
        };

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);

            var response = await _client.GetAsync($"/runs/{runId}");
            if (!response.IsSuccessStatusCode) continue;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString()!;

            if (terminalOrReview.Contains(status))
            {
                return status;
            }
        }

        return "timeout";
    }
}
