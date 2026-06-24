using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Workflows;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Tests for the "new workflow from scratch" flow (Feature 015 US9).
/// Validates that:
/// - The blank YAML template shipped to the frontend parses and loads cleanly through
///   <see cref="WorkflowDefinitionLoader"/> (catches regressions to the template format).
/// - After the PUT endpoint saves a new workflow to disk, the registry reflects it
///   immediately so the coordinator can select it without an explicit Sync call.
/// </summary>
public sealed class NewWorkflowFromScratchTests : IClassFixture<ProjectsWebApplicationFactory>
{
    // The blank template that WorkflowEditor.tsx ships to the browser. Keep in sync with
    // apps/web/src/components/WorkflowEditor.tsx BLANK_TEMPLATE.
    private const string BlankTemplateYaml = """
        id: my-workflow
        name: My Workflow
        description: Describe what this workflow does and when to use it.
        version: "1.0"

        trigger:
          type: manual

        start: agent

        nodes:
          - id: agent
            type: prompt
            label: Agent
            agent: lead

          - id: done
            type: terminal
            label: Done

        edges:
          - from: agent
            to: done
        """;

    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public NewWorkflowFromScratchTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ── Unit: blank template is a valid workflow definition ─────────────────────────────────────

    [Fact]
    public void BlankTemplate_ParsesAndLoadsSuccessfully()
    {
        var result = WorkflowDefinitionLoader.Load(BlankTemplateYaml, "my-workflow");

        result.IsValid.Should().BeTrue(because: $"the blank template must load cleanly; error: {result.Error}");
        result.Definition.Should().NotBeNull();
        result.Definition!.Id.Should().Be("my-workflow");
        result.Definition.Name.Should().Be("My Workflow");
        result.Definition.Trigger.Type.Should().Be(WorkflowTriggerType.Manual);
        result.Definition.Start.Should().Be("agent");
        result.Definition.Nodes.Should().HaveCount(2);
        result.Definition.Edges.Should().HaveCount(1);
    }

    // ── Integration: PUT saves, registry reflects immediately, GET list returns the workflow ────

    [Fact]
    public async Task PutNewWorkflow_ThenGetWorkflows_ReturnsNewWorkflow()
    {
        var (projectId, _) = await CreateProjectAsync();

        // PUT the blank template (simulating "Save" from WorkflowEditor).
        var putResp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        putResp.StatusCode.Should().Be(HttpStatusCode.OK, "PUT must succeed for a valid template");
        var detail = await putResp.Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("id").GetString().Should().Be("my-workflow");

        // GET the list — the registry must already reflect the saved workflow (Sync is called
        // inside the PUT handler so no separate /sync call is needed by the coordinator).
        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/workflows");
        var workflows = list.GetProperty("workflows");
        var ids = Enumerable.Range(0, workflows.GetArrayLength())
            .Select(i => workflows[i].GetProperty("id").GetString())
            .ToList();

        ids.Should().Contain("my-workflow",
            because: "the registry must pick up the saved workflow without a separate Sync");
    }

    [Fact]
    public async Task PutNewWorkflow_WorkflowIsValidAndSelectable()
    {
        var (projectId, _) = await CreateProjectAsync();

        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/workflows");
        var workflows = list.GetProperty("workflows");
        var myWorkflow = Enumerable.Range(0, workflows.GetArrayLength())
            .Select(i => workflows[i])
            .FirstOrDefault(w => w.GetProperty("id").GetString() == "my-workflow");

        myWorkflow.ValueKind.Should().NotBe(JsonValueKind.Undefined, "my-workflow must appear in the list");
        myWorkflow.GetProperty("valid").GetBoolean().Should().BeTrue(
            because: "the blank template must pass validation so it is coordinator-selectable");
        myWorkflow.GetProperty("is_built_in").GetBoolean().Should().BeFalse(
            because: "a user-saved workflow is not built-in");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(string ProjectId, string WorkingDirectory)> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"New Workflow Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
        return (id, dir);
    }
}
