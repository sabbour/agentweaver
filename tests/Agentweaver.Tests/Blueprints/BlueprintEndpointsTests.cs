using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Blueprints;

/// <summary>
/// Integration tests for the blueprint runtime (Feature 012): listing predefined blueprints,
/// applying a blueprint at project creation, the role constraint (reject unknown roles/no minting),
/// and generate-output validation. Uses live SQLite + the real catalog and
/// casting pipeline through the web host; only the external model is stubbed (StubBlueprintGenerator).
/// </summary>
public sealed class BlueprintEndpointsTests : IClassFixture<BlueprintsWebApplicationFactory>
{
    private readonly BlueprintsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BlueprintEndpointsTests(BlueprintsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<(string Id, string Dir)> CreateBlankWithBlueprintAsync(CreateProjectRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("project_id").GetString()!, body.GetProperty("working_directory").GetString()!);
    }

    [Fact]
    public async Task GetBlueprints_ReturnsSixPredefined_WithCatalogRosters()
    {
        var response = await _client.GetAsync("/api/blueprints");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprints = body.GetProperty("blueprints").EnumerateArray().ToList();
        blueprints.Should().HaveCount(5);

        var ids = blueprints.Select(b => b.GetProperty("id").GetString()).ToList();
        ids.Should().Contain(new[]
        {
            "blueprint-content-authoring",
            "blueprint-ai-agent-engineering",
            "blueprint-product-management",
            "blueprint-software-development",
            "blueprint-pm-and-software-development",
        });

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        foreach (var b in blueprints)
        {
            var roster = b.GetProperty("roster").EnumerateArray().Select(r => r.GetString()!).ToList();
            roster.Should().NotBeEmpty();
            foreach (var roleId in roster)
                catalog.HasRole(roleId).Should().BeTrue($"roster role '{roleId}' must resolve in the catalog");

            b.GetProperty("workflow").GetString().Should().NotBeNullOrWhiteSpace();
            b.GetProperty("review_policy").GetString().Should().NotBeNullOrWhiteSpace();
            b.GetProperty("sandbox_profile").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task CreateProject_WithPredefinedBlueprint_SeedsRosterAndDefaults()
    {
        var dir = _factory.NewWorkingDirectory();
        var request = new CreateProjectRequest
        {
            Name = "SD Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            BlueprintId = "blueprint-software-development",
        };

        var (id, _) = await CreateBlankWithBlueprintAsync(request);

        // Project defaults reflect the blueprint.
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await store.GetAsync(ProjectId.Parse(id));
        project.Should().NotBeNull();
        project!.DefaultWorkflowId.Should().Be("software-delivery");
        project.ActiveReviewPolicyName.Should().Be("default");
        project.SandboxProfile.Should().Be("default");

        // Roster seeded by casting contains the blueprint roles.
        var casting = _factory.Services.GetRequiredService<CastingService>();
        var team = await casting.GetTeamAsync(id, CancellationToken.None);
        team.Should().NotBeNull();
        var roleIds = team!.Members.Select(m => m.Role.Id).ToList();
        roleIds.Should().Contain(new[]
        {
            "lead-architect", "frontend-engineer", "backend-engineer", "security-engineer", "docs-writer",
        });
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithRestrictedSandbox_WritesConcretePolicy()
    {
        var dir = _factory.NewWorkingDirectory();
        var request = new CreateProjectRequest
        {
            Name = "Restricted Sandbox Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-restricted",
                Name = "Restricted",
                Description = "Uses the restricted sandbox preset.",
                Roster = ["backend-engineer"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "restricted",
            },
        };

        var (id, _) = await CreateBlankWithBlueprintAsync(request);

        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await store.GetAsync(ProjectId.Parse(id));
        project!.SandboxProfile.Should().Be("restricted");

        var policyStore = _factory.Services.GetRequiredService<ISandboxPolicyStore>();
        var policy = await policyStore.GetPolicyAsync(dir);
        policy.RequireApprovalForAllShell.Should().BeTrue(
            "the blueprint sandbox profile must be enforced via a persisted concrete policy");
        policy.NetworkEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateBlueprint_SecurityEngineerRole_IsAccepted()
    {
        // bp-allow-security: the core "security-engineer" role (added to the catalog by tank-4) must be
        // rosterable in blueprints. Validation is catalog-driven, so it is accepted once the role lands.
        var request = new ValidateBlueprintRequest
        {
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-quick-software",
                Name = "Quick Software",
                Description = "Frontend, backend, and security.",
                Roster = ["frontend-engineer", "backend-engineer", "security-engineer"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeTrue(
            (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ValidateBlueprint_UnknownRole_IsRejected()
    {
        var request = new ValidateBlueprintRequest
        {
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-bogus",
                Name = "Bogus",
                Description = "Has a role nobody registered.",
                Roster = ["totally-unknown-role"],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/blueprints/validate", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeFalse();
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        errors.Should().Contain(e => e!.Contains("totally-unknown-role"));
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithUnknownRole_IsRejected()
    {
        var dir = _factory.NewWorkingDirectory();
        // Blueprints never mint roles: an inline blueprint that rosters a non-catalog role is rejected
        // at creation and no role is created.
        const string unknownRoleId = "growth-hacker";
        var request = new CreateProjectRequest
        {
            Name = "Unknown Role Blueprint Project",
            Origin = "blank",
            WorkingDirectory = dir,
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-with-unknown-role",
                Name = "With Unknown Role",
                Description = "References a role that is not in the catalog.",
                Roster = ["backend-engineer", unknownRoleId],
                Workflow = "default",
                ReviewPolicy = "default",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_blueprint");
        var details = body.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        details.Should().Contain(e => e!.Contains(unknownRoleId));

        // No role was minted: the catalog still does not know the unknown role.
        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }

    [Fact]
    public async Task CreateProject_InlineBlueprintWithUnknownWorkflowOrPolicy_IsRejected()
    {
        var request = new CreateProjectRequest
        {
            Name = "Unknown References Blueprint Project",
            Origin = "blank",
            WorkingDirectory = _factory.NewWorkingDirectory(),
            Blueprint = new BlueprintDto
            {
                Id = "blueprint-with-unknown-refs",
                Name = "With Unknown References",
                Description = "References workflow and review policy ids that are unavailable.",
                Roster = ["backend-engineer"],
                Workflow = "missing-workflow",
                ReviewPolicy = "missing-review-policy",
                SandboxProfile = "default",
            },
        };

        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_blueprint");
        var details = body.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        details.Should().Contain(e => e!.Contains("missing-workflow"));
        details.Should().Contain(e => e!.Contains("missing-review-policy"));
    }

    [Fact]
    public async Task GenerateBlueprint_MalformedModelOutput_Returns422()
    {
        _factory.Generator.Response = "I am sorry, I cannot produce that.";

        var response = await _client.PostAsJsonAsync(
            "/api/blueprints/generate", new GenerateBlueprintRequest { Description = "a data team" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("blueprint_generation_failed");
    }

    [Fact]
    public async Task GenerateBlueprint_RosterRoleNotInCatalog_Returns422_NoRoleMinted()
    {
        // The model rostered a role that is not in the catalog; generation must reject (no minting).
        const string unknownRoleId = "growth-hacker";
        _factory.Generator.Response = $$"""
            {
              "id": "blueprint-generated-bad",
              "name": "Generated Bad Team",
              "description": "Rosters a non-catalog role.",
              "roster": ["backend-engineer", "{{unknownRoleId}}"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/blueprints/generate", new GenerateBlueprintRequest { Description = "a growth team" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("blueprint_generation_failed");

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateBlueprint_ValidModelOutput_ReturnsBlueprint()
    {
        _factory.Generator.Response = """
            {
              "id": "blueprint-generated-data",
              "name": "Generated Data Team",
              "description": "A small data team.",
              "roster": ["backend-engineer", "docs-writer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "restricted"
            }
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/blueprints/generate", new GenerateBlueprintRequest { Description = "a data team" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprint = body.GetProperty("blueprint");
        blueprint.GetProperty("id").GetString().Should().Be("blueprint-generated-data");
        blueprint.GetProperty("name").GetString().Should().Be("Generated Data Team");
        blueprint.GetProperty("description").GetString().Should().Be("A small data team.");
        blueprint.GetProperty("workflow").GetString().Should().Be("software-delivery");
        blueprint.GetProperty("review_policy").GetString().Should().Be("default");
        blueprint.GetProperty("sandbox_profile").GetString().Should().Be("restricted");
        var roster = blueprint.GetProperty("roster").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        roster.Should().Contain(new[] { "backend-engineer", "docs-writer" });
        body.TryGetProperty("new_roles", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateBlueprint_BespokeRoleMissingFromRoster_IsAutoRostered_ReturnsBlueprint()
    {
        // Mirrors the live staging bug: the LLM declared 'job-match-analyst' in bespoke_roles but
        // forgot to add it to roster. After the reconcile fix, generation must succeed and the
        // auto-rostered bespoke id must appear in the returned roster.
        _factory.Generator.Response = """
            {
              "id": "job-search-team",
              "name": "Job Search Team",
              "description": "Finds and compares jobs, generates a customized CV, and creates an interview guide.",
              "roster": ["backend-engineer"],
              "bespoke_roles": [
                {
                  "id": "job-match-analyst",
                  "title": "Job Match Analyst",
                  "charter": "Analyzes job postings against the user profile. Scores and ranks matches. Identifies skill gaps and strengths. Provides actionable recommendations."
                }
              ],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/blueprints/generate", new GenerateBlueprintRequest { Description = "Find jobs based on my profile, compare them, generate a customized CV, create an interview guide" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blueprint = body.GetProperty("blueprint");
        var roster = blueprint.GetProperty("roster").EnumerateArray()
            .Select(r => r.GetString()).ToList();
        roster.Should().Contain("job-match-analyst",
            "the bespoke role declared in bespoke_roles must be auto-rostered when the LLM omits it from roster");
    }

    [Fact]
    public async Task GenerateBlueprint_RosterRoleNeitherCatalogNorBespoke_IsStillRejected()
    {
        // Confirms that the reconcile step does NOT loosen validation for a roster role that has
        // no catalog entry and no bespoke definition — it must still be rejected (no role minting).
        const string unknownRoleId = "mystery-guru";
        _factory.Generator.Response = $$"""
            {
              "id": "blueprint-still-bad",
              "name": "Still Bad Team",
              "description": "Rosters a role with no catalog entry and no bespoke definition.",
              "roster": ["backend-engineer", "{{unknownRoleId}}"],
              "bespoke_roles": [],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/blueprints/generate", new GenerateBlueprintRequest { Description = "a mystery team" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("blueprint_generation_failed");

        var catalog = _factory.Services.GetRequiredService<CatalogReader>();
        catalog.HasRole(unknownRoleId).Should().BeFalse();
    }
}
