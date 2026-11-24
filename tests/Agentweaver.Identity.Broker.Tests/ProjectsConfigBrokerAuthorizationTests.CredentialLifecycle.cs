extern alias GatewayHost;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task DedicatedByokCredentialLifecycleSupportsPutQuarantineRecoveryCancelDisableAndPinnedRedemption()
    {
        using var signingCert = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var projects = _runBindingProjects;

        var fakeVault = new FakeByokVault();
        var logCollector = new InMemoryLogCollector();
        await using var broker = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                ConfigureRunBindingSettings(settings);
                settings["IdentityBroker__SecretRedemption__ByokVaultUri"] =
                    "https://agentweaver-byok-kv-v1.vault.azure.net/";
                settings["IdentityBroker__SecretRedemption__ByokVaultHost"] =
                    "agentweaver-byok-kv-v1.vault.azure.net";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                services.AddSingleton<ILoggerProvider>(logCollector);
                services.RemoveAll<IModelProviderByokVault>();
                services.AddSingleton<IModelProviderByokVault>(fakeVault);
                services.RemoveAll<ModelProviderCredentialRedemptionBackends>();
                services.AddSingleton(new ModelProviderCredentialRedemptionBackends(fakeVault));
            });

        using var brokerClient = broker.CreateClient();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(signingCert),
            () => new ForwardToClientHandler(projects.Client),
            async (owner, req, ct) =>
            {
                var client = owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker
                    ? brokerClient
                    : projects.Client;
                using var clone = new HttpRequestMessage(req.Method, req.RequestUri!.PathAndQuery);
                foreach (var header in req.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (req.Content is not null)
                {
                    var bytes = await req.Content.ReadAsByteArrayAsync(ct);
                    clone.Content = new ByteArrayContent(bytes);
                    foreach (var header in req.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var resp = await client.SendAsync(clone, ct);
                resp.RequestMessage = req;
                return resp;
            });

        // Legacy private publication endpoint must remain 503.
        using (var legacyReq = new HttpRequestMessage(HttpMethod.Post, "/internal/byok/credential-publications"))
        {
            var legacyResp = await projects.Client.SendAsync(legacyReq);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, legacyResp.StatusCode);
        }

        // Set up PlatformAdmin and Project Owner memberships.
        var adminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var adminSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, adminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);

        var project = await CreateProjectsTestProjectAsync(
            projects.Client, adminToken, TenantId, "BYOK Credential Lifecycle");
        var projectToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, project.ProjectId, null, []))
            .GetProperty("access_token").GetString()!;
        var projectSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(projectToken).Claims, "sub");
        var projectMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, projectSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            projectMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        var providerId = "cred-proj-provider-" + Guid.NewGuid().ToString("N")[..8];

        // Create a project BYOK provider draft via Gateway -> Projects.
        var createDraftOp = Guid.NewGuid();
        using (var createResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/byok-providers",
            projectToken,
            new
            {
                operationId = createDraftOp,
                expectedRevision = 0,
                name = "Test BYOK Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            }))
        {
            Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        }

        // Existing-vault reference attachment must be rejected (400).
        using (var existingRefResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                keyVaultSecret = "my-existing-secret",
                keyVaultVersion = new string('a', 32),
            }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, existingRefResp.StatusCode);
            var body = await existingRefResp.Content.ReadAsStringAsync();
            Assert.Contains("existing_vault_reference_not_supported", body);
        }

        var listResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/byok-providers",
            projectToken, null);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        providerId = listDoc.RootElement.GetProperty("providers")[0].GetProperty("id").GetString()!;

        // 1. Denied actions: unbound token on project route, TenantAdmin.
        using (var unboundResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-denied-unbound",
            }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unboundResp.StatusCode);
        }

        // Multi-tenant actor: missing selector rejected (403), valid selector succeeds on platform route.
        const string secondTenant = "tenant-2";
        await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSub, secondTenant);
        var multiTenantAdminToken = (await IssueTokensAsync(
            broker, "projects.admin", [], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var multiSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(multiTenantAdminToken).Claims, "sub");
        var multiM1 = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, multiSub, TenantId);
        await AddMembershipAsync(projects.PrivilegedFixtureDataSource, multiSub, secondTenant);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, multiM1.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        // Platform provider create + PUT + DELETE.
        var platOp = Guid.NewGuid();
        using var platCreateReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/byok-providers")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = platOp,
                expectedRevision = 0,
                name = "Platform Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            }), Encoding.UTF8, "application/json"),
        };
        platCreateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", multiTenantAdminToken);
        platCreateReq.Headers.Add("X-Agentweaver-Tenant", TenantId);
        using var platCreateResp = await gateway.Client.SendAsync(platCreateReq);
        Assert.Equal(HttpStatusCode.OK, platCreateResp.StatusCode);
        using var platDoc = JsonDocument.Parse(await platCreateResp.Content.ReadAsStringAsync());
        var platProviderId = platDoc.RootElement.GetProperty("id").GetString()!;

        // Without X-Agentweaver-Tenant selector, multi-tenant actor is rejected (403).
        using (var noSelResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/platform/byok-providers/{platProviderId}/credential",
            multiTenantAdminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-platform-no-selector",
            }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noSelResp.StatusCode);
        }

        // With X-Agentweaver-Tenant selector, platform PUT succeeds.
        using var platPutReq = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/platform/byok-providers/{platProviderId}/credential")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-platform-secret-99999",
            }), Encoding.UTF8, "application/json"),
        };
        platPutReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", multiTenantAdminToken);
        platPutReq.Headers.Add("X-Agentweaver-Tenant", TenantId);
        using var platPutResp = await gateway.Client.SendAsync(platPutReq);
        var platPutBody = await platPutResp.Content.ReadAsStringAsync();
        Assert.True(platPutResp.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)platPutResp.StatusCode}: {platPutBody}\nLogs:\n{logCollector.AllText}");
        Assert.Contains("\"published\"", platPutBody);
        Assert.DoesNotContain("sk-platform-secret-99999", platPutBody);

        // Platform DELETE disables cleanly.
        using var platDelReq = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{platProviderId}/credential?operationId={Guid.NewGuid():D}&expectedProviderRevision=2&expectedCredentialRevision=1");
        platDelReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", multiTenantAdminToken);
        platDelReq.Headers.Add("X-Agentweaver-Tenant", TenantId);
        using var platDelResp = await gateway.Client.SendAsync(platDelReq);
        Assert.Equal(HttpStatusCode.OK, platDelResp.StatusCode);
        Assert.Contains("\"disabled\"", await platDelResp.Content.ReadAsStringAsync());
        Assert.DoesNotContain("sk-platform-secret-99999", logCollector.AllText);
        var tenantAdminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], "tenant-admin-only", project.ProjectId, null, []))
            .GetProperty("access_token").GetString()!;
        var tenantAdminSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(tenantAdminToken).Claims, "sub");
        var taMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, taMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        using (var taResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            tenantAdminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-denied-tenant-admin",
            }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, taResp.StatusCode);
        }
        Assert.Equal(1, fakeVault.WriteCalls);
        Assert.DoesNotContain("sk-denied-tenant-admin", logCollector.AllText);

        // 2. Ambiguous Vault write -> Quarantine -> No re-send -> Wrong evidence rejected -> Exact evidence recovers!
        const string secretValue1 = "sk-live-secret-never-logged-11111";
        fakeVault.FailNextWriteAmbiguously = true;
        var op1 = Guid.NewGuid();
        using (var qResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = op1,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = secretValue1,
            }))
        {
            var qBody = await qResp.Content.ReadAsStringAsync();
            Assert.True(qResp.StatusCode == HttpStatusCode.ServiceUnavailable && qBody.Contains("quarantined"),
                $"Status={qResp.StatusCode} Body={qBody} WriteCalls={fakeVault.WriteCalls}");
            Assert.DoesNotContain(secretValue1, qBody);
        }
        Assert.Equal(2, fakeVault.WriteCalls);

        // A disable that reuses this publication operation ID must not cancel the claim.
        using (var collision = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = op1,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 1,
                cancel = false,
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
            Assert.Contains("credential_slot_conflict", await collision.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, fakeVault.WriteCalls);

        // Retry with raw apiKey must NOT call Vault again!
        using (var retryNoSend = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = op1,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = secretValue1,
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, retryNoSend.StatusCode);
            Assert.Contains("quarantined", await retryNoSend.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, fakeVault.WriteCalls);

        // Recovery with wrong version must fail without calling WriteVersionAsync.
        using (var badRecovery = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = op1,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                recoverySecretVersion = new string('f', 32),
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, badRecovery.StatusCode);
            Assert.Contains("credential_recovery_evidence_invalid", await badRecovery.Content.ReadAsStringAsync());
        }

        // Recovery with exact version created by op1 succeeds and publishes!
        var createdVersion1 = fakeVault.LastWrittenVersion!;
        using (var goodRecovery = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = op1,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                recoverySecretVersion = createdVersion1,
            }))
        {
            var body = await goodRecovery.Content.ReadAsStringAsync();
            Assert.True(goodRecovery.StatusCode == HttpStatusCode.OK, $"Status={goodRecovery.StatusCode} Body={body}");
            Assert.True(goodRecovery.Headers.CacheControl?.NoStore);
            Assert.Contains("\"published\"", body);
            Assert.Contains(createdVersion1, body);
            Assert.DoesNotContain(secretValue1, body);
            Assert.DoesNotContain("byok-project-", body);
        }
        Assert.Equal(2, fakeVault.WriteCalls);

        // 3. Cancellation on a quarantined op is final; exact receipt wins if already published.
        var opCancel = Guid.NewGuid();
        fakeVault.FailNextWriteAmbiguously = true;
        using (var q2Resp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = opCancel,
                expectedProviderRevision = 2,
                expectedCredentialRevision = 1,
                apiKey = "sk-live-secret-cancel-22222",
            }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, q2Resp.StatusCode);
        }
        using (var cancelResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential?operationId={opCancel:D}&cancel=true",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
            Assert.Contains("\"cancelled\"", await cancelResp.Content.ReadAsStringAsync());
        }
        // Retry of cancelled operation cannot reopen it.
        using (var reopenResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential",
            projectToken,
            new
            {
                operationId = opCancel,
                expectedProviderRevision = 2,
                expectedCredentialRevision = 1,
                apiKey = "sk-live-secret-cancel-22222",
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, reopenResp.StatusCode);
            Assert.Contains("credential_operation_cancelled", await reopenResp.Content.ReadAsStringAsync());
        }
        // Cancel on already-published op1: exact receipt wins!
        using (var cancelPublishedResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential?operationId={op1:D}&cancel=true",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, cancelPublishedResp.StatusCode);
            Assert.Contains("\"published\"", await cancelPublishedResp.Content.ReadAsStringAsync());
        }

        // 4. Disable blocks new pins while historical pin remains redeemable.
        // Disable credential via DELETE.
        var opDisable = Guid.NewGuid();
        using (var disableResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential?operationId={opDisable:D}&expectedProviderRevision=2&expectedCredentialRevision=1",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);
            Assert.Contains("\"disabled\"", await disableResp.Content.ReadAsStringAsync());
        }

        // Retry disable replays cleanly.
        using (var disableReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/projects/{project.ProjectId}/byok-providers/{providerId}/credential?operationId={opDisable:D}&expectedProviderRevision=2&expectedCredentialRevision=1",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, disableReplay.StatusCode);
            Assert.Contains("\"disabled\"", await disableReplay.Content.ReadAsStringAsync());
        }

        // Historical pin from op1 remains redeemable!
        using (var scope = broker.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ModelProviderCredentialStore>();
            var head = await store.ReadHeadByProviderAsync(
                ModelProviderCredentialScope.Project, project.ProjectId, providerId);
            Assert.NotNull(head);
            Assert.Equal(ModelProviderCredentialState.Disabled, head!.State);

            var pinned = await store.ReadPinnedAsync(
                new ModelProviderCredentialIdentity(
                    head.CredentialSlotId, ModelProviderCredentialScope.Project, project.ProjectId, providerId),
                new SecretRef($"byok-project-{providerId}", createdVersion1));
            Assert.Equal(ModelProviderCredentialPinnedStatus.Found, pinned.Status);

            var redeemed = await fakeVault.RedeemAsync(
                new SecretRedemptionRequest(pinned.Credential!.Reference, "model-session", "run-1"),
                CancellationToken.None);
            Assert.Equal(secretValue1, redeemed.GetValue());
            redeemed.Invalidate();
        }

        // Secrecy check: raw keys never appeared in any logs.
        Assert.DoesNotContain(secretValue1, logCollector.AllText);
        Assert.DoesNotContain("sk-live-secret-cancel-22222", logCollector.AllText);
    }

    [Fact]
    public async Task CredentialLifecycleHandlesMembershipBumpReplayStaleDisableAndLockContention()
    {
        using var signingCert = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var projects = _runBindingProjects;
        var fakeVault = new FakeByokVault();

        await using var broker = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                ConfigureRunBindingSettings(settings);
                settings["IdentityBroker__SecretRedemption__ByokVaultUri"] =
                    "https://agentweaver-byok-kv-v1.vault.azure.net/";
                settings["IdentityBroker__SecretRedemption__ByokVaultHost"] =
                    "agentweaver-byok-kv-v1.vault.azure.net";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                services.RemoveAll<IModelProviderByokVault>();
                services.AddSingleton<IModelProviderByokVault>(fakeVault);
                services.RemoveAll<ModelProviderCredentialRedemptionBackends>();
                services.AddSingleton(new ModelProviderCredentialRedemptionBackends(fakeVault));
            });

        using var brokerClient = broker.CreateClient();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(signingCert),
            () => new ForwardToClientHandler(projects.Client),
            async (owner, req, ct) =>
            {
                var client = owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker
                    ? brokerClient
                    : projects.Client;
                using var clone = new HttpRequestMessage(req.Method, req.RequestUri!.PathAndQuery);
                foreach (var header in req.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (req.Content is not null)
                {
                    var bytes = await req.Content.ReadAsByteArrayAsync(ct);
                    clone.Content = new ByteArrayContent(bytes);
                    foreach (var header in req.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var resp = await client.SendAsync(clone, ct);
                resp.RequestMessage = req;
                return resp;
            });

        var adminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var adminSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        using var createResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, "/api/v1/platform/byok-providers",
            adminToken, new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Lifecycle Repair Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var providerId = createDoc.RootElement.GetProperty("id").GetString()!;

        // 1. Publish succeeds; bumping membership revision does NOT break replay, revoking membership DOES block replay.
        var opPublish = Guid.NewGuid();
        using (var putResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken, new
            {
                operationId = opPublish,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-repair-publish-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        await using (var bump = projects.PrivilegedFixtureDataSource.CreateCommand(
            "UPDATE projects_config.tenant_memberships SET revision = revision + 1 WHERE membership_id = @id"))
        {
            bump.Parameters.AddWithValue("id", membership.MembershipId);
            Assert.Equal(1, await bump.ExecuteNonQueryAsync());
        }

        using (var replayAfterBump = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken, new
            {
                operationId = opPublish,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-repair-publish-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, replayAfterBump.StatusCode);
            Assert.Contains("\"published\"", await replayAfterBump.Content.ReadAsStringAsync());
        }

        // 2. Lock contention on tenant_memberships fails closed (503) and keeps DisablePending fenced until cancelled.
        var contendedOp = Guid.NewGuid();
        await using (var lockConn = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
        await using (var lockTx = await lockConn.BeginTransactionAsync())
        {
            await using var holdLock = new NpgsqlCommand(
                "SELECT membership_id FROM projects_config.tenant_memberships WHERE membership_id = @id FOR UPDATE",
                lockConn, lockTx);
            holdLock.Parameters.AddWithValue("id", membership.MembershipId);
            _ = await holdLock.ExecuteScalarAsync();

            using var contendedDelete = await SendCredentialJsonAsync(
                gateway.Client, HttpMethod.Delete,
                $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={contendedOp:D}",
                adminToken, null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, contendedDelete.StatusCode);
            await lockTx.RollbackAsync();
        }
        using (var cancelContended = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={contendedOp:D}&cancel=true",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, cancelContended.StatusCode);
        }

        // 3. Stale expectedCredentialRevision on DELETE returns 409 BEFORE Projects clears the binding.
        using (var staleDelete = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={Guid.NewGuid():D}&expectedCredentialRevision=99",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        }
        using (var readAfterStale = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get, $"/api/v1/platform/byok-providers/{providerId}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, readAfterStale.StatusCode);
            using var doc = JsonDocument.Parse(await readAfterStale.Content.ReadAsStringAsync());
            Assert.Equal(2L, doc.RootElement.GetProperty("revision").GetInt64());
        }

        // 4. DELETE omitting optional expectedProviderRevision & expectedCredentialRevision disables and replays after loss.
        var opDisableOmitted = Guid.NewGuid();
        using (var delOmitted = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={opDisableOmitted:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, delOmitted.StatusCode);
            Assert.Contains("\"disabled\"", await delOmitted.Content.ReadAsStringAsync());
        }
        using (var delOmittedReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={opDisableOmitted:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, delOmittedReplay.StatusCode);
            Assert.Contains("\"disabled\"", await delOmittedReplay.Content.ReadAsStringAsync());
        }

        // 5. Revoking membership blocks replay (403).
        await using (var revoke = projects.PrivilegedFixtureDataSource.CreateCommand(
            "UPDATE projects_config.tenant_memberships SET state = 'Revoked', revision = revision + 1, revoked_by = 'test', revoked_at = now() WHERE membership_id = @id"))
        {
            revoke.Parameters.AddWithValue("id", membership.MembershipId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }
        using (var revokedReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken, new
            {
                operationId = opPublish,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-repair-publish-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        }
    }

    [Fact]
    public async Task CredentialOperationStatusGetSupportsBothScopesSplitStatePendingZeroMutationsAndRedaction()
    {
        using var signingCert = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var projects = _runBindingProjects;

        var fakeVault = new FakeByokVault();
        await using var broker = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                ConfigureRunBindingSettings(settings);
                settings["IdentityBroker__SecretRedemption__ByokVaultUri"] =
                    "https://agentweaver-byok-kv-v1.vault.azure.net/";
                settings["IdentityBroker__SecretRedemption__ByokVaultHost"] =
                    "agentweaver-byok-kv-v1.vault.azure.net";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                services.RemoveAll<IModelProviderByokVault>();
                services.AddSingleton<IModelProviderByokVault>(fakeVault);
                services.RemoveAll<ModelProviderCredentialRedemptionBackends>();
                services.AddSingleton(new ModelProviderCredentialRedemptionBackends(fakeVault));
            });

        using var brokerClient = broker.CreateClient();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(signingCert),
            () => new ForwardToClientHandler(projects.Client),
            async (owner, req, ct) =>
            {
                var client = owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker
                    ? brokerClient
                    : projects.Client;
                using var clone = new HttpRequestMessage(req.Method, req.RequestUri!.PathAndQuery);
                foreach (var header in req.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (req.Content is not null)
                {
                    var bytes = await req.Content.ReadAsByteArrayAsync(ct);
                    clone.Content = new ByteArrayContent(bytes);
                    foreach (var header in req.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var resp = await client.SendAsync(clone, ct);
                resp.RequestMessage = req;
                return resp;
            });

        var adminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var adminSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        var otherAdminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var otherSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(otherAdminToken).Claims, "sub");
        var otherMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, otherSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, otherMembership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        using var createPlatform = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, "/api/v1/platform/byok-providers",
            adminToken, new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Status Get Platform Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            });
        Assert.Equal(HttpStatusCode.OK, createPlatform.StatusCode);
        var platformProviderId = JsonDocument.Parse(await createPlatform.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        // 1. Unknown operationId returns 404 without mutating preflights or calling Vault.
        var preflightCountBefore = await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights");
        var vaultCountBefore = fakeVault.WriteCalls;
        using (var missing = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential/operations/{Guid.NewGuid():D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.True(missing.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(preflightCountBefore, await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights"));
        Assert.Equal(vaultCountBefore, fakeVault.WriteCalls);

        // 2. Published operation returns 200 published with no secret version, credential revision, or hash.
        var opPublish = Guid.NewGuid();
        using (var putResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{platformProviderId}/credential",
            adminToken, new
            {
                operationId = opPublish,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-status-get-secret-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        using (var publishedStatus = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential/operations/{opPublish:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, publishedStatus.StatusCode);
            Assert.True(publishedStatus.Headers.CacheControl?.NoStore);
            var json = await publishedStatus.Content.ReadAsStringAsync();
            Assert.DoesNotContain("sk-status-get-secret-11111", json);
            Assert.DoesNotContain("credentialVersion", json);
            Assert.DoesNotContain("credentialRevision", json);
            Assert.DoesNotContain("configurationHash", json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("published", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("platform", doc.RootElement.GetProperty("scope").GetString());
            Assert.Equal(2L, doc.RootElement.GetProperty("providerRevision").GetInt64());
            Assert.True(doc.RootElement.GetProperty("hasCredential").GetBoolean());
        }

        // 3. Mismatched actor gets 409; mismatched provider returns 409.
        using (var wrongActor = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential/operations/{opPublish:D}",
            otherAdminToken, null))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongActor.StatusCode);
        }

        // 4. Split-state pending case 1: Projects committed Publish, but Broker claim remains PublicationPending.
        // GET MUST remain publication_pending (not published) and perform zero mutations.
        using var createSplitPub = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, "/api/v1/platform/byok-providers",
            adminToken, new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Split Pub Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            });
        Assert.Equal(HttpStatusCode.OK, createSplitPub.StatusCode);
        var splitPubProviderId = JsonDocument.Parse(await createSplitPub.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var opSplitPub = Guid.NewGuid();
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        using (var diScope = broker.Services.CreateScope())
        {
            var store = diScope.ServiceProvider.GetRequiredService<ModelProviderCredentialStore>();
            var pubClient = diScope.ServiceProvider.GetRequiredService<BrokerModelProviderCredentialPublicationV2Client>();
            var preflight1 = await pubClient.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opSplitPub, ModelProviderBindingScope.Platform, null, splitPubProviderId,
                    issuer, adminSub, TenantId, null, null, "draft-snapshot"),
                CancellationToken.None);
            var slotId = preflight1.CredentialSlotId ?? Guid.NewGuid();
            var secretName = $"byok-platform-{splitPubProviderId}";
            var pubClaim = new ClaimModelProviderCredentialPublication(
                new ClaimModelProviderCredentialWrite(
                    opSplitPub, 0, slotId, ModelProviderCredentialScope.Platform, null, splitPubProviderId,
                    ModelProviderCredentialVaultSource.Byok, secretName, adminSub),
                1, "credential-publication", issuer, preflight1.ResolvedTenant);
            _ = await store.ClaimPublicationWriteAsync(pubClaim, CancellationToken.None);
            await store.MarkPublicationDispatchedAsync(pubClaim, CancellationToken.None);
            var writtenRef = await fakeVault.WriteVersionAsync(
                secretName,
                new SecretCredential("sk-split-pub-11111", DateTimeOffset.UtcNow.AddDays(30)),
                opSplitPub, slotId, CancellationToken.None);
            var pending = await store.CompletePublicationWriteAsync(pubClaim, writtenRef, CancellationToken.None);
            var preflight2 = await pubClient.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opSplitPub, ModelProviderBindingScope.Platform, null, splitPubProviderId,
                    issuer, adminSub, TenantId, null, null, "draft-snapshot"),
                CancellationToken.None);
            _ = await pubClient.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opSplitPub,
                    preflight2.PreflightId,
                    1,
                    ModelProviderBindingScope.Platform,
                    null,
                    splitPubProviderId,
                    writtenRef.Version,
                    new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, splitPubProviderId),
                    writtenRef,
                    issuer,
                    adminSub,
                    preflight1.ResolvedTenant,
                    preflight2.MembershipId,
                    preflight2.MembershipRevision,
                    TenantId,
                    null),
                CancellationToken.None);
        }

        var preflightsBeforeSplitPub = await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights");
        using (var splitPub = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{splitPubProviderId}/credential/operations/{opSplitPub:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, splitPub.StatusCode);
            using var doc = JsonDocument.Parse(await splitPub.Content.ReadAsStringAsync());
            Assert.Equal("publication_pending", doc.RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(preflightsBeforeSplitPub, await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights"));

        // 5. Split-state pending case 2: Projects committed Disable, but Broker claim remains DisablePending.
        // GET MUST remain disable_pending (not disabled) and perform zero mutations; replay completes it.
        var opDisable = Guid.NewGuid();
        using (var diScope = broker.Services.CreateScope())
        {
            var store = diScope.ServiceProvider.GetRequiredService<ModelProviderCredentialStore>();
            var pubClient = diScope.ServiceProvider.GetRequiredService<BrokerModelProviderCredentialPublicationV2Client>();
            var disableClaim = await store.ClaimDisableAsync(
                new ClaimModelProviderCredentialDisable(
                    opDisable, null, null, ModelProviderCredentialScope.Platform, null, platformProviderId,
                    adminSub, issuer),
                CancellationToken.None);
            _ = await pubClient.DisableAsync(
                new ModelProviderCredentialDisableRequest(
                    opDisable, null, disableClaim.OriginalCredentialRevision,
                    disableClaim.CredentialSlotId, disableClaim.CurrentVersion,
                    ModelProviderBindingScope.Platform, null, platformProviderId,
                    issuer, adminSub, TenantId, null),
                CancellationToken.None);
        }
        var preflightsBeforeSplitDisable = await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights");
        using (var splitDisable = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential/operations/{opDisable:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, splitDisable.StatusCode);
            using var doc = JsonDocument.Parse(await splitDisable.Content.ReadAsStringAsync());
            Assert.Equal("disable_pending", doc.RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(preflightsBeforeSplitDisable, await CountRowsAsync(
            projects.PrivilegedFixtureDataSource, "SELECT COUNT(*) FROM projects_config.model_provider_credential_preflights"));

        using (var finishDisable = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential?operationId={opDisable:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, finishDisable.StatusCode);
        }
        using (var disabledStatus = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/platform/byok-providers/{platformProviderId}/credential/operations/{opDisable:D}",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, disabledStatus.StatusCode);
            using var doc = JsonDocument.Parse(await disabledStatus.Content.ReadAsStringAsync());
            Assert.Equal("disabled", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(3L, doc.RootElement.GetProperty("providerRevision").GetInt64());
        }

        // 6. Project scope GET works with project-bound runless token and rejects revoked authority (403).
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, adminToken, TenantId, "Status Project");
        var projectId = project.ProjectId;
        var projectToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, projectId, null, ["project_owner"]))
            .GetProperty("access_token").GetString()!;
        var projectSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(projectToken).Claims, "sub");
        var projectMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, projectSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, projectMembership.MembershipId,
            ProjectAuthorityResourceType.Project, projectId, ProjectAuthorityRole.Owner);

        using var createProjProvider = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, $"/api/v1/projects/{projectId}/byok-providers",
            projectToken, new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Status Get Project Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            });
        Assert.Equal(HttpStatusCode.OK, createProjProvider.StatusCode);
        var projProviderId = JsonDocument.Parse(await createProjProvider.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var opProjPub = Guid.NewGuid();
        using (var putProj = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put,
            $"/api/v1/projects/{projectId}/byok-providers/{projProviderId}/credential",
            projectToken, new
            {
                operationId = opProjPub,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-proj-status-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, putProj.StatusCode);
        }

        using (var projStatus = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/projects/{projectId}/byok-providers/{projProviderId}/credential/operations/{opProjPub:D}",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, projStatus.StatusCode);
            using var doc = JsonDocument.Parse(await projStatus.Content.ReadAsStringAsync());
            Assert.Equal("published", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("project", doc.RootElement.GetProperty("scope").GetString());
            Assert.Equal(projectId, doc.RootElement.GetProperty("projectId").GetString());
        }

        await using (var revoke = projects.PrivilegedFixtureDataSource.CreateCommand(
            "UPDATE projects_config.tenant_memberships SET state = 'Revoked', revision = revision + 1, revoked_by = 'test', revoked_at = now() WHERE membership_id = @id"))
        {
            revoke.Parameters.AddWithValue("id", projectMembership.MembershipId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }
        using (var revokedGet = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get,
            $"/api/v1/projects/{projectId}/byok-providers/{projProviderId}/credential/operations/{opProjPub:D}",
            projectToken, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedGet.StatusCode);
        }
    }

    private static async Task<long> CountRowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ByokCredentialLifecycleDisableRejectsStaleSlotOrVersionUnderProviderLockAndPreservesReplayAndPins()
    {
        using var signingCert = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var projects = _runBindingProjects;

        var fakeVault = new FakeByokVault();
        await using var broker = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                ConfigureRunBindingSettings(settings);
                settings["IdentityBroker__SecretRedemption__ByokVaultUri"] =
                    "https://agentweaver-byok-kv-v1.vault.azure.net/";
                settings["IdentityBroker__SecretRedemption__ByokVaultHost"] =
                    "agentweaver-byok-kv-v1.vault.azure.net";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                services.RemoveAll<IModelProviderByokVault>();
                services.AddSingleton<IModelProviderByokVault>(fakeVault);
                services.RemoveAll<ModelProviderCredentialRedemptionBackends>();
                services.AddSingleton(new ModelProviderCredentialRedemptionBackends(fakeVault));
            });

        using var brokerClient = broker.CreateClient();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(signingCert),
            () => new ForwardToClientHandler(projects.Client),
            async (owner, req, ct) =>
            {
                var client = owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker
                    ? brokerClient
                    : projects.Client;
                using var clone = new HttpRequestMessage(req.Method, req.RequestUri!.PathAndQuery);
                foreach (var header in req.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (req.Content is not null)
                {
                    var bytes = await req.Content.ReadAsByteArrayAsync(ct);
                    clone.Content = new ByteArrayContent(bytes);
                    foreach (var header in req.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var resp = await client.SendAsync(clone, ct);
                resp.RequestMessage = req;
                return resp;
            });

        var adminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(adminToken);
        var adminSub = SingleClaim(jwt.Claims, "sub");
        var adminIssuer = SingleClaim(jwt.Claims, "iss");
        var adminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, adminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);

        string providerId;
        using (var saveDraft = await SendCredentialJsonAsync(
            gateway.Client,
            HttpMethod.Post,
            "/api/v1/platform/byok-providers",
            adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0L,
                name = "Conditional Disable Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            }))
        {
            Assert.Equal(HttpStatusCode.OK, saveDraft.StatusCode);
            using var doc = JsonDocument.Parse(await saveDraft.Content.ReadAsStringAsync());
            providerId = doc.RootElement.GetProperty("id").GetString()!;
        }

        string version1;
        using (var put1 = await SendCredentialJsonAsync(
            gateway.Client,
            HttpMethod.Put,
            $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1L,
                expectedCredentialRevision = 0L,
                apiKey = "sk-v1-secret-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, put1.StatusCode);
            using var doc = JsonDocument.Parse(await put1.Content.ReadAsStringAsync());
            version1 = doc.RootElement.GetProperty("credentialVersion").GetString()!;
        }

        string version2;
        using (var put2 = await SendCredentialJsonAsync(
            gateway.Client,
            HttpMethod.Put,
            $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 2L,
                expectedCredentialRevision = 1L,
                apiKey = "sk-v2-secret-22222",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
            using var doc = JsonDocument.Parse(await put2.Content.ReadAsStringAsync());
            version2 = doc.RootElement.GetProperty("credentialVersion").GetString()!;
        }

        Guid slotId;
        using (var scope = broker.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ModelProviderCredentialStore>();
            var head = await store.ReadHeadByProviderAsync(
                ModelProviderCredentialScope.Platform, null, providerId);
            Assert.NotNull(head);
            slotId = head!.CredentialSlotId;

            var pubClient = scope.ServiceProvider.GetRequiredService<BrokerModelProviderCredentialPublicationV2Client>();
            var staleVersionEx = await Assert.ThrowsAsync<BrokerCredentialPublicationV2Exception>(() =>
                pubClient.DisableAsync(
                    new ModelProviderCredentialDisableRequest(
                        Guid.NewGuid(),
                        null,
                        1L,
                        slotId,
                        version1,
                        ModelProviderBindingScope.Platform,
                        null,
                        providerId,
                        adminIssuer,
                        adminSub,
                        TenantId,
                        TenantId),
                    CancellationToken.None));
            Assert.Equal(HttpStatusCode.Conflict, staleVersionEx.StatusCode);

            var staleSlotEx = await Assert.ThrowsAsync<BrokerCredentialPublicationV2Exception>(() =>
                pubClient.DisableAsync(
                    new ModelProviderCredentialDisableRequest(
                        Guid.NewGuid(),
                        null,
                        2L,
                        Guid.NewGuid(),
                        version2,
                        ModelProviderBindingScope.Platform,
                        null,
                        providerId,
                        adminIssuer,
                        adminSub,
                        TenantId,
                        TenantId),
                    CancellationToken.None));
            Assert.Equal(HttpStatusCode.Conflict, staleSlotEx.StatusCode);
        }

        // Rotated binding remains intact; matching disable succeeds and replays cleanly.
        var disableOpId = Guid.NewGuid();
        using (var disable = await SendCredentialJsonAsync(
            gateway.Client,
            HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken,
            new
            {
                operationId = disableOpId,
                expectedProviderRevision = 3L,
                expectedCredentialRevision = 2L,
            }))
        {
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            using var doc = JsonDocument.Parse(await disable.Content.ReadAsStringAsync());
            Assert.Equal("disabled", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(4L, doc.RootElement.GetProperty("providerRevision").GetInt64());
        }

        using (var replay = await SendCredentialJsonAsync(
            gateway.Client,
            HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential",
            adminToken,
            new
            {
                operationId = disableOpId,
                expectedProviderRevision = 3L,
                expectedCredentialRevision = 2L,
            }))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var doc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal("disabled", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(4L, doc.RootElement.GetProperty("providerRevision").GetInt64());
        }

        using (var scope = broker.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ModelProviderCredentialStore>();
            var identity = new ModelProviderCredentialIdentity(
                slotId, ModelProviderCredentialScope.Platform, null, providerId);
            var pin1 = await store.ReadPinnedAsync(identity, new SecretRef($"byok-platform-{providerId}", version1));
            var pin2 = await store.ReadPinnedAsync(identity, new SecretRef($"byok-platform-{providerId}", version2));
            Assert.Equal(ModelProviderCredentialPinnedStatus.Found, pin1.Status);
            Assert.Equal(ModelProviderCredentialPinnedStatus.Found, pin2.Status);
        }
    }

    private static async Task<HttpResponseMessage> SendCredentialJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string bearerToken,
        object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
        }
        return await client.SendAsync(request);
    }

    private sealed class StubRateCardOwnerClient : ProjectsConfig::Agentweaver.Projects.Config.IRuntimeRateCardOwnerClient
    {
        public Func<string, string, CancellationToken, Task<RuntimeRateCardPin?>>? OnReadPinAsync { get; set; }
        public int Calls { get; private set; }

        public Task<RuntimeRateCardPin?> ReadPinAsync(string id, string version, CancellationToken cancellationToken)
        {
            Calls++;
            return OnReadPinAsync is null
                ? Task.FromResult<RuntimeRateCardPin?>(null)
                : OnReadPinAsync(id, version, cancellationToken);
        }

        public async Task<bool> VerifyAsync(string id, string version, CancellationToken cancellationToken) =>
            await ReadPinAsync(id, version, cancellationToken).ConfigureAwait(false) is not null;
    }

    [Fact]
    public async Task SameVersionRecheckTransitionsDraftToAvailableWithoutNewVaultVersionAndKeepsReplayAndRecoveryDistinct()
    {
        using var signingCert = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var rateCardClient = new StubRateCardOwnerClient();
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            new X509SecurityKey(signingCert),
            brokerRunBindingAudience: BrokerRunBindingAudience,
            brokerRunBindingServerCertificate: _runBindingTlsAuthority.ServerCertificate,
            configureServices: services =>
            {
                services.RemoveAll<ProjectsConfig::Agentweaver.Projects.Config.IRuntimeRateCardOwnerClient>();
                services.AddSingleton<ProjectsConfig::Agentweaver.Projects.Config.IRuntimeRateCardOwnerClient>(rateCardClient);
            });

        var fakeVault = new FakeByokVault();
        await using var broker = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                ConfigureRunBindingSettings(settings);
                settings["IdentityBroker__RunBinding__ProjectsOwnerAddress"] = projects.BaseAddress.AbsoluteUri;
                settings["IdentityBroker__SecretRedemption__ByokVaultUri"] =
                    "https://agentweaver-byok-kv-v1.vault.azure.net/";
                settings["IdentityBroker__SecretRedemption__ByokVaultHost"] =
                    "agentweaver-byok-kv-v1.vault.azure.net";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                services.RemoveAll<IModelProviderByokVault>();
                services.AddSingleton<IModelProviderByokVault>(fakeVault);
                services.RemoveAll<ModelProviderCredentialRedemptionBackends>();
                services.AddSingleton(new ModelProviderCredentialRedemptionBackends(fakeVault));
            });

        using var brokerClient = broker.CreateClient();
        await using var gateway = await GatewayResourceServer.StartAsync(
            new X509SecurityKey(signingCert),
            () => new ForwardToClientHandler(projects.Client),
            async (owner, req, ct) =>
            {
                var client = owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker
                    ? brokerClient
                    : projects.Client;
                using var clone = new HttpRequestMessage(req.Method, req.RequestUri!.PathAndQuery);
                foreach (var header in req.Headers)
                {
                    if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (req.Content is not null)
                {
                    var bytes = await req.Content.ReadAsByteArrayAsync(ct);
                    clone.Content = new ByteArrayContent(bytes);
                    foreach (var header in req.Content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                var resp = await client.SendAsync(clone, ct);
                resp.RequestMessage = req;
                return resp;
            });

        var adminToken = (await IssueTokensAsync(
            broker, "projects.admin", [TenantId], null, null, null, ["platform_admin"]))
            .GetProperty("access_token").GetString()!;
        var adminSub = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, adminSub, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        // 1. Create platform provider draft without pricing.
        using var createResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, "/api/v1/platform/byok-providers", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Recheck Flow Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var providerId = createDoc.RootElement.GetProperty("id").GetString()!;

        // 2. Upload key while pricing is absent -> Draft, key saved (1 Vault write).
        var initialOp = Guid.NewGuid();
        using var putResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = initialOp,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-recheck-secret-12345",
            });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        using var putDoc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());
        Assert.Equal("published", putDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Draft", putDoc.RootElement.GetProperty("providerState").GetString());
        var savedVersion = putDoc.RootElement.GetProperty("credentialVersion").GetString()!;
        Assert.Equal(1, fakeVault.WriteCalls);

        // 3. Invalid recheck requests rejected (400).
        using (var badRecheck = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 2,
                expectedCredentialRevision = 0,
                recheckExistingVersion = true,
            }))
            Assert.Equal(HttpStatusCode.BadRequest, badRecheck.StatusCode);

        using (var mixedRecheck = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 2,
                expectedCredentialRevision = 1,
                apiKey = "sk-should-not-mix",
                recheckExistingVersion = true,
            }))
            Assert.Equal(HttpStatusCode.BadRequest, mixedRecheck.StatusCode);

        // The original key-upload operation can replay publication without sending the key.
        using (var keylessReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = initialOp,
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                recheckExistingVersion = true,
            }))
        {
            Assert.Equal(HttpStatusCode.OK, keylessReplay.StatusCode);
            using var keylessReplayDoc = JsonDocument.Parse(
                await keylessReplay.Content.ReadAsStringAsync());
            Assert.Equal("published", keylessReplayDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, fakeVault.WriteCalls);
        }

        // 4. Attach rate card to draft (rev 3).
        using var updateResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 2,
                name = "Recheck Flow Provider",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
                rate_card_id = "manual:broker-card",
                rate_card_version = "2026-11-22",
            });
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 5. Same-version recheck with verified card -> Available, NO new Vault version.
        rateCardClient.OnReadPinAsync = (id, version, _) => Task.FromResult<RuntimeRateCardPin?>(
            new RuntimeRateCardPin(
                id,
                version,
                new string('a', 64),
                new ManualRuntimeRateCardReadProvenance("https://example.com/prices", new DateOnly(2026, 11, 22))));

        var recheckOp = Guid.NewGuid();
        using var recheckResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = recheckOp,
                expectedProviderRevision = 3,
                expectedCredentialRevision = 1,
                recheckExistingVersion = true,
            });
        var recheckBody = await recheckResp.Content.ReadAsStringAsync();
        Assert.True(recheckResp.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)recheckResp.StatusCode}: {recheckBody}");
        using var recheckDoc = JsonDocument.Parse(recheckBody);
        Assert.Equal("published", recheckDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Available", recheckDoc.RootElement.GetProperty("providerState").GetString());
        Assert.Equal(4, recheckDoc.RootElement.GetProperty("providerRevision").GetInt64());
        Assert.Equal(2, recheckDoc.RootElement.GetProperty("credentialRevision").GetInt64());
        Assert.Equal(savedVersion, recheckDoc.RootElement.GetProperty("credentialVersion").GetString());
        Assert.Equal(1, fakeVault.WriteCalls);
        Assert.Equal(1, fakeVault.VerifyStoredCalls);
        Assert.Equal(1, rateCardClient.Calls);

        // Exact replay of recheck returns saved receipt without calling Vault or Events again.
        using var recheckReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = recheckOp,
                expectedProviderRevision = 3,
                expectedCredentialRevision = 1,
                recheckExistingVersion = true,
            });
        Assert.Equal(HttpStatusCode.OK, recheckReplay.StatusCode);
        using var replayDoc = JsonDocument.Parse(await recheckReplay.Content.ReadAsStringAsync());
        Assert.Equal("Available", replayDoc.RootElement.GetProperty("providerState").GetString());
        Assert.Equal(1, fakeVault.VerifyStoredCalls);
        Assert.Equal(1, rateCardClient.Calls);

        // 6. Disabled Vault version rejects recheck (409).
        fakeVault.SetEnabled($"byok-platform-{providerId}", savedVersion, false);
        using var disabledVaultRecheck = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId}/credential", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 4,
                expectedCredentialRevision = 2,
                recheckExistingVersion = true,
            });
        Assert.Equal(HttpStatusCode.Conflict, disabledVaultRecheck.StatusCode);

        // 7. Disable credential returns provider head to Draft.
        using var delResp = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId}/credential?operationId={Guid.NewGuid():D}&expectedProviderRevision=4&expectedCredentialRevision=2",
            adminToken, null);
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        using var delDoc = JsonDocument.Parse(await delResp.Content.ReadAsStringAsync());
        Assert.Equal("disabled", delDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Draft", delDoc.RootElement.GetProperty("providerState").GetString());

        using var readAfterDisable = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Get, $"/api/v1/platform/byok-providers/{providerId}", adminToken, null);
        Assert.Equal(HttpStatusCode.OK, readAfterDisable.StatusCode);
        using var afterDisableDoc = JsonDocument.Parse(await readAfterDisable.Content.ReadAsStringAsync());
        Assert.Equal("Draft", afterDisableDoc.RootElement.GetProperty("state").GetString());

        // 8. Create a fresh provider and verify DELETE vs PUT separation, durable DisablePending fence, cancel tombstone, replay, and revoked replay.
        string providerId2;
        using (var create2 = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Post, "/api/v1/platform/byok-providers", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedRevision = 0,
                name = "Disable Fence Provider 2",
                type = "openai",
                base_url = "https://api.openai.com/v1",
                model = "gpt-5",
                wire_api = "responses",
                reasoning_efforts = new[] { "high" },
            }))
        {
            Assert.Equal(HttpStatusCode.OK, create2.StatusCode);
            using var doc2 = JsonDocument.Parse(await create2.Content.ReadAsStringAsync());
            providerId2 = doc2.RootElement.GetProperty("id").GetString()!;
        }
        using (var putInitial2 = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId2}/credential", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 1,
                expectedCredentialRevision = 0,
                apiKey = "sk-reenable-secret-67890",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, putInitial2.StatusCode);
        }

        // Cancel with mismatched revision on Projects returns 409 without releasing DisablePending until v2/cancel commits.
        var staleDisableOp = Guid.NewGuid();
        using (var staleDelete = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId2}/credential?operationId={staleDisableOp:D}&expectedProviderRevision=99&expectedCredentialRevision=1",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        }

        // Concurrent PUT is blocked (409) while DisablePending is still active (even after Projects returned 409).
        using (var fencedPut = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId2}/credential", adminToken,
            new
            {
                operationId = Guid.NewGuid(),
                expectedProviderRevision = 2,
                expectedCredentialRevision = 1,
                apiKey = "sk-fenced-secret-11111",
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, fencedPut.StatusCode);
        }

        // PUT with the DisablePending operationId is rejected (409).
        using (var sameOpPut = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Put, $"/api/v1/platform/byok-providers/{providerId2}/credential", adminToken,
            new
            {
                operationId = staleDisableOp,
                expectedProviderRevision = 2,
                expectedCredentialRevision = 1,
                apiKey = "sk-same-op-secret-22222",
            }))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameOpPut.StatusCode);
        }

        // Cancel with ?cancel=true commits Projects v2/cancel tombstone and releases DisablePending.
        using (var cancelDelete = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId2}/credential?operationId={staleDisableOp:D}&cancel=true",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, cancelDelete.StatusCode);
            using var cancelDoc = JsonDocument.Parse(await cancelDelete.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", cancelDoc.RootElement.GetProperty("status").GetString());
        }

        // After cancel tombstone, a valid DELETE completes and replays idempotently.
        var finalDisableOp = Guid.NewGuid();
        using (var finalDelete = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId2}/credential?operationId={finalDisableOp:D}&expectedProviderRevision=2&expectedCredentialRevision=1",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, finalDelete.StatusCode);
        }
        using (var replayDelete = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId2}/credential?operationId={finalDisableOp:D}&expectedProviderRevision=2&expectedCredentialRevision=1",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, replayDelete.StatusCode);
        }

        // Revoking the actor's Projects.Config authority blocks completed DELETE replay (403 Forbidden).
        await using (var revokeConn = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
        await using (var revokeCmd = new NpgsqlCommand("""
            UPDATE projects_config.project_role_assignments
            SET state = 'Revoked',
                revision = revision + 1
            WHERE membership_id = @membershipId
              AND role = 'PlatformAdmin'
            """, revokeConn))
        {
            revokeCmd.Parameters.AddWithValue("membershipId", adminMembership.MembershipId);
            await revokeCmd.ExecuteNonQueryAsync();
        }
        using (var revokedDeleteReplay = await SendCredentialJsonAsync(
            gateway.Client, HttpMethod.Delete,
            $"/api/v1/platform/byok-providers/{providerId2}/credential?operationId={finalDisableOp:D}&expectedProviderRevision=2&expectedCredentialRevision=1",
            adminToken, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedDeleteReplay.StatusCode);
        }
    }

    private sealed class ForwardToClientHandler(HttpClient target) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var clone = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery);
            foreach (var header in request.Headers)
            {
                if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                clone.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            var resp = await target.SendAsync(clone, cancellationToken);
            resp.RequestMessage = request;
            return resp;
        }
    }

    private sealed class FakeByokVault : IModelProviderByokVault, ISecretRedemption
    {
        private readonly Dictionary<(string Name, string Version), (string Value, Guid OpId, Guid SlotId, bool Enabled)> _secrets = new();
        public bool IsConfigured => true;
        public bool FailNextWriteAmbiguously { get; set; }
        public int WriteCalls { get; private set; }
        public int VerifyStoredCalls { get; private set; }
        public string? LastWrittenVersion { get; private set; }

        public void SetEnabled(string secretName, string secretVersion, bool enabled)
        {
            if (_secrets.TryGetValue((secretName, secretVersion), out var entry))
                _secrets[(secretName, secretVersion)] = entry with { Enabled = enabled };
        }

        public Task<SecretRef> WriteVersionAsync(
            string secretName,
            SecretCredential credential,
            Guid operationId,
            Guid credentialSlotId,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            var version = Guid.NewGuid().ToString("N");
            _secrets[(secretName, version)] = (credential.GetValue(), operationId, credentialSlotId, true);
            LastWrittenVersion = version;
            if (FailNextWriteAmbiguously)
            {
                FailNextWriteAmbiguously = false;
                throw new TimeoutException("Simulated ambiguous Key Vault timeout.");
            }
            return Task.FromResult(new SecretRef(secretName, version));
        }

        public Task<SecretRef?> VerifyRecoveryVersionAsync(
            string secretName,
            string secretVersion,
            Guid operationId,
            Guid credentialSlotId,
            CancellationToken cancellationToken)
        {
            if (_secrets.TryGetValue((secretName, secretVersion), out var entry) &&
                entry.Enabled &&
                entry.OpId == operationId &&
                entry.SlotId == credentialSlotId)
                return Task.FromResult<SecretRef?>(new SecretRef(secretName, secretVersion));
            return Task.FromResult<SecretRef?>(null);
        }

        public Task<SecretRef?> VerifyStoredVersionAsync(
            string secretName,
            string secretVersion,
            Guid credentialSlotId,
            CancellationToken cancellationToken)
        {
            VerifyStoredCalls++;
            if (_secrets.TryGetValue((secretName, secretVersion), out var entry) &&
                entry.Enabled &&
                !string.IsNullOrEmpty(entry.Value) &&
                entry.SlotId == credentialSlotId)
                return Task.FromResult<SecretRef?>(new SecretRef(secretName, secretVersion));
            return Task.FromResult<SecretRef?>(null);
        }

        public Task<SecretCredential> RedeemAsync(SecretRedemptionRequest request, CancellationToken cancellationToken)
        {
            if (!_secrets.TryGetValue((request.Secret.Id, request.Secret.Version), out var entry))
                throw new InvalidOperationException("Secret not found.");
            return Task.FromResult(new SecretCredential(
                entry.Value, DateTimeOffset.UtcNow.AddMinutes(5), TimeProvider.System));
        }
    }

    private sealed class InMemoryLogCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public string AllText => string.Join('\n', _lines);
        public ILogger CreateLogger(string categoryName) => new CollectorLogger(_lines);
        public void Dispose() { }

        private sealed class CollectorLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
