using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigPostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task ReplaysLegacyNonCostFingerprintAndSnapshotWithoutMeterSource()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "legacy-tenant-" + suffix;
        var admin = await SeedCallerAsync(db, store, "legacy-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var owner = await SeedCallerAsync(db, store, "legacy-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        var platform = await service.UpdatePlatformRuntimeDefaultsAsync(
            admin, head.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await service.CreateProjectAsync(owner, "Legacy selection", CancellationToken.None);
        var configuration = await service.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, project.ConfigurationRevision, ProjectSettings(), CancellationToken.None);
        var membership = await SeedMembershipAsync(store, "legacy-orchestrator-" + suffix, tenantId);
        await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorityRole.Orchestrator, "fixture");
        var caller = await ResolveCallerAsync(
            db, "legacy-orchestrator-" + suffix, tenantId, ["api.read", "projects.orchestrator"]);
        var request = RunRequest(configuration.Revision, platform.Revision);
        request = request with
        {
            Context = request.Context with
            {
                RequiredEgress =
                [
                    new NetworkEgressRule(
                        NetworkEgressPurpose.PublicHttps,
                        NetworkEgressDestinationKind.Fqdn,
                        "api.example.com",
                        443,
                        EgressProtocol.Tcp),
                ],
            },
        };
        var runId = "legacy-run-" + suffix;
        var legacyBytes = LegacyFingerprintBytes(project.ProjectId, runId, request);
        var expectedCanonical = $$"""
            {"projectId":"{{project.ProjectId}}","runId":"{{runId}}","expectedProjectConfigRevision":{{configuration.Revision}},"expectedPlatformRuntimeRevision":{{platform.Revision}},"contextRevision":"provider-catalog-revision-1","modelReferences":["platform-model","project-model"],"providerRequirements":[{"seam":"sandbox","requiredAdapterVersion":"1.0.0","requiredOptionsSchemaVersion":1,"requiredCapabilities":["container.create"],"requiredL3L4Capabilities":[],"requiredL7Capabilities":[]},{"seam":"networkPolicy","requiredAdapterVersion":"1.0.0","requiredOptionsSchemaVersion":1,"requiredCapabilities":[],"requiredL3L4Capabilities":["networkpolicy.cidr","networkpolicy.dns","networkpolicy.fqdn","networkpolicy.l3l4"],"requiredL7Capabilities":[]}],"requiredEgress":[{"host":"api.example.com","port":443,"protocol":"tcp"}]}
            """;
        Assert.Equal(Encoding.UTF8.GetBytes(expectedCanonical), legacyBytes);
        var legacyFingerprint = Convert.ToHexString(SHA256.HashData(legacyBytes)).ToLowerInvariant();
        var legacySnapshot = JsonSerializer.Serialize(new
        {
            project.ProjectId,
            RunId = runId,
            ProjectRevision = project.Revision,
            ProjectConfigurationRevision = configuration.Revision,
            PlatformRuntimeRevision = platform.Revision,
            ContextRevision = request.Context.Revision,
            ModelSelection = new ModelSelectionSettings("project-model"),
            Providers = new[]
            {
                new
                {
                    Cardinality = ProviderCardinality.Exclusive,
                    Seam = ProviderSeam.Sandbox,
                    Candidates = new[]
                    {
                        new EffectiveProviderCandidate(
                            ProviderSeam.Sandbox, "sandbox-project", "1.0.0", 1, "options-v3",
                            ProviderHostingPattern.KubernetesController,
                            ["container.create"], ["container.create"]),
                    },
                },
            },
            EgressAllowlist = new[]
            {
                new { Host = "api.example.com", Port = 443, Protocol = EgressProtocol.Tcp.ToString() },
            },
            RunLimits = new CopilotRunLimits
            {
                MaxModelTurns = 6, MaxToolCalls = 100, MaxChildren = 0, MaxConcurrentChildren = 0,
                MaxWallTimeSeconds = 3600, MaxPromptTokens = 20000,
            },
            ProjectConfiguration = LegacyProjectConfiguration(configuration.Configuration),
        }, LegacyJsonOptions());
        Assert.DoesNotContain("\"meterSource\"", legacySnapshot);
        db.RunSelections.Add(new ProjectRunSelectionRecord
        {
            RunId = runId,
            ProjectId = project.ProjectId,
            ProjectRevision = project.Revision,
            ProjectConfigurationRevision = configuration.Revision,
            PlatformRuntimeRevision = platform.Revision,
            ContextRevision = request.Context.Revision,
            RequestFingerprint = legacyFingerprint,
            SnapshotJson = legacySnapshot,
            CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
        var storedBefore = await db.RunSelections.AsNoTracking().SingleAsync(item => item.RunId == runId);

        var replay = await service.AcceptRunSelectionAsync(
            caller, project.ProjectId, runId, request, CancellationToken.None);
        var read = await service.GetRunSelectionAsync(
            caller, project.ProjectId, runId, CancellationToken.None);
        Assert.Equal("project-model", replay.ModelSelection.Reference);
        Assert.Equal("options-v3", Assert.Single(Assert.Single(read.Providers).Candidates).OptionsRevision);
        Assert.Null(Assert.Single(replay.Providers).MeterSource);
        Assert.Null(Assert.Single(read.Providers).MeterSource);
        var storedAfter = await db.RunSelections.AsNoTracking().SingleAsync(item => item.RunId == runId);
        Assert.Equal(legacyFingerprint, storedAfter.RequestFingerprint);
        Assert.Equal(storedBefore.SnapshotJson, storedAfter.SnapshotJson);
        Assert.Equal(storedBefore.CreatedAt, storedAfter.CreatedAt);

        var freshRunId = "fresh-structured-format-" + suffix;
        await service.AcceptRunSelectionAsync(
            caller,
            project.ProjectId,
            freshRunId,
            RunRequest(configuration.Revision, platform.Revision),
            CancellationToken.None);
        var fresh = await db.RunSelections.AsNoTracking().SingleAsync(item => item.RunId == freshRunId);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                StructuredFingerprintBytes(
                    project.ProjectId,
                    freshRunId,
                    RunRequest(configuration.Revision, platform.Revision)))).ToLowerInvariant(),
            fresh.RequestFingerprint);
    }

    [Fact]
    public async Task PersistsMeterKeyedCostCandidatesAndRejectsMissingDuplicateOrUnknownSources()
    {
        var catalog = ProviderCatalogConfiguration.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Seam"] = "Cost",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Id"] = "cost-test",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdapterVersion"] = "1.0.0",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsSchemaVersion"] = "1",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Hosting"] = "InProcess",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:AdvertisedCapabilities:0"] = "cost.usage.price",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:Enabled"] = "true",
                ["ProjectsConfig:ProviderCatalog:Registrations:0:OptionsRevision"] = "cost-options-v1",
                ["ProjectsConfig:ProviderCatalog:MeterSourceSelections:0:MeterSource"] = "copilot.nano_aiu",
                ["ProjectsConfig:ProviderCatalog:MeterSourceSelections:0:ProviderId"] = "cost-test",
                ["ProjectsConfig:ProviderCatalog:MeterSourceSelections:1:MeterSource"] = "hosted.tokens",
                ["ProjectsConfig:ProviderCatalog:MeterSourceSelections:1:ProviderId"] = "cost-test",
            }).Build());
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = new ProjectsConfigService(db, catalog, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var admin = await SeedCallerAsync(db, store, "cost-admin-" + suffix, "tenant-cost-" + suffix,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var owner = await SeedCallerAsync(db, store, "cost-owner-" + suffix, "tenant-cost-" + suffix,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            "tenant-cost-" + suffix, ProjectAuthorityRole.TenantAdmin);
        var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        var defaults = await service.UpdatePlatformRuntimeDefaultsAsync(
            admin, head.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await service.CreateProjectAsync(owner, "Cost selection", CancellationToken.None);
        var membership = await SeedMembershipAsync(store, "cost-orchestrator-" + suffix, owner.TenantId);
        await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorityRole.Orchestrator, "fixture");
        var caller = await ResolveCallerAsync(
            db, "cost-orchestrator-" + suffix, owner.TenantId, ["api.read", "projects.orchestrator"]);
        var requirement = new ProviderRequirement
        {
            Seam = ProviderSeam.Cost,
            MeterSource = "copilot.nano_aiu",
            RequiredAdapterVersion = "1.0.0",
            RequiredOptionsSchemaVersion = 1,
            RequiredCapabilities = ImmutableHashSet.Create("cost.usage.price"),
        };
        var request = new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = project.ConfigurationRevision,
            ExpectedPlatformRuntimeRevision = defaults.Revision,
            Context = new RunSelectionContext
            {
                Revision = "cost-context-v1",
                AvailableModelSelectionReferences = ImmutableHashSet.Create("platform-model"),
                ProviderRequirements = [requirement, requirement with { MeterSource = "hosted.tokens" }],
            },
        };
        var runId = "cost-run-" + suffix;
        var selected = await service.AcceptRunSelectionAsync(
            caller, project.ProjectId, runId, request, CancellationToken.None);
        Assert.Equal(2, selected.Providers.Length);
        var cost = selected.Providers[0];
        Assert.Equal(ProviderCardinality.KeyedByMeterSource, cost.Cardinality);
        Assert.Equal("copilot.nano_aiu", cost.MeterSource);
        Assert.Equal("cost-options-v1", Assert.Single(cost.Candidates).OptionsRevision);
        Assert.Equal("hosted.tokens", selected.Providers[1].MeterSource);
        var replay = await service.AcceptRunSelectionAsync(
            caller, project.ProjectId, runId, request, CancellationToken.None);
        Assert.Equal(cost.MeterSource, replay.Providers[0].MeterSource);
        var read = await service.GetRunSelectionAsync(
            caller, project.ProjectId, runId, CancellationToken.None);
        Assert.Equal("hosted.tokens", read.Providers[1].MeterSource);
        var ownerRead = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.GetRunSelectionAsync(owner, project.ProjectId, runId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, ownerRead.StatusCode);

        foreach (var requirements in new ImmutableArray<ProviderRequirement>[]
        {
            [requirement with { MeterSource = null }],
            [requirement, requirement],
            [requirement with { Seam = ProviderSeam.Sandbox }],
        })
        {
            var invalid = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.AcceptRunSelectionAsync(caller, project.ProjectId, "invalid-" + Guid.NewGuid().ToString("N"),
                    request with { Context = request.Context with { ProviderRequirements = requirements } },
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        }
        var unknown = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.AcceptRunSelectionAsync(caller, project.ProjectId, "unknown-" + suffix,
                request with { Context = request.Context with
                {
                    ProviderRequirements = [requirement with { MeterSource = "unknown.meter" }],
                } }, CancellationToken.None));
        Assert.Equal("provider_resolution_failed", unknown.Code);
    }

    [Fact]
    public async Task KeepsRunSelectionsImmutableAndEnforcesTenantScopedRunSelection()
    {
        var providerCatalog = CreateProviderCatalog();

        await using var context = CreateDbContext();
        var service = new ProjectsConfigService(context, providerCatalog, TimeProvider.System);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
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
                        RequiredEgress = ImmutableArray.CreateRange(new NetworkEgressRule[] { null! }),
                    },
                },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, nullEgress.StatusCode);

        var selection = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, "run-immutable-1", request, CancellationToken.None);
        Assert.Equal("project-model", selection.ModelSelection.Reference);
        Assert.Equal(ModelSourceMode.Byok, selection.ModelSelection.SourceMode);
        var sandboxSelection = Assert.Single(selection.Providers.Where(provider =>
            provider.Seam == ProviderSeam.Sandbox));
        Assert.Equal("sandbox-project", Assert.Single(sandboxSelection.Candidates).ProviderId);
        Assert.Equal("options-v3", Assert.Single(sandboxSelection.Candidates).OptionsRevision);
        Assert.Equal(6, selection.RunLimits.MaxModelTurns);
        Assert.Equal("api.example.com", Assert.Single(selection.EgressAllowlist).Destination);
        Assert.Equal(2, selection.EgressBaseline.Length);
        Assert.Equal(NetworkEgressPurpose.ModelEndpoint, Assert.Single(selection.RequiredEgress).Purpose);
        var networkSelection = Assert.Single(selection.Providers.Where(provider =>
            provider.Seam == ProviderSeam.NetworkPolicy));
        Assert.Equal(NetworkPolicyLayer.L3L4, Assert.Single(networkSelection.Candidates).Layer);
        Assert.Equal("cilium", Assert.Single(networkSelection.Candidates).ProviderId);

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
        Assert.Equal("sandbox-project", Assert.Single(
            Assert.Single(replayed.Providers.Where(provider => provider.Seam == ProviderSeam.Sandbox))
                .Candidates).ProviderId);
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

        Assert.True(orchestrator.HasRole(
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator));
        await authorityStore.RevokeMembershipAsync(orchestrator.MembershipId, 1, "fixture");
        var staleAuthorization = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.GetRunSelectionAsync(
                orchestrator, project.ProjectId, "run-immutable-1", CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, staleAuthorization.StatusCode);
    }

    [Fact]
    public async Task PersistsSourceControlSecretReferencesInAcceptedSnapshotAcrossConfigChangesAndRestart()
    {
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.RepositoryCheckout,
            SourceControlCapabilities.IssueWrite,
            SourceControlCapabilities.PullRequestRead,
            SourceControlCapabilities.PullRequestWrite,
            SourceControlCapabilities.ReviewRead,
            SourceControlCapabilities.Merge);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(
                        ProviderSeam.SourceControl,
                        SourceControlProviderIds.GitHub,
                        new Version(1, 0, 0),
                        1,
                        ProviderHostingPattern.InProcess,
                        capabilities),
                    true,
                    "github-options-v1",
                    1)
            ],
            [new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []).Value);

        await using var context = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = new ProjectsConfigService(context, catalog, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "tenant-source-control-" + suffix;
        var admin = await SeedCallerAsync(
            context, authorityStore, "source-control-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var owner = await SeedCallerAsync(
            context, authorityStore, "source-control-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var defaultsHead = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        var defaults = await service.UpdatePlatformRuntimeDefaultsAsync(
            admin, defaultsHead.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await service.CreateProjectAsync(owner, "SourceControl selection", CancellationToken.None);
        var originalSettings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "repo"),
            new SecretRef("github-api", "api-v1"),
            new SecretRef("github-checkout", "checkout-v1"),
            new SecretRef("github-webhook", "webhook-v1"));
        var configuration = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            ProjectSettings() with { SourceControl = originalSettings },
            CancellationToken.None);
        var membership = await SeedMembershipAsync(
            authorityStore, "source-control-orchestrator-" + suffix, tenantId);
        await authorityStore.AssignRoleAsync(
            membership,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var caller = await ResolveCallerAsync(
            context, "source-control-orchestrator-" + suffix, tenantId,
            ["api.read", "projects.orchestrator"]);
        var sourceRequirement = new ProviderRequirement
        {
            Seam = ProviderSeam.SourceControl,
            RequiredAdapterVersion = "1.0.0",
            RequiredOptionsSchemaVersion = 1,
            RequiredCapabilities = ImmutableHashSet.Create(
                StringComparer.Ordinal, SourceControlCapabilities.RepositoryRead),
        };
        var request = new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = configuration.Revision,
            ExpectedPlatformRuntimeRevision = defaults.Revision,
            Context = new RunSelectionContext
            {
                Revision = "source-control-context-v1",
                AvailableModelSelectionReferences =
                    ImmutableHashSet.Create(StringComparer.Ordinal, "project-model", "platform-model"),
                ProviderRequirements = [sourceRequirement],
            },
        };
        var runId = "source-control-run-" + suffix;
        var selected = await service.AcceptRunSelectionAsync(
            caller, project.ProjectId, runId, request, CancellationToken.None);

        Assert.Equal(originalSettings.Repository, selected.ProjectConfiguration.SourceControl!.Repository);
        Assert.Equal("api-v1", selected.ProjectConfiguration.SourceControl.ApiSecretReference!.Version);
        Assert.Equal("checkout-v1", selected.ProjectConfiguration.SourceControl.CheckoutSecretReference!.Version);
        Assert.Equal("webhook-v1", selected.ProjectConfiguration.SourceControl.WebhookSecretReference!.Version);
        Assert.Equal(SourceControlProviderIds.GitHub,
            Assert.Single(Assert.Single(selected.Providers).Candidates).ProviderId);
        var originalRecord = await context.RunSelections.AsNoTracking().SingleAsync(
            item => item.ProjectId == project.ProjectId && item.RunId == runId);

        var duplicateRequirement = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.AcceptRunSelectionAsync(
                caller,
                project.ProjectId,
                "source-control-duplicate-" + suffix,
                request with
                {
                    Context = request.Context with
                    {
                        ProviderRequirements = [sourceRequirement, sourceRequirement],
                    },
                },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, duplicateRequirement.StatusCode);

        var changedSettings = new SourceControlProjectSettings(
            new SourceControlRepositoryIdentity("octo", "different"),
            new SecretRef("github-api", "api-v2"),
            new SecretRef("github-checkout", "checkout-v2"));
        var changedConfiguration = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            configuration.Revision,
            configuration.Configuration with { SourceControl = changedSettings },
            CancellationToken.None);
        Assert.Equal(configuration.Revision + 1, changedConfiguration.Revision);

        await using var restartedContext = CreateDbContext();
        var restartedService = new ProjectsConfigService(restartedContext, catalog, TimeProvider.System);
        var replayed = await restartedService.GetRunSelectionAsync(
            caller, project.ProjectId, runId, CancellationToken.None);
        Assert.Equal(configuration.Revision, replayed.ProjectConfigurationRevision);
        Assert.Equal(originalSettings.Repository, replayed.ProjectConfiguration.SourceControl!.Repository);
        Assert.Equal("api-v1", replayed.ProjectConfiguration.SourceControl.ApiSecretReference!.Version);
        Assert.Equal("checkout-v1", replayed.ProjectConfiguration.SourceControl.CheckoutSecretReference!.Version);
        Assert.Equal("webhook-v1", replayed.ProjectConfiguration.SourceControl.WebhookSecretReference!.Version);
        var persisted = await restartedContext.RunSelections.AsNoTracking().SingleAsync(
            item => item.ProjectId == project.ProjectId && item.RunId == runId);
        Assert.Equal(originalRecord.SnapshotJson, persisted.SnapshotJson);
    }

    [Fact]
    public async Task AuthorityRevocationUsesCasAuditAndPreservesTheLastProjectOwner()
    {
        await using var context = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
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
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
            await store.RevokeRoleAssignmentAsync(firstOwnerAssignment.AssignmentId, 1, "fixture");
        }

        await using (var revokeLastOwnerContext = CreateDbContext())
        {
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                store.RevokeRoleAssignmentAsync(secondOwnerAssignment.AssignmentId, 1, "fixture"));
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                store.RevokeMembershipAsync(secondOwnerMembershipId, 1, "fixture"));
        }

        await using (var staleCasContext = CreateDbContext())
        {
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
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

    [Fact]
    public async Task AuthorityStoreRetriesSerializationFailuresWithFreshContexts()
    {
        var options = CreateDbContextOptions();
        await using var context = new ProjectsConfigDbContext(options);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
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
            .CreateProjectAsync(tenantAdmin, "Serialization retry project", CancellationToken.None);
        var member = await authorityStore.GrantMembershipAsync(
            TestIssuer,
            $"contributor-{Guid.NewGuid():N}",
            tenantId,
            "fixture");

        ProjectRoleAssignmentRecord assignment = null!;
        await WithFirstSerializationFailureAsync(
            RetryTriggerTarget.AssignmentInsert,
            () => AssignContributorAsync());
        async Task AssignContributorAsync() =>
            assignment = await authorityStore.AssignRoleAsync(
                member.MembershipId,
                ProjectAuthorityResourceType.Project,
                project.ProjectId,
                ProjectAuthorityRole.Contributor,
                "fixture");

        await using (var verifyAssignmentContext = CreateDbContext())
        {
            Assert.Equal(1, await verifyAssignmentContext.RoleAssignments.AsNoTracking()
                .CountAsync(item => item.MembershipId == member.MembershipId));
            Assert.Equal(1, await verifyAssignmentContext.AuthorityAudit.AsNoTracking()
                .CountAsync(item =>
                    item.AssignmentId == assignment.AssignmentId &&
                    item.EventType == "role_assigned" &&
                    item.Revision == 1));
        }

        await WithFirstSerializationFailureAsync(
            RetryTriggerTarget.AssignmentUpdate,
            () => authorityStore.RevokeRoleAssignmentAsync(
                assignment.AssignmentId, 1, "fixture"));
        await using (var verifyRoleRevocationContext = CreateDbContext())
        {
            var stored = await verifyRoleRevocationContext.RoleAssignments.AsNoTracking()
                .SingleAsync(item => item.AssignmentId == assignment.AssignmentId);
            Assert.Equal(ProjectAuthorityRecordState.Revoked, stored.State);
            Assert.Equal(2, stored.Revision);
            Assert.Equal(1, await verifyRoleRevocationContext.AuthorityAudit.AsNoTracking()
                .CountAsync(item =>
                    item.AssignmentId == assignment.AssignmentId &&
                    item.EventType == "role_revoked" &&
                    item.Revision == 2));
        }

        var membershipToRevoke = await authorityStore.GrantMembershipAsync(
            TestIssuer,
            $"membership-revoke-{Guid.NewGuid():N}",
            tenantId,
            "fixture");
        await WithFirstSerializationFailureAsync(
            RetryTriggerTarget.MembershipUpdate,
            () => authorityStore.RevokeMembershipAsync(
                membershipToRevoke.MembershipId, 1, "fixture"));
        await using (var verifyMembershipRevocationContext = CreateDbContext())
        {
            var stored = await verifyMembershipRevocationContext.TenantMemberships.AsNoTracking()
                .SingleAsync(item => item.MembershipId == membershipToRevoke.MembershipId);
            Assert.Equal(ProjectAuthorityRecordState.Revoked, stored.State);
            Assert.Equal(2, stored.Revision);
            Assert.Equal(1, await verifyMembershipRevocationContext.AuthorityAudit.AsNoTracking()
                .CountAsync(item =>
                    item.MembershipId == membershipToRevoke.MembershipId &&
                    item.EventType == "membership_revoked" &&
                    item.Revision == 2));
        }
    }

    [Fact]
    public async Task CastingProposalsRequireCurrentRevisionsAndRejectWithoutChangingProjectConfiguration()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "casting-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Casting lifecycle", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            CastingConfiguration("agent-a", "Initial charter"),
            CancellationToken.None);

        var rejected = await service.CreateCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            CastingConfiguration("agent-a", "Rejected draft"),
            CancellationToken.None);
        var amended = await service.UpdateCastingProposalAsync(
            owner,
            project.ProjectId,
            rejected.ProposalId,
            rejected.DraftRevision,
            CastingConfiguration("agent-a", "Amended draft"),
            CancellationToken.None);
        Assert.Equal(2, amended.DraftRevision);

        var staleDraft = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                owner, project.ProjectId, amended.ProposalId, rejected.DraftRevision, CancellationToken.None));
        Assert.Equal("stale_casting_proposal", staleDraft.Code);

        var historyBeforeReject = await db.ProjectConfigurationRevisions.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId);
        var runsBeforeReject = await db.RunSelections.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId);
        var rejection = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, amended.ProposalId, amended.DraftRevision, CancellationToken.None);
        var rejectionReplay = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, amended.ProposalId, amended.DraftRevision, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Rejected, rejection.State);
        Assert.Equal(rejection.State, rejectionReplay.State);
        Assert.Equal(historyBeforeReject, await db.ProjectConfigurationRevisions.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId));
        Assert.Equal(runsBeforeReject, await db.RunSelections.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId));

        var rejectedConfirm = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                owner, project.ProjectId, amended.ProposalId, amended.DraftRevision, CancellationToken.None));
        Assert.Equal("casting_proposal_conflict", rejectedConfirm.Code);

        var pending = await service.CreateCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            CastingConfiguration("agent-b", "Confirmed charter"),
            CancellationToken.None);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, pending.ProposalId, pending.DraftRevision, CancellationToken.None);
        var confirmReplay = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, pending.ProposalId, pending.DraftRevision, CancellationToken.None);
        var confirmedRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirming the proposal did not return its configuration revision.");
        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.Equal(initial.Revision + 1, confirmedRevision);
        Assert.Equal(confirmedRevision, confirmReplay.ConfirmedConfigurationRevision);
        Assert.Equal(confirmed.Result?.Casting.ToArray(), confirmReplay.Result?.Casting.ToArray());
        Assert.Equal(historyBeforeReject + 1, await db.ProjectConfigurationRevisions.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId));

        var staleBase = await service.CreateCastingProposalAsync(
            owner,
            project.ProjectId,
            confirmedRevision,
            CastingConfiguration("agent-c", "Stale base"),
            CancellationToken.None);
        await using var updateDb = CreateDbContext();
        var updateService = CreateCastingProposalService(updateDb);
        await updateService.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            confirmedRevision,
            CastingConfiguration("agent-d", "Current configuration"),
            CancellationToken.None);
        var staleConfiguration = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                owner, project.ProjectId, staleBase.ProposalId, staleBase.DraftRevision, CancellationToken.None));
        Assert.Equal("stale_project_configuration", staleConfiguration.Code);
        Assert.Equal(ProjectCastingProposalState.Pending,
            (await service.GetCastingProposalAsync(owner, project.ProjectId, staleBase.ProposalId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ScenarioCastingProposalBuildsCatalogRosterPreservesUnrelatedSettingsAndConfirms()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "scenario-casting-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "scenario-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Scenario casting", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            new ProjectConfiguration
            {
                AgentCharters = [new ProjectAgentCharter("existing-agent", "Existing", "builder", "Existing charter")],
                Casting = [new ProjectAgentCast("existing-agent", "builder", 0)],
                DefaultWorkflowId = "baseline-workflow",
                Skills = [new SkillCatalogSetting("existing-skill", true, 0)],
            },
            CancellationToken.None);
        var revisionsBeforeProposal = await db.ProjectConfigurationRevisions.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId);

        var proposal = await service.CreateScenarioCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            "quick-software-development",
            CancellationToken.None);

        Assert.Equal(ProjectCastingProposalState.Pending, proposal.State);
        Assert.Equal(initial.Revision, proposal.BaseConfigurationRevision);
        Assert.Equal(
            new[] { "frontend-engineer", "backend-engineer", "security-engineer", "devops-engineer", "qa-engineer" },
            proposal.Draft.Casting.Select(item => item.AgentId));
        Assert.Equal("baseline-workflow", proposal.Draft.DefaultWorkflowId);
        Assert.Equal(initial.Configuration.Skills.ToArray(), proposal.Draft.Skills.ToArray());
        Assert.Contains("Responsibilities:", proposal.Draft.AgentCharters[1].Charter, StringComparison.Ordinal);
        Assert.Equal(revisionsBeforeProposal, await db.ProjectConfigurationRevisions.AsNoTracking()
            .CountAsync(item => item.ProjectId == project.ProjectId));

        var listed = await service.ListCastingProposalsAsync(
            owner, project.ProjectId, 10, CancellationToken.None);
        var reviewed = await service.GetCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, CancellationToken.None);
        Assert.Contains(listed, item => item.ProposalId == proposal.ProposalId);
        Assert.Equal(proposal.Draft.AgentCharters.ToArray(), reviewed.Draft.AgentCharters.ToArray());
        Assert.Equal(proposal.Draft.Casting.ToArray(), reviewed.Draft.Casting.ToArray());
        Assert.Equal(proposal.Draft.Skills.ToArray(), reviewed.Draft.Skills.ToArray());

        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.Equal("baseline-workflow", confirmed.Result?.DefaultWorkflowId);
        Assert.Equal(initial.Configuration.Skills.ToArray(), confirmed.Result?.Skills.ToArray());
    }

    [Fact]
    public async Task ManualCastingProposalRejectsInvalidRolesAndRejectLeavesConfigurationUnchanged()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "manual-casting-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "manual-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Manual casting", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner,
            project.ProjectId,
            project.ConfigurationRevision,
            CastingConfiguration("existing-agent", "Existing charter"),
            CancellationToken.None);

        foreach (var invalidRoles in new[]
        {
            ImmutableArray<string>.Empty,
            ImmutableArray.Create("writer", "writer"),
            ImmutableArray.Create("writer", "missing-role"),
            ImmutableArray.Create("writer", "coordinator"),
        })
        {
            var invalid = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.CreateManualCastingProposalAsync(
                    owner, project.ProjectId, initial.Revision, invalidRoles, CancellationToken.None));
            Assert.Equal(400, invalid.StatusCode);
        }

        var proposal = await service.CreateManualCastingProposalAsync(
            owner,
            project.ProjectId,
            initial.Revision,
            ["writer", "editor"],
            CancellationToken.None);
        Assert.Equal(new[] { "writer", "editor" }, proposal.Draft.Casting.Select(item => item.Role));
        Assert.Equal(new[] { 0, 1 }, proposal.Draft.Casting.Select(item => item.Order));
        Assert.Equal(new[] { "Writer", "Editor" }, proposal.Draft.AgentCharters.Select(item => item.Name));

        var rejection = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        Assert.Equal(ProjectCastingProposalState.Rejected, rejection.State);
        Assert.Equal(initial.Revision, (await service.GetProjectConfigurationAsync(
            owner, project.ProjectId, null, CancellationToken.None)).Revision);
        Assert.Equal(initial.Configuration.Casting.ToArray(),
            (await service.GetProjectConfigurationAsync(
                owner, project.ProjectId, null, CancellationToken.None)).Configuration.Casting.ToArray());
    }

    [Fact]
    public async Task CastingTransferRoundTripPreservesUnrelatedConfigurationAndRecordsProvenance()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "transfer-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "transfer-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var source = await service.CreateProjectAsync(owner, "Transfer source", CancellationToken.None);
        var sourceConfiguration = await service.UpdateProjectConfigurationAsync(
            owner,
            source.ProjectId,
            source.ConfigurationRevision,
            CastingConfiguration(
                "source-agent",
                "Source charter",
                [new SkillCatalogSetting("source-skill", true, 0)]) with
            {
                DefaultWorkflowId = "source-workflow",
            },
            CancellationToken.None);
        var transfer = await service.ExportCastingTransferAsync(
            owner, source.ProjectId, sourceConfiguration.Revision, CancellationToken.None);
        Assert.Equal(ProjectCastingTransfer.CurrentFormatVersion, transfer.FormatVersion);
        Assert.Equal(source.ProjectId, transfer.SourceProjectId);
        Assert.Equal(sourceConfiguration.Revision, transfer.SourceConfigurationRevision);
        Assert.Equal(64, transfer.ContentDigest.Length);

        var target = await service.CreateProjectAsync(owner, "Transfer target", CancellationToken.None);
        var targetConfiguration = await service.UpdateProjectConfigurationAsync(
            owner,
            target.ProjectId,
            target.ConfigurationRevision,
            CastingConfiguration(
                "target-agent",
                "Target charter",
                [new SkillCatalogSetting("target-skill", true, 0)]) with
            {
                DefaultWorkflowId = "target-workflow",
            },
            CancellationToken.None);
        var imported = await service.ImportCastingTransferAsync(
            owner,
            target.ProjectId,
            new ImportProjectCastingTransferRequest
            {
                ExpectedConfigurationRevision = targetConfiguration.Revision,
                Transfer = transfer,
            },
            CancellationToken.None);

        Assert.Equal(targetConfiguration.Revision, imported.BaseConfigurationRevision);
        Assert.Equal(transfer.AgentCharters.ToArray(), imported.Draft.AgentCharters.ToArray());
        Assert.Equal(transfer.Casting.ToArray(), imported.Draft.Casting.ToArray());
        Assert.Equal(targetConfiguration.Configuration.Skills.ToArray(), imported.Draft.Skills.ToArray());
        Assert.Equal("target-workflow", imported.Draft.DefaultWorkflowId);
        Assert.Equal(source.ProjectId, imported.TransferProvenance?.SourceProjectId);
        Assert.Equal(sourceConfiguration.Revision, imported.TransferProvenance?.SourceConfigurationRevision);
        Assert.Equal(targetConfiguration.Revision,
            (await service.GetProjectConfigurationAsync(owner, target.ProjectId, null, CancellationToken.None)).Revision);

        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, target.ProjectId, imported.ProposalId, imported.DraftRevision, CancellationToken.None);
        Assert.Equal(
            targetConfiguration.Configuration.Skills.ToArray(),
            confirmed.Result?.Skills.ToArray());
        Assert.Equal("target-workflow", confirmed.Result?.DefaultWorkflowId);
        var confirmedRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirming the proposal did not return its configuration revision.");

        var tampered = transfer with
        {
            Casting = [transfer.Casting[0] with { Order = transfer.Casting[0].Order + 1 }],
        };
        var digestMismatch = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ImportCastingTransferAsync(
                owner,
                target.ProjectId,
                new ImportProjectCastingTransferRequest
                {
                    ExpectedConfigurationRevision = confirmedRevision,
                    Transfer = tampered,
                },
                CancellationToken.None));
        Assert.Equal("casting_transfer_digest_mismatch", digestMismatch.Code);

        var unsupported = transfer with { FormatVersion = ProjectCastingTransfer.CurrentFormatVersion + 1 };
        var unsupportedVersion = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ImportCastingTransferAsync(
                owner,
                target.ProjectId,
                new ImportProjectCastingTransferRequest
                {
                    ExpectedConfigurationRevision = confirmedRevision,
                    Transfer = unsupported,
                },
                CancellationToken.None));
        Assert.Equal("unsupported_casting_transfer_version", unsupportedVersion.Code);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unsupportedVersion.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCastingProposalConfirmationAcceptsOnlyOneCurrentRoster()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "concurrent-casting-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "concurrent-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Concurrent casting", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, project.ConfigurationRevision,
            CastingConfiguration("agent-initial", "Initial"), CancellationToken.None);
        var first = await service.CreateCastingProposalAsync(
            owner, project.ProjectId, initial.Revision,
            CastingConfiguration("agent-first", "First"), CancellationToken.None);
        var second = await service.CreateCastingProposalAsync(
            owner, project.ProjectId, initial.Revision,
            CastingConfiguration("agent-second", "Second"), CancellationToken.None);

        async Task<ProjectConfigException?> TryConfirmAsync(ProjectCastingProposal proposal)
        {
            try
            {
                await service.ConfirmCastingProposalAsync(
                    owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
                return null;
            }
            catch (ProjectConfigException exception)
            {
                return exception;
            }
        }

        var outcomes = await Task.WhenAll(TryConfirmAsync(first), TryConfirmAsync(second));
        Assert.Single(outcomes, exception => exception is null);
        var conflict = Assert.IsType<ProjectConfigException>(Assert.Single(outcomes, item => item is not null));
        Assert.Equal("stale_project_configuration", conflict.Code);
        await using var currentDb = CreateDbContext();
        var currentService = CreateCastingProposalService(currentDb);
        Assert.Equal(initial.Revision + 1,
            (await currentService.GetProjectConfigurationAsync(
                owner, project.ProjectId, null, CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task AcceptedRunRetainsTheCastingRevisionAfterLaterProposalConfirmation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "run-casting-tenant-" + suffix;
        var platformAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            "run-casting-platform-admin-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var platformHead = await service.GetPlatformRuntimeDefaultsAsync(platformAdmin, CancellationToken.None);
        var platform = await service.UpdatePlatformRuntimeDefaultsAsync(
            platformAdmin, platformHead.Revision, PlatformDefaults(), CancellationToken.None);
        var tenantAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            "run-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(tenantAdmin, "Run casting affinity", CancellationToken.None);
        var initial = await service.UpdateProjectConfigurationAsync(
            tenantAdmin, project.ProjectId, project.ConfigurationRevision,
            CastingConfiguration("agent-before-run", "Before run"), CancellationToken.None);
        var orchestratorSubject = "run-casting-orchestrator-" + suffix;
        var orchestratorMembership = await SeedMembershipAsync(authorityStore, orchestratorSubject, tenantId);
        await authorityStore.AssignRoleAsync(
            orchestratorMembership,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var orchestrator = await ResolveCallerAsync(
            db, orchestratorSubject, tenantId, ["api.read", "projects.orchestrator"]);
        var request = RunRequest(initial.Revision, platform.Revision);
        var runId = "casting-affinity-" + suffix;
        var accepted = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, runId, request, CancellationToken.None);

        var proposal = await service.CreateCastingProposalAsync(
            tenantAdmin,
            project.ProjectId,
            initial.Revision,
            CastingConfiguration("agent-after-run", "After run"),
            CancellationToken.None);
        await service.ConfirmCastingProposalAsync(
            tenantAdmin, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);

        var replay = await service.AcceptRunSelectionAsync(
            orchestrator, project.ProjectId, runId, request, CancellationToken.None);
        var read = await service.GetRunSelectionAsync(
            orchestrator, project.ProjectId, runId, CancellationToken.None);
        Assert.Equal(initial.Revision, accepted.ProjectConfigurationRevision);
        Assert.Equal(accepted.ProjectConfigurationRevision, replay.ProjectConfigurationRevision);
        Assert.Equal(accepted.ProjectConfiguration.Casting.ToArray(), replay.ProjectConfiguration.Casting.ToArray());
        Assert.Equal(accepted.ProjectConfiguration.Casting.ToArray(), read.ProjectConfiguration.Casting.ToArray());
        Assert.Equal("agent-before-run", Assert.Single(read.ProjectConfiguration.Casting).AgentId);
    }

    [Fact]
    public async Task CastingProposalConfirmationFailsAfterCurrentOwnerAuthorityIsRevoked()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "revoked-casting-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            "revoked-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(caller, "Revoked casting owner", CancellationToken.None);
        var proposal = await service.CreateCastingProposalAsync(
            caller,
            project.ProjectId,
            project.ConfigurationRevision,
            CastingConfiguration("agent-revoked", "Should not confirm"),
            CancellationToken.None);
        _ = await SeedCallerAsync(
            db,
            authorityStore,
            "remaining-casting-admin-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var currentAssignment = await db.RoleAssignments.AsNoTracking()
            .SingleAsync(item =>
                item.MembershipId == caller.MembershipId &&
                item.ResourceType == ProjectAuthorityResourceType.Tenant &&
                item.Role == ProjectAuthorityRole.TenantAdmin &&
                item.State == ProjectAuthorityRecordState.Active);
        await authorityStore.RevokeRoleAssignmentAsync(currentAssignment.AssignmentId, 1, "fixture");

        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ConfirmCastingProposalAsync(
                caller, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(ProjectCastingProposalState.Pending,
            (await service.GetCastingProposalAsync(
                (await ResolveCallerAsync(
                    db, "remaining-casting-admin-" + suffix, tenantId, ["api.read", "projects.admin"])),
                project.ProjectId,
                proposal.ProposalId,
                CancellationToken.None)).State);
        Assert.Equal(project.ConfigurationRevision,
            (await service.GetProjectConfigurationAsync(
                (await ResolveCallerAsync(
                    db, "remaining-casting-admin-" + suffix, tenantId, ["api.read", "projects.admin"])),
                project.ProjectId,
                null,
                CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task CastingProposalsUseLockFunctionWithSelectOnlyRuntimeAuthorityTables()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var ownerService = CreateCastingProposalService(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "runtime-casting-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "runtime-casting-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await ownerService.CreateProjectAsync(owner, "Runtime casting locks", CancellationToken.None);
        var initial = await ownerService.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, project.ConfigurationRevision,
            CastingConfiguration("agent-initial", "Initial"), CancellationToken.None);
        var assignment = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == owner.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Tenant &&
            item.ResourceId == tenantId &&
            item.Role == ProjectAuthorityRole.TenantAdmin &&
            item.State == ProjectAuthorityRecordState.Active);

        var roleName = "aw_casting_runtime_" + suffix;
        var password = Guid.NewGuid().ToString("N");
        var roleCreated = false;
        async Task ExecuteOwnerSqlAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await ExecuteOwnerSqlAsync(
                $"CREATE ROLE \"{roleName}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
            roleCreated = true;
            await ExecuteOwnerSqlAsync($"GRANT USAGE ON SCHEMA projects_config TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync($"GRANT SELECT, UPDATE ON projects_config.projects TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON projects_config.project_configuration_revisions TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON projects_config.project_casting_proposals TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT ON projects_config.tenant_memberships, projects_config.project_role_assignments TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT ON projects_config.authority_audit TO \"{roleName}\"");

            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
            var runtimeOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(runtimeDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema))
                .Options;
            await using var runtimeDb = new ProjectsConfigDbContext(runtimeOptions);
            var missingExecute = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtimeDataSource));
            Assert.Contains(
                "EXECUTE on projects_config.lock_casting_authority",
                missingExecute.Message);
            await ExecuteOwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean) TO "{roleName}"
                """);
            await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtimeDataSource);

            await using var privilegeCheck = await fixture.DataSource.OpenConnectionAsync();
            await using (var command = new NpgsqlCommand(
                """
                SELECT has_table_privilege(@role, 'projects_config.tenant_memberships', 'SELECT')
                   AND NOT has_table_privilege(@role, 'projects_config.tenant_memberships', 'UPDATE')
                   AND has_table_privilege(@role, 'projects_config.project_role_assignments', 'SELECT')
                   AND NOT has_table_privilege(@role, 'projects_config.project_role_assignments', 'UPDATE')
                   AND has_function_privilege(
                       @role,
                       'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
                       'EXECUTE')
                   AND NOT EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_proc AS routine
                       CROSS JOIN LATERAL pg_catalog.aclexplode(
                           COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) AS acl
                       WHERE routine.oid = pg_catalog.to_regprocedure(
                           'projects_config.lock_casting_authority(uuid,text,text,text,bigint,text,boolean)')
                         AND acl.grantee = 0
                         AND acl.privilege_type = 'EXECUTE')
                """,
                privilegeCheck))
            {
                command.Parameters.AddWithValue("role", roleName);
                Assert.True((bool)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Runtime privilege check returned no result.")));
            }

            var runtimeService = new ProjectsConfigService(
                runtimeDb,
                CreateProviderCatalog(),
                TimeProvider.System,
                new PooledDbContextFactory<ProjectsConfigDbContext>(runtimeOptions));

            var proposal = await runtimeService.CreateCastingProposalAsync(
                owner,
                project.ProjectId,
                initial.Revision,
                CastingConfiguration("agent-runtime", "Approved at runtime"),
                CancellationToken.None);
            var confirmed = await runtimeService.ConfirmCastingProposalAsync(
                owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
            var replay = await runtimeService.ConfirmCastingProposalAsync(
                owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
            Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
            Assert.Equal(confirmed.ConfirmedConfigurationRevision, replay.ConfirmedConfigurationRevision);

            await using (var lockConnection = await runtimeDataSource.OpenConnectionAsync())
            await using (var lockTransaction = await lockConnection.BeginTransactionAsync())
            await using (var lockCommand = new NpgsqlCommand(
                """
                SELECT projects_config.lock_casting_authority(
                    @membership_id, @issuer, @subject, @tenant_id, @revision, @project_id, true)
                """,
                lockConnection,
                lockTransaction))
            {
                lockCommand.Parameters.AddWithValue("membership_id", owner.MembershipId);
                lockCommand.Parameters.AddWithValue("issuer", owner.Issuer);
                lockCommand.Parameters.AddWithValue("subject", owner.ActorId);
                lockCommand.Parameters.AddWithValue("tenant_id", owner.TenantId);
                lockCommand.Parameters.AddWithValue("revision", owner.MembershipRevision);
                lockCommand.Parameters.AddWithValue("project_id", project.ProjectId);
                Assert.Equal("authorized", await lockCommand.ExecuteScalarAsync());

                await using var revokeConnection = await fixture.DataSource.OpenConnectionAsync();
                await using (var timeout = new NpgsqlCommand("SET lock_timeout = '1s'", revokeConnection))
                    await timeout.ExecuteNonQueryAsync();
                await using var revokeCommand = new NpgsqlCommand(
                    """
                    UPDATE projects_config.project_role_assignments
                    SET state = 'Revoked', revision = revision + 1
                    WHERE assignment_id = @assignment_id
                    """,
                    revokeConnection);
                revokeCommand.Parameters.AddWithValue("assignment_id", assignment.AssignmentId);
                var blockedRevoke = await Assert.ThrowsAsync<PostgresException>(
                    () => revokeCommand.ExecuteNonQueryAsync());
                Assert.Equal("55P03", blockedRevoke.SqlState);

                await lockTransaction.CommitAsync();
            }

            await authorityStore.RevokeRoleAssignmentAsync(assignment.AssignmentId, 1, "fixture");
            var denied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                runtimeService.CreateCastingProposalAsync(
                    owner,
                    project.ProjectId,
                    confirmed.ConfirmedConfigurationRevision!.Value,
                    CastingConfiguration("agent-revoked", "Must not be accepted"),
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
        }
        finally
        {
            if (roleCreated)
            {
                await ExecuteOwnerSqlAsync($"DROP OWNED BY \"{roleName}\"");
                await ExecuteOwnerSqlAsync($"DROP ROLE \"{roleName}\"");
            }
        }
    }

    private ProjectsConfigService CreateCastingProposalService(ProjectsConfigDbContext db) =>
        new(
            db,
            CreateProviderCatalog(),
            TimeProvider.System,
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()));

    private static ProjectConfiguration CastingConfiguration(
        string agentId,
        string charter,
        ImmutableArray<SkillCatalogSetting> skills = default) =>
        new()
        {
            AgentCharters = [new ProjectAgentCharter(agentId, agentId, "builder", charter)],
            Casting = [new ProjectAgentCast(agentId, "builder", 0)],
            Skills = skills.IsDefault ? [] : skills,
        };

    private async Task WithFirstSerializationFailureAsync(
        RetryTriggerTarget target,
        Func<Task> operation)
    {
        var (table, eventName) = target switch
        {
            RetryTriggerTarget.AssignmentInsert => ("project_role_assignments", "INSERT"),
            RetryTriggerTarget.AssignmentUpdate => ("project_role_assignments", "UPDATE"),
            RetryTriggerTarget.MembershipUpdate => ("tenant_memberships", "UPDATE"),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var suffix = Guid.NewGuid().ToString("N");
        var sequence = $"projects_config.test_retry_sequence_{suffix}";
        var function = $"projects_config.test_retry_function_{suffix}";
        var trigger = $"test_retry_trigger_{suffix}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await ExecuteAsync($"CREATE SEQUENCE {sequence}");
            await ExecuteAsync($"""
                CREATE FUNCTION {function}() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                BEGIN
                    IF nextval('{sequence}'::regclass) = 1 THEN
                        RAISE EXCEPTION 'forced serialization retry' USING ERRCODE = '40001';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                """);
            await ExecuteAsync($"""
                CREATE TRIGGER {trigger}
                BEFORE {eventName} ON projects_config.{table}
                FOR EACH ROW EXECUTE FUNCTION {function}();
                """);

            await operation();

            await using var attempts = new NpgsqlCommand($"SELECT last_value FROM {sequence}", connection);
            Assert.Equal(2L, await attempts.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger} ON projects_config.{table}");
            await ExecuteAsync($"DROP FUNCTION IF EXISTS {function}()");
            await ExecuteAsync($"DROP SEQUENCE IF EXISTS {sequence}");
        }
    }

    private enum RetryTriggerTarget
    {
        AssignmentInsert,
        AssignmentUpdate,
        MembershipUpdate,
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
        return new ProjectsConfigDbContext(CreateDbContextOptions());
    }

    private DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

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

        var networkCapabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "networkpolicy.l3l4",
            "networkpolicy.fqdn",
            "networkpolicy.cidr",
            "networkpolicy.dns");
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [
                Registration("sandbox-platform", "options-v1"),
                Registration("sandbox-project", "options-v3"),
                new ProviderRegistration(
                    new ProviderDescriptor(
                        ProviderSeam.NetworkPolicy,
                        "cilium",
                        new Version(1, 0, 0),
                        1,
                        ProviderHostingPattern.KubernetesController,
                        networkCapabilities),
                    true,
                    "cilium-options-v1",
                    1)
            ],
            [new ProviderSelection(ProviderSeam.Sandbox, "sandbox-platform")],
            [new ProviderOverridePermission(ProviderSeam.Sandbox, "sandbox-project")],
            layerSelections: [new ProviderLayerSelection(NetworkPolicyLayer.L3L4, "cilium")]).Value);
    }

    private static PlatformRuntimeDefaults PlatformDefaults() => new()
    {
        ModelSelection = new ModelSelectionSettings("platform-model", SourceMode: ModelSourceMode.HostedCopilot,
            ConnectionId: Guid.Parse("8b92ad7d-dbd3-4a80-877c-6999fdc3d3bc")),
        EgressBaseline =
        [
            Fqdn(NetworkEgressPurpose.ModelEndpoint, "api.example.com"),
            Fqdn(NetworkEgressPurpose.PackageRegistry, "storage.example.com"),
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
        ModelSelection = new ModelSelectionSettings("project-model",
            new SecretRef("project-model-key", "v1"), ModelSourceMode.Byok),
        ProviderOverrides = [new ProjectProviderOverride(ProviderSeam.Sandbox, "sandbox-project")],
        EgressNarrowing = [Fqdn(NetworkEgressPurpose.ModelEndpoint, "api.example.com")],
        RunLimits = new CopilotRunLimitOverrides
        {
            MaxModelTurns = 6,
            MaxChildren = 0,
        },
    };

    private static byte[] LegacyFingerprintBytes(
        string projectId, string runId, AcceptRunSelectionRequest request)
    {
        // Preserve the anonymous-object format used before meter-source selection.
        var canonical = new
        {
            projectId,
            runId,
            request.ExpectedProjectConfigRevision,
            request.ExpectedPlatformRuntimeRevision,
            ContextRevision = request.Context.Revision,
            ModelReferences = request.Context.AvailableModelSelectionReferences.Order(StringComparer.Ordinal).ToArray(),
            ProviderRequirements = request.Context.ProviderRequirements
                .OrderBy(item => item.Seam)
                .Select(item => new
                {
                    item.Seam,
                    item.RequiredAdapterVersion,
                    item.RequiredOptionsSchemaVersion,
                    RequiredCapabilities = item.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                    RequiredL3L4Capabilities = item.RequiredL3L4Capabilities.Order(StringComparer.Ordinal).ToArray(),
                    RequiredL7Capabilities = item.RequiredL7Capabilities.Order(StringComparer.Ordinal).ToArray(),
                }).ToArray(),
            RequiredEgress = ProjectConfigurationValidator
                .ValidateEgressRules(request.Context.RequiredEgress)
                .Select(rule => new
                {
                    Host = rule.Destination,
                    rule.Port,
                    rule.Protocol,
                })
                .OrderBy(rule => rule.Host, StringComparer.Ordinal)
                .ThenBy(rule => rule.Port).ThenBy(rule => rule.Protocol).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, LegacyJsonOptions());
    }

    private static object LegacyProjectConfiguration(ProjectConfiguration configuration) => new
    {
        configuration.ModelSelection,
        configuration.ProviderOverrides,
        configuration.OrderedProviderOverrides,
        configuration.AgentCharters,
        configuration.Casting,
        configuration.BlueprintWorkflowReferences,
        configuration.Skills,
        EgressNarrowing = configuration.EgressNarrowing?.Select(rule => new
        {
            Host = rule.Destination,
            rule.Port,
            rule.Protocol,
        }).ToArray(),
        configuration.RunLimits,
    };

    private static byte[] StructuredFingerprintBytes(
        string projectId, string runId, AcceptRunSelectionRequest request)
    {
        var canonical = new
        {
            projectId,
            runId,
            request.ExpectedProjectConfigRevision,
            request.ExpectedPlatformRuntimeRevision,
            ContextRevision = request.Context.Revision,
            ModelReferences = request.Context.AvailableModelSelectionReferences.Order(StringComparer.Ordinal).ToArray(),
            ProviderRequirements = request.Context.ProviderRequirements
                .OrderBy(item => item.Seam)
                .ThenBy(item => item.MeterSource, StringComparer.Ordinal)
                .Select(item => new StructuredProviderRequirementFingerprint(
                    item.Seam,
                    item.RequiredAdapterVersion,
                    item.RequiredOptionsSchemaVersion,
                    item.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                    item.RequiredL3L4Capabilities.Order(StringComparer.Ordinal).ToArray(),
                    item.RequiredL7Capabilities.Order(StringComparer.Ordinal).ToArray(),
                    item.MeterSource)).ToArray(),
            RequiredEgress = ProjectConfigurationValidator
                .ValidateEgressRules(request.Context.RequiredEgress)
                .OrderBy(rule => rule.Purpose)
                .ThenBy(rule => rule.DestinationKind)
                .ThenBy(rule => rule.Destination, StringComparer.Ordinal)
                .ThenBy(rule => rule.Port)
                .ThenBy(rule => rule.Protocol)
                .ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, LegacyJsonOptions());
    }

    private sealed record StructuredProviderRequirementFingerprint(
        ProviderSeam Seam,
        string RequiredAdapterVersion,
        int RequiredOptionsSchemaVersion,
        string[] RequiredCapabilities,
        string[] RequiredL3L4Capabilities,
        string[] RequiredL7Capabilities,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MeterSource);

    private static JsonSerializerOptions LegacyJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

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
                new ProviderRequirement
                {
                    Seam = ProviderSeam.NetworkPolicy,
                    RequiredAdapterVersion = "1.0.0",
                    RequiredOptionsSchemaVersion = 1,
                    RequiredL3L4Capabilities = ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "networkpolicy.l3l4",
                        "networkpolicy.fqdn",
                        "networkpolicy.cidr",
                        "networkpolicy.dns"),
                },
            ],
            RequiredEgress = [Fqdn(NetworkEgressPurpose.ModelEndpoint, "api.example.com")],
        },
    };

    private static NetworkEgressRule Fqdn(NetworkEgressPurpose purpose, string host) =>
        new(purpose, NetworkEgressDestinationKind.Fqdn, host, 443, EgressProtocol.Tcp);
}

public sealed class ProjectsConfigPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

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
