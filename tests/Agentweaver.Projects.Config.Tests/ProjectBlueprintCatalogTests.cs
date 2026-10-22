using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectBlueprintCatalogTests
{
    private static ProjectConfiguration Empty => ProjectConfigurationValidator.Validate(new ProjectConfiguration());

    // A valid custom blueprint for tests to modify. It uses two catalog roles and the only registered workflow.
    private static BlueprintDefinition Inline(
        ImmutableArray<string>? roster = null,
        ImmutableArray<string>? workflows = null) => new()
    {
        Id = "custom-team",
        Name = "Custom team",
        Roster = roster ?? ["backend-engineer", "qa-engineer"],
        Workflows = workflows ?? ["default"],
        ReviewPolicy = "default",
        SandboxProfile = "default",
    };

    [Fact]
    public void ListPreservesThe0xBlueprintsWithRostersWorkflowsAndSkillBindings()
    {
        var entries = ProjectBlueprintCatalog.List().Blueprints;

        // All five 0.x built-ins are listed, sorted by ID.
        Assert.Equal(
            new[]
            {
                "blueprint-ai-agent-engineering",
                "blueprint-content-authoring",
                "blueprint-pm-and-software-development",
                "blueprint-product-management",
                "blueprint-software-development",
            },
            entries.Select(item => item.Blueprint.Id));
        // Spot-check two blueprints: workflows keep their 0.x order and the first one is the default...
        var software = entries.Single(item => item.Blueprint.Id == "blueprint-software-development");
        Assert.Equal(new[] { "software-delivery", "bug-fix" }, software.Blueprint.Workflows);
        Assert.Equal("software-delivery", software.DefaultWorkflowId);
        Assert.Equal("default", software.Blueprint.ReviewPolicy);
        // ...and the content blueprint keeps its restricted sandbox and its writer skill binding.
        var content = entries.Single(item => item.Blueprint.Id == "blueprint-content-authoring");
        Assert.Equal("restricted", content.Blueprint.SandboxProfile);
        Assert.Contains(content.Blueprint.SkillBindings, binding => binding.RoleId == "writer");
    }

    [Fact]
    public void ListReportsMissingWorkflowsInsteadOfHidingBlueprints()
    {
        // Every built-in uses a workflow that isn't registered yet. They stay listed but marked unavailable
        // with a "workflow_unavailable" code, never "role_missing", and codes come back in sorted order.
        foreach (var entry in ProjectBlueprintCatalog.List().Blueprints)
        {
            Assert.Equal(BlueprintExportability.Unavailable, entry.Exportability.Status);
            Assert.Contains("workflow_unavailable", entry.Exportability.Codes);
            Assert.DoesNotContain("role_missing", entry.Exportability.Codes);
            Assert.Equal(entry.Exportability.Codes.Order(StringComparer.Ordinal), entry.Exportability.Codes);
        }
    }

    [Fact]
    public void ExportabilityUsesStable0xCodes()
    {
        // Arrange: one blueprint that triggers every exportability code: an invalid ID, a reserved role
        // ("scribe"), an unknown role, a duplicate role, a role ID with capitals, a duplicate workflow, an
        // unregistered workflow, and a duplicate blueprint ID (passed in by the caller).
        var exportability = ProjectBlueprintCatalog.Exportability(new BlueprintDefinition
        {
            Id = "Bad Id",
            Roster = ["scribe", "missing-role", "qa-engineer", "qa-engineer", "QA-Engineer"],
            Workflows = ["default", "default", "not-registered"],
        }, duplicateId: true);

        // The codes are the exact 0.x strings, each once, sorted.
        Assert.Equal(
            new[]
            {
                "blueprint_duplicate_id",
                "blueprint_duplicate_role",
                "blueprint_duplicate_workflow",
                "blueprint_invalid",
                "blueprint_invalid_id",
                "role_invalid_id",
                "role_missing",
                "role_reserved",
                "workflow_unavailable",
            },
            exportability.Codes);
        // Control case: the valid inline blueprint is exportable.
        Assert.Equal(
            BlueprintExportability.Exportable,
            ProjectBlueprintCatalog.Exportability(Inline()).Status);
    }

    [Fact]
    public void EveryBuiltInPassesEveryValidateRuleExceptWorkflowAvailability()
    {
        // Skip only the workflow-availability rule, so any other problem in a built-in fails this test.
        foreach (var blueprint in ProjectBlueprintCatalog.Blueprints)
        {
            var result = ProjectBlueprintCatalog.Validate(blueprint, requireAvailableWorkflows: false);
            Assert.True(result.Valid, $"{blueprint.Id}: {string.Join(" ", result.Errors)}");
            Assert.DoesNotContain("blueprint_invalid", ProjectBlueprintCatalog.Exportability(blueprint).Codes);
        }
    }

    [Fact]
    public void ExportabilityReportsAnyValidateFailure()
    {
        // An unknown review policy has no dedicated exportability code, so it must show up as "blueprint_invalid".
        var exportability = ProjectBlueprintCatalog.Exportability(Inline() with { ReviewPolicy = "strict" });

        Assert.Equal(BlueprintExportability.Unavailable, exportability.Status);
        Assert.Equal(new[] { "blueprint_invalid" }, exportability.Codes);
    }

    [Fact]
    public void ValidateReturnsEvery0xRuleError()
    {
        // Arrange: a blueprint that breaks every 0.x validation rule at once (no ID or name, reserved, unknown
        // and duplicate roles, bad workflows and policies, bad skill bindings, and bad bespoke roles, including
        // a charter one character over the 8192 limit).
        var result = ProjectBlueprintCatalog.Validate(new BlueprintDefinition
        {
            Roster = ["coordinator", "unknown-role", "writer", "writer"],
            Workflows = ["default", "default", "unknown-workflow"],
            ReviewPolicy = "strict",
            SandboxProfile = "open",
            SkillBindings =
            [
                new BlueprintSkillBinding { RoleId = "editor", Skills = ["style"] },
                new BlueprintSkillBinding { RoleId = "writer", Skills = [] },
            ],
            BespokeRoles =
            [
                new BlueprintBespokeRole { Id = "writer", Charter = "Writes." },
                new BlueprintBespokeRole { Id = "outsider", Charter = new string('x', 8193) },
            ],
        });

        // Validation doesn't stop at the first problem: every rule reports its exact 0.x message.
        Assert.False(result.Valid);
        Assert.Contains("Blueprint id is required.", result.Errors);
        Assert.Contains("Blueprint name is required.", result.Errors);
        Assert.Contains("Unknown review_policy 'strict'. Supported: default.", result.Errors);
        Assert.Contains("Duplicate workflow 'default'.", result.Errors);
        Assert.Contains("Unknown workflow 'unknown-workflow'.", result.Errors);
        Assert.Contains("Unknown sandbox_profile 'open'. Supported: default, restricted.", result.Errors);
        Assert.Contains("Role 'coordinator' is reserved and cannot be cast.", result.Errors);
        Assert.Contains("Unknown role 'unknown-role'.", result.Errors);
        Assert.Contains("Duplicate roster role 'writer'.", result.Errors);
        Assert.Contains("Skill binding role 'editor' is not in the roster.", result.Errors);
        Assert.Contains("Skill binding for role 'writer' must list at least one skill.", result.Errors);
        Assert.Contains("Bespoke role 'writer' collides with a catalog role.", result.Errors);
        Assert.Contains("Bespoke role 'outsider' charter exceeds 8192 characters.", result.Errors);
        Assert.Contains("Bespoke role 'outsider' is not in the roster.", result.Errors);
        // Control case: the valid inline blueprint passes.
        Assert.True(ProjectBlueprintCatalog.Validate(Inline()).Valid);
    }

    [Fact]
    public void CreateDraftRejectsAmbiguousUnknownUnavailableAndInvalidBlueprints()
    {
        // Five bad requests: both a built-in ID and an inline blueprint, neither, an unknown ID, a built-in whose
        // workflows aren't registered, and an inline blueprint that casts a reserved role.
        var both = Assert.Throws<ProjectConfigException>(() =>
            ProjectBlueprintCatalog.CreateDraft(Empty, "blueprint-software-development", Inline()));
        var neither = Assert.Throws<ProjectConfigException>(() =>
            ProjectBlueprintCatalog.CreateDraft(Empty, null, null));
        var unknown = Assert.Throws<ProjectConfigException>(() =>
            ProjectBlueprintCatalog.CreateDraft(Empty, "blueprint-missing", null));
        var unavailable = Assert.Throws<ProjectConfigException>(() =>
            ProjectBlueprintCatalog.CreateDraft(Empty, "blueprint-software-development", null));
        var invalid = Assert.Throws<ProjectConfigException>(() =>
            ProjectBlueprintCatalog.CreateDraft(Empty, null, Inline(roster: ["scribe"])));

        // Each failure has its own error code; the unavailable one also names the reason code in its message.
        Assert.Equal("invalid_blueprint_request", both.Code);
        Assert.Equal("invalid_blueprint_request", neither.Code);
        Assert.Equal("unknown_blueprint", unknown.Code);
        Assert.Equal("blueprint_unavailable", unavailable.Code);
        Assert.Contains("workflow_unavailable", unavailable.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_blueprint", invalid.Code);
        // All of them are client errors (HTTP 400).
        Assert.All(new[] { both, neither, unknown, unavailable, invalid },
            exception => Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode));
    }

    [Fact]
    public void CreateDraftCastsRosterReferencesWorkflowsAndAppliesBindingsAndSandbox()
    {
        // Arrange: an existing project with a different default workflow, one pinned skill (revision and digest
        // set) that the blueprint binds, and one unrelated skill that must be left alone.
        var digest = new string('a', 64);
        var current = Empty with
        {
            DefaultWorkflowId = "previous",
            Skills =
            [
                new SkillCatalogSetting("api-data-safety", true, 0)
                {
                    Revision = 1, ContentDigest = digest, AgentIds = [],
                },
                new SkillCatalogSetting("legacy", false, 1),
            ],
        };
        // The blueprint casts one catalog role and one bespoke role, binds the pinned skill, and asks for the
        // restricted sandbox.
        var blueprint = Inline(roster: ["backend-engineer", "release-captain"]) with
        {
            SandboxProfile = "restricted",
            SkillBindings = [new BlueprintSkillBinding { RoleId = "backend-engineer", Skills = ["api-data-safety"] }],
            BespokeRoles = [new BlueprintBespokeRole { Id = "release-captain", Title = "Release Captain", Charter = "Owns releases." }],
        };

        // Act: create the draft, then run the normal project validator to prove the draft is a valid configuration.
        var application = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);
        var draft = ProjectConfigurationValidator.Validate(application.Draft);

        // Roster roles are cast in order. The bespoke role uses its charter as written; the catalog role gets
        // a generated charter with a responsibilities section.
        Assert.Equal(new[] { "backend-engineer", "release-captain" }, draft.Casting.Select(item => item.AgentId));
        Assert.Equal("Owns releases.", draft.AgentCharters[1].Charter);
        Assert.Contains("Responsibilities:", draft.AgentCharters[0].Charter, StringComparison.Ordinal);
        // The blueprint's workflow is referenced and replaces the previous default.
        Assert.Equal(new[] { new BlueprintWorkflowReference("custom-team", "default") }, draft.BlueprintWorkflowReferences);
        Assert.Equal("default", draft.DefaultWorkflowId);
        // The bound skill is now scoped to the role's agent; the unrelated skill is unchanged.
        Assert.True(draft.Skills[0].Enabled);
        Assert.Equal(new[] { "backend-engineer" }, draft.Skills[0].AgentIds!.Value);
        Assert.Equal(current.Skills[1], draft.Skills[1]);
        Assert.Empty(application.UnresolvedSkillBindings);
        // The restricted sandbox leaves no project egress rules.
        Assert.True(draft.EgressNarrowing is { IsEmpty: true });
    }

    [Fact]
    public void CreateDraftListsEverySkillBindingItCannotApply()
    {
        // Arrange: one installed skill without a pinned revision, one pinned skill, and bindings that also name
        // a skill that isn't installed at all (from two different roles).
        var digest = new string('a', 64);
        var current = Empty with
        {
            Skills =
            [
                new SkillCatalogSetting("unpinned", true, 0),
                new SkillCatalogSetting("pinned", true, 1) { Revision = 1, ContentDigest = digest, AgentIds = [] },
            ],
        };
        var blueprint = Inline(roster: ["backend-engineer", "qa-engineer"]) with
        {
            SkillBindings =
            [
                new BlueprintSkillBinding { RoleId = "qa-engineer", Skills = ["missing", "pinned"] },
                new BlueprintSkillBinding { RoleId = "backend-engineer", Skills = ["unpinned", "missing"] },
            ],
        };

        var application = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);

        // Every binding that can't be applied is reported with its reason, sorted by role and then skill.
        Assert.Equal(
            new[]
            {
                new BlueprintUnresolvedSkillBinding("backend-engineer", "missing", BlueprintUnresolvedSkillBinding.SkillNotInstalled),
                new BlueprintUnresolvedSkillBinding("backend-engineer", "unpinned", BlueprintUnresolvedSkillBinding.SkillNotPinned),
                new BlueprintUnresolvedSkillBinding("qa-engineer", "missing", BlueprintUnresolvedSkillBinding.SkillNotInstalled),
            },
            application.UnresolvedSkillBindings);
        // The unpinned skill is left as it was, the pinned one is bound, and no entry is invented for the
        // missing skill.
        Assert.Equal(current.Skills[0], application.Draft.Skills[0]);
        Assert.Equal(new[] { "qa-engineer" }, application.Draft.Skills[1].AgentIds!.Value);
        Assert.DoesNotContain(application.Draft.Skills, skill => skill.SkillId == "missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateDraftKeepsTheUserEnabledSetting(bool enabled)
    {
        // Arrange: two pinned skills with the same enabled flag. "bound" is used by the blueprint; "orphaned"
        // is bound to an agent that the new draft removes.
        var current = Empty with
        {
            Skills =
            [
                new SkillCatalogSetting("bound", enabled, 0) { Revision = 1, ContentDigest = new string('a', 64), AgentIds = [] },
                new SkillCatalogSetting("orphaned", enabled, 1) { Revision = 1, ContentDigest = new string('b', 64), AgentIds = ["old-agent"] },
            ],
        };
        var blueprint = Inline(roster: ["backend-engineer"]) with
        {
            SkillBindings = [new BlueprintSkillBinding { RoleId = "backend-engineer", Skills = ["bound"] }],
        };

        var application = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);

        // Applying a blueprint never turns a skill on or off.
        Assert.All(application.Draft.Skills, skill => Assert.Equal(enabled, skill.Enabled));
        // The bound skill moves to the new agent and the orphaned skill loses its stale agent.
        Assert.Equal(new[] { "backend-engineer" }, application.Draft.Skills[0].AgentIds!.Value);
        Assert.Empty(application.Draft.Skills[1].AgentIds!.Value);
        // A disabled bound skill is reported so the user knows the binding has no effect yet.
        Assert.Equal(
            enabled
                ? []
                : new[] { new BlueprintUnresolvedSkillBinding("backend-engineer", "bound", BlueprintUnresolvedSkillBinding.SkillDisabled) },
            application.UnresolvedSkillBindings);
    }

    [Fact]
    public void ConfigurationAllowsSeveralWorkflowsForOneBlueprintButNotDuplicatePairs()
    {
        // One blueprint may contribute several workflows...
        var several = Empty with
        {
            BlueprintWorkflowReferences =
            [
                new BlueprintWorkflowReference("blueprint-a", "software-delivery"),
                new BlueprintWorkflowReference("blueprint-a", "bug-fix"),
            ],
        };
        Assert.Equal(2, ProjectConfigurationValidator.Validate(several).BlueprintWorkflowReferences.Length);

        // ...but the same (blueprint, workflow) pair twice is rejected.
        var duplicate = several with
        {
            BlueprintWorkflowReferences = several.BlueprintWorkflowReferences.Add(
                new BlueprintWorkflowReference("blueprint-a", "bug-fix")),
        };
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(duplicate));
    }
}
