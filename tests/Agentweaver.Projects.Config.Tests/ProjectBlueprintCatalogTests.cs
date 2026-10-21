using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectBlueprintCatalogTests
{
    private static ProjectConfiguration Empty => ProjectConfigurationValidator.Validate(new ProjectConfiguration());

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
        var software = entries.Single(item => item.Blueprint.Id == "blueprint-software-development");
        Assert.Equal(new[] { "software-delivery", "bug-fix" }, software.Blueprint.Workflows);
        Assert.Equal("software-delivery", software.DefaultWorkflowId);
        Assert.Equal("default", software.Blueprint.ReviewPolicy);
        var content = entries.Single(item => item.Blueprint.Id == "blueprint-content-authoring");
        Assert.Equal("restricted", content.Blueprint.SandboxProfile);
        Assert.Contains(content.Blueprint.SkillBindings, binding => binding.RoleId == "writer");
    }

    [Fact]
    public void ListReportsMissingWorkflowsInsteadOfHidingBlueprints()
    {
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
        var exportability = ProjectBlueprintCatalog.Exportability(new BlueprintDefinition
        {
            Id = "Bad Id",
            Roster = ["scribe", "missing-role", "qa-engineer", "qa-engineer", "QA-Engineer"],
            Workflows = ["default", "default", "not-registered"],
        }, duplicateId: true);

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
        Assert.Equal(
            BlueprintExportability.Exportable,
            ProjectBlueprintCatalog.Exportability(Inline()).Status);
    }

    [Fact]
    public void EveryBuiltInPassesEveryValidateRuleExceptWorkflowAvailability()
    {
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
        var exportability = ProjectBlueprintCatalog.Exportability(Inline() with { ReviewPolicy = "strict" });

        Assert.Equal(BlueprintExportability.Unavailable, exportability.Status);
        Assert.Equal(new[] { "blueprint_invalid" }, exportability.Codes);
    }

    [Fact]
    public void ValidateReturnsEvery0xRuleError()
    {
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
        Assert.True(ProjectBlueprintCatalog.Validate(Inline()).Valid);
    }

    [Fact]
    public void CreateDraftRejectsAmbiguousUnknownUnavailableAndInvalidBlueprints()
    {
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

        Assert.Equal("invalid_blueprint_request", both.Code);
        Assert.Equal("invalid_blueprint_request", neither.Code);
        Assert.Equal("unknown_blueprint", unknown.Code);
        Assert.Equal("blueprint_unavailable", unavailable.Code);
        Assert.Contains("workflow_unavailable", unavailable.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_blueprint", invalid.Code);
        Assert.All(new[] { both, neither, unknown, unavailable, invalid },
            exception => Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode));
    }

    [Fact]
    public void CreateDraftCastsRosterReferencesWorkflowsAndAppliesBindingsAndSandbox()
    {
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
        var blueprint = Inline(roster: ["backend-engineer", "release-captain"]) with
        {
            SandboxProfile = "restricted",
            SkillBindings = [new BlueprintSkillBinding { RoleId = "backend-engineer", Skills = ["api-data-safety"] }],
            BespokeRoles = [new BlueprintBespokeRole { Id = "release-captain", Title = "Release Captain", Charter = "Owns releases." }],
        };

        var application = ProjectBlueprintCatalog.CreateDraft(current, null, blueprint);
        var draft = ProjectConfigurationValidator.Validate(application.Draft);

        Assert.Equal(new[] { "backend-engineer", "release-captain" }, draft.Casting.Select(item => item.AgentId));
        Assert.Equal("Owns releases.", draft.AgentCharters[1].Charter);
        Assert.Contains("Responsibilities:", draft.AgentCharters[0].Charter, StringComparison.Ordinal);
        Assert.Equal(new[] { new BlueprintWorkflowReference("custom-team", "default") }, draft.BlueprintWorkflowReferences);
        Assert.Equal("default", draft.DefaultWorkflowId);
        Assert.True(draft.Skills[0].Enabled);
        Assert.Equal(new[] { "backend-engineer" }, draft.Skills[0].AgentIds!.Value);
        Assert.Equal(current.Skills[1], draft.Skills[1]);
        Assert.Empty(application.UnresolvedSkillBindings);
        Assert.True(draft.EgressNarrowing is { IsEmpty: true });
    }

    [Fact]
    public void CreateDraftListsEverySkillBindingItCannotApply()
    {
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

        Assert.Equal(
            new[]
            {
                new BlueprintUnresolvedSkillBinding("backend-engineer", "missing", BlueprintUnresolvedSkillBinding.SkillNotInstalled),
                new BlueprintUnresolvedSkillBinding("backend-engineer", "unpinned", BlueprintUnresolvedSkillBinding.SkillNotPinned),
                new BlueprintUnresolvedSkillBinding("qa-engineer", "missing", BlueprintUnresolvedSkillBinding.SkillNotInstalled),
            },
            application.UnresolvedSkillBindings);
        Assert.Equal(current.Skills[0], application.Draft.Skills[0]);
        Assert.Equal(new[] { "qa-engineer" }, application.Draft.Skills[1].AgentIds!.Value);
        Assert.DoesNotContain(application.Draft.Skills, skill => skill.SkillId == "missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateDraftKeepsTheUserEnabledSetting(bool enabled)
    {
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

        Assert.All(application.Draft.Skills, skill => Assert.Equal(enabled, skill.Enabled));
        Assert.Equal(new[] { "backend-engineer" }, application.Draft.Skills[0].AgentIds!.Value);
        Assert.Empty(application.Draft.Skills[1].AgentIds!.Value);
        Assert.Equal(
            enabled
                ? []
                : new[] { new BlueprintUnresolvedSkillBinding("backend-engineer", "bound", BlueprintUnresolvedSkillBinding.SkillDisabled) },
            application.UnresolvedSkillBindings);
    }

    [Fact]
    public void ConfigurationAllowsSeveralWorkflowsForOneBlueprintButNotDuplicatePairs()
    {
        var several = Empty with
        {
            BlueprintWorkflowReferences =
            [
                new BlueprintWorkflowReference("blueprint-a", "software-delivery"),
                new BlueprintWorkflowReference("blueprint-a", "bug-fix"),
            ],
        };
        Assert.Equal(2, ProjectConfigurationValidator.Validate(several).BlueprintWorkflowReferences.Length);

        var duplicate = several with
        {
            BlueprintWorkflowReferences = several.BlueprintWorkflowReferences.Add(
                new BlueprintWorkflowReference("blueprint-a", "bug-fix")),
        };
        Assert.Throws<ProjectConfigException>(() => ProjectConfigurationValidator.Validate(duplicate));
    }
}
