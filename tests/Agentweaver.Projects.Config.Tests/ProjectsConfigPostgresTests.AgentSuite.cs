using System.Formats.Tar;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config.Tests.AgentSuite;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private const string AgentSuiteValidManifest =
        "sha256:499d25ae869a1dbfb9dc30b32bbb95e46bafffe32d4ba0ce5499eb8f8adb6967";

    [Fact]
    public async Task AgentSuiteImportStagesReviewOnlyAgentsAndIsIdempotent()
    {
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

        var proposal = Assert.IsType<ProjectCastingProposal>(first.Proposal);
        Assert.Equal(AgentSuiteValidManifest, first.Import.SourceIdentity);
        Assert.Equal(AgentSuiteValidManifest, first.Import.ManifestDigest);
        var agent = Assert.Single(first.Import.Agents);
        Assert.Equal($"agentsuite:{first.Import.SuiteName}:{agent.NativeId}:499d25ae869a", agent.EffectiveAgentId);
        Assert.False(agent.Runnable);
        Assert.Contains(agent.Blockers, item => item.Code == "runtime_unavailable");
        var charter = Assert.Single(proposal.Draft.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);
        Assert.Equal("Repeat the user's request in one short sentence.\n", charter.Charter);
        Assert.Contains(proposal.Draft.AgentCharters, item => item.AgentId == "existing-agent");
        Assert.Contains(proposal.Draft.Casting, item => item.AgentId == agent.EffectiveAgentId && item.Order == 1);
        Assert.Equal(seeded.Revision,
            (await service.GetProjectConfigurationAsync(owner, project.ProjectId, null, CancellationToken.None)).Revision);

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
        AssertSameAgentSuiteRecord(first.Import, await service.GetAgentSuiteImportAsync(
            owner, project.ProjectId, AgentSuiteValidManifest, store, CancellationToken.None));

        var replay = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(seeded.Revision, "valid"), store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, replay.Proposal?.ProposalId);
        var retagged = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(seeded.Revision, "valid-retagged"), store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, retagged.Proposal?.ProposalId);
        Assert.Single(await service.ListCastingProposalsAsync(owner, project.ProjectId, 50, CancellationToken.None));

        var confirmed = await service.ConfirmCastingProposalAsync(
            owner, project.ProjectId, proposal.ProposalId, proposal.DraftRevision, CancellationToken.None);
        var confirmedRevision = confirmed.ConfirmedConfigurationRevision
            ?? throw new InvalidOperationException("Confirmation returned no revision.");
        Assert.Contains(confirmed.Result!.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);
        var current = await service.GetProjectConfigurationAsync(owner, project.ProjectId, confirmedRevision, CancellationToken.None);
        Assert.Contains(current.Configuration.AgentCharters, item => item.AgentId == agent.EffectiveAgentId);

        var unchanged = await service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(confirmedRevision, "valid"), store, CancellationToken.None);
        Assert.Null(unchanged.Proposal);
        AssertSameAgentSuiteRecord(first.Import, unchanged.Import);

        var stale = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(confirmedRevision + 5, "valid"), store, CancellationToken.None));
        Assert.Equal("stale_project_configuration", stale.Code);
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task AgentSuiteImportRejectsInvalidSuitesAndNeverOverwritesAgents()
    {
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

        var rejected = await Assert.ThrowsAsync<AgentSuiteImportRejectedException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "config-not-empty"), store,
            CancellationToken.None));
        Assert.Equal("agentsuite_invalid", rejected.Code);
        Assert.Contains("AgentSuite config blob must be the exact bytes {}", rejected.Errors);

        var unauthorized = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            reader, project.ProjectId, AgentSuiteRequest(project.ConfigurationRevision, "valid"), store,
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, unauthorized.StatusCode);

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

        var edited = await SeedHistoricalConfigurationAsync(
            db, project, owner.ActorId, CastingConfiguration(effectiveId, "Edited charter"));
        var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
            owner, project.ProjectId, AgentSuiteRequest(edited.Revision, "valid"), store, CancellationToken.None));
        Assert.Equal("agentsuite_identity_conflict", conflict.Code);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
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

        async Task<ProjectConfigException> InvalidAsync(ImportProjectAgentSuiteRequest request) =>
            await Assert.ThrowsAsync<ProjectConfigException>(() => service.ImportAgentSuiteAsync(
                owner, project.ProjectId, request, store, CancellationToken.None));
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
        var badBlueprint = await InvalidAsync(AgentSuiteRequest(revision, "team") with
        {
            Blueprint = new ProjectAgentSuiteBlueprint { SkillBindings = [new("planner", ["docs"])] },
        });
        Assert.Equal("invalid_agentsuite_blueprint", badBlueprint.Code);
        // The projection must also pass the 0.x blueprint rules; only the default workflow is registered today,
        // so naming another workflow is rejected.
        var unavailableWorkflow = await InvalidAsync(AgentSuiteRequest(revision, "team") with
        {
            Blueprint = new ProjectAgentSuiteBlueprint { Workflows = ["default", "feature-development"] },
        });
        Assert.Equal("invalid_blueprint", unavailableWorkflow.Code);
        Assert.Contains("Unknown workflow 'feature-development'.", unavailableWorkflow.Message, StringComparison.Ordinal);

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
        Assert.Equal("agentweaver-team", record.SuiteName);
        Assert.Equal(["lead", "reviewer", "writer"], record.Agents.Select(item => item.NativeId).Order());
        Assert.All(record.Agents, item => Assert.False(item.Runnable));
        var lead = record.Agents.Single(item => item.NativeId == "lead");
        var writer = record.Agents.Single(item => item.NativeId == "writer");
        var reviewer = record.Agents.Single(item => item.NativeId == "reviewer");
        Assert.Equal(["reviewer", "writer"], lead.Invokes.Select(item => item.Agent).Order());
        Assert.Contains(lead.Blockers, item => item.Code == "capability_model_unsupported");
        Assert.Contains(lead.Blockers, item => item.Code == "policy_not_enforced");
        Assert.DoesNotContain(lead.Blockers, item => item.Code == "policy_version_unsupported");
        Assert.Contains(writer.Blockers, item => item.Code == "policy_version_unsupported");
        Assert.Null(reviewer.PolicyDigest);
        Assert.DoesNotContain(reviewer.Blockers, item => item.Code == "policy_not_enforced");

        var provider = Assert.Single(record.ToolProviders);
        Assert.Equal("unsupported", provider.Support);
        var remote = Assert.IsType<ProjectAgentSuiteRemoteMcp>(provider.Remote);
        Assert.Equal("streamable-http", remote.Transport);
        Assert.Equal("Authorization", Assert.Single(remote.Headers).Name);
        Assert.All(provider.Tools, tool => Assert.Equal("search-delegated-connection", tool.DelegatedAuthorization?.BindingRef));
        Assert.Equal(new ProjectAgentSuiteToolConnection("search@1.0.0", connectionId), Assert.Single(record.ToolConnections));
        Assert.Equal("agentweaver-team", record.Blueprint.DisplayName);
        Assert.Equal("web-team", record.Blueprint.SourceBlueprintId);
        Assert.Equal(ProjectAgentSuiteBlueprint.RestrictedSandbox, record.Blueprint.SandboxProfile);
        Assert.Equal("writer", Assert.Single(record.Blueprint.SkillBindings).Role);

        var proposal = Assert.IsType<ProjectCastingProposal>(imported.Proposal);
        Assert.Equal(3, proposal.Draft.AgentCharters.Length);
        Assert.Equal("Team lead",
            proposal.Draft.AgentCharters.Single(item => item.AgentId == lead.EffectiveAgentId).Name);
        // The blueprint goes through the inline apply path: workflow references, default workflow, sandbox, skills.
        Assert.Equal([new BlueprintWorkflowReference("web-team", "default")], proposal.Draft.BlueprintWorkflowReferences.ToArray());
        Assert.Equal("default", proposal.Draft.DefaultWorkflowId);
        Assert.True(proposal.Draft.EgressNarrowing is { IsEmpty: true });
        // The "writer" role's "docs" skill isn't installed in this project, so the binding is reported, not applied.
        Assert.Equal(
            [new BlueprintUnresolvedSkillBinding("writer", "docs", BlueprintUnresolvedSkillBinding.SkillNotInstalled)],
            imported.UnresolvedSkillBindings.ToArray());

        var leadPolicy = await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, lead.EffectiveAgentId, store, CancellationToken.None);
        Assert.Equal(record.SourceIdentity, leadPolicy.SourceIdentity);
        var v2 = Assert.IsType<ProjectAgentSuitePolicy>(leadPolicy.Policy);
        Assert.Equal("application/vnd.agentsuite.policy.v2+json", v2.MediaType);
        Assert.Equal("agentsuite.policy/v2", v2.Version);
        Assert.Equal(new ProjectAgentSuiteEgressRule("10.0.0.2", "TCP", 443), Assert.Single(v2.EgressAllow!.Value));
        Assert.Equal(["network/egress-allow/v1"], v2.RequiredCapabilities.ToArray());
        var v1 = Assert.IsType<ProjectAgentSuitePolicy>((await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, writer.EffectiveAgentId, store, CancellationToken.None)).Policy);
        Assert.Equal("application/vnd.agentsuite.policy.v1+json", v1.MediaType);
        Assert.Null(v1.EgressAllow);
        Assert.Null((await service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, reviewer.EffectiveAgentId, store, CancellationToken.None)).Policy);
        var unknown = await Assert.ThrowsAsync<ProjectConfigException>(() => service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, "agentsuite:agentweaver-team:planner:000000000000", store, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, unknown.StatusCode);
        var native = await Assert.ThrowsAsync<ProjectConfigException>(() => service.GetAgentSuiteAgentPolicyAsync(
            owner, project.ProjectId, "lead", store, CancellationToken.None));
        Assert.Equal("invalid_agentsuite_agent_id", native.Code);

        var replay = await service.ImportAgentSuiteAsync(owner, project.ProjectId, request, store, CancellationToken.None);
        Assert.Equal(proposal.ProposalId, replay.Proposal?.ProposalId);
        // A replayed import returns the same unresolved bindings as the first import.
        Assert.Equal(imported.UnresolvedSkillBindings.ToArray(), replay.UnresolvedSkillBindings.ToArray());
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

    private static string AgentSuiteFixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSuite");

    private static ImportProjectAgentSuiteRequest AgentSuiteRequest(long revision, string fixture) => new()
    {
        ExpectedConfigurationRevision = revision,
        Format = ImportProjectAgentSuiteRequest.OciLayoutTarFormat,
        Archive = Convert.ToBase64String(AgentSuiteLayoutTar(Path.Combine(AgentSuiteFixtureRoot, fixture))),
    };

    /// <summary>Packs a committed OCI layout directory into a deterministic tar with sorted regular files.</summary>
    private static byte[] AgentSuiteLayoutTar(string directory)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Select(item => Path.GetRelativePath(directory, item).Replace('\\', '/'))
                         .Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, path));
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes, writable: false),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                });
            }
        }
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
