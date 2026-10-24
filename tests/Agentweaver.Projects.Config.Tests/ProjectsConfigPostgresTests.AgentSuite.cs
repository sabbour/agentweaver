using System.IO.Compression;
using System.Formats.Tar;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Projects.Config.AgentSuite;
using Agentweaver.Projects.Config.Tests.AgentSuite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using FixtureTarEntry = Agentweaver.Projects.Config.Tests.AgentSuite.TarEntry;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private const string AgentSuiteValidManifest =
        "sha256:499d25ae869a1dbfb9dc30b32bbb95e46bafffe32d4ba0ce5499eb8f8adb6967";

    [Fact]
    public async Task AgentSuiteImportStagesReviewOnlyAgentsAndIsIdempotent()
    {
        // Arrange: a tenant admin owns a project that already has one configured agent.
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Suite target", CancellationToken.None);
        var seeded = await SeedHistoricalConfigurationAsync(
            db, project, owner.ActorId, CastingConfiguration("existing-agent", "Existing charter"));

        var first = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(seeded.Revision, "valid"), store, CancellationToken.None);

        // The import is identified by the OCI manifest digest.
        var proposal = Assert.IsType<ProjectCastingProposal>(first.Proposal);
        Assert.Equal(AgentSuiteValidManifest, first.Import.SourceIdentity);
        Assert.Equal(AgentSuiteValidManifest, first.Import.ManifestDigest);
        // The effective ID ends with the first 12 hex characters of the manifest digest.
        var agent = Assert.Single(first.Import.Agents);
        Assert.Equal($"agentsuite:{first.Import.SuiteName}:{agent.NativeId}:499d25ae869a", agent.EffectiveAgentId);
        // Imported agents are review-only: they cannot run until a runtime exists.
        Assert.False(agent.Runnable);
        Assert.Contains(agent.Blockers, item => item.Code == "runtime_unavailable");
        // The proposal adds the native agent with its instructions as the charter,
        // keeps the existing agent, and casts the new agent after it.
        var charter = Assert.Single(proposal.Draft.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);
        Assert.Equal("Repeat the user's request in one short sentence.\n", charter.Charter);
        Assert.Contains(proposal.Draft.AgentCharters, item => item.AgentId == "existing-agent");
        Assert.Contains(proposal.Draft.Casting, item => item.AgentId == agent.EffectiveAgentId && item.Order == 1);
        // Staging a proposal does not change the live configuration revision.
        Assert.Equal(seeded.Revision,
            (await service.GetProjectConfigurationAsync(owner, project.ProjectId, null, CancellationToken.None)).Revision);

        // Every blob that exists as a fixture file is stored byte-for-byte in the object store.
        // Blobs without a fixture file (such as extracted content files) are skipped here.
        foreach (var blob in first.Import.Blobs)
        {
            var path = Path.Combine(AgentSuiteFixtureRoot, "valid", "blobs", "sha256", blob.Digest[7..]);
            if (!File.Exists(path))
                continue;
            using var read = await store.ReadAsync(new ObjectKey($"agentsuite/blobs/sha256/{blob.Digest[7..]}"))
                ?? throw new InvalidOperationException("A retained blob is missing.");
            using var copy = new MemoryStream();
            await read.Content.CopyToAsync(copy);
            Assert.Equal(await File.ReadAllBytesAsync(path), copy.ToArray());
        }
        // Reading the import back returns exactly the record the import call returned.
        AssertSameAgentSuiteRecord(first.Import, await service.GetAgentSuiteImportAsync(
            owner, project.ProjectId, AgentSuiteValidManifest, store, CancellationToken.None));

        // Idempotency: importing the same suite again, or the same manifest under another tag,
        // returns the existing proposal instead of creating a new one.
        var replay = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(seeded.Revision, "valid"), store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, replay.Proposal?.ProposalId);
        var retagged = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(seeded.Revision, "valid-retagged"), store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, retagged.Proposal?.ProposalId);
        Assert.Single(await service.ListCastingProposalsAsync(owner, project.ProjectId, 50, CancellationToken.None));

        // Confirming the proposal applies the imported agent to a new configuration revision.
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        var confirmedRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirmation returned no revision.");
        Assert.Contains(confirmed.Result!.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);
        var current = await service.GetProjectConfigurationAsync(owner, project.ProjectId, confirmedRevision, CancellationToken.None);
        Assert.Contains(current.Configuration.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);

        // Importing again after confirmation finds the agent already applied:
        // no new proposal, and the stored import record is unchanged.
        var unchanged = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(confirmedRevision, "valid"), store, CancellationToken.None);
        Assert.Null(unchanged.Proposal);
        AssertSameAgentSuiteRecord(first.Import, unchanged.Import);

        // Revision fence: an expected revision that does not match the current one is a 409 conflict.
        var stale = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(confirmedRevision + 5, "valid"), store, CancellationToken.None));
        Assert.Equal("stale_project_configuration", stale.Code);
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task AgentSuiteExportRoundTripsCurrentTeamAndRebindsRemoteTools()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-export-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-export-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var source = await service.CreateProjectAsync(owner, "Export source", CancellationToken.None);
        var initial = await SeedHistoricalConfigurationAsync(
            db, source, owner.ActorId, CastingConfiguration("existing-agent", "Existing charter"));
        var sourceConnectionId = Guid.NewGuid().ToString("D");
        var sourceRequest = AgentSuiteTeamWithRemoteDependency(initial.Revision);
        var sourceValidation = AgentSuiteValidator.Validate(
            AgentSuiteArchiveFormat.ContentTarGzip, Convert.FromBase64String(sourceRequest.Archive));
        Assert.True(sourceValidation.Accepted,
            string.Join("\n", sourceValidation.NativeErrors.Concat(sourceValidation.ProfileErrors.Select(item => item.Message))));

        // Import and confirm the source suite so its current team includes both local and imported agents.
        var original = await service.ImportAgentSuiteAsync(
            owner, source.ProjectId, sourceRequest with
            {
                ToolConnections = [new("search@1.0.0", sourceConnectionId)],
            }, store, CancellationToken.None);
        var originalProposal = Assert.IsType<ProjectCastingProposal>(original.Proposal);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, source.ProjectId, originalProposal.ProposalId, originalProposal.DraftRevision, CancellationToken.None);
        var sourceRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirmation returned no configuration revision.");
        var current = await service.GetProjectConfigurationAsync(owner, source.ProjectId, sourceRevision, CancellationToken.None);

        // The exported bytes must pass the same native and Agentweaver validator used for imports.
        var exported = await service.ExportAgentSuiteTeamAsync(
            owner, source.ProjectId, sourceRevision, store, CancellationToken.None);
        var archive = Convert.FromBase64String(exported.Archive);
        var validation = AgentSuiteValidator.Validate(AgentSuiteArchiveFormat.ContentTarGzip, archive);
        Assert.True(validation.Accepted,
            string.Join("\n", validation.NativeErrors.Concat(validation.ProfileErrors.Select(item => item.Message))));
        var graph = Assert.IsType<AgentSuiteGraph>(validation.Graph);
        Assert.Equal(
            current.Configuration.Casting.OrderBy(item => item.Order).Select(item => item.Role),
            graph.Agents.Select(item => item.Id));
        var lead = Assert.Single(graph.Agents, item => item.Id == "lead");
        Assert.Equal(["reviewer", "writer"], lead.Invokes.Select(item => item.Agent).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(graph.Agents, item => !item.ToolProviders.IsEmpty);

        // Check the copied provider has only portable Environment binding slots, not source endpoints or secrets.
        var files = ReadAgentSuiteContentArchive(archive);
        var writerPolicyDigest = Assert.Single(original.Import.Agents, item => item.NativeId == "writer").PolicyDigest;
        Assert.NotNull(writerPolicyDigest);
        using var writerManifest = JsonDocument.Parse(files["agents/writer.json"]);
        var writerPolicy = writerManifest.RootElement.GetProperty("policy");
        var writerPolicyPath = writerPolicy.GetProperty("path").GetString()!;
        Assert.Equal(writerPolicyDigest, writerPolicy.GetProperty("descriptor").GetProperty("digest").GetString());
        Assert.Equal(writerPolicyDigest, AgentSuiteArchives.Digest(files[writerPolicyPath]));
        using var suite = JsonDocument.Parse(files["agentsuite.json"]);
        var catalogPath = suite.RootElement.GetProperty("toolProviderCatalog").GetProperty("path").GetString()!;
        using var catalog = JsonDocument.Parse(files[catalogPath]);
        var providerPath = catalog.RootElement.GetProperty("toolProviders").EnumerateArray().Single()
            .GetProperty("path").GetString()!;
        using var provider = JsonDocument.Parse(files[providerPath]);
        var remote = provider.RootElement.GetProperty("remote");
        Assert.Equal("target-environment-mcp", remote.GetProperty("endpointRef").GetString());
        Assert.Empty(remote.GetProperty("headers").EnumerateArray());
        Assert.Equal("target-environment-mcp",
            remote.GetProperty("network").EnumerateArray().Single().GetProperty("destinationRef").GetString());
        Assert.Equal("target-environment-authorization",
            provider.RootElement.GetProperty("tools").EnumerateArray().First()
                .GetProperty("delegatedAuthorization").GetProperty("credential").GetProperty("bindingRef").GetString());
        var packageText = string.Join('\n', files.Values.Select(Encoding.UTF8.GetString));
        Assert.DoesNotContain("search-mcp-endpoint", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("search-mcp-auth", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("search-mcp-egress", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("search-delegated-connection", packageText, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.2", packageText, StringComparison.Ordinal);

        // Import the exported package through the existing service and bind its tool provider to the target project.
        var target = await service.CreateProjectAsync(owner, "Export target", CancellationToken.None);
        var targetConnectionId = Guid.NewGuid().ToString("D");
        var roundTrip = await service.ImportAgentSuiteAsync(owner, target.ProjectId, new ImportProjectAgentSuiteRequest
        {
            ExpectedConfigurationRevision = target.ConfigurationRevision,
            Format = ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
            Archive = exported.Archive,
            ToolConnections = [new("search@1.0.0", targetConnectionId)],
        }, store, CancellationToken.None);
        var targetProposal = Assert.IsType<ProjectCastingProposal>(roundTrip.Proposal);
        Assert.Equal(graph.Agents.Select(item => item.Id), targetProposal.Draft.Casting.Select(item => item.Role));
        var dependencies = roundTrip.Import.Agents.SelectMany(item => item.ToolDependencies)
            .Where(item => item.ToolProvider == "search@1.0.0").ToArray();
        Assert.NotEmpty(dependencies);
        Assert.All(dependencies, item =>
        {
            Assert.Equal(AgentSuiteImportPlanner.RemoteMcpKind, item.Kind);
            Assert.Equal(targetConnectionId, item.ConnectionId);
        });
        Assert.Equal(
            writerPolicyDigest,
            Assert.Single(roundTrip.Import.Agents, item => item.NativeId == "writer").PolicyDigest);
    }

    [Fact]
    public async Task AgentSuiteExportRejectsEnvironmentSpecificPolicyDestinations()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-export-policy-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-export-policy-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Policy export source", CancellationToken.None);
        var imported = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "team"), store,
            CancellationToken.None);
        var proposal = Assert.IsType<ProjectCastingProposal>(imported.Proposal);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);

        var rejected = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ExportAgentSuiteTeamAsync(
            owner, project.ProjectId, confirmed.ConfirmedConfigurationRevision, store, CancellationToken.None));
        Assert.Equal("agentsuite_team_not_exportable", rejected.Code);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, rejected.StatusCode);
        Assert.Contains("egress policy with environment-specific destinations", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.2", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentSuiteImportRejectsChangedLayerBytesUnderThePinnedSourceIdentity()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-pinned-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-pinned-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Pinned suite", CancellationToken.None);

        var request = AgentSuiteRequest(project.ConfigurationRevision, "valid");
        var imported = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, request, store, CancellationToken.None);
        Assert.Equal(AgentSuiteValidManifest, imported.Import.SourceIdentity);

        var changedArchive = AgentSuiteLayoutTar(
            Path.Combine(AgentSuiteFixtureRoot, "valid"),
            "blobs/sha256/c8a66d0cacdb86b823914a813ed7b2ecd323ae7908096e4b3839173c19c03b03");
        var changed = await Assert.ThrowsAsync<AgentSuiteImportRejectedException>(() =>
            service.ImportAgentSuiteAsync(
                owner,
                project.ProjectId,
                request with { Archive = Convert.ToBase64String(changedArchive) },
                store,
                CancellationToken.None));

        Assert.Equal("agentsuite_invalid", changed.Code);
        Assert.Contains(changed.Errors, error => error.Contains("digest", StringComparison.OrdinalIgnoreCase));
        AssertSameAgentSuiteRecord(imported.Import, await service.GetAgentSuiteImportAsync(
            owner, project.ProjectId, AgentSuiteValidManifest, store, CancellationToken.None));
        Assert.Single(await service.ListCastingProposalsAsync(owner, project.ProjectId, 50, CancellationToken.None));
        Assert.Equal(project.ConfigurationRevision,
            (await service.GetProjectConfigurationAsync(owner, project.ProjectId, null, CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task AgentSuiteImportIsDeniedAfterCurrentAuthorityIsRevoked()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-revoked-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-revoked-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var remainingAdmin = await SeedCallerAsync(
            db, authorityStore, "agentsuite-remaining-admin-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Revoked suite owner", CancellationToken.None);
        var assignment = await db.RoleAssignments.AsNoTracking()
            .SingleAsync(item =>
                item.MembershipId == owner.MembershipId &&
                item.ResourceType == ProjectAuthorityResourceType.Tenant &&
                item.Role == ProjectAuthorityRole.TenantAdmin &&
                item.State == ProjectAuthorityRecordState.Active);
        await authorityStore.RevokeRoleAssignmentAsync(assignment.AssignmentId, assignment.Revision, "fixture");

        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            owner,
            project.ProjectId,
            AgentSuiteRequest(project.ConfigurationRevision, "valid"),
            store,
            CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(await service.ListCastingProposalsAsync(remainingAdmin, project.ProjectId, 50, CancellationToken.None));
        var missingRecord = await Assert.ThrowsAsync<ProjectConfigException>(() => service.GetAgentSuiteImportAsync(
            remainingAdmin, project.ProjectId, AgentSuiteValidManifest, store, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, missingRecord.StatusCode);
        Assert.Equal(project.ConfigurationRevision,
            (await service.GetProjectConfigurationAsync(remainingAdmin, project.ProjectId, null, CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task ConfirmedAgentSuiteRosterReachesTheAcceptedRunConsumer()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-run-tenant-" + suffix;
        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "agentsuite-run-platform-admin-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await ConfigurePlatformDefaultsAsync(db, authorityStore, service, suffix, tenantId);
        var platform = await service.GetPlatformRuntimeDefaultsAsync(platformAdmin, CancellationToken.None);
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-run-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "AgentSuite run target", CancellationToken.None);

        var imported = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "valid"), store,
            CancellationToken.None);
        var proposal = Assert.IsType<ProjectCastingProposal>(imported.Proposal);
        var agent = Assert.Single(imported.Import.Agents);
        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        var confirmedRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirmation returned no revision.");

        var orchestratorSubject = "agentsuite-run-orchestrator-" + suffix;
        var orchestratorMembership = await SeedMembershipAsync(authorityStore, orchestratorSubject, tenantId);
        await authorityStore.AssignRoleAsync(
            orchestratorMembership,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var orchestrator = await ResolveCallerAsync(
            db, orchestratorSubject, tenantId, ["api.read", "projects.orchestrator"]);
        var accepted = await service.AcceptRunSelectionAsync(
            orchestrator,
            project.ProjectId,
            "agentsuite-run-" + suffix,
            RunRequest(confirmedRevision, platform.Revision),
            CancellationToken.None);

        // The real consumer needs the complete immutable snapshot, including the accepted model routes.
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(
            accepted,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var catalogType = Assembly.Load("Agentweaver.Orchestrator")
            .GetType("Agentweaver.Orchestrator.CoordinatorWorkflowCatalog", throwOnError: true)!;
        var createContext = catalogType.GetMethod(
            "CreateRunSelectionContext", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("The Orchestrator run-selection consumer is unavailable.");
        var context = Assert.IsType<WorkPlanRunSelectionContext>(
            createContext.Invoke(null, [snapshot.RootElement]));
        var consumedRole = Assert.Single(context.Roles, role => role.RoleId == agent.NativeId);
        Assert.Contains(agent.EffectiveAgentId, consumedRole.EligibleAgentIds);
        Assert.Contains(accepted.ModelSelection.Reference, consumedRole.ModelSelectionReferences);
        Assert.NotEmpty(context.ModelTaskSelections);
        Assert.Contains(
            accepted.ProjectConfiguration.AgentCharters,
            charter => charter.AgentId == agent.EffectiveAgentId && charter.Role == agent.NativeId);
        Assert.Contains(
            accepted.ProjectConfiguration.Casting,
            cast => cast.AgentId == agent.EffectiveAgentId && cast.Role == agent.NativeId);
    }

    [Fact]
    public async Task AgentSuiteImportRejectsInvalidSuitesAndNeverOverwritesAgents()
    {
        // Arrange: an admin owner and a reader who has only the api.read scope.
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-conflict-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-conflict-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var reader = await SeedCallerAsync(
            db, authorityStore, "agentsuite-reader-" + suffix, tenantId, ["api.read"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await service.CreateProjectAsync(owner, "Suite conflict", CancellationToken.None);

        // A suite that fails validation is rejected with the validator's error messages.
        var rejected = await Assert.ThrowsAsync<AgentSuiteImportRejectedException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "config-not-empty"), store,
            CancellationToken.None));
        Assert.Equal("agentsuite_invalid", rejected.Code);
        Assert.Contains("AgentSuite config blob must be the exact bytes {}", rejected.Errors);

        // Without the projects.admin scope the caller cannot import.
        var unauthorized = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            reader, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "valid"), store,
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, unauthorized.StatusCode);

        // Stage a proposal, reject it, then import again: the import returns the same rejected
        // proposal instead of staging a fresh one.
        var staged = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "valid"), store,
            CancellationToken.None);
        var effectiveId = Assert.Single(staged.Import.Agents).EffectiveAgentId;
        var rejectedProposal = await service.RejectCastingProposalAsync(
            owner, project.ProjectId, staged.Proposal!.ProposalId, staged.Proposal.DraftRevision, CancellationToken.None);
        var replay = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "valid"), store,
            CancellationToken.None);
        Assert.Equal(rejectedProposal.ProposalId, replay.Proposal?.ProposalId);
        Assert.Equal(ProjectCastingProposalState.Rejected, replay.Proposal?.State);

        // Simulate a user edit: the configuration now holds an agent with the imported effective ID
        // but a different charter.
        var edited = await SeedHistoricalConfigurationAsync(
            db, project, owner.ActorId, CastingConfiguration(effectiveId, "Edited charter"));
        // No overwrite: the import refuses with an identity conflict ...
        var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(edited.Revision, "valid"), store, CancellationToken.None));
        Assert.Equal("agentsuite_identity_conflict", conflict.Code);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        // ... and the user's edited charter is still in place.
        Assert.Equal("Edited charter", Assert.Single(
            (await service.GetProjectConfigurationAsync(owner, project.ProjectId, null, CancellationToken.None))
                .Configuration.AgentCharters).Charter);
    }

    [Fact]
    public async Task AgentSuiteImportRecordsTeamToolConnectionsPoliciesAndBlueprint()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-team-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-team-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        // The blueprint below uses the restricted sandbox, which needs platform runtime defaults to exist.
        await ConfigurePlatformDefaultsAsync(db, authorityStore, service, suffix, tenantId);
        var project = await service.CreateProjectAsync(owner, "Team target", CancellationToken.None);
        var revision = project.ConfigurationRevision;
        const string connectionId = "6f1c2d4e-8a9b-4c3d-9e2f-1a2b3c4d5e6f";

        // Helper: expects the import to fail request validation.
        async Task<ProjectConfigException> InvalidAsync(ImportProjectAgentSuiteRequest request) =>
            await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
                owner, project.ProjectId, request, store, CancellationToken.None));
        // Bad tool connections, in order: empty GUID, uppercase GUID, unknown provider version,
        // and the same provider bound twice. Each one is a 400 bad request.
        foreach (var connections in new ProjectAgentSuiteToolConnection[][]
                 {
                     [new("search@1.0.0", Guid.Empty.ToString("D"))],
                     [new("search@1.0.0", connectionId.ToUpperInvariant())],
                     [new("search@2.0.0", connectionId)],
                     [new("search@1.0.0", connectionId), new("search@1.0.0", "11111111-2222-3333-4444-555555555555")],
                 })
        {
            var invalid = await InvalidAsync(AgentSuiteRequest(revision, "team") with { ToolConnections = [.. connections] });
            Assert.Equal("invalid_agentsuite_tool_connection", invalid.Code);
            Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        }
        // A blueprint skill binding for "planner", a role the suite does not define, is invalid.
        var badBlueprint = await InvalidAsync(AgentSuiteRequest(revision, "team") with
        {
            Blueprint = new ProjectAgentSuiteBlueprint { SkillBindings = [new("planner", ["docs"])] },
        });
        Assert.Equal("invalid_agentsuite_blueprint", badBlueprint.Code);

        // Blueprint workflow validation must finish before the archive or proposal is stored.
        var unavailableWorkflow = await InvalidAsync(AgentSuiteRequest(revision, "team") with
        {
            Blueprint = new ProjectAgentSuiteBlueprint { Workflows = ["default", "feature-development"] },
        });
        Assert.Equal("invalid_blueprint", unavailableWorkflow.Code);
        Assert.Contains("Unknown workflow 'feature-development'.", unavailableWorkflow.Message, StringComparison.Ordinal);

        // Profile refusals: model secret references, and the upstream "coordinator-workers" suite
        // whose coordinator agent uses a reserved orchestration role.
        var secrets = await Assert.ThrowsAsync<AgentSuiteImportRejectedException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(revision, "team-model-secret-refs"), store, CancellationToken.None));
        Assert.Contains(secrets.Errors, item => item.StartsWith("agentsuite_secret_reference:", StringComparison.Ordinal));
        var reserved = await Assert.ThrowsAsync<AgentSuiteImportRejectedException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, new ImportProjectAgentSuiteRequest
            {
                ExpectedConfigurationRevision = revision,
                Format = ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
                Archive = Convert.ToBase64String(AgentSuiteArchives.DirectoryContentLayer(
                    Path.Combine(AgentSuiteArchives.UpstreamRoot, "coordinator-workers"))),
            }, store, CancellationToken.None));
        Assert.Equal(["agentsuite_reserved_role: Agent 'coordinator' uses a reserved orchestration role."], reserved.Errors.ToArray());

        // Act: a valid team import with one tool connection and a blueprint.
        var blueprint = new ProjectAgentSuiteBlueprint
        {
            SourceBlueprintId = "web-team",
            SandboxProfile = ProjectAgentSuiteBlueprint.RestrictedSandbox,
            SkillBindings = [new("writer", ["docs"])],
        };
        var request = AgentSuiteRequest(revision, "team") with
        {
            ToolConnections = [new("search@1.0.0", connectionId)],
            Blueprint = blueprint,
        };
        var imported = await service.ImportAgentSuiteAsync(owner, project.ProjectId, request, store, CancellationToken.None);

        var record = imported.Import;
        // All three agents are imported and none is runnable.
        Assert.Equal("agentweaver-team", record.SuiteName);
        Assert.Equal(["lead", "reviewer", "writer"], record.Agents.Select(item => item.NativeId).Order());
        Assert.All(record.Agents, item => Assert.False(item.Runnable));
        var lead = record.Agents.Single(item => item.NativeId == "lead");
        var writer = record.Agents.Single(item => item.NativeId == "writer");
        var reviewer = record.Agents.Single(item => item.NativeId == "reviewer");
        Assert.Equal(["reviewer", "writer"], lead.Invokes.Select(item => item.Agent).Order());
        // Blockers explain why each agent cannot run:
        // lead uses a capability-based model and a v2 policy that is recorded but not enforced;
        // writer uses an older policy version; reviewer has no policy, so no policy blocker.
        Assert.Contains(lead.Blockers, item => item.Code == "capability_model_unsupported");
        Assert.Contains(lead.Blockers, item => item.Code == "policy_not_enforced");
        Assert.DoesNotContain(lead.Blockers, item => item.Code == "policy_version_unsupported");
        Assert.Contains(writer.Blockers, item => item.Code == "policy_version_unsupported");
        Assert.Null(reviewer.PolicyDigest);
        Assert.DoesNotContain(reviewer.Blockers, item => item.Code == "policy_not_enforced");

        // The remote MCP provider is recorded but marked unsupported. Its tools use the
        // delegated connection, and the caller's connection ID is stored as given.
        var provider = Assert.Single(record.ToolProviders);
        Assert.Equal("unsupported", provider.Support);
        var remote = Assert.IsType<ProjectAgentSuiteRemoteMcp>(provider.Remote);
        Assert.Equal("streamable-http", remote.Transport);
        Assert.Equal("Authorization", Assert.Single(remote.Headers).Name);
        Assert.All(provider.Tools, tool => Assert.Equal("search-delegated-connection", tool.DelegatedAuthorization?.BindingRef));
        Assert.Equal(new ProjectAgentSuiteToolConnection("search@1.0.0", connectionId), Assert.Single(record.ToolConnections));
        // The blueprint takes its display name from the suite and keeps the caller's other fields.
        Assert.Equal("agentweaver-team", record.Blueprint.DisplayName);
        Assert.Equal("web-team", record.Blueprint.SourceBlueprintId);
        Assert.Equal(ProjectAgentSuiteBlueprint.RestrictedSandbox, record.Blueprint.SandboxProfile);
        Assert.Equal("writer", Assert.Single(record.Blueprint.SkillBindings).Role);

        var proposal = Assert.IsType<ProjectCastingProposal>(imported.Proposal);
        // One charter per agent, using the agent's display name.
        Assert.Equal(3, proposal.Draft.AgentCharters.Length);
        Assert.Equal("Team lead",
            proposal.Draft.AgentCharters.Single(item => item.AgentId == lead.EffectiveAgentId).Name);
        // The blueprint uses the inline apply path for workflows, sandbox and unresolved skill bindings.
        Assert.Equal([new BlueprintWorkflowReference("web-team", "default")], proposal.Draft.BlueprintWorkflowReferences.ToArray());
        Assert.Equal("default", proposal.Draft.DefaultWorkflowId);
        Assert.True(proposal.Draft.EgressNarrowing is { IsEmpty: true });
        Assert.Equal(
            [new BlueprintUnresolvedSkillBinding("writer", "docs", BlueprintUnresolvedSkillBinding.SkillNotInstalled)],
            imported.UnresolvedSkillBindings.ToArray());

        // Policies are read back from the stored blobs by effective agent ID.
        // lead has a v2 policy with an egress allow rule and a required capability.
        var leadPolicy = await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, lead.EffectiveAgentId, store, CancellationToken.None);
        Assert.Equal(record.SourceIdentity, leadPolicy.SourceIdentity);
        var v2 = Assert.IsType<ProjectAgentSuitePolicy>(leadPolicy.Policy);
        Assert.Equal("application/vnd.agentsuite.policy.v2+json", v2.MediaType);
        Assert.Equal("agentsuite.policy/v2", v2.Version);
        Assert.Equal(new ProjectAgentSuiteEgressRule("10.0.0.2", "TCP", 443), Assert.Single(v2.EgressAllow!.Value));
        Assert.Equal(["network/egress-allow/v1"], v2.RequiredCapabilities.ToArray());
        // writer has a v1 policy, which has no egress rules.
        var v1 = Assert.IsType<ProjectAgentSuitePolicy>((await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, writer.EffectiveAgentId, store, CancellationToken.None)).Policy);
        Assert.Equal("application/vnd.agentsuite.policy.v1+json", v1.MediaType);
        Assert.Null(v1.EgressAllow);
        // reviewer has no policy.
        Assert.Null((await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, reviewer.EffectiveAgentId, store, CancellationToken.None)).Policy);
        // A well-formed effective ID that was never imported is 404; a native ID alone is not accepted.
        var unknown = await Assert.ThrowsAsync<ProjectConfigException>(() => service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, "agentsuite:agentweaver-team:planner:000000000000", store, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, unknown.StatusCode);
        var native = await Assert.ThrowsAsync<ProjectConfigException>(() => service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, "lead", store, CancellationToken.None));
        Assert.Equal("invalid_agentsuite_agent_id", native.Code);

        // Replaying the identical request returns the same proposal.
        var replay = await service.ImportAgentSuiteAsync(owner, project.ProjectId, request, store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, replay.Proposal?.ProposalId);
        Assert.Equal(imported.UnresolvedSkillBindings.ToArray(), replay.UnresolvedSkillBindings.ToArray());
        // Importing the same suite without the tool connection and blueprint conflicts with
        // the stored record (409), and the stored record stays unchanged.
        var rebound = await InvalidAsync(AgentSuiteRequest(revision, "team"));
        Assert.Equal("agentsuite_import_conflict", rebound.Code);
        Assert.Equal(StatusCodes.Status409Conflict, rebound.StatusCode);
        AssertSameAgentSuiteRecord(record, await service.GetAgentSuiteImportAsync(
            owner, project.ProjectId, record.SourceIdentity, store, CancellationToken.None));
    }

    /// <summary>
    /// Importing a converted built-in archive with its projection gives the same proposal as inline 0.x apply.
    /// Effective agent IDs are mapped back to native IDs. A repeat import after confirmation changes nothing.
    /// </summary>
    [Fact]
    public async Task AgentSuiteImportOfConvertedBuiltInsMatchesInlineBlueprintApply()
    {
        // Arrange: a tenant admin, and platform defaults so built-ins with the restricted sandbox can apply.
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = CreateCastingProposalService(db);
        var store = new SkillTestObjectStore();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "agentsuite-blueprint-tenant-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, "agentsuite-blueprint-owner-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        await ConfigurePlatformDefaultsAsync(db, authorityStore, service, suffix, tenantId);

        foreach (var builtIn in ProjectBlueprintCatalog.Blueprints)
        {
            // Each built-in gets a fresh project so earlier imports can't affect it.
            var project = await service.CreateProjectAsync(owner, builtIn.Name, CancellationToken.None);
            var current = await service.GetProjectConfigurationAsync(owner, project.ProjectId, null, CancellationToken.None);
            // Only the default workflow is registered today, so both paths use it.
            var blueprint = builtIn with { Workflows = ["default"] };
            // Convert the built-in to an archive and send it with its projection, as a client would.
            var package = AgentSuiteBlueprintConverter.Convert(blueprint, AgentSuiteBlueprintConverterTests.TestOnlyInputs);
            var request = new ImportProjectAgentSuiteRequest
            {
                ExpectedConfigurationRevision = current.Revision,
                Format = ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
                Archive = Convert.ToBase64String(package.ContentArchive.AsSpan()),
                Blueprint = package.Blueprint,
            };

            // Act: import the archive through the service, and build the inline 0.x draft from the same starting
            // configuration for comparison.
            var imported = await service.ImportAgentSuiteAsync(
                owner, project.ProjectId, request, store, CancellationToken.None);
            var inline = ProjectBlueprintCatalog.CreateDraft(current.Configuration, null, blueprint);

            // Import creates a proposal. Map its effective agent IDs back to native IDs so the drafts compare directly.
            var proposal = Assert.IsType<ProjectCastingProposal>(imported.Proposal);
            var native = imported.Import.Agents.ToDictionary(
                agent => agent.EffectiveAgentId, agent => agent.NativeId, StringComparer.Ordinal);
            string Native(string agentId) => native.GetValueOrDefault(agentId, agentId);
            // Assert: charters, casting, workflows, egress and unresolved bindings match the inline apply.
            Assert.Equal(
                inline.Draft.AgentCharters.Select(item => (item.AgentId, item.Name, item.Role, item.Charter)),
                proposal.Draft.AgentCharters.Select(item => (Native(item.AgentId), item.Name, item.Role, item.Charter)));
            Assert.Equal(
                inline.Draft.Casting.Select(item => (item.AgentId, item.Role, item.Order)),
                proposal.Draft.Casting.Select(item => (Native(item.AgentId), item.Role, item.Order)));
            Assert.Equal(
                inline.Draft.BlueprintWorkflowReferences.ToArray(), proposal.Draft.BlueprintWorkflowReferences.ToArray());
            Assert.Equal(inline.Draft.DefaultWorkflowId, proposal.Draft.DefaultWorkflowId);
            Assert.Equal(inline.Draft.EgressNarrowing?.ToArray(), proposal.Draft.EgressNarrowing?.ToArray());
            Assert.Equal(inline.UnresolvedSkillBindings.ToArray(), imported.UnresolvedSkillBindings.ToArray());

            // Confirm the proposal, then import the same archive again at the new revision. Nothing changes, so
            // no new proposal is created, but the unresolved bindings are still reported.
            var confirmed = await service.ConfirmCastingProposalAsync(
                owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
            var repeat = await service.ImportAgentSuiteAsync(
                owner,
                project.ProjectId,
                request with { ExpectedConfigurationRevision = confirmed.ConfirmedConfigurationRevision!.Value },
                store,
                CancellationToken.None);
            Assert.Null(repeat.Proposal);
            Assert.Equal(inline.UnresolvedSkillBindings.ToArray(), repeat.UnresolvedSkillBindings.ToArray());
        }
    }

    private static void AssertSameAgentSuiteRecord(ProjectAgentSuiteImport expected, ProjectAgentSuiteImport actual) =>
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));

    private static Dictionary<string, byte[]> ReadAgentSuiteContentArchive(byte[] archive)
    {
        // Read regular files independently of the production content parser so the assertions inspect stored bytes.
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            using var content = new MemoryStream();
            (entry.DataStream ?? throw new InvalidOperationException("A regular tar entry has no data stream."))
                .CopyTo(content);
            files.Add(entry.Name, content.ToArray());
        }
        return files;
    }

    private static string AgentSuiteFixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSuite");

    private static ImportProjectAgentSuiteRequest AgentSuiteRequest(long revision, string fixture) => new()
    {
        ExpectedConfigurationRevision = revision,
        Format = ImportProjectAgentSuiteRequest.OciLayoutTarFormat,
        Archive = Convert.ToBase64String(AgentSuiteLayoutTar(Path.Combine(AgentSuiteFixtureRoot, fixture))),
    };

    private static ImportProjectAgentSuiteRequest AgentSuiteTeamWithRemoteDependency(long revision)
    {
        // Give the remote provider a harmless executable variant so AgentSuite compositions can pin it.
        var files = AgentSuiteArchives.FixtureContent("team");
        const string providerPath = "tool-providers/search/1.0.0/tool-provider.json";
        const string payloadRoot = "tool-providers/search/1.0.0/payload";
        var executable = Encoding.UTF8.GetBytes("#!/bin/sh\nexit 0\n");
        files[$"{payloadRoot}/bin/search"] = executable;
        var providerFiles = new JsonArray
        {
            new JsonObject
            {
                ["path"] = "bin/search",
                ["type"] = "file",
                ["mode"] = 493,
                ["uid"] = 0,
                ["gid"] = 0,
                ["size"] = executable.Length,
                ["digest"] = AgentSuiteArchives.Digest(executable),
                ["component"] = "search",
            },
            new JsonObject
            {
                ["path"] = "bin/search-link",
                ["type"] = "symlink",
                ["mode"] = 420,
                ["uid"] = 0,
                ["gid"] = 0,
                ["linkTarget"] = "search",
                ["component"] = "search",
            },
            new JsonObject
            {
                ["path"] = "empty",
                ["type"] = "directory",
                ["mode"] = 493,
                ["uid"] = 0,
                ["gid"] = 0,
                ["size"] = 0,
                ["component"] = "search",
            },
        };
        var symlink = new FixtureTarEntry($"{payloadRoot}/bin/search-link", [], '2')
        {
            LinkName = "search",
        };
        var emptyDirectory = FixtureTarEntry.Directory($"{payloadRoot}/empty");
        var variant = new JsonObject
        {
            ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" },
            ["variantDigest"] = string.Empty,
            ["installRoot"] = "/opt/search",
            ["relocatable"] = true,
            ["payloadRoot"] = payloadRoot,
            ["entrypoint"] = "/opt/search/bin/search",
            ["arguments"] = new JsonArray(),
            ["searchPath"] = new JsonArray(),
            ["environment"] = new JsonArray(),
            ["writablePaths"] = new JsonArray(),
            ["network"] = new JsonArray(),
            ["runtime"] = new JsonObject
            {
                ["abi"] = "static",
                ["cpuBaseline"] = "x86-64-v1",
                ["requiredBasePaths"] = new JsonArray(),
            },
            ["files"] = providerFiles,
            ["dependencies"] = new JsonArray(),
        };
        variant["variantDigest"] = AgentSuiteArchives.Digest(
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(variant.ToJsonString())));
        var provider = JsonNode.Parse(files[providerPath])?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite search provider is invalid.");
        provider["variants"] = new JsonArray(variant);
        files[providerPath] = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(provider.ToJsonString()));

        var catalog = JsonNode.Parse(files["tool-providers/catalog.json"])?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite provider catalog is invalid.");
        var providerReference = (catalog["toolProviders"]?.AsArray()
            ?? throw new InvalidOperationException("The AgentSuite provider catalog has no providers."))
            .OfType<JsonObject>()
            .Single(item => item["id"]?.GetValue<string>() == "search");
        var providerDescriptor = providerReference["descriptor"]?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite provider descriptor is invalid.");
        providerDescriptor["digest"] = AgentSuiteArchives.Digest(files[providerPath]);
        providerDescriptor["size"] = files[providerPath].Length;
        files["tool-providers/catalog.json"] =
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(catalog.ToJsonString()));
        AgentSuiteContentLayerTests.Rebind(files, "tool-providers/catalog.json");

        var lead = JsonNode.Parse(files["agents/lead.json"])?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite lead manifest is invalid.");
        // Keep the filesystem-only writer policy while excluding the source egress destination from this fixture.
        lead.Remove("policy");
        files.Remove("policies/lead.json");
        lead["toolProviders"] = new JsonArray(new JsonObject
        {
            ["id"] = "search",
            ["version"] = "1.0.0",
            ["executionMode"] = "shared-sandbox",
        });
        files["agents/lead.json"] = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(lead.ToJsonString()));
        AgentSuiteContentLayerTests.Rebind(files, "agents/lead.json");

        var providerDigest = AgentSuiteArchives.Digest(files[providerPath]);
        var composition = JsonNode.Parse(files["compositions/lead-linux-amd64.json"])?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite lead composition is invalid.");
        composition["toolProviders"] = new JsonArray(new JsonObject
        {
            ["id"] = "search",
            ["version"] = "1.0.0",
            ["manifestDigest"] = providerDigest,
            ["variantDigest"] = variant["variantDigest"]!.GetValue<string>(),
            ["executionMode"] = "shared-sandbox",
        });
        files["compositions/lead-linux-amd64.json"] =
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(composition.ToJsonString()));
        AgentSuiteContentLayerTests.Rebind(files, "compositions/lead-linux-amd64.json");

        var suite = JsonNode.Parse(files["agentsuite.json"])?.AsObject()
            ?? throw new InvalidOperationException("The AgentSuite suite manifest is invalid.");
        suite["capabilities"] = new JsonArray("bundled-stdio-mcp");
        files["agentsuite.json"] = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(suite.ToJsonString()));

        var entries = AgentSuiteArchives.Entries(files)
            .Select(entry => entry.Path == $"{payloadRoot}/bin/search" ? entry with { Mode = 0x1ED } : entry)
            .Append(emptyDirectory)
            .Append(symlink);
        var archive = AgentSuiteArchives.Gzip(AgentSuiteArchives.Tar(entries));
        return new ImportProjectAgentSuiteRequest
        {
            ExpectedConfigurationRevision = revision,
            Format = ImportProjectAgentSuiteRequest.ContentTarGzipFormat,
            Archive = Convert.ToBase64String(archive),
        };
    }

    /// <summary>Packs a committed OCI layout directory into a deterministic tar with sorted regular files.</summary>
    private static byte[] AgentSuiteLayoutTar(string directory, string? changedPath = null)
    {
        using var output = new MemoryStream();
        var changed = false;
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Select(item => Path.GetRelativePath(directory, item).Replace('\\', '/'))
                         .Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, path));
                if (string.Equals(path, changedPath, StringComparison.Ordinal))
                {
                    bytes[0] ^= 0x01;
                    changed = true;
                }
                // Fixed mode 0644 and epoch time keep the archive bytes the same on every machine.
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes, writable: false),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                });
            }
        }
        if (changedPath is not null && !changed)
            throw new InvalidOperationException($"The requested AgentSuite fixture path '{changedPath}' was not found.");
        return output.ToArray();
    }

    /// <summary>Configures platform runtime defaults, which a restricted sandbox's egress narrowing requires.</summary>
    private static async Task ConfigurePlatformDefaultsAsync(
        ProjectsConfigDbContext db, ProjectsConfigPrivilegedAuthorityStore authorityStore, ProjectsConfigService service,
        string suffix, string tenantId)
    {
        // Changing platform defaults needs a platform admin, so seed one for this test run.
        var admin = await SeedCallerAsync(
            db, authorityStore, "agentsuite-platform-admin-" + suffix, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        // Platform defaults are shared by all tests, so only write them if no test has done so yet (revision 0).
        var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        if (head.Revision == 0)
            await service.UpdatePlatformRuntimeDefaultsAsync(admin, head.Revision, PlatformDefaults(), CancellationToken.None);
    }
}
