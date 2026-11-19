using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Security.Claims;
using System.Net;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Theory]
    [InlineData("23505", "pk_model_provider_reference_reservations")]
    [InlineData("23514", "ck_model_provider_reference_cross_table")]
    public async Task PublicModelProviderDraftNamedDatabaseConflictReturns409(string sqlState, string constraint)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var name = "endpoint_conflict_" + operation.ToString("N");
        await using var owner = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using (var install = new Npgsql.NpgsqlCommand($"""
            CREATE FUNCTION projects_config.{name}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.operation_id = '{operation}'::uuid THEN
                    RAISE EXCEPTION 'injected conflict' USING ERRCODE = '{sqlState}', CONSTRAINT = '{constraint}';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {name} BEFORE INSERT
            ON projects_config.model_provider_reference_reservations
            FOR EACH ROW EXECUTE FUNCTION projects_config.{name}();
            """, owner))
            await install.ExecuteNonQueryAsync();
        try
        {
            using var host = await StartDraftEndpointHostAsync(fixture.DataSource, new CapturedLogs(), true);
            using var client = CreateDraftEndpointClient(host, caller.ActorId, caller.TenantId);
            var body = SaveDraftBody(operation, 0, "Conflict");
            using var request = SaveRequest("/api/platform/byok-providers",
                body[..^1] + ",\"reference\":\"conflict-" + operation.ToString("N") + "\"}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("reference_conflict", await response.Content.ReadAsStringAsync());
            Assert.False(await db.ModelProviderDrafts.AsNoTracking().AnyAsync(item => item.ProviderId == providerId));
            Assert.False(await db.ModelProviderDraftRevisions.AsNoTracking().AnyAsync(item => item.OperationId == operation));
            Assert.False(await db.ModelProviderReferenceReservations.AsNoTracking().AnyAsync(item => item.OperationId == operation));
        }
        finally
        {
            await using var remove = new Npgsql.NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS {name} ON projects_config.model_provider_reference_reservations;
                DROP FUNCTION IF EXISTS projects_config.{name}();
                """, owner);
            await remove.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PublicModelProviderDraftCorruptStoredSelectorReturnsUnavailable()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "corrupt-selector-" + suffix;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var owner = await SeedCallerAsync(db, authority, "owner-" + suffix, tenant,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Corrupt selector", CancellationToken.None);
        var head = await db.Projects.SingleAsync(item => item.ProjectId == project.ProjectId);
        var originalRevision = head.ConfigurationRevision;
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
            new ProjectConfiguration { ModelSelection = new("sentinel") }, LegacyJsonOptions()))!;
        json["modelSelection"]!["reference"] = 42;
        db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
        {
            ProjectId = project.ProjectId, Revision = originalRevision + 1,
            ConfigurationJson = json.ToJsonString(), UpdatedByActorId = owner.ActorId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        head.ConfigurationRevision++;
        await db.SaveChangesAsync();
        try
        {
            using var host = await StartDraftEndpointHostAsync(fixture.DataSource, new CapturedLogs(), true);
            using var client = CreateDraftEndpointClient(host, caller.ActorId, caller.TenantId);
            var body = SaveDraftBody(Guid.NewGuid(), 0, "Corrupt");
            using var invalid = SaveRequest("/api/platform/byok-providers", body[..^1] + ",\"reference\":42}");
            using var invalidResponse = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            Assert.Contains("model_provider_draft_invalid_request", await invalidResponse.Content.ReadAsStringAsync());
            using var request = SaveRequest("/api/platform/byok-providers", body[..^1] + ",\"reference\":\"safe-reference\"}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("model_provider_draft_unavailable", responseBody);
            Assert.DoesNotContain("sentinel", responseBody);
            Assert.DoesNotContain("modelSelection", responseBody);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        finally
        {
            head.ConfigurationRevision = originalRevision;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PublicModelProviderDraftReferenceIsCreateOnlyAndPreservesExactSpelling()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        using var host = await StartDraftEndpointHostAsync(fixture.DataSource, new CapturedLogs(), true);
        using var client = CreateDraftEndpointClient(host, caller.ActorId, caller.TenantId);
        var operation = Guid.NewGuid();
        var reference = "Case.Reference-" + Guid.NewGuid().ToString("N");
        var body = SaveDraftBody(operation, 0, "Reference");
        string WithReference(string field) => body[..^1] + ",\"reference\":" + field + "}";
        foreach (var value in new[] { "null", "42", "\"\"", "\"_invalid\"", "\"has__gap\"" })
        {
            using var invalid = SaveRequest("/api/platform/byok-providers", WithReference(value));
            using var response = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        using (var request = SaveRequest("/api/platform/byok-providers", WithReference("\"" + reference + "\"")))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(reference, json.RootElement.GetProperty("reference").GetString());
            Assert.Equal("byok-" + operation.ToString("N"), json.RootElement.GetProperty("id").GetString());
        }
        using (var update = SaveRequest(
            "/api/platform/byok-providers/byok-" + operation.ToString("N"),
            SaveDraftBody(Guid.NewGuid(), 1, "Rename")[..^1] + ",\"reference\":null}", HttpMethod.Put))
        using (var response = await client.SendAsync(update))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var read = await client.GetAsync("/api/platform/byok-providers/byok-" + operation.ToString("N")))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(reference, json.RootElement.GetProperty("reference").GetString());
        }
    }

    [Fact]
    public async Task PublicModelProviderDraftOwnerErrorsAreNoStoreAndDoNotLogRequestBodies()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "draft-http-tenant-" + suffix;
        var subject = "draft-http-admin-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, subject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var projectSubject = "draft-http-project-owner-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, projectSubject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Draft HTTP", CancellationToken.None);
        var revisionsBefore = await db.ModelProviderDraftRevisions.CountAsync();
        var logs = new CapturedLogs();
        using var host = await StartDraftEndpointHostAsync(fixture.DataSource, logs);
        var routes = new[]
        {
            (Path: "/api/platform/byok-providers", Subject: subject, ProjectId: (string?)null),
            (Path: $"/api/projects/{project.ProjectId}/byok-providers", Subject: projectSubject,
                ProjectId: (string?)project.ProjectId),
        };
        foreach (var route in routes)
        {
            using var anonymous = host.GetTestClient();
            using (var response = await anonymous.GetAsync(route.Path))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            }

            using var invalidBearer = host.GetTestClient();
            invalidBearer.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-bearer");
            using (var response = await invalidBearer.GetAsync(route.Path))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            }

            using var client = CreateDraftEndpointClient(
                host, route.Subject, tenantId, projectId: route.ProjectId);
            foreach (var field in new[]
            {
                "api_key", "apiKey", "headers", "credentialVersion", "credential_version",
                "vaultUri", "vault_uri", "secretName", "secret_name", "rateCardId",
                "scope", "projectId", "providerId", "state",
            })
            {
                var json =
                    $$"""{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":0,"name":"x","type":"openai","base_url":"https://api.openai.com/v1","model":"m","{{field}}":"draft-secret-sentinel"}""";
                using var request = SaveRequest(route.Path, json);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Contains("model_provider_draft_invalid_request", responseBody);
                Assert.DoesNotContain("draft-secret-sentinel", responseBody);
            }

            foreach (var json in new[]
            {
                """{"operationId":""",
                """{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":0,"name":"x","name":"draft-secret-sentinel","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
                """{"operationId":"00000000-0000-0000-0000-000000000000","expectedRevision":0,"name":"x","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
                """{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","name":"x","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
                """{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":null,"name":"x","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
                """{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":1,"name":"x","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
            })
            {
                using var request = SaveRequest(route.Path, json);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Contains("model_provider_draft_invalid_request", responseBody);
                Assert.DoesNotContain("draft-secret-sentinel", responseBody);
            }
            if (route.ProjectId is not null)
            {
                foreach (var reference in new[] { "null", "\"Reference\"" })
                {
                    var body = SaveDraftBody(Guid.NewGuid(), 0, "x");
                    using var request = SaveRequest(route.Path, body[..^1] + ",\"reference\":" + reference + "}");
                    using var response = await client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }
            }
            foreach (var reference in new[] { "null", "\"Reference\"" })
            {
                var body = SaveDraftBody(Guid.NewGuid(), 1, "x");
                using var request = SaveRequest(route.Path + "/provider-id",
                    body[..^1] + ",\"reference\":" + reference + "}", HttpMethod.Put);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            var providerId = "bad-input-" + Guid.NewGuid().ToString("N");
            using (var update = SaveRequest(
                route.Path + "/" + providerId,
                SaveDraftBody(Guid.NewGuid(), 0, "x"),
                HttpMethod.Put))
            using (var response = await client.SendAsync(update))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            }

            using (var unsupported = SaveRequest(
                route.Path, """{"sentinel":"draft-secret-sentinel"}""", contentType: "text/plain"))
            using (var response = await client.SendAsync(unsupported))
            {
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Contains("model_provider_draft_unsupported_media_type", responseBody);
                Assert.DoesNotContain("draft-secret-sentinel", responseBody);
            }

            foreach (var bodyKind in new[] { "fixed", "chunked", "headerless" })
            {
                using var request = OversizedSaveRequest(route.Path, bodyKind);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Contains("request_body_too_large", await response.Content.ReadAsStringAsync());
            }
        }

        Assert.DoesNotContain("draft-secret-sentinel", logs.Text);
        Assert.DoesNotContain("api_key", logs.Text);
        Assert.DoesNotContain("\"operationId\"", logs.Text);
        Assert.DoesNotContain("expectedRevision\":null", logs.Text);
        Assert.DoesNotContain("JsonException", logs.Text);
        Assert.Equal(revisionsBefore, await db.ModelProviderDraftRevisions.CountAsync());
    }

    [Fact]
    public async Task PublicModelProviderDraftRoutesSupportBothScopesAndStableBoundedPaging()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "draft-routes-tenant-" + suffix;
        var platformSubject = "draft-routes-platform-" + suffix;
        var platformCaller = await SeedCallerAsync(
            db, authorityStore, platformSubject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var projectSubject = "draft-routes-owner-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, projectSubject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Draft routes", CancellationToken.None);
        using var host = await StartDraftEndpointHostAsync(fixture.DataSource, new CapturedLogs());
        using var platformClient = CreateDraftEndpointClient(host, platformSubject, tenantId);
        using var projectClient = CreateDraftEndpointClient(
            host, projectSubject, tenantId, projectId: project.ProjectId);

        var createOperation = Guid.NewGuid();
        var platformId = "byok-" + createOperation.ToString("N");
        var createBody = SaveDraftBody(createOperation, 0, "Shared name");
        using (var request = SaveRequest("/api/platform/byok-providers", createBody))
        using (var response = await platformClient.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("byok-" + createOperation.ToString("N"),
                created.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, created.RootElement.GetProperty("revision").GetInt64());
        }
        using (var retry = SaveRequest("/api/platform/byok-providers", createBody))
        using (var response = await platformClient.SendAsync(retry))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var replay = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("byok-" + createOperation.ToString("N"),
                replay.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, replay.RootElement.GetProperty("revision").GetInt64());
        }
        Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(
            revision => revision.ProviderId == platformId));
        Assert.Equal(1, await db.ModelProviderDraftRevisions
            .Where(revision => revision.ProviderId == platformId)
            .Select(revision => revision.ProviderRevision)
            .SingleAsync());
        using (var changedReplay = SaveRequest(
            "/api/platform/byok-providers",
            SaveDraftBody(createOperation, 0, "Changed content")))
        using (var response = await platformClient.SendAsync(changedReplay))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using (var read = await platformClient.GetAsync("/api/platform/byok-providers/" + platformId))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var item = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(platformId, item.RootElement.GetProperty("id").GetString());
        }
        var updateOperation = Guid.NewGuid();
        using (var update = SaveRequest(
            "/api/platform/byok-providers/" + platformId,
            SaveDraftBody(updateOperation, 1, "Updated name"),
            HttpMethod.Put))
        using (var response = await platformClient.SendAsync(update))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, saved.RootElement.GetProperty("revision").GetInt64());
        }

        async Task<(HttpStatusCode Status, string Body)> ConcurrentUpdateAsync(string body)
        {
            using var request = SaveRequest(
                "/api/platform/byok-providers/" + platformId, body, HttpMethod.Put);
            using var response = await platformClient.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var concurrentBodies = new[]
        {
            SaveDraftBody(Guid.NewGuid(), 2, "Concurrent A"),
            SaveDraftBody(Guid.NewGuid(), 2, "Concurrent B"),
        };
        var concurrentResults = await Task.WhenAll(concurrentBodies.Select(ConcurrentUpdateAsync));
        var winningIndex = Array.FindIndex(concurrentResults, result => result.Status == HttpStatusCode.OK);
        Assert.True(winningIndex >= 0);
        var winner = concurrentResults[winningIndex];
        Assert.Equal(HttpStatusCode.OK, winner.Status);
        Assert.Equal(HttpStatusCode.Conflict,
            Assert.Single(concurrentResults, result => result.Status == HttpStatusCode.Conflict).Status);
        using (var laterUpdate = SaveRequest(
            "/api/platform/byok-providers/" + platformId,
            SaveDraftBody(Guid.NewGuid(), 3, "Later update"),
            HttpMethod.Put))
        using (var response = await platformClient.SendAsync(laterUpdate))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var retry = SaveRequest(
            "/api/platform/byok-providers/" + platformId,
            concurrentBodies[winningIndex],
            HttpMethod.Put))
        using (var response = await platformClient.SendAsync(retry))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var replay = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(3, replay.RootElement.GetProperty("revision").GetInt64());
        }

        var projectOperation = Guid.NewGuid();
        var projectBody = SaveDraftBody(projectOperation, 0, "Project model");
        var projectPath = $"/api/projects/{project.ProjectId}/byok-providers";
        using (var create = SaveRequest(projectPath, projectBody))
        using (var response = await projectClient.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("project", created.RootElement.GetProperty("scope").GetString());
            Assert.Equal(project.ProjectId, created.RootElement.GetProperty("projectId").GetString());
            Assert.Equal(1, created.RootElement.GetProperty("revision").GetInt64());
        }
        var projectId = "byok-" + projectOperation.ToString("N");
        using (var read = await projectClient.GetAsync(projectPath + "/" + projectId))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var item = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(projectId, item.RootElement.GetProperty("id").GetString());
        }
        using (var update = SaveRequest(
            projectPath + "/" + projectId,
            SaveDraftBody(Guid.NewGuid(), 1, "Project updated"),
            HttpMethod.Put))
        using (var response = await projectClient.SendAsync(update))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var wrongScope = await projectClient.GetAsync(projectPath + "/" + platformId))
            Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);
        using (var missing = await projectClient.GetAsync(projectPath + "/missing-provider"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var platformWrongScope = await platformClient.GetAsync(
            "/api/platform/byok-providers/" + projectId))
            Assert.Equal(HttpStatusCode.NotFound, platformWrongScope.StatusCode);
        using (var platformMissing = await platformClient.GetAsync("/api/platform/byok-providers/missing-provider"))
            Assert.Equal(HttpStatusCode.NotFound, platformMissing.StatusCode);
        using (var projectPage = await projectClient.GetAsync(projectPath + "?pageSize=100"))
        {
            Assert.Equal(HttpStatusCode.OK, projectPage.StatusCode);
            using var page = JsonDocument.Parse(await projectPage.Content.ReadAsStringAsync());
            Assert.Equal([projectId], page.RootElement.GetProperty("providers").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray());
            Assert.Null(page.RootElement.GetProperty("nextAfterProviderId").GetString());
        }

        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var pagePrefix = "zz-drafts-" + suffix;
        for (var index = 1; index <= 101; index++)
            await service.SaveAsync(
                platformCaller, ModelProviderBindingScope.Platform, null, $"{pagePrefix}-{index:000}",
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Repeated name", "openai", "https://api.openai.com/v1",
                    "model", null, null, [], null),
                CancellationToken.None);

        var unsafeId = "legacy-url-" + suffix;
        await service.SaveAsync(
            platformCaller, ModelProviderBindingScope.Platform, null, unsafeId,
            new SaveModelProviderDraftRequest(
                Guid.NewGuid(), 0, "Historical URL", "openai",
                "https://api.openai.com/v1?api_key=legacy-url-sentinel", "model",
                null, null, [], null),
            CancellationToken.None);
        using (var unsafeRead = await platformClient.GetAsync("/api/platform/byok-providers/" + unsafeId))
        {
            Assert.Equal(HttpStatusCode.Conflict, unsafeRead.StatusCode);
            var body = await unsafeRead.Content.ReadAsStringAsync();
            using var error = JsonDocument.Parse(body);
            Assert.Equal("model_provider_draft_metadata_requires_update",
                error.RootElement.GetProperty("code").GetString());
            Assert.Equal(unsafeId, error.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, error.RootElement.GetProperty("revision").GetInt64());
            Assert.DoesNotContain("legacy-url-sentinel", body);
            Assert.DoesNotContain("api_key", body);
        }
        using (var repair = SaveRequest(
            "/api/platform/byok-providers/" + unsafeId,
            SaveDraftBody(Guid.NewGuid(), 1, "Repaired URL"),
            HttpMethod.Put))
        using (var response = await platformClient.SendAsync(repair))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, saved.RootElement.GetProperty("revision").GetInt64());
        }

        using (var firstPage = await platformClient.GetAsync(
            $"/api/platform/byok-providers?pageSize=100&afterProviderId={pagePrefix}-000"))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var page = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var rows = page.RootElement.GetProperty("providers").EnumerateArray().ToArray();
            Assert.Equal(100, rows.Length);
            Assert.All(rows, row => Assert.Equal("Repeated name", row.GetProperty("name").GetString()));
            var cursor = page.RootElement.GetProperty("nextAfterProviderId").GetString();
            Assert.Equal($"{pagePrefix}-100", cursor);
            Assert.Equal(Enumerable.Range(1, 100)
                .Select(index => $"{pagePrefix}-{index:000}").ToArray(),
                rows.Select(row => row.GetProperty("id").GetString()!).ToArray());

            await service.SaveAsync(
                platformCaller, ModelProviderBindingScope.Platform, null, cursor!,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 1, "Renamed during paging", "openai",
                    "https://api.openai.com/v1", "model", null, null, [], null),
                CancellationToken.None);
            using var secondPage = await platformClient.GetAsync(
                $"/api/platform/byok-providers?pageSize=100&afterProviderId={cursor}");
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            using var next = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            var nextRows = next.RootElement.GetProperty("providers").EnumerateArray().ToArray();
            Assert.Equal([$"{pagePrefix}-101"],
                nextRows.Select(row => row.GetProperty("id").GetString()!).ToArray());
            Assert.Null(next.RootElement.GetProperty("nextAfterProviderId").GetString());
        }
    }

    private static HttpClient CreateDraftEndpointClient(
        IHost host,
        string subject,
        string tenantId,
        string? projectId = null,
        string scopes = "api.read projects.admin")
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        if (projectId is not null)
            client.DefaultRequestHeaders.Add("X-Test-Bound-Project", projectId);
        return client;
    }

    private static HttpRequestMessage SaveRequest(
        string path,
        string body,
        HttpMethod? method = null,
        string contentType = "application/json") =>
        new(method ?? HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

    private static string SaveDraftBody(Guid operationId, long expectedRevision, string name) =>
        $$"""{"operationId":"{{operationId:D}}","expectedRevision":{{expectedRevision}},"name":"{{name}}","type":"openai","base_url":"https://api.openai.com/v1","model":"test-model"}""";

    private static HttpRequestMessage OversizedSaveRequest(string path, string bodyKind)
    {
        var body = new string('x', ModelProviderDraftEndpoints.MaximumSaveBodyBytes + 1);
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (bodyKind == "fixed")
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        else
        {
            request.Content = new LengthlessDraftContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Version = bodyKind == "chunked" ? HttpVersion.Version11 : HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            Assert.Null(request.Content.Headers.ContentLength);
            Assert.False(request.Headers.Contains("Transfer-Encoding"));
        }
        return request;
    }

    private static async Task<IHost> StartDraftEndpointHostAsync(
        Npgsql.NpgsqlDataSource dataSource,
        ILoggerProvider loggerProvider,
        bool enableReferences = false) =>
        await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(ProjectSummaryTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                            ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddDbContextFactory<ProjectsConfigDbContext>(options => options.UseNpgsql(
                        dataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
                    services.AddScoped<ProjectAuthorizationOwner>();
                    if (enableReferences)
                    {
                        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AgentHost:ModelBindingsRevision"] = "reference-test-v1",
                                ["AgentHost:ModelBindings:fixture-binding:SourceMode"] = "Byok",
                                ["AgentHost:ModelBindings:fixture-binding:Enabled"] = "false",
                            }).Build();
                        var bindings = ModelBindingCapabilities.FromConfiguration(configuration);
                        services.AddScoped<ModelProviderDraftService>(sp => new(
                            sp.GetRequiredService<ProjectsConfigDbContext>(),
                            TimeProvider.System, null, bindings, true));
                    }
                    else
                        services.AddScoped<ModelProviderDraftService>();
                });
                web.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseModelProviderDraftNoStore();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseModelProviderDraftBodyLimit();
                    app.UseEndpoints(endpoints => endpoints.MapModelProviderDraftEndpoints());
                });
            })
            .StartAsync();

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly StringBuilder _text = new();
        public string Text => _text.ToString();

        public ILogger CreateLogger(string categoryName) => new CapturedLogger(_text);
        public void Dispose() { }

        private sealed class CapturedLogger(StringBuilder text) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (text)
                {
                    text.AppendLine(formatter(state, exception));
                    if (exception is not null)
                        text.AppendLine(exception.ToString());
                }
            }
        }
    }

    private sealed class LengthlessDraftContent(string value) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
