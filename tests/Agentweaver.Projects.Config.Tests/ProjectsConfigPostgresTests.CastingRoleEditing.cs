using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ProposalMemberRoleEditsAreValidatedRevisionBoundAndAppliedOnlyOnConfirmation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-role-edit-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-role-edit-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting role edit", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            CastingConfiguration("agent-a", "Initial charter"),
            CancellationToken.None);
        var rejectedProposal = await service.CreateCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            CastingConfiguration("agent-a", "Proposal charter"),
            CancellationToken.None);

        var edited = await service.UpdateCastingProposalMemberRoleAsync(
            owner,
            project.ProjectId,
            rejectedProposal.ProposalId,
            "agent-a",
            rejectedProposal.DraftRevision,
            "docs-writer",
            "  Staff Writer  ",
            CancellationToken.None);
        Assert.Equal(rejectedProposal.DraftRevision + 1, edited.DraftRevision);
        Assert.Equal("docs-writer", Assert.Single(edited.Draft.Casting).Role);
        Assert.Equal("docs-writer", Assert.Single(edited.Draft.AgentCharters).Role);
        Assert.Equal("Staff Writer", Assert.Single(edited.Draft.AgentCharters).CustomRoleTitle);

        // Editing and rejecting the proposal must leave the live project configuration untouched.
        var liveBeforeReject = await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None);
        Assert.Equal(initial.Revision, liveBeforeReject.Revision);
        Assert.Equal(initial.Configuration.Casting.ToArray(), liveBeforeReject.Configuration.Casting.ToArray());
        Assert.Null(Assert.Single(liveBeforeReject.Configuration.AgentCharters).CustomRoleTitle);

        var invalidRole = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.UpdateCastingProposalMemberRoleAsync(
                owner, project.ProjectId, edited.ProposalId, "agent-a", edited.DraftRevision,
                "unknown-role", "Custom", CancellationToken.None));
        Assert.Equal("unknown_casting_role", invalidRole.Code);
        var invalidTitle = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.UpdateCastingProposalMemberRoleAsync(
                owner, project.ProjectId, edited.ProposalId, "agent-a", edited.DraftRevision,
                "docs-writer", new string('x', 121), CancellationToken.None));
        Assert.Equal("invalid_custom_role_title", invalidTitle.Code);
        var staleEdit = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.UpdateCastingProposalMemberRoleAsync(
                owner, project.ProjectId, edited.ProposalId, "agent-a", rejectedProposal.DraftRevision,
                "docs-writer", null, CancellationToken.None));
        Assert.Equal("stale_casting_proposal", staleEdit.Code);

        var rejected = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, edited.ProposalId, edited.DraftRevision, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Rejected, rejected.State);
        var liveAfterReject = await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None);
        Assert.Equal(initial.Revision, liveAfterReject.Revision);
        Assert.Null(Assert.Single(liveAfterReject.Configuration.AgentCharters).CustomRoleTitle);

        var titleOnlyConfiguration = CastingConfiguration("agent-a", "Customized charter") with
        {
            AgentCharters =
            [
                new ProjectAgentCharter("agent-a", "agent-a", "docs-writer", "Customized charter")
                {
                    CustomRoleTitle = "Existing title",
                },
            ],
            Casting = [new ProjectAgentCast("agent-a", "docs-writer", 0)],
        };
        var titleOnlyProposal = await service.CreateCastingProposalAsync(
            owner, project.ProjectId, initial.Revision, titleOnlyConfiguration, CancellationToken.None);
        var titleOnlyEdit = await service.UpdateCastingProposalMemberRoleAsync(
            owner, project.ProjectId, titleOnlyProposal.ProposalId, "agent-a", titleOnlyProposal.DraftRevision,
            "docs-writer", "Updated title", CancellationToken.None);
        var titleOnlyCharter = Assert.Single(titleOnlyEdit.Draft.AgentCharters);
        Assert.Equal("docs-writer", titleOnlyCharter.Role);
        Assert.Equal("Customized charter", titleOnlyCharter.Charter);
        Assert.Equal("Updated title", titleOnlyCharter.CustomRoleTitle);
        _ = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, titleOnlyProposal.ProposalId, titleOnlyEdit.DraftRevision, CancellationToken.None);

        var confirmProposal = await service.CreateCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            CastingConfiguration("agent-a", "Proposal charter"),
            CancellationToken.None);
        var confirmDraft = await service.UpdateCastingProposalMemberRoleAsync(
            owner,
            project.ProjectId,
            confirmProposal.ProposalId,
            "agent-a",
            confirmProposal.DraftRevision,
            "frontend-engineer",
            "Product Interface Engineer",
            CancellationToken.None);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, confirmDraft.ProposalId, confirmDraft.DraftRevision, CancellationToken.None);

        // Confirmation applies both the unchanged catalog ID and its separate display title.
        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.Equal(initial.Revision + 1, confirmed.ConfirmedConfigurationRevision);
        Assert.Equal("frontend-engineer", Assert.Single(confirmed.Result!.Casting).Role);
        Assert.Equal("Product Interface Engineer", Assert.Single(confirmed.Result.AgentCharters).CustomRoleTitle);
        await using var confirmedDb = CreateDbContext();
        var confirmedService = CreateCastingProposalService(confirmedDb);
        var liveAfterConfirm = await confirmedService.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None);
        Assert.Equal(initial.Revision + 1, liveAfterConfirm.Revision);
        Assert.Equal("frontend-engineer", Assert.Single(liveAfterConfirm.Configuration.Casting).Role);
        Assert.Equal("Product Interface Engineer", Assert.Single(liveAfterConfirm.Configuration.AgentCharters).CustomRoleTitle);
    }
}
