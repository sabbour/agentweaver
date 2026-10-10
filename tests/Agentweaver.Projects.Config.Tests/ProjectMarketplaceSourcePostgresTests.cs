using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectMarketplaceSourcePostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    private const string TestIssuer = "https://marketplace.test/";

    [Fact]
    public async Task Source_create_update_remove_is_revisioned_and_tombstoned()
    {
        await using var db = CreateDbContext();
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projectId = Guid.NewGuid().ToString("N");
        var (caller, tenantId) = await SeedProjectAndOwnerAsync(db, authority, projectId);
        var service = CreateService(db);

        var created = await service.CreateAsync(
            caller,
            projectId,
            new CreateMarketplaceSourceRequest
            {
                Repository = "https://github.com/Contoso/Skills.git",
                RequestedRef = "release/1.0",
                Subpath = "/skills/",
            },
            CancellationToken.None);

        Assert.Equal("skills", created.Name);
        Assert.Equal("SKILLS", created.NormalizedName);
        Assert.Equal("contoso/skills", created.Repository);
        Assert.Equal("release/1.0", created.RequestedRef);
        Assert.Equal("skills", created.Subpath);
        Assert.Equal(1, created.Revision);
        Assert.Equal(ProjectMarketplaceSourceState.Active, created.State);
        var importSource = await service.GetForImportAsync(
            caller, projectId, created.SourceId, created.Revision, CancellationToken.None);
        Assert.Equal(created.SourceId, importSource.SourceId);
        Assert.Equal(created.Revision, importSource.Revision);

        var updated = await service.UpdateAsync(
            caller,
            projectId,
            created.SourceId,
            new UpdateMarketplaceSourceRequest
            {
                ExpectedRevision = created.Revision,
                Name = "Team skills",
                Repository = "Contoso/Skills",
                RequestedRef = "main",
                Subpath = "catalog",
            },
            CancellationToken.None);

        Assert.Equal(2, updated.Revision);
        Assert.Equal("TEAM SKILLS", updated.NormalizedName);
        Assert.Equal("main", updated.RequestedRef);
        Assert.Equal("catalog", updated.Subpath);

        var staleUpdate = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.UpdateAsync(
                caller,
                projectId,
                created.SourceId,
                new UpdateMarketplaceSourceRequest
                {
                    ExpectedRevision = created.Revision,
                    Name = "Stale",
                    Repository = "contoso/skills",
                },
                CancellationToken.None));
        Assert.Equal("marketplace_source_revision_conflict", staleUpdate.Code);

        var removed = await service.RemoveAsync(
            caller,
            projectId,
            created.SourceId,
            updated.Revision,
            CancellationToken.None);
        Assert.Equal(3, removed.Revision);
        Assert.Equal(ProjectMarketplaceSourceState.Removed, removed.State);
        Assert.Empty(await service.ListAsync(caller, projectId, includeRemoved: false, CancellationToken.None));
        var retained = Assert.Single(await service.ListAsync(
            caller, projectId, includeRemoved: true, CancellationToken.None));
        Assert.Equal(created.SourceId, retained.SourceId);
        Assert.Equal(ProjectMarketplaceSourceState.Removed, retained.State);
        var removedBrowse = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service.GetForBrowseAsync(
                caller, projectId, created.SourceId, removed.Revision, CancellationToken.None));
        Assert.Equal("marketplace_source_not_found", removedBrowse.Code);
        Assert.Equal(tenantId, caller.TenantId);
    }

    [Fact]
    public async Task Source_names_are_unique_without_case_and_expected_revision_serializes_concurrent_writes()
    {
        await using var db1 = CreateDbContext();
        await using var db2 = CreateDbContext();
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projectId = Guid.NewGuid().ToString("N");
        var (caller1, tenantId) = await SeedProjectAndOwnerAsync(db1, authority, projectId);
        var subject = caller1.ActorId;
        var caller2 = await ResolveCallerAsync(db2, subject, tenantId);
        var service1 = CreateService(db1);
        var service2 = CreateService(db2);

        var source = await service1.CreateAsync(
            caller1,
            projectId,
            new CreateMarketplaceSourceRequest
            {
                Name = "Community",
                Repository = "contoso/skills",
            },
            CancellationToken.None);
        var duplicate = await Assert.ThrowsAsync<MarketplaceSourceException>(() =>
            service1.CreateAsync(
                caller1,
                projectId,
                new CreateMarketplaceSourceRequest
                {
                    Name = "community",
                    Repository = "contoso/other-skills",
                },
                CancellationToken.None));
        Assert.Equal("marketplace_source_name_conflict", duplicate.Code);

        async Task<string> UpdateAsync() =>
            await CaptureOutcomeAsync(() => service1.UpdateAsync(
                caller1,
                projectId,
                source.SourceId,
                new UpdateMarketplaceSourceRequest
                {
                    ExpectedRevision = source.Revision,
                    Name = "Updated",
                    Repository = "contoso/skills",
                },
                CancellationToken.None));

        async Task<string> RemoveAsync() =>
            await CaptureOutcomeAsync(() => service2.RemoveAsync(
                caller2,
                projectId,
                source.SourceId,
                source.Revision,
                CancellationToken.None));

        var outcomes = await Task.WhenAll(UpdateAsync(), RemoveAsync());
        Assert.Single(outcomes, outcome => outcome == "ok");
        Assert.Single(outcomes, outcome => outcome == "marketplace_source_revision_conflict");
    }

    [Fact]
    public async Task Non_owner_cannot_manage_sources_and_project_authority_is_rechecked()
    {
        await using var db = CreateDbContext();
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projectId = Guid.NewGuid().ToString("N");
        var (owner, tenantId) = await SeedProjectAndOwnerAsync(db, authority, projectId);
        var viewerSubject = "viewer-" + Guid.NewGuid().ToString("N");
        var membership = await authority.GrantMembershipAsync(TestIssuer, viewerSubject, tenantId, "fixture");
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Viewer,
            "fixture");
        var viewer = await ResolveCallerAsync(db, viewerSubject, tenantId);
        var service = CreateService(db);

        var denied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.CreateAsync(
                viewer,
                projectId,
                new CreateMarketplaceSourceRequest { Repository = "contoso/skills" },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, denied.StatusCode);

        var source = await service.CreateAsync(
            owner,
            projectId,
            new CreateMarketplaceSourceRequest { Repository = "contoso/skills" },
            CancellationToken.None);
        var importAccessDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.GetForImportAsync(
                viewer, projectId, source.SourceId, source.Revision, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, importAccessDenied.StatusCode);
        await authority.RevokeMembershipAsync(membership.MembershipId, 1, "fixture");
        var accessDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ListAsync(viewer, projectId, includeRemoved: false, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, accessDenied.StatusCode);
        Assert.NotEqual(Guid.Empty, source.SourceId);
    }

    private static async Task<string> CaptureOutcomeAsync(
        Func<Task<ProjectMarketplaceSourceRecord>> operation)
    {
        try
        {
            _ = await operation();
            return "ok";
        }
        catch (MarketplaceSourceException exception)
        {
            return exception.Code;
        }
    }

    private async Task<(ProjectAuthorizationContext Caller, string TenantId)> SeedProjectAndOwnerAsync(
        ProjectsConfigDbContext db,
        ProjectsConfigPrivilegedAuthorityStore authority,
        string projectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "tenant-" + suffix;
        var subject = "owner-" + suffix;
        var membership = await authority.GrantMembershipAsync(TestIssuer, subject, tenantId, "fixture");
        await db.Projects.AddAsync(new ProjectRecord
        {
            ProjectId = projectId,
            TenantId = tenantId,
            CreatedByActorId = subject,
            Name = "Marketplace project",
            State = ProjectLifecycleState.Active,
            Revision = 1,
            ConfigurationRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        return (await ResolveCallerAsync(db, subject, tenantId), tenantId);
    }

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        ProjectsConfigDbContext db,
        string subject,
        string tenantId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope", "api.read projects.admin"),
        ], "test"));
        return new ProjectAuthorizationOwner(db, new ProjectsConfigIdentityOptions(TestIssuer))
            .ResolveAsync(principal, [tenantId], CancellationToken.None);
    }

    private ProjectMarketplaceSourceService CreateService(ProjectsConfigDbContext db)
    {
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        return new ProjectMarketplaceSourceService(
            new MarketplaceSourceStore(db),
            projects,
            TimeProvider.System);
    }

    private ProjectsConfigDbContext CreateDbContext() =>
        new(CreateDbContextOptions());

    private DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

    private static ProviderCatalog CreateProviderCatalog()
    {
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                "marketplace-test",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true,
            "marketplace-options-v1",
            1);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, "marketplace-test")],
            []).Value);
    }
}
