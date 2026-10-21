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
        // One charter per agent, using the agent's display name; no workflows are added.
        Assert.Equal(3, proposal.Draft.AgentCharters.Length);
        Assert.Equal("Team lead",
            proposal.Draft.AgentCharters.Single(item => item.AgentId == lead.EffectiveAgentId).Name);
        Assert.Empty(proposal.Draft.BlueprintWorkflowReferences);

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
        // Importing the same suite without the tool connection and blueprint conflicts with
        // the stored record (409), and the stored record stays unchanged.
        var rebound = await InvalidAsync(AgentSuiteRequest(revision, "team"));
        Assert.Equal("agentsuite_import_conflict", rebound.Code);
        Assert.Equal(StatusCodes.Status409Conflict, rebound.StatusCode);
        AssertSameAgentSuiteRecord(record, await service.GetAgentSuiteImportAsync(
            owner, project.ProjectId, record.SourceIdentity, store, CancellationToken.None));
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
                // Fixed mode 0644 and epoch time keep the archive bytes the same on every machine.
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
}
