using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

[Collection("Knowledge PostgreSQL")]
public sealed class KnowledgeEndpointBoundaryTests(KnowledgePostgresFixture postgres)
{
    private static readonly string TestIssuer = "https://identity.test/";

    [Fact]
    public async Task EndpointUsesFreshOwnerAuthorizationAndForwardsOriginalCallerToken()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "boundary",
                    importance = "medium",
                    tags = new[] { "boundary" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-create-1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            [
                "/api/authorization/context",
                "/api/projects/project-a/runs/run-a/selection",
                "/api/authorization/context",
                "/api/projects/project-a/runs/run-a/selection"
            ],
            owner.Paths.ToArray());
        Assert.All(owner.AuthorizationHeaders, header => Assert.Equal("Bearer caller-token", header));
        Assert.All(owner.CacheControlHeaders, header => Assert.Null(header));
    }

    [Fact]
    public async Task EndpointAcceptsUnboundCallerWithCurrentOwnerAuthority()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "unbound owner",
                    importance = "medium",
                    tags = new[] { "unbound" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "unbound-token");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-unbound-owner");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            [
                "/api/authorization/context",
                "/api/projects/project-a/runs/run-a/selection",
                "/api/authorization/context",
                "/api/projects/project-a/runs/run-a/selection"
            ],
            owner.Paths.ToArray());
    }

    [Fact]
    public async Task EndpointRechecksWriteAuthorityAfterProviderBindingBeforeCreatingRecord()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(
            database.Options, writeAllowed: true, revokeWriteAfterFirstSelection: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "must not be committed after revocation",
                    importance = "medium",
                    tags = new[] { "revoked" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", "caller-token");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-revoked-after-binding");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            [
                "/api/authorization/context",
                "/api/projects/project-a/runs/run-a/selection",
                "/api/authorization/context"
            ],
            owner.Paths.ToArray());
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{database.Options.Schema}\".knowledge_records",
            connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DecisionArchiveRestoreApprovalAndExportUseTheVersionedOwnerRoutes()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        var importEndpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText ==
                "/api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import");
        Assert.Equal(
            (long)KnowledgeRecordTransferContract.MaximumBytes,
            importEndpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);

        async Task<Guid> CreateDecisionAsync(string key, string content, string agentId = "agent-a")
        {
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/project-a/runs/run-a/agents/{agentId}/records")
            {
                Content = JsonContent.Create(
                    new
                    {
                        kind = "proposal",
                        type = "architecture",
                        title = "decision",
                        content,
                        importance = "medium",
                        tags = new[] { "lifecycle" }
                    },
                    options: JsonOptions)
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            create.Headers.TryAddWithoutValidation("Idempotency-Key", $"{key}-create");
            using var created = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var proposalId = createdJson.RootElement.GetProperty("record").GetProperty("recordId").GetGuid();

            using var promote = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/project-a/runs/run-a/agents/{agentId}/proposals/{proposalId:D}/promote")
            {
                Content = JsonContent.Create(new { expectedRevision = 1 }, options: JsonOptions)
            };
            promote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            promote.Headers.TryAddWithoutValidation("Idempotency-Key", $"{key}-promote");
            using var promoted = await client.SendAsync(promote);
            Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
            using var promotedJson = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
            return promotedJson.RootElement.GetProperty("decision").GetProperty("recordId").GetGuid();
        }

        async Task<HttpResponseMessage> UpdateDecisionAsync(
            Guid recordId,
            int expectedRevision,
            string state,
            Guid? supersededByRecordId = null,
            string agentId = "agent-a",
            string? keySuffix = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["expectedRevision"] = expectedRevision,
                ["type"] = "architecture",
                ["title"] = "decision",
                ["content"] = recordId == Guid.Empty ? "unused" : "content",
                ["importance"] = "medium",
                ["tags"] = new[] { "lifecycle" },
                ["state"] = state
            };
            if (supersededByRecordId is { } target)
                payload["supersededByRecordId"] = target;
            var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/projects/project-a/runs/run-a/agents/{agentId}/records/{recordId:D}")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key", $"decision-{recordId:N}-{keySuffix ?? state}");
            var response = await client.SendAsync(request);
            request.Dispose();
            return response;
        }

        var first = await CreateDecisionAsync("first-decision", "first content");
        var second = await CreateDecisionAsync("second-decision", "second content");
        var foreignDirectTarget = await CreateDecisionAsync(
            "foreign-direct-target", "foreign direct target", "agent-b");
        using (var foreignDirect = await UpdateDecisionAsync(
                   first, 1, "superseded", foreignDirectTarget, keySuffix: "foreign-direct"))
        {
            Assert.Equal(HttpStatusCode.Conflict, foreignDirect.StatusCode);
            Assert.Contains("invalid_replacement",
                await foreignDirect.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var transitiveSource = await CreateDecisionAsync("foreign-chain-source", "chain source");
        var chainTarget = await CreateDecisionAsync("foreign-chain-target", "same-agent link");
        var foreignDownstream = await CreateDecisionAsync(
            "foreign-chain-downstream", "foreign downstream", "agent-b");
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{database.Options.Schema}".knowledge_records
            SET state = 'Superseded', superseded_by_record_id = @replacement
            WHERE project_id = @project AND record_id = @record
            """, connection))
        {
            command.Parameters.AddWithValue("replacement", foreignDownstream);
            command.Parameters.AddWithValue("project", "project-a");
            command.Parameters.AddWithValue("record", chainTarget);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        using (var foreignTransitive = await UpdateDecisionAsync(
                   transitiveSource, 1, "superseded", chainTarget, keySuffix: "foreign-transitive"))
        {
            Assert.Equal(HttpStatusCode.Conflict, foreignTransitive.StatusCode);
            Assert.Contains("invalid_replacement",
                await foreignTransitive.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{database.Options.Schema}".knowledge_records
            SET state = 'Active', superseded_by_record_id = NULL
            WHERE project_id = @project AND record_id = @record
            """, connection))
        {
            command.Parameters.AddWithValue("project", "project-a");
            command.Parameters.AddWithValue("record", chainTarget);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using (var supersede = await UpdateDecisionAsync(
                   first, 1, "superseded", second))
        {
            Assert.Equal(HttpStatusCode.OK, supersede.StatusCode);
            using var document = JsonDocument.Parse(await supersede.Content.ReadAsStringAsync());
            Assert.Equal("superseded",
                document.RootElement.GetProperty("record").GetProperty("state").GetString());
            Assert.Equal(second,
                document.RootElement.GetProperty("record").GetProperty("supersededByRecordId").GetGuid());
        }

        using (var archive = await UpdateDecisionAsync(second, 1, "archived"))
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        using var rejectedApproval = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/records/{second:D}/approve")
        {
            Content = JsonContent.Create(new { expectedRevision = 2 }, options: JsonOptions)
        };
        rejectedApproval.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", "caller-token");
        rejectedApproval.Headers.TryAddWithoutValidation("Idempotency-Key", "approve-while-archived");
        using var rejected = await client.SendAsync(rejectedApproval);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var restore = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/records/{second:D}/restore")
        {
            Content = JsonContent.Create(
                new { expectedRevision = 2, revision = 1, reason = "restore archived decision" },
                options: JsonOptions)
        };
        restore.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        restore.Headers.TryAddWithoutValidation("Idempotency-Key", "restore-archived-decision");
        using var restored = await client.SendAsync(restore);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var restoredJson = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        Assert.Equal("active", restoredJson.RootElement.GetProperty("record").GetProperty("state").GetString());
        Assert.Equal("pending", restoredJson.RootElement.GetProperty("record").GetProperty("trustState").GetString());

        using var approve = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/records/{second:D}/approve")
        {
            Content = JsonContent.Create(new { expectedRevision = 3, reason = "approve restored decision" },
                options: JsonOptions)
        };
        approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        approve.Headers.TryAddWithoutValidation("Idempotency-Key", "approve-restored-decision");
        using var approved = await client.SendAsync(approve);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var export = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records/export");
        export.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        using var exported = await client.SendAsync(export);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("no-store", exported.Headers.CacheControl?.ToString());
        using var bundle = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal(KnowledgeRecordTransferContract.Format,
            bundle.RootElement.GetProperty("format").GetString());
        Assert.Equal(KnowledgeRecordTransferContract.SchemaVersion,
            bundle.RootElement.GetProperty("schemaVersion").GetInt32());
        var records = bundle.RootElement.GetProperty("records");
        Assert.Equal(4, records.GetArrayLength());
        Assert.All(records.EnumerateArray(), entry =>
        {
            Assert.Equal("agent-a", entry.GetProperty("record").GetProperty("agentId").GetString());
            Assert.True(entry.GetProperty("revisions").GetArrayLength() > 0);
        });

        using var import = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records/import")
        {
            Content = JsonContent.Create(bundle.RootElement.Clone(), options: JsonOptions)
        };
        import.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        import.Headers.TryAddWithoutValidation("Idempotency-Key", "import-existing-bundle");
        using var collision = await client.SendAsync(import);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Contains("knowledge_transfer_conflict",
            await collision.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-issuer-token")]
    [InlineData("cross-issuer-token")]
    [InlineData("duplicate-bound-token")]
    [InlineData("cross-identity-token")]
    [InlineData("wrong-raw-issuer-token")]
    public async Task EndpointRejectsCallerBoundsWithoutIssuerProvenance(string token)
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "must not be written",
                    importance = "medium",
                    tags = new[] { "invalid-issuer" }
                },
                options: JsonOptions)
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-invalid-issuer-" + token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("invalid_authority_response", await response.Content.ReadAsStringAsync());
        Assert.Equal(["/api/authorization/context"], owner.Paths.ToArray());
    }

    [Fact]
    public async Task EndpointDeniesMetadataReaderFromWritesAndPrivateContext()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        using var write = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "memory",
                    type = "note",
                    content = "denied",
                    importance = "medium",
                    tags = new[] { "boundary" }
                },
                options: JsonOptions)
        };
        write.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        write.Headers.TryAddWithoutValidation("Idempotency-Key", "boundary-denied-1");
        using var denied = await client.SendAsync(write);
        using var contextRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/context?maxItems=21");
        contextRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var bounded = await client.SendAsync(contextRequest);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bounded.StatusCode);
        Assert.Contains("missing_effective_accessprivateknowledge", await bounded.Content.ReadAsStringAsync());
        Assert.Equal(2, owner.Paths.Count(path => path == "/api/authorization/context"));
        Assert.DoesNotContain(owner.Paths, path => path.EndsWith("/selection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndpointEnforcesContextBoundsForCurrentProjectOwner()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var contextRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/context?maxItems=21");
        contextRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");

        using var response = await client.SendAsync(contextRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("context_budget_exceeded", await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["/api/authorization/context", "/api/projects/project-a/runs/run-a/selection"],
            owner.Paths.ToArray());
    }

    [Fact]
    public async Task AcceptedEffectReceiptIsNoStoreRedactedAndPendingDeliveryDoesNotUndoPromotion()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        using var create = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/agents/agent-a/records")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "proposal",
                    type = "architectural",
                    title = "Private proposal",
                    content = "private-proposal-content",
                    rationale = "private-proposal-rationale",
                    importance = "medium",
                    tags = new[] { "receipt-test" }
                },
                options: JsonOptions)
        };
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        create.Headers.TryAddWithoutValidation("Idempotency-Key", "receipt-proposal");
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var proposalId = createdDocument.RootElement.GetProperty("record").GetProperty("recordId").GetGuid();

        using var promote = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/project-a/runs/run-a/agents/agent-a/proposals/{proposalId:D}/promote")
        {
            Content = JsonContent.Create(new { expectedRevision = 1 }, options: JsonOptions)
        };
        promote.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        promote.Headers.TryAddWithoutValidation("Idempotency-Key", "receipt-promote");
        using var promoted = await client.SendAsync(promote);
        Assert.Equal(HttpStatusCode.Created, promoted.StatusCode);
        using var promotedDocument = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync());
        var receiptId = promotedDocument.RootElement.GetProperty("outboxEventId").GetGuid();
        Assert.Equal("PENDING", promotedDocument.RootElement.GetProperty("delivery").GetString());
        Assert.Equal("events_audience_required",
            promotedDocument.RootElement.GetProperty("deliveryCode").GetString());
        Assert.Equal("actor-a",
            promotedDocument.RootElement.GetProperty("requiredAudienceSubject").GetString());
        Assert.Equal("events-tests",
            promotedDocument.RootElement.GetProperty("requiredAudience").GetString());

        using var read = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        read.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var response = await client.SendAsync(read);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("private-proposal-content", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-proposal-rationale", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caller-token", body, StringComparison.Ordinal);
        Assert.Contains("\"receiptId\"", body, StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"actor-a\"", body, StringComparison.Ordinal);

        var revokedOwner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: false);
        await using var revokedApp = await CreateAppAsync(database, revokedOwner);
        using var revokedClient = revokedApp.GetTestClient();
        using var revokedRead = new HttpRequestMessage(
            HttpMethod.Get, $"/internal/accepted-effects/{receiptId:D}");
        revokedRead.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", "caller-token");
        using var revokedResponse = await revokedClient.SendAsync(revokedRead);
        Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);
        Assert.Equal("no-store", revokedResponse.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(receiptId.ToString("D"), await revokedResponse.Content.ReadAsStringAsync());

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var status = new NpgsqlCommand(
            $"SELECT delivered_at IS NULL FROM \"{database.Options.Schema}\".outbox_events WHERE id = @id",
            connection);
        status.Parameters.AddWithValue("id", receiptId);
        Assert.True((bool)(await status.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ScopedAcceptedEffectReceiptDoesNotCreateMissingMemoryProviderBinding()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/projects/project-a/runs/run-a/accepted-effects/{Guid.NewGuid():D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("memory_provider_unavailable", body, StringComparison.Ordinal);
        Assert.Contains("no persisted Memory provider binding", body, StringComparison.Ordinal);
        Assert.Equal(
            ["/api/authorization/context", "/api/projects/project-a/runs/run-a/selection"],
            owner.Paths.ToArray());

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var bindingCount = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{database.Options.Schema}\".memory_provider_bindings",
            connection);
        Assert.Equal(0L, (long)(await bindingCount.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ScopedCosmosReceiptUsesPersistedPinAndNeverFallsBackWhenResourceChangesOrAdapterIsMissing()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var receiptId = Guid.NewGuid();
        var receipt = CreateReceipt(receiptId);
        var options = CosmosOptions("cosmos-resource-a");
        var store = new ReceiptOnlyCosmosMemoryStore(options, receipt);
        var cosmosProvider = new CosmosMemoryProvider(store, options);
        var owner = new FakeProjectsOwnerHandler(
            database.Options,
            writeAllowed: true,
            selectedMemoryDescriptor: cosmosProvider.Descriptor,
            selectedOptionsSchemaVersion: options.OptionsSchemaVersion,
            selectedOptionsRevision: options.OptionsRevision);

        await using (var app = await CreateAppAsync(
                         database, owner, cosmosProvider: cosmosProvider, cosmosOptions: options))
        using (var client = app.GetTestClient())
        {
            using var search = new HttpRequestMessage(
                HttpMethod.Get, "/api/projects/project-a/runs/run-a/agents/agent-a/records");
            search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            using var searchResponse = await client.SendAsync(search);
            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

            using var read = new HttpRequestMessage(
                HttpMethod.Get,
                $"/internal/projects/project-a/runs/run-a/accepted-effects/{receiptId:D}");
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            using var readResponse = await client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
            Assert.Equal("no-store", readResponse.Headers.CacheControl?.ToString());
            Assert.Contains(receiptId.ToString("D"), await readResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                (CosmosMemoryProvider.ProviderId, options.ResourceId),
                await ReadMemoryBindingAsync(database));
        }

        var changedOptions = options with { ResourceId = "cosmos-resource-b" };
        var changedProvider = new CosmosMemoryProvider(store, changedOptions);
        var changedOwner = new FakeProjectsOwnerHandler(
            database.Options,
            writeAllowed: true,
            selectedMemoryDescriptor: changedProvider.Descriptor,
            selectedOptionsSchemaVersion: changedOptions.OptionsSchemaVersion,
            selectedOptionsRevision: changedOptions.OptionsRevision);
        await using (var changedApp = await CreateAppAsync(
                         database,
                         changedOwner,
                         cosmosProvider: changedProvider,
                         cosmosOptions: changedOptions))
        using (var client = changedApp.GetTestClient())
        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/internal/projects/project-a/runs/run-a/accepted-effects/{receiptId:D}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("memory_provider_binding_conflict", await response.Content.ReadAsStringAsync());
        }

        var missingAdapterOwner = new FakeProjectsOwnerHandler(
            database.Options,
            writeAllowed: true,
            selectedMemoryDescriptor: cosmosProvider.Descriptor,
            selectedOptionsSchemaVersion: options.OptionsSchemaVersion,
            selectedOptionsRevision: options.OptionsRevision);
        await using (var missingAdapterApp = await CreateAppAsync(
                         database,
                         missingAdapterOwner,
                         cosmosProvider: cosmosProvider,
                         cosmosOptions: options,
                         includeCosmosAdapter: false))
        using (var client = missingAdapterApp.GetTestClient())
        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/internal/projects/project-a/runs/run-a/accepted-effects/{receiptId:D}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("memory_provider_unavailable", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(
            (CosmosMemoryProvider.ProviderId, options.ResourceId),
            await ReadMemoryBindingAsync(database));
    }

    private async Task<WebApplication> CreateAppAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase database,
        FakeProjectsOwnerHandler owner,
        HttpMessageHandler? eventsHandler = null,
        CosmosMemoryProvider? cosmosProvider = null,
        CosmosMemoryOptions? cosmosOptions = null,
        bool includeCosmosAdapter = true)
    {
        var provider = database.Provider;
        var registrations = new List<ProviderRegistration>
        {
            new(
            provider.Descriptor,
            Enabled: true,
            database.Options.OptionsRevision,
            database.Options.OptionsSchemaVersion)
        };
        var overridePermissions = new List<ProviderOverridePermission>
        {
            new(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)
        };
        if (cosmosProvider is not null)
        {
            if (cosmosOptions is null)
                throw new ArgumentNullException(nameof(cosmosOptions));
            registrations.Add(new ProviderRegistration(
                cosmosProvider.Descriptor,
                Enabled: true,
                cosmosOptions.OptionsRevision,
                cosmosOptions.OptionsSchemaVersion));
            overridePermissions.Add(new ProviderOverridePermission(
                ProviderSeam.Memory, CosmosMemoryProvider.ProviderId));
        }
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            registrations,
            [new ProviderSelection(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)],
            overridePermissions).Value);
        var runtimeOptions = new KnowledgeRuntimeOptions(
            new Uri("https://identity.test/"),
            "knowledge-tests",
            new Uri("https://projects.test/"),
            new Uri("https://events.test/"),
            "events-tests",
            database.Options,
            MaximumContextCandidates: 100,
            DefaultContextItems: 20,
            DefaultContextTokens: 1000);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(new PostgresOutbox(database.DataSource, database.Options.Schema));
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(new ProviderResolver(catalog));
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<IMemoryProvider>(provider);
        var memoryProviders = new Dictionary<string, IMemoryProvider>(StringComparer.Ordinal)
        {
            [NativePostgresMemoryProvider.ProviderId] = provider
        };
        if (cosmosProvider is not null && includeCosmosAdapter)
            memoryProviders.Add(CosmosMemoryProvider.ProviderId, cosmosProvider);
        builder.Services.AddSingleton<IReadOnlyDictionary<string, IMemoryProvider>>(memoryProviders);
        builder.Services.AddSingleton<ProjectsConfigClient>(services => new ProjectsConfigClient(
            new HttpClient(owner) { BaseAddress = runtimeOptions.ProjectsConfigBaseAddress },
            services.GetRequiredService<IHttpContextAccessor>(),
            runtimeOptions));
        builder.Services.AddSingleton(new HttpClient(eventsHandler ?? new UnauthorizedEventsHandler())
        {
            BaseAddress = runtimeOptions.EventsBaseAddress
        });
        builder.Services.AddSingleton<AcceptedEffectRelay>();
        builder.Services.AddScoped<KnowledgeProviderBindingService>();
        builder.Services.AddSingleton<MemoryContextCompiler>();
        builder.Services.AddScoped<KnowledgeApplicationService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKnowledgeEndpoints();
        await app.StartAsync();
        return app;
    }

    private static CosmosMemoryOptions CosmosOptions(string resourceId) =>
        new(
            new Uri("https://memory.documents.azure.com/"),
            "agentweaver",
            "knowledge",
            resourceId,
            1,
            "cosmos-options-v1",
            CosmosMemoryOptions.CurrentOptionsSchemaVersion);

    private static AcceptedEffectReceipt CreateReceipt(Guid receiptId) =>
        new(
            receiptId,
            AcceptedEffectContractVersions.CurrentSchemaVersion,
            AcceptedEffectContractVersions.CurrentEventVersion,
            "project-a",
            "run-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            TestIssuer,
            "actor-a",
            "tenant-a",
            "project-a",
            "run-a",
            ProjectAuthorityResourceType.Project,
            "project-a",
            1,
            1,
            1,
            1,
            "context-v1",
            DateTimeOffset.UtcNow);

    private static async Task<(string ProviderId, string ResourceId)> ReadMemoryBindingAsync(
        NativePostgresMemoryProviderTests.KnowledgeDatabase database)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT provider_id, resource_id
            FROM "{database.Options.Schema}".memory_provider_bindings
            WHERE project_id = 'project-a' AND run_id = 'run-a'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private sealed class ReceiptOnlyCosmosMemoryStore(
        CosmosMemoryOptions options,
        AcceptedEffectReceipt receipt) : ICosmosMemoryDocumentStore
    {
        private readonly MemoryStoredDocument _stored = new(
            new KnowledgeMemoryDocument(
                $"accepted-effect:{receipt.ReceiptId:N}",
                receipt.ProjectId,
                "accepted-effect",
                Receipt: receipt),
            "test-etag");

        public Task<CosmosMemoryContainerIdentity> ReadContainerIdentityAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CosmosMemoryContainerIdentity(
                options.DatabaseId,
                options.ContainerId,
                [CosmosMemoryOptions.PartitionKeyPath],
                DefaultTimeToLiveSeconds: null,
                HasRequiredSearchCompositeIndex: true));
        }

        public Task<MemoryStoredDocument?> ReadAsync(
            string projectId,
            string documentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<MemoryStoredDocument?>(
                projectId == _stored.Document.ProjectId && documentId == _stored.Document.Id
                    ? _stored
                    : null);
        }

        public Task<IReadOnlyList<KnowledgeMemoryDocument>> FindAcceptedEffectAsync(
            Guid receiptId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KnowledgeMemoryDocument> documents = receiptId == receipt.ReceiptId
                ? [_stored.Document]
                : [];
            return Task.FromResult(documents);
        }

        public Task<KnowledgeRecordPage> SearchAsync(
            KnowledgeRecordQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new KnowledgeRecordPage(
                ImmutableArray<KnowledgeRecord>.Empty, 0, query.Page, query.PageSize));
        }

        public Task<IReadOnlyList<KnowledgeRecord>> ReadTransferCandidatesAsync(
            string projectId,
            string agentId,
            int maximumRecords,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<KnowledgeRecord>>([]);
        }

        public Task<IReadOnlyCollection<Guid>> FindRevisionIdsAsync(
            string projectId,
            IReadOnlyCollection<Guid> revisionIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyCollection<Guid>>([]);
        }

        public Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
            string projectId,
            Guid recordId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new KnowledgeRecordRevisionPage(
                ImmutableArray<KnowledgeRecordRevision>.Empty, 0, page, pageSize));
        }

        public Task<IReadOnlyList<KnowledgeRecord>> ReadContextCandidatesAsync(
            string projectId,
            string agentId,
            string runId,
            int maximumRecords,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<KnowledgeRecord>>([]);
        }

        public Task<bool> HasUndeliveredPredecessorAsync(
            string projectId,
            string streamId,
            long sequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<MemoryBatchResult> ExecuteBatchAsync(
            string projectId,
            IReadOnlyList<MemoryBatchOperation> operations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MemoryBatchResult(MemoryBatchStatus.Failed));
        }
    }

    private sealed class FakeProjectsOwnerHandler(
        NativePostgresMemoryOptions options,
        bool writeAllowed,
        bool bindCaller = true,
        ProviderDescriptor? selectedMemoryDescriptor = null,
        int? selectedOptionsSchemaVersion = null,
        string? selectedOptionsRevision = null,
        bool revokeWriteAfterFirstSelection = false) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly ConcurrentQueue<string?> _authorizationHeaders = new();
        private readonly ConcurrentQueue<string?> _cacheControlHeaders = new();
        private int _writeAllowed = writeAllowed ? 1 : 0;
        private int _selectionCount;

        public IReadOnlyCollection<string> Paths => _paths;
        public IReadOnlyCollection<string?> AuthorizationHeaders => _authorizationHeaders;
        public IReadOnlyCollection<string?> CacheControlHeaders => _cacheControlHeaders;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _paths.Enqueue(path);
            _authorizationHeaders.Enqueue(request.Headers.Authorization?.ToString());
            _cacheControlHeaders.Enqueue(request.Headers.CacheControl?.ToString());
            object payload;
            if (path == "/api/authorization/context")
                payload = Authority();
            else if (path == "/api/projects/project-a/runs/run-a/selection")
            {
                payload = Selection();
                if (revokeWriteAfterFirstSelection &&
                    Interlocked.Increment(ref _selectionCount) == 1)
                    Interlocked.Exchange(ref _writeAllowed, 0);
            }
            else
                throw new InvalidOperationException($"Unexpected Projects & Config route '{path}'.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            });
        }

        private ProjectAuthorizationContextResponse Authority()
        {
            var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadProjects, 1));
            permissions.Add(new ProjectAuthorizationPermissionGrant(
                ProjectAuthorizationPermission.ReadRunSelection, 1));
            if (Volatile.Read(ref _writeAllowed) != 0)
            {
                permissions.Add(new ProjectAuthorizationPermissionGrant(
                    ProjectAuthorizationPermission.WriteProjects, 1));
                permissions.Add(new ProjectAuthorizationPermissionGrant(
                    ProjectAuthorizationPermission.AccessPrivateKnowledge, 1));
            }
            return new ProjectAuthorizationContextResponse(
                1,
                "https://identity.test/",
                "actor-a",
                "tenant-a",
                1,
                bindCaller ? "project-a" : null,
                bindCaller ? "run-a" : null,
                [new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    permissions.ToImmutable())]);
        }

        private ProjectRunSelectionResponse Selection()
        {
            var descriptor = selectedMemoryDescriptor ?? new ProviderDescriptor(
                ProviderSeam.Memory,
                NativePostgresMemoryProvider.ProviderId,
                NativePostgresMemoryProvider.AdapterVersion,
                options.OptionsSchemaVersion,
                ProviderHostingPattern.RemoteService,
                MemoryProviderCapabilities.All);
            return new ProjectRunSelectionResponse(
                "project-a",
                "run-a",
                1,
                1,
                1,
                "context-v1",
                [new EffectiveProviderSelection(
                    ProviderCardinality.Exclusive,
                    ProviderSeam.Memory,
                    [new EffectiveProviderCandidate(
                        ProviderSeam.Memory,
                        descriptor.Id,
                        descriptor.AdapterVersion.ToString(),
                        selectedOptionsSchemaVersion ?? descriptor.OptionsSchemaVersion,
                        selectedOptionsRevision ?? options.OptionsRevision,
                        descriptor.Hosting,
                        descriptor.AdvertisedCapabilities.ToImmutableArray(),
                        MemoryProviderCapabilities.All.ToImmutableArray())])],
                new ProjectRunLimitSnapshot(1000));
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = AuthenticationHeaderValue.TryParse(
                Request.Headers.Authorization.ToString(), out var authorization)
                ? authorization.Parameter
                : null;
            var subjectIssuer = token == "missing-issuer-token" ? string.Empty : TestIssuer;
            var projectIssuer = token == "cross-issuer-token" ? "https://other-identity.test/" : TestIssuer;
            var claims = new List<Claim>
            {
                IssuedClaim("sub", "actor-a", subjectIssuer),
                IssuedClaim("tenant_id", "tenant-a", TestIssuer)
            };
            if (token != "unbound-token")
            {
                claims.Add(IssuedClaim("project_id", "project-a", projectIssuer));
                claims.Add(IssuedClaim("run_id", "run-a", TestIssuer));
            }
            if (token == "duplicate-bound-token")
                claims.Add(IssuedClaim("project_id", "project-a", TestIssuer));
            if (token == "wrong-raw-issuer-token")
                claims.Add(IssuedClaim("iss", "https://other-identity.test/", TestIssuer));

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            if (token == "cross-identity-token")
                principal.AddIdentity(new ClaimsIdentity(
                    [
                        IssuedClaim("project_id", "project-a", TestIssuer),
                        IssuedClaim("run_id", "run-a", TestIssuer)
                    ],
                    Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }

        private static Claim IssuedClaim(string type, string value, string issuer) =>
            new(type, value, ClaimValueTypes.String, issuer);
    }

    private sealed class UnauthorizedEventsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
