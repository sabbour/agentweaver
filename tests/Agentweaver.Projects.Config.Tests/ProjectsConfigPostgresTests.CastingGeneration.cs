using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task CastingGenerationStagesReviewableOutputAndRejectionLeavesConfigurationUnchanged()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-generation-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-generation-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting generation", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            new ProjectConfiguration
            {
                ModelSelection = new ModelSelectionSettings("preserved-model"),
                AgentCharters = [new ProjectAgentCharter("existing", "Existing", "builder", "Existing charter")],
                Casting = [new ProjectAgentCast("existing", "builder", 0)],
            },
            CancellationToken.None);

        var request = await service.StartCastingGenerationAsync(
            owner,
            project.ProjectId,
            StartRequest(initial.Revision, "formulate-" + suffix, "casting-run-" + suffix),
            CancellationToken.None);
        Assert.Equal(ProjectCastingGenerationRequestState.Pending, request.State);
        var pendingProposal = await service.GetCastingProposalAsync(
            owner, project.ProjectId, request.ProposalId, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Pending, pendingProposal.State);
        Assert.Equal(ProjectCastingGenerationRequestState.Pending, pendingProposal.Generation?.State);
        Assert.Equal(initial.Revision, pendingProposal.BaseConfigurationRevision);
        Assert.Equal(initial.Configuration.Casting.ToArray(), pendingProposal.Draft.Casting.ToArray());

        var completed = await service.CompleteCastingGenerationAsync(
            owner,
            project.ProjectId,
            request.RequestId,
            GeneratedResult(request.RunId, "generated"),
            CancellationToken.None);
        Assert.Equal(ProjectCastingGenerationRequestState.Completed, completed.State);
        var proposal = await service.GetCastingProposalAsync(
            owner, project.ProjectId, request.ProposalId, CancellationToken.None);
        Assert.Equal(2, proposal.DraftRevision);
        Assert.Equal(ProjectCastingGenerationRequestState.Completed, proposal.Generation?.State);
        Assert.Equal("preserved-model", proposal.Draft.ModelSelection?.Reference);
        Assert.Equal("generated", Assert.Single(proposal.Draft.Casting).AgentId);

        var live = await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None);
        Assert.Equal(initial.Revision, live.Revision);
        Assert.Equal(initial.Configuration.Casting.ToArray(), live.Configuration.Casting.ToArray());

        var rejected = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, request.ProposalId, proposal.DraftRevision, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Rejected, rejected.State);
        live = await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None);
        Assert.Equal(initial.Revision, live.Revision);
        Assert.Equal(initial.Configuration.Casting.ToArray(), live.Configuration.Casting.ToArray());
        Assert.Equal(1, await db.CastingGenerationRequests.AsNoTracking()
            .CountAsync(item => item.ProposalId == request.ProposalId));
    }

    [Fact]
    public async Task CastingGenerationRejectsStaleConfirmationAndKeepsTheProposalPending()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-generation-stale-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-generation-stale-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting generation stale", CancellationToken.None);
        var generation = await service.StartCastingGenerationAsync(
            owner,
            project.ProjectId,
            StartRequest(
                project.ConfigurationRevision,
                "stale-" + suffix,
                "casting-stale-run-" + suffix,
                ProjectCastingGenerationMode.Analyze),
            CancellationToken.None);
        Assert.Equal(ProjectCastingGenerationMode.Analyze, generation.Mode);
        var completed = await service.CompleteCastingGenerationAsync(
            owner,
            project.ProjectId,
            generation.RequestId,
            GeneratedResult(generation.RunId, "generated"),
            CancellationToken.None);
        var changed = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            CastingConfiguration("newer", "Current configuration"),
            CancellationToken.None);

        var stale = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                owner,
                project.ProjectId,
                generation.ProposalId,
                2,
                CancellationToken.None));
        Assert.Equal("stale_project_configuration", stale.Code);
        var proposal = await service.GetCastingProposalAsync(
            owner, project.ProjectId, generation.ProposalId, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Pending, proposal.State);
        Assert.Equal(ProjectCastingGenerationRequestState.Completed, proposal.Generation?.State);
        Assert.Equal(changed.Revision, (await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None)).Revision);
        Assert.Equal(1, completed.BaseConfigurationRevision);
    }

    [Fact]
    public async Task CastingGenerationCreationAndCompletionAreConcurrentAndIdempotent()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-generation-concurrency-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-generation-concurrency-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting generation concurrency", CancellationToken.None);
        var start = StartRequest(project.ConfigurationRevision, "concurrent-" + suffix, "casting-run-" + suffix);

        var starts = await Task.WhenAll(
            service.StartCastingGenerationAsync(owner, project.ProjectId, start, CancellationToken.None),
            service.StartCastingGenerationAsync(
                owner,
                project.ProjectId,
                start with { RunId = "retry-run-" + suffix },
                CancellationToken.None));
        Assert.Equal(starts[0].RequestId, starts[1].RequestId);
        Assert.Equal(starts[0].ProposalId, starts[1].ProposalId);
        Assert.Equal(starts[0].RunId, starts[1].RunId);

        var result = GeneratedResult(starts[0].RunId, "generated");
        var completions = await Task.WhenAll(
            service.CompleteCastingGenerationAsync(
                owner, project.ProjectId, starts[0].RequestId, result, CancellationToken.None),
            service.CompleteCastingGenerationAsync(
                owner, project.ProjectId, starts[0].RequestId, result, CancellationToken.None));
        Assert.All(completions, completion =>
            Assert.Equal(ProjectCastingGenerationRequestState.Completed, completion.State));
        Assert.Equal(1, await db.CastingGenerationRequests.AsNoTracking()
            .CountAsync(item => item.ProposalId == starts[0].ProposalId));
        Assert.Equal(1, await db.CastingProposals.AsNoTracking()
            .CountAsync(item => item.ProposalId == starts[0].ProposalId));
        Assert.Equal(2, (await service.GetCastingProposalAsync(
            owner, project.ProjectId, starts[0].ProposalId, CancellationToken.None)).DraftRevision);
        var changedOutput = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.CompleteCastingGenerationAsync(
                owner,
                project.ProjectId,
                starts[0].RequestId,
                GeneratedResult(starts[0].RunId, "different-output"),
                CancellationToken.None));
        Assert.Equal("casting_generation_conflict", changedOutput.Code);

        var conflictingReplay = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.StartCastingGenerationAsync(
                owner,
                project.ProjectId,
                start with { RequestDigest = new string('b', 64) },
                CancellationToken.None));
        Assert.Equal("casting_generation_idempotency_conflict", conflictingReplay.Code);
    }

    [Fact]
    public async Task CastingGenerationCannotConfirmBeforeCompletionAndCanBeCancelledByRejection()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-generation-cancel-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-generation-cancel-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting generation cancellation", CancellationToken.None);
        var generation = await service.StartCastingGenerationAsync(
            owner,
            project.ProjectId,
            StartRequest(project.ConfigurationRevision, "cancel-" + suffix, "casting-cancel-run-" + suffix),
            CancellationToken.None);

        var notReady = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                owner, project.ProjectId, generation.ProposalId, 1, CancellationToken.None));
        Assert.Equal("casting_generation_not_ready", notReady.Code);
        var rejected = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, generation.ProposalId, 1, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Rejected, rejected.State);
        Assert.Equal(ProjectCastingGenerationRequestState.Cancelled,
            (await service.GetCastingGenerationRequestAsync(
                owner, project.ProjectId, generation.RequestId, CancellationToken.None)).State);

        var lateCompletion = await service.CompleteCastingGenerationAsync(
            owner,
            project.ProjectId,
            generation.RequestId,
            GeneratedResult(generation.RunId, "too-late"),
            CancellationToken.None);
        Assert.Equal(ProjectCastingGenerationRequestState.Cancelled, lateCompletion.State);
        Assert.Equal(project.ConfigurationRevision, (await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None)).Revision);
    }

    private static StartProjectCastingGenerationRequest StartRequest(
        long revision,
        string idempotencyKey,
        string runId,
        ProjectCastingGenerationMode mode = ProjectCastingGenerationMode.Formulate) =>
        new()
        {
            ExpectedConfigurationRevision = revision,
            Mode = mode,
            RunId = runId,
            IdempotencyKey = idempotencyKey,
            RequestDigest = new string('a', 64),
        };

    private static CompleteProjectCastingGenerationRequest GeneratedResult(
        string runId,
        string agentId)
    {
        var configuration = CastingConfiguration(agentId, "Generated charter");
        return new()
        {
            RunId = runId,
            AgentCharters = configuration.AgentCharters,
            Casting = configuration.Casting,
        };
    }
}
