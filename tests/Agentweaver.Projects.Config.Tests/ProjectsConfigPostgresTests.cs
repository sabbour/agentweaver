using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigPostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task KeepsRunSelectionsImmutableAndEnforcesTenantScopedRunSelection()
    {
        var providerCatalog = CreateProviderCatalog();

        await using var context = CreateDbContext();
        var service = new ProjectsConfigService(context, providerCatalog, TimeProvider.System);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(context, TimeProvider.System);
        var platformAdmin = await SeedCallerAsync(
            context, authorityStore, "platform-admin", "tenant-1",
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var owner = await SeedCallerAsync(
            context, authorityStore, "owner-1", "tenant-1",
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, "tenant-1", ProjectAuthorityRole.TenantAdmin);
        var orchestratorMembership = await SeedMembershipAsync(
            authorityStore, "orchestrator-1", "tenant-1");
        var defaults = await service.GetPlatformRuntimeDefaultsAsync(platformAdmin, CancellationToken.None);
        var platformRevision = await service.UpdatePlatformRuntimeDefaultsAsync(
            platformAdmin, defaults.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await service.CreateProjectAsync(owner, "Test project", CancellationToken.None);
        await authorityStore.AssignRoleAsync(
            orchestratorMembership,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var orchestrator = await ResolveCallerAsync(
            context, "orchestrator-1", "tenant-1", ["api.read", "projects.orchestrator"]);
        var configuration = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            ProjectSettings(),
            CancellationToken.None);

        var request = RunRequest(configuration.Revision, platformRevision.Revision);
        var missingContext = await Assert.ThrowsAsync<ProjectConfigException>(() => service.AcceptRunSelectionAsync(
            orchestrator,
            project.ProjectId,
            "run-invalid-context-1",
            request with { Context = null! },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, missingContext.StatusCode);
        var nullRequirement = await Assert.ThrowsAsync<ProjectConfigException>(() => service.AcceptRunSelectionAsync(
            orchestrator,
            project.ProjectId,
            "run-invalid-requirement-1",
            request with
            {
                Context = request.Context with
                {
                    ProviderRequirements = ImmutableArray.CreateRange(new ProviderRequirement[] { null! }),
                },
            },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, nullRequirement.StatusCode);
        var irrelevantCapabilities = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.AcceptRunSelectionAsync(
                orchestrator,
                project.ProjectId,
                "run-invalid-capabilities-1",
                request with
                {
                    Context = request.Context with
                    {
                        ProviderRequirements =
                        [
                            request.Context.ProviderRequirements[0] with
                            {
                                RequiredL3L4Capabilities =
                                    ImmutableHashSet.Create(StringComparer.Ordinal, "network.egress"),
                            },
                        ],
                    },
                },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, irrelevantCapabilities.StatusCode);
        var nullEgress = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.AcceptRunSelectionAsync(
                orchestrator,
                project.ProjectId,
                "run-invalid-egress-1",
                request with
                {
                    Context = request.Context with
                    {
                        RequiredEgress = ImmutableArray.CreateRange(new ProjectEgressRule[] { null! }),
                    },
                },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, nullEgress.StatusCode);

        var selection = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", request, CancellationToken.None);
        Assert.Equal("project-model", selection.ModelSelection.Reference);
        Assert.Equal("sandbox-project", Assert.Single(Assert.Single(selection.Providers).Candidates).ProviderId);
        Assert.Equal("options-v3", Assert.Single(Assert.Single(selection.Providers).Candidates).OptionsRevision);
        Assert.Equal(6, selection.RunLimits.MaxModelTurns);
        Assert.Equal("api.example.com", Assert.Single(selection.EgressAllowlist).Host);

        var unavailableRequest = RunRequest(configuration.Revision, platformRevision.Revision) with
        {
            Context = RunRequest(configuration.Revision, platformRevision.Revision).Context with
            {
                AvailableModelSelectionReferences = ImmutableHashSet.Create(
                    StringComparer.Ordinal, "platform-model"),
            },
        };
        var unavailable = await Assert.ThrowsAsync<ProjectConfigException>(() => service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-unavailable-1", unavailableRequest, CancellationToken.None));
        Assert.Equal("model_selection_unavailable", unavailable.Code);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unavailable.StatusCode);

        var staleRevision = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings(), CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, staleRevision.StatusCode);

        var changedRequest = request with
        {
            Context = request.Context with { Revision = "provider-catalog-revision-2" },
        };
        var reusedRunId = await Assert.ThrowsAsync<ProjectConfigException>(() => service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", changedRequest, CancellationToken.None));
        Assert.Equal("run_selection_conflict", reusedRunId.Code);

        var overLimit = await Assert.ThrowsAsync<ProjectConfigException>(() => service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            configuration.Revision,
            ProjectSettings() with
            {
                RunLimits = new CopilotRunLimitOverrides { MaxModelTurns = 13 },
            },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, overLimit.StatusCode);

        var nextConfiguration = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            configuration.Revision,
            ProjectSettings() with { ModelSelection = new ModelSelectionSettings("project-model-next") },
            CancellationToken.None);
        Assert.Equal(configuration.Revision + 1, nextConfiguration.Revision);
        var replayed = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", request, CancellationToken.None);
        Assert.Equal(selection.ProjectConfigurationRevision, replayed.ProjectConfigurationRevision);
        Assert.Equal(selection.ModelSelection.Reference, replayed.ModelSelection.Reference);
        Assert.Equal("sandbox-project", Assert.Single(Assert.Single(replayed.Providers).Candidates).ProviderId);
        var stored = await service.GetRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", CancellationToken.None);
        Assert.Equal(selection.ProjectConfigurationRevision, stored.ProjectConfigurationRevision);
        Assert.Equal(selection.ModelSelection.Reference, stored.ModelSelection.Reference);

        var ownerOnly = await SeedCallerAsync(
            context, authorityStore, "different-actor", "tenant-1", ["api.read"]);
        var unauthorized = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.GetProjectAsync(ownerOnly, project.ProjectId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, unauthorized.StatusCode);
        var otherTenantAdmin = await SeedCallerAsync(
            context, authorityStore, "other-tenant-admin", "tenant-2",
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, "tenant-2", ProjectAuthorityRole.TenantAdmin);
        var crossTenant = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.GetProjectAsync(otherTenantAdmin, project.ProjectId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, crossTenant.StatusCode);

        var archived = await service.UpdateProjectAsync(
            owner,
            project.ProjectId,
            project.Revision,
            project.Name,
            ProjectLifecycleState.Archived,
            CancellationToken.None);
        var replayAfterArchive = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", request, CancellationToken.None);
        Assert.Equal(selection.ModelSelection.Reference, replayAfterArchive.ModelSelection.Reference);
        Assert.Equal(selection.ProjectConfigurationRevision, replayAfterArchive.ProjectConfigurationRevision);
        var newRunAfterArchive = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.AcceptRunSelectionAsync(
                orchestrator,
                project.ProjectId,
                "run-after-archive-1",
                request with { Context = request.Context with { Revision = "provider-catalog-revision-3" } },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, newRunAfterArchive.StatusCode);
        Assert.Equal(ProjectLifecycleState.Archived, archived.State);

        await AssertImmutableAsync(
            "UPDATE projects_config.project_configuration_revisions SET configuration = '{}'::jsonb WHERE project_id = @project_id AND revision = 1",
            ("project_id", project.ProjectId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.platform_runtime_revisions WHERE revision = @revision",
            ("revision", platformRevision.Revision));
        await AssertImmutableAsync(
            "UPDATE projects_config.project_run_selections SET snapshot = '{}'::jsonb WHERE run_id = @run_id",
            ("run_id", selection.RunId));
        await AssertImmutableAsync(
            "TRUNCATE projects_config.project_run_selections");
    }

    [Fact]
    public async Task AuthorityRevocationUsesCasAuditAndPreservesTheLastProjectOwner()
    {
        await using var context = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(context, TimeProvider.System);
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var tenantAdmin = await SeedCallerAsync(
            context,
            authorityStore,
            $"tenant-admin-{Guid.NewGuid():N}",
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(context, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Authority test project", CancellationToken.None);
        var secondOwnerMembershipId = await SeedMembershipAsync(
            authorityStore, $"second-owner-{Guid.NewGuid():N}", tenantId);
        var firstOwnerAssignment = await authorityStore.AssignRoleAsync(
            tenantAdmin.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        var secondOwnerAssignment = await authorityStore.AssignRoleAsync(
            secondOwnerMembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            "fixture");

        await using (var revokeFirstOwnerContext = CreateDbContext())
        {
            var store = new ProjectsConfigPrivilegedAuthorityStore(revokeFirstOwnerContext, TimeProvider.System);
            await store.RevokeRoleAssignmentAsync(firstOwnerAssignment.AssignmentId, 1, "fixture");
        }

        await using (var revokeLastOwnerContext = CreateDbContext())
        {
            var store = new ProjectsConfigPrivilegedAuthorityStore(revokeLastOwnerContext, TimeProvider.System);
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                store.RevokeRoleAssignmentAsync(secondOwnerAssignment.AssignmentId, 1, "fixture"));
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                store.RevokeMembershipAsync(secondOwnerMembershipId, 1, "fixture"));
        }

        await using (var staleCasContext = CreateDbContext())
        {
            var store = new ProjectsConfigPrivilegedAuthorityStore(staleCasContext, TimeProvider.System);
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                store.RevokeRoleAssignmentAsync(firstOwnerAssignment.AssignmentId, 1, "fixture"));
        }

        ProjectAuthorityAuditRecord roleRevoked;
        await using (var verifyContext = CreateDbContext())
        {
            var firstOwner = await verifyContext.RoleAssignments.AsNoTracking()
                .SingleAsync(item => item.AssignmentId == firstOwnerAssignment.AssignmentId);
            var lastOwner = await verifyContext.RoleAssignments.AsNoTracking()
                .SingleAsync(item => item.AssignmentId == secondOwnerAssignment.AssignmentId);
            Assert.Equal(ProjectAuthorityRecordState.Revoked, firstOwner.State);
            Assert.Equal(2, firstOwner.Revision);
            Assert.Equal(ProjectAuthorityRecordState.Active, lastOwner.State);
            Assert.Equal(1, lastOwner.Revision);
            Assert.Equal(ProjectAuthorityRecordState.Active,
                (await verifyContext.TenantMemberships.AsNoTracking()
                    .SingleAsync(item => item.MembershipId == secondOwnerMembershipId)).State);
            roleRevoked = await verifyContext.AuthorityAudit.AsNoTracking()
                .SingleAsync(item =>
                    item.AssignmentId == firstOwnerAssignment.AssignmentId &&
                    item.EventType == "role_revoked");
            Assert.Equal(2, roleRevoked.Revision);
        }

        await AssertImmutableAsync(
            "UPDATE projects_config.authority_audit SET actor = 'tampered' WHERE event_id = @event_id",
            ("event_id", roleRevoked.EventId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.authority_audit WHERE event_id = @event_id",
            ("event_id", roleRevoked.EventId));
    }

    private async Task AssertImmutableAsync(string sql, (string Name, object Value)? parameter = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameter is { } value)
            command.Parameters.AddWithValue(value.Name, value.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", exception.SqlState);
    }

    private const string TestIssuer = "https://projects-config.test/";

    private static async Task<ProjectAuthorizationContext> SeedCallerAsync(
        ProjectsConfigDbContext db,
        ProjectsConfigPrivilegedAuthorityStore authorityStore,
        string subject,
        string tenantId,
        string[] scopes,
        ProjectAuthorityResourceType? resourceType = null,
        string? resourceId = null,
        ProjectAuthorityRole? role = null)
    {
        var membershipId = await SeedMembershipAsync(authorityStore, subject, tenantId);
        if (resourceType is { } type && resourceId is not null && role is { } assignedRole)
            await authorityStore.AssignRoleAsync(membershipId, type, resourceId, assignedRole, "fixture");
        return await ResolveCallerAsync(db, subject, tenantId, scopes);
    }

    private static async Task<Guid> SeedMembershipAsync(
        ProjectsConfigPrivilegedAuthorityStore authorityStore,
        string subject,
        string tenantId)
    {
        var membership = await authorityStore.GrantMembershipAsync(
            TestIssuer, subject, tenantId, "fixture");
        return membership.MembershipId;
    }

    private static Task<ProjectAuthorizationContext> ResolveCallerAsync(
        ProjectsConfigDbContext db,
        string subject,
        string tenantId,
        string[] scopes)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope", string.Join(' ', scopes)),
        ], "test"));
        return new ProjectAuthorizationOwner(db, new ProjectsConfigIdentityOptions(TestIssuer))
            .ResolveAsync(principal, [tenantId], CancellationToken.None);
    }

    private ProjectsConfigDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        return new ProjectsConfigDbContext(options);
    }

    private static ProviderCatalog CreateProviderCatalog()
    {
        static ProviderRegistration Registration(string id, string revision) => new(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                id,
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true,
            revision,
            1);

        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [Registration("sandbox-platform", "options-v1"), Registration("sandbox-project", "options-v3")],
            [new ProviderSelection(ProviderSeam.Sandbox, "sandbox-platform")],
            [new ProviderOverridePermission(ProviderSeam.Sandbox, "sandbox-project")]).Value);
    }

    private static PlatformRuntimeDefaults PlatformDefaults() => new()
    {
        ModelSelection = new ModelSelectionSettings("platform-model"),
        EgressBaseline =
        [
            new ProjectEgressRule("api.example.com", 443, EgressProtocol.Tcp),
            new ProjectEgressRule("storage.example.com", 443, EgressProtocol.Tcp),
        ],
        RunLimits = new CopilotRunLimits
        {
            MaxModelTurns = 12,
            MaxToolCalls = 100,
            MaxChildren = 4,
            MaxConcurrentChildren = 2,
            MaxWallTimeSeconds = 3600,
            MaxPromptTokens = 20000,
        },
    };

    private static ProjectConfiguration ProjectSettings() => new()
    {
        ModelSelection = new ModelSelectionSettings("project-model"),
        ProviderOverrides = [new ProjectProviderOverride(ProviderSeam.Sandbox, "sandbox-project")],
        EgressNarrowing = [new ProjectEgressRule("api.example.com", 443, EgressProtocol.Tcp)],
        RunLimits = new CopilotRunLimitOverrides
        {
            MaxModelTurns = 6,
            MaxChildren = 0,
        },
    };

    private static AcceptRunSelectionRequest RunRequest(long projectRevision, long platformRevision) => new()
    {
        ExpectedProjectConfigRevision = projectRevision,
        ExpectedPlatformRuntimeRevision = platformRevision,
        Context = new RunSelectionContext
        {
            Revision = "provider-catalog-revision-1",
            AvailableModelSelectionReferences =
                ImmutableHashSet.Create(StringComparer.Ordinal, "project-model", "platform-model"),
            ProviderRequirements =
            [
                new ProviderRequirement
                {
                    Seam = ProviderSeam.Sandbox,
                    RequiredAdapterVersion = "1.0.0",
                    RequiredOptionsSchemaVersion = 1,
                    RequiredCapabilities = ImmutableHashSet.Create(StringComparer.Ordinal, "container.create"),
                },
            ],
            RequiredEgress = [new ProjectEgressRule("api.example.com", 443, EgressProtocol.Tcp)],
        },
    };
}

public sealed class ProjectsConfigPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.MigrateAsync(DataSource, options);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
