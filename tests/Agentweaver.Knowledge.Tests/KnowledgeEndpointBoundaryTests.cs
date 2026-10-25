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
    public async Task ProjectSearchDiscoversRecordsAcrossExistingRunsAndAgents()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        async Task CreateRecordAsync(string runId, string agentId, string key, string content)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/project-a/runs/{runId}/agents/{agentId}/records")
            {
                Content = JsonContent.Create(
                    new
                    {
                        kind = "memory",
                        type = "note",
                        content,
                        importance = "medium",
                        tags = new[] { "project-search" }
                    },
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await CreateRecordAsync("run-a", "agent-a", "project-search-a", "first run");
        await CreateRecordAsync("run-b", "agent-b", "project-search-b", "second run");
        using var search = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/knowledge/records?includeInactive=true&page=1&pageSize=50");
        search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");

        using var response = await client.SendAsync(search);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(
            ["agent-a", "agent-b"],
            items.Select(item => item.GetProperty("record").GetProperty("agentId").GetString())
                .Order(StringComparer.Ordinal));
        Assert.Contains(owner.Paths, path => path == "/api/projects/project-a/runs/run-a/selection");
        Assert.Contains(owner.Paths, path => path == "/api/projects/project-a/runs/run-b/selection");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ProjectSearchCountsOnlyCallerVisiblePendingProposalsBeforePaging()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        async Task<Guid> CreateAsync(string runId, string agentId, string key, string kind)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/project-a/runs/{runId}/agents/{agentId}/records")
            {
                Content = JsonContent.Create(new
                {
                    kind,
                    type = "note",
                    title = "proposal",
                    content = key,
                    importance = "medium",
                    tags = Array.Empty<string>()
                }, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("record").GetProperty("recordId").GetGuid();
        }

        var first = await CreateAsync("run-a", "agent-a", "pending-first", "proposal");
        var second = await CreateAsync("run-b", "agent-b", "pending-second", "proposal");
        var rejected = await CreateAsync("run-a", "agent-a", "rejected-proposal", "proposal");
        var promoted = await CreateAsync("run-a", "agent-a", "promoted-proposal", "proposal");
        await CreateAsync("run-a", "agent-a", "active-memory", "memory");
        using (var reject = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/api/projects/project-a/runs/run-a/agents/agent-a/proposals/{rejected:D}/reject")
               { Content = JsonContent.Create(new { expectedRevision = 1 }, options: JsonOptions) })
        {
            reject.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            reject.Headers.TryAddWithoutValidation("Idempotency-Key", "reject-third");
            using var response = await client.SendAsync(reject);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var promotion = await database.Provider.PromoteProposalAsync(
            "project-a",
            "run-a",
            promoted,
            1,
            new string('a', 64),
            new AcceptedEffectAuthorizationBounds(
                "https://identity.test/",
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
                "context-a"),
            "promote-excluded-proposal");
        Assert.Equal(KnowledgeWriteStatus.Updated, promotion.Status);
        var promotedProposal = await database.Provider.ReadAsync("project-a", promoted);
        Assert.Equal(KnowledgeRecordState.Promoted, promotedProposal!.State);
        Assert.Equal(KnowledgeTrustState.Pending, promotedProposal.TrustState);

        async Task<JsonDocument> SearchAsync(int page)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/projects/project-a/knowledge/records?kind=proposal&state=pending&page={page}&pageSize=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        using var page1 = await SearchAsync(1);
        using var page2 = await SearchAsync(2);
        Assert.Equal(2, page1.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page2.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { first, second }.Order(),
            new[] { page1, page2 }.Select(body => body.RootElement.GetProperty("items")[0]
                    .GetProperty("record").GetProperty("recordId").GetGuid())
                .Order());
        Assert.All(new[] { page1, page2 }, body =>
            Assert.Equal("pending", body.RootElement.GetProperty("items")[0]
                .GetProperty("record").GetProperty("state").GetString()));

        // The new filter must not bypass the unbound-token and current private-knowledge checks.
        using var runBound = new HttpRequestMessage(HttpMethod.Get,
            "/api/projects/project-a/knowledge/records?kind=proposal&state=pending");
        runBound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        using var denied = await client.SendAsync(runBound);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        owner.DenyRunSelectionPermission();
        using var unbound = new HttpRequestMessage(HttpMethod.Get,
            "/api/projects/project-a/knowledge/records?kind=proposal&state=pending");
        unbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
        using var revoked = await client.SendAsync(unbound);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Theory]
    [InlineData("kind=memory&state=pending")]
    [InlineData("state=pending")]
    [InlineData("kind=proposal&state=active")]
    public async Task ProjectSearchRejectsUnsupportedStateFilter(string filters)
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/projects/project-a/knowledge/records?{filters}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AgentHistoryReturnsOnlyMemoryForTheRequestedAgent()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(
            database.Options, writeAllowed: true, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        async Task CreateRecordAsync(string runId, string agentId, string key, string kind, string content)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/project-a/runs/{runId}/agents/{agentId}/records")
            {
                Content = JsonContent.Create(
                    new
                    {
                        kind,
                        type = "note",
                        content,
                        importance = "medium",
                        tags = new[] { "agent-history" }
                    },
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        // Real PostgreSQL-backed records prove that the route filters by agent and Memory kind.
        await CreateRecordAsync("run-a", "agent-a", "agent-history-a1", "memory", "Agent A memory");
        await CreateRecordAsync("run-a", "agent-b", "agent-history-b1", "memory", "Agent B memory");
        await CreateRecordAsync("run-b", "agent-a", "agent-history-a2", "sessionContext", "Agent A session");
        owner.DenyRunSelectionPermission();
        owner.ClearPaths();
        using var historyRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/internal/projects/project-a/agents/agent-a/history");
        historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
        historyRequest.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");

        using var historyResponse = await client.SendAsync(historyRequest);
        using var body = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var entries = root.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal("project-a", root.GetProperty("projectId").GetString());
        Assert.Equal("agent-a", root.GetProperty("agentId").GetString());
        Assert.Equal(["Agent A memory"], entries.Select(item => item.GetProperty("content").GetString()));
        Assert.DoesNotContain("accessRunId", entries[0].EnumerateObject().Select(property => property.Name));
        Assert.Contains("/internal/projects/project-a/runs/run-a/knowledge-selection", owner.Paths);
        Assert.Contains("/internal/projects/project-a/runs/run-b/knowledge-selection", owner.Paths);
        Assert.DoesNotContain(owner.Paths, path => path.EndsWith("/selection", StringComparison.Ordinal));
        Assert.Equal("no-store", historyResponse.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AgentHistoryRequiresCurrentPrivateKnowledgePermission()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: false, bindCaller: false);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/internal/projects/project-a/agents/agent-a/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(["/api/authorization/context"], owner.Paths);
    }

    [Fact]
    public async Task ProjectSearchPagesGloballyAcrossDistinctProviderResources()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var now = DateTimeOffset.UtcNow;
        var cosmosRecords = new[]
        {
            SearchRecord("agent-b", "cosmos newest", now.AddMinutes(1), "origin-run"),
            SearchRecord("agent-b", "cosmos oldest", now.AddMinutes(-1), "origin-run")
        };
        var cosmosOptions = CosmosOptions("cosmos-resource-a");
        var cosmosStore = new ReceiptOnlyCosmosMemoryStore(
            cosmosOptions, CreateReceipt(Guid.NewGuid()), cosmosRecords);
        var cosmosProvider = new CosmosMemoryProvider(cosmosStore, cosmosOptions);
        var owner = new FakeProjectsOwnerHandler(
            database.Options,
            writeAllowed: true,
            bindCaller: false,
            selectedMemoryByRun: runId => runId == "run-b"
                ? (cosmosProvider.Descriptor, cosmosOptions.OptionsSchemaVersion, cosmosOptions.OptionsRevision, null)
                : null);
        await using var app = await CreateAppAsync(
            database,
            owner,
            cosmosProvider: cosmosProvider,
            cosmosOptions: cosmosOptions);
        using var client = app.GetTestClient();

        async Task CreateNativeRecordAsync(string key, string content)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/projects/project-a/runs/run-a/agents/agent-a/records")
            {
                Content = JsonContent.Create(
                    new
                    {
                        kind = "memory",
                        type = "note",
                        content,
                        importance = "medium",
                        tags = new[] { "project-search" }
                    },
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await CreateNativeRecordAsync("global-page-a", "native first");
        await CreateNativeRecordAsync("global-page-b", "native second");

        using (var bindCosmos = new HttpRequestMessage(
                   HttpMethod.Get,
                   "/api/projects/project-a/runs/run-b/agents/agent-b/records"))
        {
            bindCosmos.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            using var response = await client.SendAsync(bindCosmos);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        async Task<(int TotalCount, (string AccessRunId, string RecordId, string? SourceRunId)[] Items)>
            SearchPageAsync(int page)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/projects/project-a/knowledge/records?includeInactive=true&page={page}&pageSize=2");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;
            var items = root.GetProperty("items").EnumerateArray()
                .Select(item =>
                {
                    var record = item.GetProperty("record");
                    return (
                        AccessRunId: item.GetProperty("accessRunId").GetString()!,
                        RecordId: record.GetProperty("recordId").GetString()!,
                        SourceRunId: record.GetProperty("sourceRunId").GetString());
                })
                .ToArray();
            return (root.GetProperty("totalCount").GetInt32(), items);
        }

        var firstPage = await SearchPageAsync(1);
        var secondPage = await SearchPageAsync(2);

        Assert.Equal(4, firstPage.TotalCount);
        Assert.Equal(4, secondPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Length);
        Assert.Equal(2, secondPage.Items.Length);
        Assert.Equal("run-b", firstPage.Items[0].AccessRunId);
        Assert.Equal(cosmosRecords[0].RecordId.ToString(), firstPage.Items[0].RecordId);
        Assert.Equal("origin-run", firstPage.Items[0].SourceRunId);
        Assert.Equal("run-a", firstPage.Items[1].AccessRunId);
        Assert.Equal("run-a", secondPage.Items[0].AccessRunId);
        Assert.Equal("run-b", secondPage.Items[1].AccessRunId);
        Assert.Equal(cosmosRecords[1].RecordId.ToString(), secondPage.Items[1].RecordId);
        Assert.Equal(
            4,
            firstPage.Items.Concat(secondPage.Items)
                .Select(item => item.RecordId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task ProjectSearchCountsSharedResourceOnceAcrossLegacyAndCurrentCapabilityPins()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var legacyDescriptor = database.Provider.Descriptor with
        {
            AdvertisedCapabilities = MemoryProviderCapabilities.All
                .Remove(MemoryProviderCapabilities.AcceptedEffectDelivery)
        };
        var owner = new FakeProjectsOwnerHandler(
            database.Options,
            writeAllowed: true,
            bindCaller: false,
            selectedMemoryByRun: runId => runId == "run-b"
                ? (legacyDescriptor, database.Options.OptionsSchemaVersion, database.Options.OptionsRevision,
                    legacyDescriptor.AdvertisedCapabilities.ToImmutableArray())
                : null);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();

        async Task CreateRecordAsync(string key, string content)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/projects/project-a/runs/run-a/agents/agent-a/records")
            {
                Content = JsonContent.Create(
                    new
                    {
                        kind = "memory",
                        type = "note",
                        content,
                        importance = "medium",
                        tags = new[] { "project-search" }
                    },
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await CreateRecordAsync("legacy-capability-pin-a", "first record");
        await CreateRecordAsync("legacy-capability-pin-b", "second record");
        using (var bindLegacyPin = new HttpRequestMessage(
                   HttpMethod.Get,
                   "/api/projects/project-a/runs/run-b/agents/agent-b/records"))
        {
            bindLegacyPin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
            using var response = await client.SendAsync(bindLegacyPin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT negotiated_capabilities
            FROM "{database.Options.Schema}".memory_provider_bindings
            WHERE project_id = 'project-a' AND run_id IN ('run-a', 'run-b')
            ORDER BY run_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            using var currentPin = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(MemoryProviderCapabilities.All.Count, currentPin.RootElement.GetArrayLength());
            Assert.True(await reader.ReadAsync());
            using var legacyPin = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(MemoryProviderCapabilities.All.Count - 1, legacyPin.RootElement.GetArrayLength());
            Assert.False(await reader.ReadAsync());
        }

        using var search = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/knowledge/records?includeInactive=true&page=1&pageSize=10");
        search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unbound-token");
        using var searchResponse = await client.SendAsync(search);
        using var searchBody = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        var items = searchBody.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Equal(2, searchBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, items.Length);
        Assert.Equal(2, items.Select(item => item.GetProperty("record").GetProperty("recordId").GetString())
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ProjectSearchRejectsRunBoundAuthority()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/knowledge/records");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "project_wide_authority_required",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("code").GetString());
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

    [Fact]
    public async Task DecisionLifecycleChangesCurrentContextAndKeepsAcceptedRevisionsReadable()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(database.Options, writeAllowed: true);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        const string agentRoute = "/api/projects/project-a/runs/run-a/agents/agent-a";

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? key)
        {
            using var request = new HttpRequestMessage(method, agentRoute + path);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: JsonOptions);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
            if (key is not null)
                request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            return await client.SendAsync(request);
        }

        async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        async Task<Guid> CreateDecisionAsync(string key, string type, string content)
        {
            using var created = await SendAsync(HttpMethod.Post, "/records", new
            {
                kind = "proposal",
                type,
                title = key,
                content,
                importance = "medium",
                tags = new[] { "lifecycle" }
            }, $"{key}-create");
            var proposalId = (await ReadJsonAsync(created, HttpStatusCode.Created))
                .GetProperty("record").GetProperty("recordId").GetGuid();
            using var promoted = await SendAsync(
                HttpMethod.Post, $"/proposals/{proposalId:D}/promote", new { expectedRevision = 1 }, $"{key}-promote");
            return (await ReadJsonAsync(promoted, HttpStatusCode.Created))
                .GetProperty("decision").GetProperty("recordId").GetGuid();
        }

        object DecisionBody(int expectedRevision, string type, string content, string state, Guid? replacement = null) =>
            new
            {
                expectedRevision,
                type,
                title = "decision",
                content,
                importance = "medium",
                tags = new[] { "lifecycle" },
                state,
                supersededByRecordId = replacement
            };

        async Task<Dictionary<Guid, (int Revision, Guid RevisionId)>> ReadContextDecisionsAsync()
        {
            using var response = await SendAsync(HttpMethod.Get, "/context", null, null);
            var json = await ReadJsonAsync(response, HttpStatusCode.OK);
            return json.GetProperty("revisionReferences").EnumerateArray()
                .Where(reference => reference.GetProperty("kind").GetString() == "decision")
                .ToDictionary(
                    reference => reference.GetProperty("recordId").GetGuid(),
                    reference => (
                        reference.GetProperty("revision").GetInt32(),
                        reference.GetProperty("revisionId").GetGuid()));
        }

        var original = await CreateDecisionAsync("original", "architectural", "Store run state in PostgreSQL.");
        var replacement = await CreateDecisionAsync("replacement", "architectural", "Store run state in Cosmos DB.");
        var scope = await CreateDecisionAsync("scope", "scope", "Ship P2 with the agreed scope.");

        var acceptedByPastRun = await ReadContextDecisionsAsync();
        Assert.True(acceptedByPastRun.Keys.ToHashSet().SetEquals([original, replacement, scope]));

        var supersedeBody = DecisionBody(1, "architectural", "Store run state in PostgreSQL.", "superseded", replacement);
        using (var superseded = await SendAsync(
                   HttpMethod.Put, $"/records/{original:D}", supersedeBody, "supersede-original"))
            await ReadJsonAsync(superseded, HttpStatusCode.OK);
        using (var replay = await SendAsync(
                   HttpMethod.Put, $"/records/{original:D}", supersedeBody, "supersede-original"))
            Assert.Equal(2, (await ReadJsonAsync(replay, HttpStatusCode.OK))
                .GetProperty("record").GetProperty("revision").GetInt32());
        using (var stale = await SendAsync(
                   HttpMethod.Put, $"/records/{original:D}", supersedeBody, "supersede-original-stale"))
            Assert.Equal("stale_revision", (await ReadJsonAsync(stale, HttpStatusCode.Conflict))
                .GetProperty("code").GetString());
        using (var cycle = await SendAsync(
                   HttpMethod.Put,
                   $"/records/{replacement:D}",
                   DecisionBody(1, "architectural", "Store run state in Cosmos DB.", "superseded", original),
                   "supersede-cycle"))
            await ReadJsonAsync(cycle, HttpStatusCode.Conflict);

        var afterSupersession = await ReadContextDecisionsAsync();
        Assert.DoesNotContain(original, afterSupersession.Keys);
        Assert.Equal(acceptedByPastRun[replacement], afterSupersession[replacement]);

        using (var corrected = await SendAsync(
                   HttpMethod.Put,
                   $"/records/{scope:D}",
                   DecisionBody(1, "scope", "Ship P2 with the agreed scope and its docs.", "active"),
                   "correct-scope"))
            Assert.Equal("pending", (await ReadJsonAsync(corrected, HttpStatusCode.OK))
                .GetProperty("record").GetProperty("trustState").GetString());
        Assert.DoesNotContain(scope, (await ReadContextDecisionsAsync()).Keys);
        using (var approved = await SendAsync(
                   HttpMethod.Post, $"/records/{scope:D}/approve", new { expectedRevision = 2 }, "approve-scope"))
            await ReadJsonAsync(approved, HttpStatusCode.OK);
        var afterCorrection = await ReadContextDecisionsAsync();
        Assert.Equal(3, afterCorrection[scope].Revision);
        Assert.NotEqual(acceptedByPastRun[scope].RevisionId, afterCorrection[scope].RevisionId);

        using (var archived = await SendAsync(
                   HttpMethod.Put,
                   $"/records/{replacement:D}",
                   DecisionBody(1, "architectural", "Store run state in Cosmos DB.", "archived"),
                   "archive-replacement"))
            await ReadJsonAsync(archived, HttpStatusCode.OK);
        Assert.Equal([scope], (await ReadContextDecisionsAsync()).Keys.ToArray());

        using (var restored = await SendAsync(
                   HttpMethod.Post,
                   $"/records/{replacement:D}/restore",
                   new { expectedRevision = 2, revision = 1, reason = "restore archived decision" },
                   "restore-replacement"))
            await ReadJsonAsync(restored, HttpStatusCode.OK);
        Assert.DoesNotContain(replacement, (await ReadContextDecisionsAsync()).Keys);
        using (var approvedRestore = await SendAsync(
                   HttpMethod.Post,
                   $"/records/{replacement:D}/approve",
                   new { expectedRevision = 3 },
                   "approve-restored-replacement"))
            await ReadJsonAsync(approvedRestore, HttpStatusCode.OK);
        Assert.Equal(4, (await ReadContextDecisionsAsync())[replacement].Revision);

        foreach (var (recordId, accepted) in acceptedByPastRun)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/records/{recordId:D}/revisions", null, null);
            var history = (await ReadJsonAsync(response, HttpStatusCode.OK)).GetProperty("items")
                .EnumerateArray().ToArray();
            var pinned = Assert.Single(history, item => item.GetProperty("revision").GetInt32() == accepted.Revision);
            Assert.Equal(accepted.RevisionId, pinned.GetProperty("revisionId").GetGuid());
            Assert.Equal("active", pinned.GetProperty("state").GetString());
        }
        using (var originalHistory = await SendAsync(
                   HttpMethod.Get, $"/records/{original:D}/revisions", null, null))
        {
            var items = (await ReadJsonAsync(originalHistory, HttpStatusCode.OK)).GetProperty("items")
                .EnumerateArray().ToArray();
            Assert.Equal([2, 1], items.Select(item => item.GetProperty("revision").GetInt32()));
            Assert.Equal("Store run state in PostgreSQL.", items[1].GetProperty("content").GetString());
            Assert.Equal(replacement, items[0].GetProperty("supersededByRecordId").GetGuid());
        }
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
    public async Task EndpointCannotWidenConfiguredPromptTokenBudget()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(
            database.Options, writeAllowed: true, maxPromptTokens: 500);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var contextRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/context?maxTokens=501");
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
    public async Task EndpointUsesLocalContextTokenCeilingWhenRunSelectionHasNoPromptTokenLimit()
    {
        await using var database = await NativePostgresMemoryProviderTests.KnowledgeDatabase.CreateAsync(postgres);
        var owner = new FakeProjectsOwnerHandler(
            database.Options, writeAllowed: true, maxPromptTokens: null);
        await using var app = await CreateAppAsync(database, owner);
        using var client = app.GetTestClient();
        using var contextRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-a/runs/run-a/agents/agent-a/context?maxTokens=1001");
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

    private static KnowledgeRecord SearchRecord(
        string agentId,
        string content,
        DateTimeOffset updatedAt,
        string sourceRunId) =>
        new(
            Guid.NewGuid(),
            "project-a",
            agentId,
            KnowledgeRecordKind.Memory,
            "note",
            content,
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            KnowledgeRecordState.Active,
            KnowledgeTrustState.Approved,
            1,
            Guid.NewGuid(),
            null,
            sourceRunId,
            null,
            null,
            updatedAt,
            updatedAt);

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
        AcceptedEffectReceipt receipt,
        IReadOnlyCollection<KnowledgeRecord>? records = null) : ICosmosMemoryDocumentStore
    {
        private readonly ImmutableArray<KnowledgeRecord> _records =
            records?.ToImmutableArray() ?? ImmutableArray<KnowledgeRecord>.Empty;
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
            var matching = _records
                .Where(record => record.ProjectId == query.ProjectId)
                .Where(record => query.AgentId is null || record.AgentId == query.AgentId)
                .Where(record => query.Kind is null || record.Kind == query.Kind)
                .Where(record => query.State is null || record.State == query.State)
                .Where(record => query.IncludeInactive || record.State == KnowledgeRecordState.Active)
                .Where(record => query.Query is null ||
                    record.Content.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.UpdatedAt)
                .ThenBy(record => record.RecordId)
                .ToArray();
            var offset = checked((query.Page - 1) * query.PageSize);
            return Task.FromResult(new KnowledgeRecordPage(
                matching.Skip(offset).Take(query.PageSize).ToImmutableArray(),
                matching.Length,
                query.Page,
                query.PageSize));
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
        bool revokeWriteAfterFirstSelection = false,
        int? maxPromptTokens = 1000,
        Func<string, (ProviderDescriptor Descriptor, int OptionsSchemaVersion, string OptionsRevision,
            ImmutableArray<string>? RequiredCapabilities)?>?
            selectedMemoryByRun = null,
        bool readRunSelectionAllowed = true) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly ConcurrentQueue<string?> _authorizationHeaders = new();
        private readonly ConcurrentQueue<string?> _cacheControlHeaders = new();
        private int _writeAllowed = writeAllowed ? 1 : 0;
        private int _readRunSelectionAllowed = readRunSelectionAllowed ? 1 : 0;
        private int _selectionCount;

        public IReadOnlyCollection<string> Paths => _paths;
        public IReadOnlyCollection<string?> AuthorizationHeaders => _authorizationHeaders;
        public IReadOnlyCollection<string?> CacheControlHeaders => _cacheControlHeaders;
        public void DenyRunSelectionPermission() => Interlocked.Exchange(ref _readRunSelectionAllowed, 0);
        public void ClearPaths()
        {
            while (_paths.TryDequeue(out _))
            {
            }
        }

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
            else if (path.StartsWith("/internal/projects/project-a/runs/", StringComparison.Ordinal) &&
                     path.EndsWith("/knowledge-selection", StringComparison.Ordinal))
            {
                var runId = path["/internal/projects/project-a/runs/".Length..^"/knowledge-selection".Length];
                payload = Selection(runId);
                if (revokeWriteAfterFirstSelection &&
                    Interlocked.Increment(ref _selectionCount) == 1)
                    Interlocked.Exchange(ref _writeAllowed, 0);
            }
            else if (path.StartsWith("/api/projects/project-a/runs/", StringComparison.Ordinal) &&
                     path.EndsWith("/selection", StringComparison.Ordinal))
            {
                var runId = path["/api/projects/project-a/runs/".Length..^"/selection".Length];
                payload = Selection(runId);
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
            if (Volatile.Read(ref _readRunSelectionAllowed) != 0)
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

        private ProjectRunSelectionResponse Selection(string runId)
        {
            var runSelection = selectedMemoryByRun?.Invoke(runId);
            var descriptor = runSelection?.Descriptor ?? selectedMemoryDescriptor ?? new ProviderDescriptor(
                ProviderSeam.Memory,
                NativePostgresMemoryProvider.ProviderId,
                NativePostgresMemoryProvider.AdapterVersion,
                options.OptionsSchemaVersion,
                ProviderHostingPattern.RemoteService,
                MemoryProviderCapabilities.All);
            return new ProjectRunSelectionResponse(
                "project-a",
                runId,
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
                        runSelection?.OptionsSchemaVersion ??
                            selectedOptionsSchemaVersion ?? descriptor.OptionsSchemaVersion,
                        runSelection?.OptionsRevision ?? selectedOptionsRevision ?? options.OptionsRevision,
                        descriptor.Hosting,
                        descriptor.AdvertisedCapabilities.ToImmutableArray(),
                        runSelection?.RequiredCapabilities ?? MemoryProviderCapabilities.All.ToImmutableArray())])],
                new ProjectRunLimitSnapshot(maxPromptTokens));
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
