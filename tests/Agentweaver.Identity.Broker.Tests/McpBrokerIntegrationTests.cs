extern alias McpHost;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using McpProgram = McpHost::Program;
using McpGatewayApiClient = McpHost::Agentweaver.Mcp.GatewayApiClient;
using McpGatewayToolCatalog = McpHost::Agentweaver.Mcp.GatewayToolCatalog;
using ProjectsConfig::Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task BrokerTokenTraversesMcpAndGatewayToKnowledgeWithoutMutation()
    {
        const string audience = "https://api.test/";
        const string idempotencyKey = "mcp-broker-integration";
        var token = await IssueTokenForAudienceBrokerAsync(audience, "mcp-broker-integration");
        var observedBearers = new ConcurrentQueue<string?>();
        var observedIdempotencyKeys = new ConcurrentQueue<string?>();
        var observedSourceControlRequests =
            new ConcurrentQueue<(string Method, string Path, string? Bearer, string? Tenant, string? Body)>();

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        await using var gateway = GatewayProductionResourceServer.Start(
            new X509SecurityKey(certificate),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            knowledgeOwner: (request, _) =>
            {
                observedBearers.Enqueue(request.Headers.Authorization?.ToString());
                observedIdempotencyKeys.Enqueue(
                    request.Headers.TryGetValues("Idempotency-Key", out var values)
                        ? Assert.Single(values)
                        : null);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        """{"recordId":"broker-record-a","revision":1}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            },
            orchestratorOwner: async (request, cancellationToken) =>
            {
                var body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var path = request.RequestUri?.AbsolutePath
                    ?? throw new InvalidOperationException("Gateway omitted the source-control owner URI.");
                observedSourceControlRequests.Enqueue((
                    request.Method.Method,
                    path,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                        ? tenant.Single()
                        : null,
                    body));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        path.EndsWith("/issues", StringComparison.Ordinal)
                            ? """{"number":41,"title":"Integration issue","htmlUrl":"https://github.com/octo/agentweaver/issues/41","state":"open"}"""
                            : path.EndsWith("/pin", StringComparison.Ordinal)
                                ? """{"pinId":"pin-a","repository":"octo/agentweaver","providerId":"github","resourceId":"resource-a","resourceGeneration":1,"providerRepositoryId":12345,"defaultBranch":"main","isPrivate":true,"pinnedAt":"2026-10-08T00:00:00+00:00"}"""
                            : "[]",
                        Encoding.UTF8,
                        "application/json"),
                };
            });

        await using var mcpFactory = new BrokerValidatedMcpFactory(gateway, certificate);
        using var client = mcpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var protocolVersion = await InitializeAsync(client);
        var listedTools = await PostRpcAsync(
            client,
            new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } },
            protocolVersion);
        var tools = listedTools.GetProperty("result").GetProperty("tools");
        Assert.Contains(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_createKnowledgeRecord");
        Assert.Contains(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_createSourceControlIssue");
        Assert.Contains(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_readSourceControlReviews");
        Assert.Contains(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_pinSourceControlRepository");
        Assert.DoesNotContain(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_beginCopilotConnection");
        Assert.DoesNotContain(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_listGitHubRepositorySelections");

        var result = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_createKnowledgeRecord",
                    arguments = new
                    {
                        projectId = "project-a",
                        runId = "run-a",
                        agentId = "agent-a",
                        idempotencyKey,
                        body = new
                        {
                            kind = "memory",
                            type = "note",
                            content = "issued by the broker",
                            importance = "medium",
                            tags = new[] { "mcp" },
                        },
                    },
                },
            },
            protocolVersion);

        var toolResult = result.GetProperty("result");
        Assert.False(toolResult.GetProperty("isError").GetBoolean(), JsonSerializer.Serialize(result));
        Assert.Contains(
            "\"recordId\":\"broker-record-a\"",
            toolResult.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal($"Bearer {token}", Assert.Single(observedBearers));
        Assert.Equal(idempotencyKey, Assert.Single(observedIdempotencyKeys));

        var issueResult = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_createSourceControlIssue",
                    arguments = new
                    {
                        projectId = "project-a",
                        runId = "run-a",
                        sessionId = "root-a",
                        tenantSelector = "tenant-a",
                        body = new { title = "Integration issue", body = "Created from MCP." },
                    },
                },
            },
            protocolVersion);
        Assert.False(issueResult.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains(
            "Integration issue",
            issueResult.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        var reviewResult = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_readSourceControlReviews",
                    arguments = new
                    {
                        projectId = "project-a",
                        runId = "run-a",
                        sessionId = "root-a",
                        tenantSelector = "tenant-a",
                        pullRequestNumber = 17,
                    },
                },
            },
            protocolVersion);
        Assert.False(reviewResult.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(
            new[]
            {
                ("POST", "/api/projects/project-a/runs/run-a/source-control/sessions/root-a/issues"),
                ("GET", "/api/projects/project-a/runs/run-a/source-control/sessions/root-a/pull-requests/17/reviews"),
            },
            observedSourceControlRequests.Select(request => (request.Method, request.Path)));
        Assert.All(observedSourceControlRequests, request =>
        {
            Assert.Equal($"Bearer {token}", request.Bearer);
            Assert.Equal("tenant-a", request.Tenant);
        });
        Assert.Equal(
            """{"title":"Integration issue","body":"Created from MCP."}""",
            observedSourceControlRequests.First().Body);

        var pinTool = tools.EnumerateArray().Single(
            tool => tool.GetProperty("name").GetString() == "agentweaver_pinSourceControlRepository");
        Assert.DoesNotContain(
            pinTool.GetProperty("inputSchema").GetProperty("required").EnumerateArray(),
            required => required.GetString() == "body");

        var pinResult = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 6,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_pinSourceControlRepository",
                    arguments = new
                    {
                        projectId = "project-a",
                        runId = "run-a",
                        sessionId = "root-a",
                        tenantSelector = "tenant-a",
                        body = new { selectionCode = "opaque-selection-code" },
                    },
                },
            },
            protocolVersion);
        Assert.False(pinResult.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains(
            "pin-a",
            pinResult.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        var pinRequest = observedSourceControlRequests.Last();
        Assert.Equal(
            ("POST", "/api/projects/project-a/runs/run-a/source-control/sessions/root-a/pin"),
            (pinRequest.Method, pinRequest.Path));
        Assert.Equal("""{"selectionCode":"opaque-selection-code"}""", pinRequest.Body);
        Assert.Equal($"Bearer {token}", pinRequest.Bearer);
        Assert.Equal("tenant-a", pinRequest.Tenant);

        var legacyPinResult = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 7,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_pinSourceControlRepository",
                    arguments = new
                    {
                        projectId = "project-a",
                        runId = "run-a",
                        sessionId = "root-a",
                        tenantSelector = "tenant-a",
                    },
                },
            },
            protocolVersion);
        Assert.False(legacyPinResult.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Null(observedSourceControlRequests.Last().Body);
    }

    [Fact]
    public async Task BrokerTokenTraversesMcpGatewayAndKnowledgeWithCurrentPostgresAuthorization()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        await using var database = await KnowledgeTestDatabase.CreateAsync(_connectionString);
        var catalog = CreateKnowledgeMemoryCatalog(database);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            new X509SecurityKey(certificate),
            providerCatalog: catalog,
            additionalAudiences: ["https://api.test/"]);

        var tenantAdminToken = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/",
            "mcp-knowledge-tenant-admin",
            additionalScopes: "projects.admin",
            roles: []);
        var tenantAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(tenantAdminToken).Claims, "sub");
        var tenantAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, tenantAdminToken, TenantId, "MCP live Knowledge authorization");

        var ownerToken = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/",
            "mcp-live-knowledge-owner",
            additionalScopes: "projects.admin projects.orchestrator",
            roles: []);
        var ownerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(ownerToken).Claims, "sub");
        var ownerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, ownerSubject, TenantId);
        var ownerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        var backupOwnerToken = await IssueTokenAsync(
            "projects.admin",
            [TenantId],
            "mcp-live-knowledge-backup-owner",
            null,
            null,
            []);
        var backupOwnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(backupOwnerToken).Claims, "sub");
        var backupOwnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, backupOwnerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            backupOwnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        var selection = await AcceptKnowledgeRunSelectionAsync(
            projects, database, project, tenantAdminToken);

        await using var knowledgeApp = await CreateKnowledgeTestAppAsync(
            database,
            catalog,
            projects.Client,
            new X509SecurityKey(certificate),
            additionalAudiences: ["https://api.test/"]);
        using var knowledgeClient = knowledgeApp.GetTestClient();
        await using var gateway = GatewayProductionResourceServer.Start(
            new X509SecurityKey(certificate),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            knowledgeOwner: (request, cancellationToken) =>
                ForwardKnowledgeRequestAsync(knowledgeClient, request, cancellationToken));
        await using var mcpFactory = new BrokerValidatedMcpFactory(gateway, certificate);
        using var ownerClient = mcpFactory.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ownerToken);
        var ownerProtocolVersion = await InitializeAsync(ownerClient);

        async Task<JsonElement> CreateRecordAsync(
            HttpClient client,
            string protocolVersion,
            int requestId,
            string idempotencyKey,
            string content)
        {
            var response = await PostRpcAsync(
                client,
                new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method = "tools/call",
                    @params = new
                    {
                        name = "agentweaver_createKnowledgeRecord",
                        arguments = new
                        {
                            projectId = project.ProjectId,
                            runId = selection.RunId,
                            agentId = "mcp-agent-a",
                            tenantSelector = TenantId,
                            idempotencyKey,
                            body = new
                            {
                                kind = "memory",
                                type = "note",
                                content,
                                importance = "medium",
                                tags = new[] { "mcp", "current-authority" },
                            },
                        },
                    },
                },
                protocolVersion);
            return response.GetProperty("result").Clone();
        }

        var first = await CreateRecordAsync(
            ownerClient, ownerProtocolVersion, 2, "mcp-postgres-duplicate-a", "authorized MCP write");
        var firstOwnerResponse = first.GetProperty("structuredContent").GetProperty("ownerResponse");
        Assert.False(first.GetProperty("isError").GetBoolean(), JsonSerializer.Serialize(first));
        Assert.Equal((int)HttpStatusCode.Created,
            first.GetProperty("structuredContent").GetProperty("status").GetInt32());
        var recordId = firstOwnerResponse.GetProperty("record").GetProperty("recordId").GetGuid();

        var replay = await CreateRecordAsync(
            ownerClient, ownerProtocolVersion, 3, "mcp-postgres-duplicate-a", "authorized MCP write");
        var replayOwnerResponse = replay.GetProperty("structuredContent").GetProperty("ownerResponse");
        Assert.False(replay.GetProperty("isError").GetBoolean(), JsonSerializer.Serialize(replay));
        Assert.Equal((int)HttpStatusCode.OK,
            replay.GetProperty("structuredContent").GetProperty("status").GetInt32());
        Assert.Equal(recordId, replayOwnerResponse.GetProperty("record").GetProperty("recordId").GetGuid());
        Assert.Equal(1L, await CountKnowledgeRecordsAsync());
        Assert.Equal(1L, await CountKnowledgeRevisionsAsync(recordId));

        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource, ownerAssignment.AssignmentId, expectedRevision: 1);
        var denied = await CreateRecordAsync(
            ownerClient, ownerProtocolVersion, 4, "mcp-postgres-revoked-owner", "must not be persisted");
        Assert.True(denied.GetProperty("isError").GetBoolean(), JsonSerializer.Serialize(denied));
        Assert.Equal((int)HttpStatusCode.Forbidden,
            denied.GetProperty("structuredContent").GetProperty("status").GetInt32());
        Assert.Equal(
            "missing_effective_writeprojects",
            denied.GetProperty("structuredContent").GetProperty("ownerResponse")
                .GetProperty("code").GetString());
        Assert.Equal(1L, await CountKnowledgeRecordsAsync());
        Assert.Equal(1L, await CountKnowledgeRevisionsAsync(recordId));

        async Task<long> CountKnowledgeRecordsAsync()
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{database.Options.Schema}\".knowledge_records " +
                "WHERE project_id = @project AND agent_id = @agent",
                connection);
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("agent", "mcp-agent-a");
            return (long)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The Knowledge record count was empty."));
        }

        async Task<long> CountKnowledgeRevisionsAsync(Guid id)
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{database.Options.Schema}\".knowledge_revisions WHERE record_id = @id",
                connection);
            command.Parameters.AddWithValue("id", id);
            return (long)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The Knowledge revision count was empty."));
        }
    }

    private static async Task<HttpResponseMessage> ForwardKnowledgeRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var forwarded = new HttpRequestMessage(
            request.Method,
            request.RequestUri?.PathAndQuery
                ?? throw new InvalidOperationException("The Gateway owner request had no URI."));
        foreach (var header in request.Headers)
        {
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (request.Content is not null)
        {
            var content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            forwarded.Content = content;
        }
        return await client.SendAsync(
            forwarded,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static async Task<string> InitializeAsync(HttpClient client)
    {
        var initialized = await PostRpcAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "broker-mcp-integration", version = "1" },
                },
            },
            protocolVersion: null);
        var protocolVersion = initialized.GetProperty("result").GetProperty("protocolVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(protocolVersion));

        using var notification = CreateRpcRequest(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        });
        AddProtocolVersion(notification, protocolVersion);
        using var response = await client.SendAsync(notification);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return protocolVersion;
    }

    private static async Task<JsonElement> PostRpcAsync(
        HttpClient client,
        object payload,
        string? protocolVersion)
    {
        using var request = CreateRpcRequest(payload);
        AddProtocolVersion(request, protocolVersion);
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? Assert.Single(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].TrimStart()))
            : raw;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage CreateRpcRequest(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static void AddProtocolVersion(HttpRequestMessage request, string? protocolVersion)
    {
        if (protocolVersion is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", protocolVersion);
    }

    private sealed class BrokerValidatedMcpFactory(
        GatewayProductionResourceServer gateway,
        X509Certificate2 signingCertificate) : WebApplicationFactory<McpProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Identity:Issuer", IdentityBrokerWebApplicationFactory.Issuer);
            builder.UseSetting("Identity:Audience", "https://api.test/");
            builder.UseSetting("Gateway:BaseAddress", "https://gateway.test");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigureAll<OpenIddictValidationOptions>(options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIddictConfiguration>(
                        new OpenIddictConfiguration
                        {
                            Issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                            SigningKeys = { new X509SecurityKey(signingCertificate) },
                        }));
                services.AddHttpClient<McpGatewayApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(gateway.CreateHandler);
                services.AddHttpClient<McpGatewayToolCatalog>()
                    .ConfigurePrimaryHttpMessageHandler(gateway.CreateHandler);
            });
        }
    }
}
