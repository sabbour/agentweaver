using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task RefreshAcceptedRunSkillsEndToEndCoversEligibilityDriftAndReplay()
    {
        await using var db = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(db);
        try
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
            var project = await CreateSkillInventoryProjectAsync(db, authorityStore);
            var objects = new SkillTestObjectStore();
            var objectStore = new SkillContentObjectStore(objects);
            var configurations = new ProjectsConfigService(
                db, CreateProviderCatalog(), TimeProvider.System, skillContentObjects: objectStore);
            var configured = await configurations.UpdateProjectConfigurationAsync(
                project.Owner,
                project.Project.ProjectId,
                project.Project.ConfigurationRevision,
                ProjectSettings() with
                {
                    AgentCharters =
                    [
                        new ProjectAgentCharter(
                            "agent-one", "Refresh Agent", "assistant", "Refresh test agent."),
                    ],
                    Casting = [new ProjectAgentCast("agent-one", "assistant", 0)],
                },
                CancellationToken.None);
            var skills = new SkillContentService(
                db, objectStore, configurations, TimeProvider.System);
            skills.EvaluateSkillsPolicyOverride = (_, _, _, _, _, _) => Task.CompletedTask;

            // Pre-cutover LegacyThirdParty Skill (order 0) stays exempt and reports refreshEligible: false.
            var legacyCandidate = SkillCandidate(
                "A sufficiently long pre-cutover legacy Skill instruction.",
                "# Legacy\n");
            var legacyImported = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "legacy-import-" + project.Suffix,
                    skills.Preview(legacyCandidate).ContentDigest,
                    "legacy-skill",
                    null,
                    null,
                    legacyCandidate),
                CancellationToken.None);

            await SeedSkillsPolicyAsync(
                db,
                project.Owner,
                """{"schemaVersion":1,"minimumScore":0,"requireCompletedEvidence":true}""");
            var fence = await db.SkillAdmissionFences.SingleAsync(item => item.Id == 1);
            fence.State = ProjectSkillAdmissionFenceState.Active;
            fence.ReleaseInventoryRevision = "refresh-inventory-" + project.Suffix;
            fence.PlatformSkillsPolicySha256 = "0" + new string('1', 63);
            await db.SaveChangesAsync();

            var candidateA = SkillCandidate(
                "A sufficiently long approved instruction for operator refresh A.",
                "# Refresh A\n");
            var importedA = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "refresh-import-a-" + project.Suffix,
                    skills.Preview(candidateA).ContentDigest,
                    "refresh-skill-a",
                    null,
                    new SkillContentSourceSelection(
                        "caller-source", "caller-revision", "main", new string('c', 40), "skills/a"),
                    candidateA),
                CancellationToken.None);

            var candidateB = SkillCandidate(
                "A sufficiently long approved instruction for operator refresh B.",
                "# Refresh B\n");
            var importedB = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "refresh-import-b-" + project.Suffix,
                    skills.Preview(candidateB).ContentDigest,
                    "refresh-skill-b",
                    null,
                    new SkillContentSourceSelection(
                        "caller-source", "caller-revision", "main", new string('d', 40), "skills/b"),
                    candidateB),
                CancellationToken.None);

            var assignedConfiguration = configured.Configuration with
            {
                Skills =
                [
                    new SkillCatalogSetting(legacyImported.SkillId, true, 0)
                    {
                        Revision = legacyImported.Revision,
                        ContentDigest = legacyImported.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                    new SkillCatalogSetting(importedA.SkillId, true, 1)
                    {
                        Revision = importedA.Revision,
                        ContentDigest = importedA.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                    new SkillCatalogSetting(importedB.SkillId, true, 2)
                    {
                        Revision = importedB.Revision,
                        ContentDigest = importedB.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                ],
            };
            var assignedRevision = checked(configured.Revision + 1);
            db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = project.Project.ProjectId,
                Revision = assignedRevision,
                ConfigurationJson = ProjectsConfigService.SerializeProjectConfiguration(assignedConfiguration),
                UpdatedByActorId = project.Owner.ActorId,
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            var projectRecord = await db.Projects.SingleAsync(item => item.ProjectId == project.Project.ProjectId);
            projectRecord.ConfigurationRevision = assignedRevision;
            var runId = "refresh-run-" + project.Suffix;
            var selection = new EffectiveRunSelection(
                project.Project.ProjectId,
                runId,
                project.Project.Revision,
                assignedRevision,
                project.PlatformDefaultsRevision,
                "refresh-context",
                new ModelSelectionSettings("refresh-model"),
                [],
                [],
                new CopilotRunLimits(),
                assignedConfiguration,
                [],
                null,
                []);
            db.RunSelections.Add(new ProjectRunSelectionRecord
            {
                RunId = runId,
                ProjectId = project.Project.ProjectId,
                ProjectRevision = project.Project.Revision,
                ProjectConfigurationRevision = assignedRevision,
                PlatformRuntimeRevision = project.PlatformDefaultsRevision,
                ContextRevision = "refresh-context",
                RequestFingerprint = new string('a', 64),
                SnapshotJson = ProjectsConfigService.SerializeRunSelection(selection),
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            await db.SaveChangesAsync();

            await using var host = await StartRefreshEndpointHostAsync(project.Owner.Issuer, objects);
            using var client = host.GetTestClient();
            var inputsPath = $"/api/projects/{project.Project.ProjectId}/runs/{runId}/agents/agent-one/skills/refresh-inputs";
            var refreshPath = $"/api/projects/{project.Project.ProjectId}/runs/{runId}/agents/agent-one/skills/refresh";

            // Owner HTTP validation: missing aw_actor_class (403), 415, unknown POST field (400), >64 KiB (413).
            using (var missingHuman = new HttpRequestMessage(HttpMethod.Get, inputsPath))
            {
                missingHuman.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                missingHuman.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var res = await client.SendAsync(missingHuman);
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
                Assert.True(res.Headers.CacheControl?.NoStore);
            }
            using (var unsupportedMedia = new HttpRequestMessage(HttpMethod.Post, refreshPath)
            {
                Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
            })
            {
                unsupportedMedia.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                unsupportedMedia.Headers.Add("X-Test-Actor-Class", "human");
                unsupportedMedia.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var res = await client.SendAsync(unsupportedMedia);
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
            }
            using (var unknownField = new HttpRequestMessage(HttpMethod.Post, refreshPath)
            {
                Content = new StringContent("""{"extraField":1}""", Encoding.UTF8, "application/json"),
            })
            {
                unknownField.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                unknownField.Headers.Add("X-Test-Actor-Class", "human");
                unknownField.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var res = await client.SendAsync(unknownField);
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            }
            using (var oversized = new HttpRequestMessage(HttpMethod.Post, refreshPath)
            {
                Content = new StringContent(new string('a', 65 * 1024), Encoding.UTF8, "application/json"),
            })
            {
                oversized.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                oversized.Headers.Add("X-Test-Actor-Class", "human");
                oversized.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var res = await client.SendAsync(oversized);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
            }

            // Advance Platform Policy head; GET must report ordered eligibility (legacy false, A & B true).
            await SeedSkillsPolicyAsync(
                db,
                project.Owner,
                """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
            using var getInputs = new HttpRequestMessage(HttpMethod.Get, inputsPath);
            getInputs.Headers.Add("X-Test-Sub", project.Owner.ActorId);
            getInputs.Headers.Add("X-Test-Actor-Class", "human");
            getInputs.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
            using var getInputsResponse = await client.SendAsync(getInputs);
            Assert.Equal(HttpStatusCode.OK, getInputsResponse.StatusCode);
            Assert.True(getInputsResponse.Headers.CacheControl?.NoStore);
            var inputsPayload = await getInputsResponse.Content.ReadFromJsonAsync<AcceptedRunSkillRefreshInputsResponse>();
            Assert.NotNull(inputsPayload);
            Assert.Equal(3, inputsPayload!.Assignments.Length);
            Assert.Equal("legacy-skill", inputsPayload.Assignments[0].SkillId);
            Assert.False(inputsPayload.Assignments[0].RefreshEligible);
            Assert.Equal("refresh-skill-a", inputsPayload.Assignments[1].SkillId);
            Assert.True(inputsPayload.Assignments[1].RefreshEligible);
            Assert.Equal("refresh-skill-b", inputsPayload.Assignments[2].SkillId);
            Assert.True(inputsPayload.Assignments[2].RefreshEligible);

            // Stale pin returns 409.
            var staleBody = JsonSerializer.Serialize(new
            {
                skillId = importedA.SkillId,
                revision = importedA.Revision,
                contentDigest = importedA.ContentDigest,
                expectedPlatformPolicy = new
                {
                    policyId = inputsPayload.PlatformPolicy.PolicyId,
                    revision = inputsPayload.PlatformPolicy.Revision - 1,
                    contentSha256 = inputsPayload.PlatformPolicy.ContentSha256,
                },
                expectedProjectPolicy = (object?)null,
                idempotencyKey = "stale-" + project.Suffix,
                reason = "stale check",
                correlationId = "corr-stale",
            });
            using (var stalePost = new HttpRequestMessage(HttpMethod.Post, refreshPath)
            {
                Content = new StringContent(staleBody, Encoding.UTF8, "application/json"),
            })
            {
                stalePost.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                stalePost.Headers.Add("X-Test-Actor-Class", "human");
                stalePost.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var staleResponse = await client.SendAsync(stalePost);
                Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
            }

            // Refresh both eligible Skills and verify replay on Skill A.
            foreach (var eligible in inputsPayload.Assignments.Where(item => item.RefreshEligible))
            {
                var refreshBody = JsonSerializer.Serialize(new
                {
                    skillId = eligible.SkillId,
                    revision = eligible.Revision,
                    contentDigest = eligible.ContentDigest,
                    expectedPlatformPolicy = new
                    {
                        policyId = inputsPayload.PlatformPolicy.PolicyId,
                        revision = inputsPayload.PlatformPolicy.Revision,
                        contentSha256 = inputsPayload.PlatformPolicy.ContentSha256,
                    },
                    expectedProjectPolicy = inputsPayload.ProjectPolicy,
                    idempotencyKey = $"recover-{eligible.SkillId}-{project.Suffix}",
                    reason = "recover multi-skill run",
                    correlationId = "corr-" + eligible.SkillId,
                });
                var attempts = eligible.SkillId == "refresh-skill-a" ? 2 : 1;
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    using var postRefresh = new HttpRequestMessage(HttpMethod.Post, refreshPath)
                    {
                        Content = new StringContent(refreshBody, Encoding.UTF8, "application/json"),
                    };
                    postRefresh.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                    postRefresh.Headers.Add("X-Test-Actor-Class", "human");
                    postRefresh.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                    using var postResponse = await client.SendAsync(postRefresh);
                    Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
                    Assert.True(postResponse.Headers.CacheControl?.NoStore);
                    var receipt = await postResponse.Content.ReadFromJsonAsync<AcceptedRunSkillRefreshReceipt>();
                    Assert.NotNull(receipt);
                    Assert.Equal(attempt == 1, receipt!.Replayed);
                }
            }

            var orchestratorActor = await SeedCallerAsync(
                db,
                authorityStore,
                "orchestrator-" + project.Suffix,
                project.TenantId,
                ["api.read", "projects.orchestrator"],
                ProjectAuthorityResourceType.Project,
                project.Project.ProjectId,
                ProjectAuthorityRole.Orchestrator);
            var orchestrator = await ResolveCallerAsync(
                db,
                orchestratorActor.ActorId,
                project.TenantId,
                ["api.read", "projects.orchestrator"],
                project.Project.ProjectId,
                runId);
            var delivered = await skills.ReadAcceptedRunSkillsAsync(
                orchestrator,
                project.Project.ProjectId,
                runId,
                "agent-one",
                CancellationToken.None);
            Assert.Equal(
                ["legacy-skill", "refresh-skill-a", "refresh-skill-b"],
                delivered.Select(item => item.SkillId).ToArray());
        }
        finally
        {
            await ResetSkillAdmissionCutoverAsync(db);
        }
    }

    [Fact]
    public async Task ReadRefreshInputsCoversNegativeAuthorizationAndEligibilityCases()
    {
        await using var db = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(db);
        try
        {
            var setup = await CreateSkillRefreshSetupAsync(db);
            var service = CreateSkillService(db, setup.Objects);
            await using var host = await StartRefreshEndpointHostAsync(setup.Project.Owner.Issuer, setup.Objects);
            using var client = host.GetTestClient();
            var inputsPath =
                $"/api/projects/{setup.Request.ProjectId}/runs/{setup.Request.RunId}/agents/{setup.Request.AgentId}/skills/refresh-inputs";

            // Bound project token and purpose token must return 403 even with aw_actor_class = "human".
            foreach (var (headerName, headerValue) in new[]
            {
                ("X-Test-Project-Id", setup.Request.ProjectId),
                ("X-Test-Purpose", "run-binding"),
            })
            {
                using var bound = new HttpRequestMessage(HttpMethod.Get, inputsPath);
                bound.Headers.Add("X-Test-Sub", setup.Project.Owner.ActorId);
                bound.Headers.Add("X-Test-Actor-Class", "human");
                bound.Headers.Add("X-Agentweaver-Tenant", setup.Project.Owner.TenantId);
                bound.Headers.Add(headerName, headerValue);
                using var res = await client.SendAsync(bound);
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            }

            // Foreign tenant caller must receive 404.
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
            var foreign = await SeedCallerAsync(
                db,
                authority,
                "foreign-inputs-" + setup.Project.Suffix,
                "foreign-tenant-" + setup.Project.Suffix,
                ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Tenant,
                "foreign-tenant-" + setup.Project.Suffix,
                ProjectAuthorityRole.TenantAdmin);
            using (var foreignReq = new HttpRequestMessage(HttpMethod.Get, inputsPath))
            {
                foreignReq.Headers.Add("X-Test-Sub", foreign.ActorId);
                foreignReq.Headers.Add("X-Test-Actor-Class", "human");
                foreignReq.Headers.Add("X-Agentweaver-Tenant", foreign.TenantId);
                using var res = await client.SendAsync(foreignReq);
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            }

            // Missing agent must receive 404.
            var missingAgentPath =
                $"/api/projects/{setup.Request.ProjectId}/runs/{setup.Request.RunId}/agents/missing-agent/skills/refresh-inputs";
            using (var missingAgentReq = new HttpRequestMessage(HttpMethod.Get, missingAgentPath))
            {
                missingAgentReq.Headers.Add("X-Test-Sub", setup.Project.Owner.ActorId);
                missingAgentReq.Headers.Add("X-Test-Actor-Class", "human");
                missingAgentReq.Headers.Add("X-Agentweaver-Tenant", setup.Project.Owner.TenantId);
                using var res = await client.SendAsync(missingAgentReq);
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            }

            // Inactive fence must return 503.
            var fence = await db.SkillAdmissionFences.SingleAsync(item => item.Id == 1);
            fence.State = ProjectSkillAdmissionFenceState.Draining;
            await db.SaveChangesAsync();
            using (var inactiveReq = new HttpRequestMessage(HttpMethod.Get, inputsPath))
            {
                inactiveReq.Headers.Add("X-Test-Sub", setup.Project.Owner.ActorId);
                inactiveReq.Headers.Add("X-Test-Actor-Class", "human");
                inactiveReq.Headers.Add("X-Agentweaver-Tenant", setup.Project.Owner.TenantId);
                using var res = await client.SendAsync(inactiveReq);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            }
            fence.State = ProjectSkillAdmissionFenceState.Active;
            await db.SaveChangesAsync();

            // Revoked Skill reports refreshEligible: false.
            await service.RevokeAsync(
                setup.Project.Owner,
                setup.Request.ProjectId,
                setup.Request.SkillId,
                setup.Request.Revision,
                "revoked in test",
                CancellationToken.None);
            using (var revokedInputsReq = new HttpRequestMessage(HttpMethod.Get, inputsPath))
            {
                revokedInputsReq.Headers.Add("X-Test-Sub", setup.Project.Owner.ActorId);
                revokedInputsReq.Headers.Add("X-Test-Actor-Class", "human");
                revokedInputsReq.Headers.Add("X-Agentweaver-Tenant", setup.Project.Owner.TenantId);
                using var res = await client.SendAsync(revokedInputsReq);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var payload = await res.Content.ReadFromJsonAsync<AcceptedRunSkillRefreshInputsResponse>();
                Assert.False(Assert.Single(payload!.Assignments).RefreshEligible);
            }

            // Missing admission row reports refreshEligible: false (insert a ThirdParty revision with no admission row).
            var noAdmissionCandidate = SkillCandidate(
                "A sufficiently long Skill instruction without an admission row.",
                "# No Admission\n");
            var noAdmissionDigest = service.Preview(noAdmissionCandidate).ContentDigest;
            db.SkillContentRevisions.Add(new ProjectSkillContentRevisionRecord
            {
                ProjectId = setup.Request.ProjectId,
                SkillId = "no-admission-skill",
                Revision = 1,
                Name = "no-admission-skill",
                Description = "A sufficiently long Skill instruction without an admission row.",
                ContentDigest = noAdmissionDigest,
                ObjectKey = "unused-object-key",
                ResourceCount = 0,
                TotalBytes = noAdmissionCandidate.SkillMarkdown.Length,
                CreatedByActorId = setup.Project.Owner.ActorId,
                CreatedAt = TimeProvider.System.GetUtcNow(),
                AdmissionClassification = ProjectSkillAdmissionClassification.ThirdParty,
                SourceAuthorityKind = ProjectSkillSourceAuthorityKind.CallerClaimed,
            });
            var noAdmissionConfig = ProjectSettings() with
            {
                AgentCharters = [new ProjectAgentCharter("agent-one", "Refresh Agent", "assistant", "Refresh test agent.")],
                Casting = [new ProjectAgentCast("agent-one", "assistant", 0)],
                Skills =
                [
                    new SkillCatalogSetting("no-admission-skill", true, 0)
                    {
                        Revision = 1,
                        ContentDigest = noAdmissionDigest,
                        AgentIds = ["agent-one"],
                    },
                ],
            };
            var noAdmissionConfigRev = 999L;
            db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = setup.Request.ProjectId,
                Revision = noAdmissionConfigRev,
                ConfigurationJson = ProjectsConfigService.SerializeProjectConfiguration(noAdmissionConfig),
                UpdatedByActorId = setup.Project.Owner.ActorId,
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            var noAdmissionRunId = "no-admission-run-" + setup.Project.Suffix;
            var noAdmissionSelection = new EffectiveRunSelection(
                setup.Request.ProjectId,
                noAdmissionRunId,
                setup.Project.Project.Revision,
                noAdmissionConfigRev,
                setup.Project.PlatformDefaultsRevision,
                "refresh-context",
                new ModelSelectionSettings("refresh-model"),
                [],
                [],
                new CopilotRunLimits(),
                noAdmissionConfig,
                [],
                null,
                []);
            db.RunSelections.Add(new ProjectRunSelectionRecord
            {
                RunId = noAdmissionRunId,
                ProjectId = setup.Request.ProjectId,
                ProjectRevision = setup.Project.Project.Revision,
                ProjectConfigurationRevision = noAdmissionConfigRev,
                PlatformRuntimeRevision = setup.Project.PlatformDefaultsRevision,
                ContextRevision = "refresh-context",
                RequestFingerprint = new string('b', 64),
                SnapshotJson = ProjectsConfigService.SerializeRunSelection(noAdmissionSelection),
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            await db.SaveChangesAsync();

            var noAdmissionPath =
                $"/api/projects/{setup.Request.ProjectId}/runs/{noAdmissionRunId}/agents/agent-one/skills/refresh-inputs";
            using (var noAdmissionReq = new HttpRequestMessage(HttpMethod.Get, noAdmissionPath))
            {
                noAdmissionReq.Headers.Add("X-Test-Sub", setup.Project.Owner.ActorId);
                noAdmissionReq.Headers.Add("X-Test-Actor-Class", "human");
                noAdmissionReq.Headers.Add("X-Agentweaver-Tenant", setup.Project.Owner.TenantId);
                using var res = await client.SendAsync(noAdmissionReq);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var payload = await res.Content.ReadFromJsonAsync<AcceptedRunSkillRefreshInputsResponse>();
                Assert.False(Assert.Single(payload!.Assignments).RefreshEligible);
            }
        }
        finally
        {
            await ResetSkillAdmissionCutoverAsync(db);
        }
    }

    private async Task<WebApplication> StartRefreshEndpointHostAsync(
        string issuer,
        SkillTestObjectStore objects)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddAuthentication("TestHeader")
            .AddScheme<AuthenticationSchemeOptions, TestRefreshAuthHandler>("TestHeader", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDbContextFactory<ProjectsConfigDbContext>(
            options => options.UseNpgsql(fixture.DataSource));
        builder.Services.AddScoped(provider =>
            provider.GetRequiredService<IDbContextFactory<ProjectsConfigDbContext>>().CreateDbContext());
        builder.Services.AddSingleton(CreateProviderCatalog());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new ProjectsConfigIdentityOptions(issuer));
        builder.Services.AddScoped<ProjectAuthorizationOwner>();
        builder.Services.AddScoped<ProjectsConfigService>();
        builder.Services.AddScoped(_ => new SkillContentObjectStore(objects));
        builder.Services.AddScoped<SkillContentService>();
        builder.Services.AddScoped<ISkillContentService>(p => p.GetRequiredService<SkillContentService>());
        builder.Services.AddScoped<ISkillAssignmentService>(p => p.GetRequiredService<SkillContentService>());
        builder.Services.AddScoped<ProjectGovernanceDecisionStore>();
        builder.Services.AddScoped<ProjectSkillTrustEvidenceService>();
        var host = builder.Build();
        host.UseRouting();
        host.UseAuthentication();
        host.UseAuthorization();
        host.MapSkillContentEndpoints();
        await host.StartAsync();
        return host;
    }

    private sealed class TestRefreshAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sub = Request.Headers["X-Test-Sub"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sub))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim>
            {
                new("sub", sub),
                new("scope", $"{ProjectAuthorizationOwner.ApiReadScope} {ProjectAuthorizationOwner.ProjectAdminScope}"),
            };
            if (Request.Headers.TryGetValue("X-Test-Actor-Class", out var actorClasses))
            {
                foreach (var value in actorClasses)
                    claims.Add(new Claim("aw_actor_class", value!));
            }
            if (Request.Headers.TryGetValue("X-Test-Project-Id", out var projectIds))
            {
                foreach (var value in projectIds)
                    claims.Add(new Claim("project_id", value!));
            }
            if (Request.Headers.TryGetValue("X-Test-Purpose", out var purposes))
            {
                foreach (var value in purposes)
                    claims.Add(new Claim("purpose", value!));
            }
            var identity = new ClaimsIdentity(claims, "TestHeader");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "TestHeader")));
        }
    }
}
