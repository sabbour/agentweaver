using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
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

        start: agent

        nodes:
          - id: agent
            type: prompt
            label: Agent
            agent: lead

          - id: scribe
            type: scribe
            label: Scribe

          - id: done
            type: terminal
            label: Done

        edges:
          - from: agent
            to: scribe
          - from: scribe
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
        result.Definition.Start.Should().Be("agent");
        result.Definition.Nodes.Should().HaveCount(3);
        result.Definition.Edges.Should().HaveCount(2);
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

    // ── Bug regression: allowed-set filtering (#175) ────────────────────────────────────────────

    /// <summary>
    /// Regression test for #175. When a blueprint has set AllowedWorkflowIds, a newly saved workflow
    /// whose id is not yet in that set was silently filtered out by the registry, causing FindById to
    /// return null and the PUT handler to 500 with a misleading "file permissions" message.
    /// After the fix the handler adds the new id to AllowedWorkflowIds and the workflow becomes
    /// immediately selectable.
    /// </summary>
    [Fact]
    public async Task PutNewWorkflow_WhenAllowedSetExcludesNewId_AddsIdToAllowedSetAndSucceeds()
    {
        var (projectId, _) = await CreateProjectAsync();

        // Simulate a blueprint having restricted the allowed workflow set to just "default".
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var pid = ProjectId.Parse(projectId);
        await store.UpdateAllowedWorkflowIdsAsync(pid, ["default"], DateTimeOffset.UtcNow);

        // PUT a new workflow whose id is NOT yet in the allowed set.
        var putResp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        putResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "PUT must succeed even when the project's allowed set did not yet include the new workflow id");

        var detail = await putResp.Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("id").GetString().Should().Be("my-workflow");

        // The workflow must now be in the list (registry reflects the updated allowed set).
        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/workflows");
        var workflows = list.GetProperty("workflows");
        var ids = Enumerable.Range(0, workflows.GetArrayLength())
            .Select(i => workflows[i].GetProperty("id").GetString())
            .ToList();
        ids.Should().Contain("my-workflow",
            because: "the saved workflow id must be added to AllowedWorkflowIds and appear in the registry");

        var reloadedProject = await store.GetAsync(pid);
        reloadedProject!.AllowedWorkflowIds.Should().Contain("my-workflow",
            because: "the allowed-workflow filter must persist the saved id before the post-save reload");
    }

    [Fact]
    public async Task PutNewWorkflow_WhenAllowedSetAlreadyContainsId_SucceedsWithoutDuplicatingEntry()
    {
        var (projectId, _) = await CreateProjectAsync();

        // Pre-populate AllowedWorkflowIds so the id is already present (idempotent save).
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var pid = ProjectId.Parse(projectId);
        await store.UpdateAllowedWorkflowIdsAsync(pid, ["default", "my-workflow"], DateTimeOffset.UtcNow);

        var putResp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        putResp.StatusCode.Should().Be(HttpStatusCode.OK, "PUT must succeed when the id is already allowed");

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/workflows");
        var workflows = list.GetProperty("workflows");
        var matchCount = Enumerable.Range(0, workflows.GetArrayLength())
            .Select(i => workflows[i].GetProperty("id").GetString())
            .Count(id => id == "my-workflow");
        matchCount.Should().Be(1, "the workflow must appear exactly once, not duplicated");
    }

    [Fact]
    public async Task PutTriggerConfig_PersistsStructuredPredicates_AndExposesThemViaApi()
    {
        var (projectId, _) = await CreateProjectAsync();
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        var putTrigger = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow/trigger",
            new
            {
                type = "event",
                event_name = "github.pull_request.opened",
                @if = new object[]
                {
                    new
                    {
                        or = new object[]
                        {
                            new { baseBranch = new { branch = "main" } },
                            new { baseBranch = new { branch = "release/v1" } },
                        },
                    },
                },
            });

        putTrigger.StatusCode.Should().Be(HttpStatusCode.OK);

        var trigger = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/workflows/my-workflow/trigger");
        trigger.GetProperty("trigger").GetProperty("event_name").GetString().Should().Be("github.pull_request.opened");
        trigger.GetProperty("trigger").GetProperty("if")[0].GetProperty("or").GetArrayLength().Should().Be(2);

        var detail = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/workflows/my-workflow");
        detail.GetProperty("trigger").GetProperty("if")[0].GetProperty("or").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task DeleteTriggerConfig_ClearsTrigger()
    {
        var (projectId, _) = await CreateProjectAsync();
        var yamlWithTrigger = BlankTemplateYaml + """

            trigger:
              type: event
              event_name: github.push
            """;
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = yamlWithTrigger });

        var delete = await _client.DeleteAsync($"/api/projects/{projectId}/workflows/my-workflow/trigger");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await delete.Content.ReadFromJsonAsync<JsonElement>();
        response.GetProperty("trigger").ValueKind.Should().Be(JsonValueKind.Null);

        var detail = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/workflows/my-workflow");
        detail.GetProperty("trigger").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PatchTriggerConfig_PartiallyUpdatesExistingTrigger()
    {
        var (projectId, _) = await CreateProjectAsync();
        var yamlWithTrigger = BlankTemplateYaml + """

            trigger:
              type: event
              event_name: github.pull_request.opened
              if:
                - or:
                    - base_branch: { branch: "main" }
                    - base_branch: { branch: "release/v1" }
            """;
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = yamlWithTrigger });

        var patch = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow/trigger",
            new { event_name = "github.pull_request.synchronize" });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var trigger = await patch.Content.ReadFromJsonAsync<JsonElement>();
        trigger.GetProperty("trigger").GetProperty("event_name").GetString().Should().Be("github.pull_request.synchronize");
        trigger.GetProperty("trigger").GetProperty("if")[0].GetProperty("or").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task PutTriggerConfig_NotPredicate_RoundTripsWithoutLosingWrapper()
    {
        var (projectId, _) = await CreateProjectAsync();
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow",
            new { yaml = BlankTemplateYaml });

        var put = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/my-workflow/trigger",
            new
            {
                type = "event",
                event_name = "github.issues.opened",
                @if = new object[]
                {
                    new
                    {
                        not = new
                        {
                            hasLabel = new { label = "blocked" },
                        },
                    },
                },
            });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var trigger = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/workflows/my-workflow/trigger");
        trigger.GetProperty("trigger").GetProperty("if")[0].GetProperty("not").GetProperty("hasLabel").GetProperty("label")
            .GetString().Should().Be("blocked");

        var yaml = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/workflows/my-workflow/yaml");
        yaml.GetProperty("yaml").GetString().Should().Contain(
            """
              if:
                - not:
                    has_label: { label: blocked }
            """.Replace("\n", Environment.NewLine));
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
