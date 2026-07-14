using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Contracts;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Console;

[Collection("CoordinatorOutcomeSpec")]
public sealed class ConsoleEndpointsTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public ConsoleEndpointsTests()
    {
        _owner = _factory.CreateOwnerClient();
        _other = _factory.CreateOtherClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _other.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ConsoleMessage_ListProjects_ReturnsOnlyOwnedProjects()
    {
        var ownerProjectId = await CreateProjectAsync(_owner, "Owner Console Project");
        await CreateProjectAsync(_other, "Other Console Project");

        var response = await _owner.PostAsJsonAsync("/api/console/messages", new
        {
            message = "show my projects",
            context = new { scope = "global", route = "/projects" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().Should().Be("answer");
        body.GetProperty("tools")[0].GetProperty("label").GetString().Should().Be("project_list");
        var links = body.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(l => l.GetProperty("to").GetString() == $"/projects/{ownerProjectId}");
        links.Should().NotContain(l => l.GetProperty("label").GetString() == "Other Console Project");
    }

    [Fact]
    public async Task ConsoleTurnAlias_UsesSameBackendContract()
    {
        var ownerProjectId = await CreateProjectAsync(_owner, "Owner Console Turn Alias Project");

        var response = await _owner.PostAsJsonAsync("/api/console/turn", new
        {
            message = "show my projects",
            context = new { scope = "global", route = "/console" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().Should().Be("answer");
        body.GetProperty("tools")[0].GetProperty("label").GetString().Should().Be("project_list");
        body.GetProperty("links").EnumerateArray()
            .Should().Contain(l => l.GetProperty("to").GetString() == $"/projects/{ownerProjectId}");
    }

    [Fact]
    public async Task ConsoleMessage_StartOrchestration_ReturnsConfirmationWithoutCreatingRun()
    {
        var projectId = await CreateProjectAsync(_owner, "Console Start Project");

        var response = await _owner.PostAsJsonAsync("/api/console/messages", new
        {
            text = "start orchestration to make the dashboard faster",
            context = new { scope = "project", project_id = projectId, route = $"/projects/{projectId}" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().Should().Be("gate_required");
        body.GetProperty("status").GetString().Should().Be("needs_confirmation");
        body.GetProperty("project_id").GetString().Should().Be(projectId);
        body.GetProperty("run_id").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("gate").GetProperty("kind").GetString().Should().Be("start_orchestration");
        body.GetProperty("actionable_state").GetProperty("pending_gate").GetString().Should().Be("start_orchestration");

        var runs = await _owner.GetAsync($"/api/projects/{projectId}/runs");
        runs.StatusCode.Should().Be(HttpStatusCode.OK);
        var runEnvelope = await runs.Content.ReadFromJsonAsync<JsonElement>();
        var runList = runEnvelope.GetProperty("items").EnumerateArray().ToArray();
        runList.Should().BeEmpty("the console facade must not start a run without explicit confirmation");
    }

    [Fact]
    public async Task ConsoleMessage_ProjectContext_NonOwnerReturns403()
    {
        var projectId = await CreateProjectAsync(_owner, "Owner Only Console Project");

        var response = await _other.PostAsJsonAsync("/api/console/messages", new
        {
            message = "start orchestration to do work",
            context = new { scope = "project", project_id = projectId },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("forbidden");
    }

    [Fact]
    public async Task ConsoleMessage_GateRequest_DoesNotConsumeGate()
    {
        var projectId = await CreateProjectAsync(_owner, "Console Gate Project");

        var response = await _owner.PostAsJsonAsync("/api/console/messages", new
        {
            message = "confirm and merge it",
            context = new { scope = "project", project_id = projectId },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().Should().Be("gate_required");
        body.GetProperty("gate").GetProperty("kind").GetString().Should().Be("review_merge");
        body.TryGetProperty("run_id", out var runId).Should().BeTrue();
        runId.ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = name,
            Origin = "blank",
            WorkingDirectory = _factory.NewWorkingDirectory(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }
}
