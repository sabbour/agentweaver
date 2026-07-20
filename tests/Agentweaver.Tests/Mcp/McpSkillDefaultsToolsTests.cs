using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpSkillDefaultsToolsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public McpSkillDefaultsToolsTests(ProjectsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DefaultsTools_MirrorPreviewApplyAndPreserveStaleErrorDetail()
    {
        using var http = _factory.CreateAuthenticatedClient();
        var projectId = await CreateConfirmedTeamAsync(http);
        var tools = new SkillTools(new AgentweaverApiClient(
            _factory.CreateClient(),
            new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey)));

        var previewJson = await tools.SkillDefaultsPreviewAsync(projectId, "blueprint-software-development");
        using var preview = JsonDocument.Parse(previewJson);
        var digest = preview.RootElement.GetProperty("digest").GetString()!;
        preview.RootElement.GetProperty("can_apply").GetBoolean().Should().BeTrue();

        var stale = await tools.SkillDefaultsApplyAsync(projectId, "blueprint-software-development", "stale");
        stale.Should().Contain("skill_defaults_apply failed: HTTP 409");
        stale.Should().Contain("preview is stale");

        var appliedJson = await tools.SkillDefaultsApplyAsync(projectId, "blueprint-software-development", digest);
        using var applied = JsonDocument.Parse(appliedJson);
        applied.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
    }

    [Fact]
    public void SkillTools_OptionalParametersAndCancellationTokensHaveExplicitDefaults()
    {
        var create = typeof(SkillTools).GetMethod(nameof(SkillTools.SkillCreateAsync))!;
        create.GetParameters().Single(parameter => parameter.Name == "description")
            .HasDefaultValue.Should().BeTrue();
        create.GetParameters().Single(parameter => parameter.Name == "display_name")
            .HasDefaultValue.Should().BeTrue();

        var import = typeof(SkillTools).GetMethod(nameof(SkillTools.SkillImportAsync))!;
        import.GetParameters().Single(parameter => parameter.Name == "locations")
            .HasDefaultValue.Should().BeTrue();

        foreach (var method in typeof(SkillTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name.StartsWith("Skill", StringComparison.Ordinal)))
        {
            method.GetParameters().Where(parameter => parameter.ParameterType == typeof(CancellationToken))
                .Should().OnlyContain(parameter => parameter.HasDefaultValue);
        }
    }

    private async Task<string> CreateConfirmedTeamAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"MCP defaults {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!;

        var proposal = await client.PostAsJsonAsync(
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
        proposal.EnsureSuccessStatusCode();
        var proposalId = (await proposal.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("proposal_id").GetString()!;
        var confirm = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}/confirm",
            new { });
        confirm.EnsureSuccessStatusCode();
        return projectId;
    }
}
