using FluentAssertions;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Blueprints;

/// <summary>
/// Unit tests for <see cref="BlueprintGenerationParser"/>: a pure string-to-blueprint parser exercised
/// directly without the model. Covers empty input, prose without JSON, malformed JSON, JSON embedded in
/// prose, and a well-formed blueprint with catalog role ids.
/// </summary>
public sealed class BlueprintGenerationParserTests
{
    [Fact]
    public void Parse_EmptyResponse_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("   ");
        result.Succeeded.Should().BeFalse();
        result.Blueprint.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_NoJsonObject_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("I cannot help with that request.");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("{ \"id\": \"x\", roster: [ }");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_JsonEmbeddedInProse_ExtractsBlueprint()
    {
        var raw = """
            Here is the blueprint you asked for:
            {
              "id": "blueprint-data",
              "name": "Data Team",
              "description": "Builds data products.",
              "roster": ["backend-engineer", "docs-writer"],
              "workflow": "default",
              "review_policy": "default",
              "sandbox_profile": "restricted"
            }
            Let me know if you want changes.
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.Id.Should().Be("blueprint-data");
        result.Blueprint.Roster.Should().Contain(new[] { "backend-engineer", "docs-writer" });
        result.Blueprint.SandboxProfile.Should().Be("restricted");
    }

    [Fact]
    public void Parse_BespokeRoles_AreExtracted()
    {
        var raw = """
            {
              "id": "travel-planner",
              "name": "Travel Planner",
              "description": "Plans trips.",
              "roster": ["travel-researcher", "docs-writer"],
              "bespoke_roles": [
                { "id": "travel-researcher", "title": "Travel Researcher",
                  "charter": "You research destinations. You weigh climate and logistics." }
              ],
              "workflows": ["default"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.BespokeRoles.Should().ContainSingle();
        var bespoke = result.Blueprint.BespokeRoles[0];
        bespoke.Id.Should().Be("travel-researcher");
        bespoke.Title.Should().Be("Travel Researcher");
        bespoke.Charter.Should().Contain("research destinations");
    }

    [Fact]
    public void Parse_BespokeRoleMissingCharter_IsSkipped()
    {
        var raw = """
            {
              "id": "x", "name": "X", "description": "d",
              "roster": ["a"],
              "bespoke_roles": [ { "id": "a", "title": "A" } ],
              "review_policy": "default", "sandbox_profile": "default"
            }
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.BespokeRoles.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SkillBindings_ArePreserved()
    {
        var result = BlueprintGenerationParser.Parse(
            """{"id":"x","name":"X","description":"d","roster":["lead-architect"],"workflow":"default","review_policy":"default","sandbox_profile":"default","skill_bindings":[{"role_id":"lead-architect","skills":["architecture-decisions","system-design"]}]}""");

        result.Succeeded.Should().BeTrue();
        result.Blueprint!.SkillBindings.Should().ContainSingle();
        result.Blueprint.SkillBindings[0].RoleId.Should().Be("lead-architect");
        result.Blueprint.SkillBindings[0].Skills.Should().Equal("architecture-decisions", "system-design");
    }

    [Fact]
    public async Task Generate_AutoRosterAndWorkflowFallback_PreserveSkillBindings()
    {
        var raw = """
            {
              "id": "generated",
              "name": "Generated",
              "description": "Generated blueprint.",
              "roster": ["backend-engineer"],
              "bespoke_roles": [
                {
                  "id": "specialist",
                  "title": "Specialist",
                  "charter": "Own the specialist work."
                }
              ],
              "workflows": [],
              "review_policy": "default",
              "sandbox_profile": "default",
              "skill_bindings": [
                { "role_id": "backend-engineer", "skills": ["api-data-safety"] }
              ]
            }
            """;
        var generatedYaml = DefaultWorkflowTemplate.Yaml.Replace(
            "id: default",
            "id: generated-fallback",
            StringComparison.Ordinal);
        var generated = WorkflowDefinitionLoader.Load(generatedYaml, "test");
        generated.IsValid.Should().BeTrue(generated.Error);
        var service = GenerationService(
            raw,
            new StubWorkflowGenerator(new WorkflowGenerationResult(
                generated.Definition!,
                generatedYaml,
                false)));

        var result = await service.GenerateAsync("test", CancellationToken.None);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        result.Blueprint!.Roster.Should().Contain("specialist");
        result.Blueprint.Workflows.Should().Equal("generated-fallback");
        result.Blueprint.SkillBindings.Should().ContainSingle();
        result.Blueprint.SkillBindings[0].Skills.Should().Equal("api-data-safety");
    }

    [Fact]
    public async Task Generate_FailedWorkflowFallback_PreservesSkillBindings()
    {
        var raw = """
            {
              "id": "generated",
              "name": "Generated",
              "description": "Generated blueprint.",
              "roster": ["backend-engineer"],
              "workflows": [],
              "review_policy": "default",
              "sandbox_profile": "default",
              "skill_bindings": [
                { "role_id": "backend-engineer", "skills": ["api-data-safety"] }
              ]
            }
            """;
        var service = GenerationService(
            raw,
            new StubWorkflowGenerator(new WorkflowGenerationException("injected failure")));

        var result = await service.GenerateAsync("test", CancellationToken.None);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        result.Blueprint!.Workflows.Should().Equal("default");
        result.Blueprint.SkillBindings.Should().ContainSingle();
        result.Blueprint.SkillBindings[0].Skills.Should().Equal("api-data-safety");
    }

    public static IEnumerable<object[]> PersonaDrivenBlueprints()
    {
        yield return ["ambiguous travel operations", """{"id":"travel-ops","name":"Travel Ops","description":"Coordinates trip research, itinerary writing, and review.","roster":["customer-researcher","docs-writer","quality-reviewer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["job search pipeline", """{"id":"job-search","name":"Job Search","description":"Tracks job leads, drafts materials, and reviews outreach.","roster":["customer-researcher","docs-writer","quality-reviewer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"restricted"}"""];
        yield return ["software bug fix", """{"id":"bug-fix-team","name":"Bug Fix Team","description":"Implements and reviews software fixes.","roster":["backend-engineer","qa-engineer","security-engineer"],"workflows":["software-delivery"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["public content release", """{"id":"release-notes","name":"Release Notes","description":"Drafts user-facing release notes and reviews them before publishing.","roster":["docs-writer","quality-reviewer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["incident response", """{"id":"incident-response","name":"Incident Response","description":"Triages incidents, gathers evidence, and writes summaries.","roster":["triage-lead","customer-researcher","docs-writer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"restricted"}"""];
        yield return ["security review", """{"id":"security-review","name":"Security Review","description":"Reviews implementation risk and records findings.","roster":["security-engineer","backend-engineer","quality-reviewer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["research report", """{"id":"research-report","name":"Research Report","description":"Researches a topic, synthesizes findings, and reviews the report.","roster":["customer-researcher","docs-writer","quality-reviewer"],"workflows":["default"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["frontend polish", """{"id":"frontend-polish","name":"Frontend Polish","description":"Implements UI polish with QA review.","roster":["frontend-engineer","qa-engineer","docs-writer"],"workflows":["software-delivery"],"review_policy":"default","sandbox_profile":"default"}"""];
        yield return ["data cleanup", """{"id":"data-cleanup","name":"Data Cleanup","description":"Plans cleanup, implements scripts, and validates results.","roster":["backend-engineer","qa-engineer"],"workflows":["software-delivery"],"review_policy":"default","sandbox_profile":"restricted"}"""];
        yield return ["bespoke event planning", """{"id":"event-planning","name":"Event Planning","description":"Plans logistics and communications for an event.","roster":["event-coordinator","docs-writer","quality-reviewer"],"bespoke_roles":[{"id":"event-coordinator","title":"Event Coordinator","charter":"You coordinate event logistics, owners, and timelines. You identify dependencies and escalate risks clearly."}],"workflows":["default"],"review_policy":"default","sandbox_profile":"default"}"""];
    }

    [Theory]
    [MemberData(nameof(PersonaDrivenBlueprints))]
    public void PersonaDrivenGeneratedBlueprints_PassStructuralValidation(string persona, string raw)
    {
        var parsed = BlueprintGenerationParser.Parse(raw);
        parsed.Succeeded.Should().BeTrue(persona);

        var catalog = new CatalogReader();
        var service = new BlueprintService(
            catalog,
            casting: null!,
            projectStore: null!,
            sandboxPolicyStore: null!,
            workflowRegistry: new WorkflowRegistry(new CatalogConformanceSnapshot(catalog)),
            generator: null!,
            workflowGenerator: null!,
            skillDefaults: null!,
            logger: NullLogger<BlueprintService>.Instance);

        var validation = service.Validate(parsed.Blueprint!);
        validation.Valid.Should().BeTrue($"{persona}: {string.Join("; ", validation.Errors)}");
    }

    private static BlueprintService GenerationService(
        string raw,
        IWorkflowGenerator workflowGenerator)
    {
        var catalog = new CatalogReader();
        return new BlueprintService(
            catalog,
            casting: null!,
            projectStore: null!,
            sandboxPolicyStore: null!,
            workflowRegistry: new WorkflowRegistry(catalog),
            generator: new StubBlueprintGenerator(raw),
            workflowGenerator,
            skillDefaults: null!,
            logger: NullLogger<BlueprintService>.Instance);
    }

    private sealed class StubBlueprintGenerator(string response) : IBlueprintGenerator
    {
        public Task<string> GenerateRawAsync(
            string description,
            CancellationToken ct,
            string? userId = null,
            string? targetRepository = null,
            string? modelId = null) =>
            Task.FromResult(response);
    }

    private sealed class StubWorkflowGenerator : IWorkflowGenerator
    {
        private readonly WorkflowGenerationResult? _result;
        private readonly Exception? _exception;

        public StubWorkflowGenerator(WorkflowGenerationResult result) => _result = result;
        public StubWorkflowGenerator(Exception exception) => _exception = exception;

        public Task<WorkflowGenerationResult> GenerateAsync(
            WorkflowGenerationRequest request,
            CancellationToken ct = default) =>
            _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<WorkflowGenerationResult>(_exception);
    }
}
