using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Skills;

public sealed class SkillDefaultsEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public SkillDefaultsEndpointsTests(ProjectsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PreviewAndApply_ExposeStateBoundDtoAndMaterializeBuiltIns()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var projectId = await CreateConfirmedSoftwareTeamAsync(client);

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/skill-defaults/preview",
            new { blueprint_id = "blueprint-software-development" });

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("blueprint_id").GetString().Should().Be("blueprint-software-development");
        preview.GetProperty("blueprint_version").GetString().Should().NotBeNullOrWhiteSpace();
        preview.GetProperty("digest").GetString().Should().NotBeNullOrWhiteSpace();
        preview.GetProperty("can_apply").GetBoolean().Should().BeTrue();
        preview.GetProperty("assignments").GetArrayLength().Should().Be(8);

        var staleResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/skill-defaults/apply",
            new { blueprint_id = "blueprint-software-development", digest = "not-the-preview" });
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stale = await staleResponse.Content.ReadFromJsonAsync<JsonElement>();
        stale.GetProperty("outcome").GetString().Should().Be("stale");
        stale.GetProperty("errors")[0].GetString().Should().Contain("stale");

        var applyResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/skill-defaults/apply",
            new
            {
                blueprint_id = "blueprint-software-development",
                digest = preview.GetProperty("digest").GetString(),
            });
        applyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await applyResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString().Should().Be("applied");

        var skills = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/skills");
        skills.GetArrayLength().Should().Be(8);
        skills.EnumerateArray().Should().OnlyContain(skill =>
            skill.GetProperty("provenance").GetString() == "built-in");
    }

    [Fact]
    public async Task Preview_RequiresConfirmedTeamAndReportsDetail()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/skill-defaults/preview",
            new { blueprint_id = "blueprint-software-development" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("can_apply").GetBoolean().Should().BeFalse();
        body.GetProperty("errors")[0].GetString().Should().Contain("confirmed team");
    }

    [Fact]
    public async Task Preview_ReportsManualCollisionAsBoundedBlockedAction()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var projectId = await CreateConfirmedSoftwareTeamAsync(client);
        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/skills", new
        {
            name = "system-design",
            description = "Project-specific design guidance.",
            instructions = "Use the project design process.",
        });
        create.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/skill-defaults/preview",
            new { blueprint_id = "blueprint-software-development" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("assignments").EnumerateArray()
            .Should().Contain(assignment =>
                assignment.GetProperty("skill_name").GetString() == "system-design" &&
                assignment.GetProperty("action").GetString() == "blocked");
    }

    [Fact]
    public async Task Apply_CannotReplayIdenticalPreviewAcrossProjects()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var firstProject = await CreateConfirmedSoftwareTeamAsync(client);
        var secondProject = await CreateConfirmedSoftwareTeamAsync(client);
        var firstPreviewResponse = await client.PostAsJsonAsync(
            $"/api/projects/{firstProject}/skill-defaults/preview",
            new { blueprint_id = "blueprint-software-development" });
        var secondPreviewResponse = await client.PostAsJsonAsync(
            $"/api/projects/{secondProject}/skill-defaults/preview",
            new { blueprint_id = "blueprint-software-development" });
        firstPreviewResponse.EnsureSuccessStatusCode();
        secondPreviewResponse.EnsureSuccessStatusCode();
        var firstPreview = await firstPreviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondPreview = await secondPreviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstDigest = firstPreview.GetProperty("digest").GetString();
        firstDigest.Should().NotBe(secondPreview.GetProperty("digest").GetString());

        var replay = await client.PostAsJsonAsync(
            $"/api/projects/{secondProject}/skill-defaults/apply",
            new
            {
                blueprint_id = "blueprint-software-development",
                digest = firstDigest,
            });

        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await replay.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString().Should().Be("stale");
    }

    private async Task<string> CreateConfirmedSoftwareTeamAsync(HttpClient client)
    {
        var projectId = await CreateProjectAsync(client);
        var proposalResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new
            {
                mode = "manual",
                role_ids = new[]
                {
                    "lead-architect", "frontend-engineer", "backend-engineer", "security-engineer",
                    "devops-engineer", "qa-engineer", "docs-writer",
                },
            });
        proposalResponse.EnsureSuccessStatusCode();
        var proposal = await proposalResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = proposal.GetProperty("proposal_id").GetString();
        var confirm = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}/confirm",
            new { });
        confirm.EnsureSuccessStatusCode();
        return projectId;
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Skill defaults {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }
}
