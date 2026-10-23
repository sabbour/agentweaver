using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ReRoleProposalCanDropTheLastMemberOfARoleWithAnOverride()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-rerole-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "casting-rerole-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Re-role override cleanup", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            new ProjectConfiguration
            {
                AgentCharters =
                [
                    new ProjectAgentCharter("ada", "Ada", "backend-engineer", "Build backend services."),
                    new ProjectAgentCharter("bea", "Bea", "tester", "Test the changes."),
                ],
                Casting =
                [
                    new ProjectAgentCast("ada", "backend-engineer", 0),
                    new ProjectAgentCast("bea", "tester", 1),
                ],
                ModelRoleOverrides =
                [
                    new ModelRoleSelectionOverride("backend-engineer", new ModelSelectionOverride("backend-model")),
                    new ModelRoleSelectionOverride("tester", new ModelSelectionOverride("test-model")),
                ],
                ModelAgentOverrides =
                [
                    new ModelAgentSelectionOverride("ada", new ModelSelectionOverride("ada-model")),
                ],
            },
            CancellationToken.None);

        // Changing Ada's role removes the old role from the cast, so its override must leave the reviewed draft too.
        var draft = initial.Configuration with
        {
            AgentCharters = initial.Configuration.AgentCharters
                .Select(item => item.AgentId == "ada"
                    ? item with { Role = "frontend-engineer", Charter = "Build accessible user interfaces." }
                    : item)
                .ToImmutableArray(),
            Casting = initial.Configuration.Casting
                .Select(item => item.AgentId == "ada" ? item with { Role = "frontend-engineer" } : item)
                .ToImmutableArray(),
            ModelRoleOverrides = initial.Configuration.ModelRoleOverrides
                .Where(item => item.RoleId != "backend-engineer")
                .ToImmutableArray(),
        };
        var proposal = await service.CreateCastingProposalAsync(
            owner, project.ProjectId, initial.Revision, draft, CancellationToken.None);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);

        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.DoesNotContain(confirmed.Result!.ModelRoleOverrides, item => item.RoleId == "backend-engineer");
        Assert.Contains(confirmed.Result.ModelRoleOverrides, item => item.RoleId == "tester");
        Assert.Contains(confirmed.Result.ModelAgentOverrides, item => item.AgentId == "ada");
        Assert.Contains(confirmed.Result.Casting, item => item.AgentId == "ada" && item.Role == "frontend-engineer");
    }

    [Fact]
    public async Task RemovalProposalDisablesOrRetargetsSkillsAndDropsRemovedAgentOverrides()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-removal-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "casting-removal-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Removal assignment cleanup", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            new ProjectConfiguration
            {
                AgentCharters =
                [
                    new ProjectAgentCharter("ada", "Ada", "backend-engineer", "Build backend services."),
                    new ProjectAgentCharter("bea", "Bea", "tester", "Test the changes."),
                ],
                Casting =
                [
                    new ProjectAgentCast("ada", "backend-engineer", 0),
                    new ProjectAgentCast("bea", "tester", 1),
                ],
                ModelRoleOverrides =
                [
                    new ModelRoleSelectionOverride("backend-engineer", new ModelSelectionOverride("backend-model")),
                    new ModelRoleSelectionOverride("tester", new ModelSelectionOverride("test-model")),
                ],
                ModelAgentOverrides =
                [
                    new ModelAgentSelectionOverride("ada", new ModelSelectionOverride("ada-model")),
                    new ModelAgentSelectionOverride("bea", new ModelSelectionOverride("bea-model")),
                ],
                Skills = [],
            },
            CancellationToken.None);

        // Import real pinned revisions because enabled assignments may only target content stored for this project.
        var contentService = new SkillContentService(
            db,
            new SkillContentObjectStore(new SkillTestObjectStore()),
            service,
            TimeProvider.System);
        var adaOnlyCandidate = SkillCandidate("Use Ada's project-specific skill.", "# Ada only\n");
        var adaOnlyPreview = contentService.Preview(adaOnlyCandidate);
        var adaOnly = await contentService.ImportAsync(
            owner,
            project.ProjectId,
            new SkillContentImportRequest(
                "casting-cleanup-ada-only-" + suffix,
                adaOnlyPreview.ContentDigest,
                "ada-only",
                null,
                null,
                adaOnlyCandidate),
            CancellationToken.None);
        var sharedCandidate = SkillCandidate("Use the shared project skill.", "# Shared\n");
        var sharedPreview = contentService.Preview(sharedCandidate);
        var shared = await contentService.ImportAsync(
            owner,
            project.ProjectId,
            new SkillContentImportRequest(
                "casting-cleanup-shared-" + suffix,
                sharedPreview.ContentDigest,
                "shared",
                null,
                null,
                sharedCandidate),
            CancellationToken.None);
        var withAdaSkill = await contentService.UpdateAsync(
            owner,
            project.ProjectId,
            new SkillAssignmentUpdateRequest(
                initial.Revision, adaOnly.SkillId, adaOnly.Revision, adaOnly.ContentDigest, true, 0, ["ada"]),
            CancellationToken.None);
        var configuredVersion = await contentService.UpdateAsync(
            owner,
            project.ProjectId,
            new SkillAssignmentUpdateRequest(
                withAdaSkill.Revision, shared.SkillId, shared.Revision, shared.ContentDigest, true, 1, ["ada", "bea"]),
            CancellationToken.None);
        var configured = configuredVersion.Configuration;

        // Removing Ada must disable a skill with no remaining assignees instead of turning it into an invalid empty assignment.
        // Remove overrides that would otherwise point at Ada or the role no longer present in the cast.
        var draft = configured with
        {
            AgentCharters = configured.AgentCharters
                .Where(item => item.AgentId != "ada")
                .ToImmutableArray(),
            Casting = configured.Casting
                .Where(item => item.AgentId != "ada")
                .ToImmutableArray(),
            ModelRoleOverrides = configured.ModelRoleOverrides
                .Where(item => item.RoleId != "backend-engineer")
                .ToImmutableArray(),
            ModelAgentOverrides = configured.ModelAgentOverrides
                .Where(item => item.AgentId != "ada")
                .ToImmutableArray(),
            Skills = configured.Skills
                .Select(item => item.AgentIds is { } agentIds && agentIds.Contains("ada")
                    ? item with
                    {
                        Enabled = agentIds.Length > 1 && item.Enabled,
                        AgentIds = agentIds.Where(agentId => agentId != "ada").ToImmutableArray(),
                    }
                    : item)
                .ToImmutableArray(),
        };
        // Both skill assignments appended configuration revisions, so review against the newest revision.
        var proposal = await service.CreateCastingProposalAsync(
            owner, project.ProjectId, configuredVersion.Revision, draft, CancellationToken.None);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        var disabledSkill = Assert.Single(confirmed.Result!.Skills, item => item.SkillId == "ada-only");
        var sharedSkill = Assert.Single(confirmed.Result.Skills, item => item.SkillId == "shared");

        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.False(disabledSkill.Enabled);
        Assert.Empty(disabledSkill.AgentIds!.Value);
        Assert.True(sharedSkill.Enabled);
        Assert.Equal(["bea"], sharedSkill.AgentIds!.Value.ToArray());
        Assert.DoesNotContain(confirmed.Result.ModelRoleOverrides, item => item.RoleId == "backend-engineer");
        Assert.Contains(confirmed.Result.ModelRoleOverrides, item => item.RoleId == "tester");
        Assert.DoesNotContain(confirmed.Result.ModelAgentOverrides, item => item.AgentId == "ada");
        Assert.Contains(confirmed.Result.ModelAgentOverrides, item => item.AgentId == "bea");
    }
}
