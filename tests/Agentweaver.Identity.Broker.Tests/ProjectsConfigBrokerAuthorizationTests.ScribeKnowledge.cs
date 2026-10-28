extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task ScribeKnowledgeCommitLostAcknowledgmentReplaysSamePendingMemoryAfterRestart()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        await using var database = await KnowledgeTestDatabase.CreateAsync(_connectionString);
        var catalog = CreateKnowledgeMemoryCatalog(database);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate),
            providerCatalog: catalog, additionalAudiences: ["https://api.test/"]);
        var token = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/", "scribe-knowledge-owner",
            additionalScopes: "projects.admin projects.orchestrator", roles: []);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var subject = SingleClaim(jwt.Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, token, TenantId, "Scribe pending Knowledge");
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);
        var selection = await AcceptKnowledgeRunSelectionAsync(projects, database, project, token);
        var actor = new CoordinationActor(jwt.Issuer, subject);
        var identity = new SessionIdentity(project.ProjectId, selection.RunId, "scribe-root");
        var intent = CreatePersistedScribeIntent(identity, actor);
        var persistedJson = JsonSerializer.Serialize(intent);
        var credential = new SecretCredential(token, DateTimeOffset.UtcNow.AddMinutes(5));
        var authorization = new RuntimeActorAuthorization(credential, TenantId);
        var ownerAddress = new Uri("https://knowledge.test/");

        await using (var app = await CreateKnowledgeTestAppAsync(
                         database, catalog, projects.Client, new X509SecurityKey(certificate),
                         additionalAudiences: ["https://api.test/"]))
        using (var client = new HttpClient(new ScribeLostAcknowledgmentHandler(app.GetTestServer().CreateHandler())))
        {
            var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
                new MafScribeKnowledgeClient(client, ownerAddress).WriteAsync(
                    authorization, actor, intent, CancellationToken.None));
            Assert.Equal("maf_execution_scribe_owner_unavailable", failure.Code);
        }
        var committed = Assert.Single((await database.Provider.SearchAsync(
            new KnowledgeRecordQuery(project.ProjectId, intent.AgentId, IncludeInactive: true),
            CancellationToken.None)).Items);
        Assert.Equal(KnowledgeTrustState.Pending, committed.TrustState);
        Assert.Equal(KnowledgeRecordState.Active, committed.State);

        await using var restarted = await CreateKnowledgeTestAppAsync(
            database, catalog, projects.Client, new X509SecurityKey(certificate),
            additionalAudiences: ["https://api.test/"]);
        using var restartedClient = new HttpClient(restarted.GetTestServer().CreateHandler());
        var writer = new MafScribeKnowledgeClient(restartedClient, ownerAddress);
        var recovered = JsonSerializer.Deserialize<MafExecutionScribeIntent>(persistedJson)!;
        MafScribeContract.RequireReplay(recovered, intent, actor);
        var receipt = await writer.WriteAsync(authorization, actor, recovered, CancellationToken.None);
        Assert.Equal(new MafScribeTerminalReceipt(
            intent.AssociationId, committed.RecordId, committed.Revision, committed.RevisionId), receipt);
        Assert.Equal(receipt, await writer.WriteAsync(authorization, actor, recovered, CancellationToken.None));
        Assert.DoesNotContain("full child result", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);

        var changed = intent with { Content = intent.Content.Replace("full child result", "changed child result") };
        Assert.Throws<CoordinationException>(() => MafScribeContract.RequireReplay(recovered, changed, actor));
        var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
            writer.WriteAsync(authorization, actor, changed, CancellationToken.None));
        Assert.Equal("maf_execution_scribe_intent_conflict", conflict.Code);
        var changedActor = actor with { Subject = "another-actor" };
        var deniedActor = await Assert.ThrowsAsync<CoordinationException>(() =>
            writer.WriteAsync(authorization, changedActor, recovered, CancellationToken.None));
        Assert.Equal("maf_execution_scribe_actor_changed", deniedActor.Code);

        var boundToken = CreateSignedAccessToken(subject,
            new Claim("tenant_id", TenantId), new Claim("project_id", identity.ProjectId),
            new Claim("run_id", identity.RunId));
        var boundAuthorization = new RuntimeActorAuthorization(
            new SecretCredential(boundToken, DateTimeOffset.UtcNow.AddMinutes(5)), TenantId);
        foreach (var wrongIdentity in new[]
                 {
                     new SessionIdentity("wrong-project", identity.RunId, identity.SessionId),
                     new SessionIdentity(identity.ProjectId, "wrong-run", identity.SessionId)
                 })
        {
            var deniedScope = await Assert.ThrowsAsync<CoordinationException>(() =>
                writer.WriteAsync(boundAuthorization, actor,
                    CreatePersistedScribeIntent(wrongIdentity, actor), CancellationToken.None));
            Assert.Equal("maf_execution_scribe_owner_unavailable", deniedScope.Code);
            Assert.Equal(502, deniedScope.StatusCode);
        }
        var backup = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, "scribe-backup-owner", TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, backup.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(projects.PrivilegedFixtureDataSource), TimeProvider.System)
            .RevokeMembershipAsync(membership.MembershipId, 1, "local-test-fixture");
        var stale = await Assert.ThrowsAsync<CoordinationException>(() =>
            writer.WriteAsync(authorization, actor, recovered, CancellationToken.None));
        Assert.Equal("maf_execution_scribe_owner_denied", stale.Code);
        var records = await database.Provider.SearchAsync(
            new KnowledgeRecordQuery(project.ProjectId, intent.AgentId, IncludeInactive: true),
            CancellationToken.None);
        Assert.Equal(committed, Assert.Single(records.Items));
        var revisions = await database.Provider.ReadRevisionsAsync(
            project.ProjectId, committed.RecordId, 1, 50, CancellationToken.None);
        Assert.Single(revisions.Items);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var counts = new NpgsqlCommand($"""
                         SELECT
                             (SELECT count(*) FROM "{database.Options.Schema}".knowledge_records),
                             (SELECT count(*) FROM "{database.Options.Schema}".knowledge_revisions),
                             (SELECT count(*) FROM "{database.Options.Schema}".knowledge_write_idempotency)
                         """, connection))
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
        }
        output.WriteLine($"Real Knowledge PostgreSQL commit/restart replay: {receipt.RecordId}, " +
            $"revision {receipt.Revision}, revisionId {receipt.RevisionId}; one pending-trust Memory.");
    }

    private static MafExecutionScribeIntent CreatePersistedScribeIntent(
        SessionIdentity identity, CoordinationActor actor)
    {
        const string planId = "scribe-plan";
        const string associationId = "accepted-child";
        var content = JsonSerializer.Serialize(new
        {
            projectId = identity.ProjectId,
            runId = identity.RunId,
            rootSessionId = identity.SessionId,
            workPlanId = planId,
            associationId,
            agentId = "actual-producer",
            childSessionId = MafExecutionIds.CreateChildSessionId(identity, planId, associationId),
            result = "full child result\nincluding all output and provenance"
        });
        var key = "scribe-" + Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { identity.ProjectId, identity.RunId, planId, associationId })));
        var intent = new MafExecutionScribeIntent(
            identity, planId, "scribe", associationId, "actual-producer", actor, key, content);
        MafScribeContract.ValidateIntent(intent);
        return intent;
    }

    private sealed class ScribeLostAcknowledgmentHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var committed = await base.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
            throw new HttpRequestException("Acknowledgment lost after the real Knowledge commit.");
        }
    }
}
