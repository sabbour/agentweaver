extern alias GatewayHost;
extern alias McpHost;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using GatewayProgram = GatewayHost::Program;
using McpProgram = McpHost::Program;
using GatewayApiClient = McpHost::Agentweaver.Mcp.GatewayApiClient;
using GatewayToolCatalog = McpHost::Agentweaver.Mcp.GatewayToolCatalog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation;
using System.Security.Cryptography;
using OpenIddict.Abstractions;
using Xunit;

namespace Agentweaver.Mcp.Tests;

public sealed class NativeMcpProtocolTests : IAsyncLifetime
{
    private const string TestIssuer = "https://identity.test/";
    private const string McpAudience = "https://api.test/mcp";
    private const string GatewayAudience = "https://api.test/gateway";
    private const string TestBearer = "test-token";

    private GatewayFactory _gatewayFactory = null!;
    private McpFactory _mcpFactory = null!;
    private HttpClient _client = null!;
    private string? _sessionId;
    private string? _protocolVersion;

    public Task InitializeAsync()
    {
        _gatewayFactory = new GatewayFactory();
        _ = _gatewayFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://gateway.test"),
        });
        _mcpFactory = new McpFactory(_gatewayFactory);
        _client = _mcpFactory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestBearer);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _mcpFactory.DisposeAsync();
        await _gatewayFactory.DisposeAsync();
    }

    [Fact]
    public async Task NativeToolsListAndCallsPreserveGatewayContracts()
    {
        await InitializeProtocolAsync();
        var list = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { },
        });
        var tools = list.GetProperty("result").GetProperty("tools");
        var knowledgeTool = Assert.Single(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_createKnowledgeRecord");
        Assert.DoesNotContain(
            tools.EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_streamRunEvents");

        var inputSchema = knowledgeTool.GetProperty("inputSchema");
        var required = inputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Contains("projectId", required);
        Assert.Contains("runId", required);
        Assert.Contains("agentId", required);
        Assert.Contains("body", required);
        Assert.Contains("idempotencyKey", required);
        Assert.False(inputSchema.GetProperty("properties").GetProperty("body")
            .TryGetProperty("$ref", out _));
        Assert.False(inputSchema.GetProperty("additionalProperties").GetBoolean());

        var arguments = new
        {
            projectId = "project-a",
            runId = "run-a",
            agentId = "agent-a",
            idempotencyKey = "same-key-1",
            body = new
            {
                kind = "memory",
                type = "note",
                content = "MCP retry",
                importance = "medium",
                tags = new[] { "mcp" },
            },
        };
        var createCall = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_createKnowledgeRecord",
                arguments,
            },
        };
        var first = await PostRpcAsync(createCall);
        var second = await PostRpcAsync(createCall);
        Assert.False(
            first.GetProperty("result").GetProperty("isError").GetBoolean(),
            JsonSerializer.Serialize(first));
        Assert.False(
            second.GetProperty("result").GetProperty("isError").GetBoolean(),
            JsonSerializer.Serialize(second));
        Assert.Contains(
            "\"recordId\":\"record-a\"",
            first.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(
            new string?[] { "same-key-1", "same-key-1" },
            _gatewayFactory.KnowledgeIdempotencyKeys.ToArray());
        Assert.All(_gatewayFactory.KnowledgeAuthorizationHeaders, header =>
            Assert.Equal($"Bearer {TestBearer}", header));

        var rejected = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_proposeOutcome",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                    body = new
                    {
                        expectedStateVersion = 1,
                        idempotencyKey = "outcome-1",
                        requestId = "request-a",
                        specification = new
                        {
                            id = "outcome-a",
                            goal = "complete the run",
                            desiredOutcome = "accepted",
                            scope = "project-a",
                            assumptions = "none",
                            clarifyingQuestions = Array.Empty<string>(),
                        },
                    },
                },
            },
        });
        var rejectedResult = rejected.GetProperty("result");
        Assert.True(rejectedResult.GetProperty("isError").GetBoolean());
        Assert.False(rejectedResult.GetProperty("structuredContent")
            .GetProperty("ownerResponse").GetProperty("accepted").GetBoolean());
        Assert.Contains(
            "rejected the request",
            rejectedResult.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task KnowledgeLifecycleAndTransferToolsPreserveGatewayContracts()
    {
        await InitializeProtocolAsync();
        var list = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { },
        });
        var tools = list.GetProperty("result").GetProperty("tools");
        var expectedBodyFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["restoreKnowledgeRecord"] = ["expectedRevision", "revision"],
            ["approveKnowledgeDecision"] = ["expectedRevision"],
            ["importKnowledgeRecords"] = ["format", "schemaVersion", "projectId", "agentId", "records"],
        };
        foreach (var (operation, bodyFields) in expectedBodyFields)
        {
            var tool = Assert.Single(
                tools.EnumerateArray(),
                candidate => candidate.GetProperty("name").GetString() == $"agentweaver_{operation}");
            var inputSchema = tool.GetProperty("inputSchema");
            Assert.False(inputSchema.GetProperty("additionalProperties").GetBoolean());
            var required = inputSchema.GetProperty("required")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("projectId", required);
            Assert.Contains("runId", required);
            Assert.Contains("agentId", required);
            Assert.Contains("idempotencyKey", required);
            Assert.Contains("body", required);
            var bodySchema = inputSchema.GetProperty("properties").GetProperty("body");
            Assert.Equal("object", bodySchema.GetProperty("type").GetString());
            Assert.False(bodySchema.GetProperty("additionalProperties").GetBoolean());
            Assert.DoesNotContain("$ref", bodySchema.GetRawText());
            var bodyRequired = bodySchema.GetProperty("required")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            foreach (var field in bodyFields)
                Assert.Contains(field, bodyRequired);
        }

        var exportTool = Assert.Single(
            tools.EnumerateArray(),
            candidate => candidate.GetProperty("name").GetString() == "agentweaver_exportKnowledgeRecords");
        var exportSchema = exportTool.GetProperty("inputSchema");
        Assert.False(exportSchema.GetProperty("properties").TryGetProperty("body", out _));
        Assert.False(exportSchema.GetProperty("properties").TryGetProperty("idempotencyKey", out _));

        async Task CallAsync(string operation, object arguments, int id)
        {
            var response = await PostRpcAsync(new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new
                {
                    name = $"agentweaver_{operation}",
                    arguments,
                },
            });
            Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean(),
                JsonSerializer.Serialize(response));
        }

        const string decisionId = "11111111-1111-4111-8111-111111111111";
        const string revisionId = "22222222-2222-4222-8222-222222222222";
        const string timestamp = "2026-10-08T10:00:00Z";
        var routeArguments = new
        {
            projectId = "project-a",
            runId = "run-a",
            agentId = "agent-a",
        };
        await CallAsync("restoreKnowledgeRecord", new
        {
            routeArguments.projectId,
            routeArguments.runId,
            routeArguments.agentId,
            recordId = decisionId,
            idempotencyKey = "mcp-restore",
            body = new { expectedRevision = 2, revision = 1, reason = "restore decision" },
        }, 3);
        await CallAsync("approveKnowledgeDecision", new
        {
            routeArguments.projectId,
            routeArguments.runId,
            routeArguments.agentId,
            recordId = decisionId,
            idempotencyKey = "mcp-approve",
            body = new { expectedRevision = 3, reason = "approve decision" },
        }, 4);
        await CallAsync("exportKnowledgeRecords", routeArguments, 5);
        await CallAsync("importKnowledgeRecords", new
        {
            routeArguments.projectId,
            routeArguments.runId,
            routeArguments.agentId,
            idempotencyKey = "mcp-import",
            body = new
            {
                format = "agentweaver.knowledge-transfer.v1",
                schemaVersion = 1,
                projectId = "project-a",
                agentId = "agent-a",
                records = new[]
                {
                    new
                    {
                        record = new
                        {
                            recordId = decisionId,
                            projectId = "project-a",
                            agentId = "agent-a",
                            kind = "decision",
                            type = "architecture",
                            title = "Imported decision",
                            content = "Decision content",
                            importance = "medium",
                            tags = new[] { "mcp" },
                            state = "active",
                            trustState = "approved",
                            revision = 1,
                            revisionId,
                            createdAt = timestamp,
                            updatedAt = timestamp,
                        },
                        revisions = new[]
                        {
                            new
                            {
                                recordId = decisionId,
                                revision = 1,
                                revisionId,
                                kind = "decision",
                                type = "architecture",
                                title = "Imported decision",
                                content = "Decision content",
                                importance = "medium",
                                tags = new[] { "mcp" },
                                state = "active",
                                trustState = "approved",
                                reason = "initial import",
                                createdAt = timestamp,
                            },
                        },
                    },
                },
            },
        }, 6);

        var requests = _gatewayFactory.KnowledgeRequests.ToArray();
        Assert.Equal(4, requests.Length);
        Assert.Equal(
            new[] { "POST", "POST", "GET", "POST" },
            requests.Select(request => request.Method));
        Assert.Equal(
            new[]
            {
                $"/api/projects/project-a/runs/run-a/agents/agent-a/records/{decisionId}/restore",
                $"/api/projects/project-a/runs/run-a/agents/agent-a/records/{decisionId}/approve",
                "/api/projects/project-a/runs/run-a/agents/agent-a/records/export",
                "/api/projects/project-a/runs/run-a/agents/agent-a/records/import",
            },
            requests.Select(request => request.PathAndQuery));
        Assert.Equal(
            new string?[] { "mcp-restore", "mcp-approve", null, "mcp-import" },
            requests.Select(request => request.IdempotencyKey));
        using (var restoreBody = JsonDocument.Parse(Assert.IsType<string>(requests[0].Body)))
        {
            Assert.Equal(2, restoreBody.RootElement.GetProperty("expectedRevision").GetInt32());
            Assert.Equal(1, restoreBody.RootElement.GetProperty("revision").GetInt32());
        }
        using (var approveBody = JsonDocument.Parse(Assert.IsType<string>(requests[1].Body)))
            Assert.Equal(3, approveBody.RootElement.GetProperty("expectedRevision").GetInt32());
        Assert.Null(requests[2].Body);
        using (var importBody = JsonDocument.Parse(Assert.IsType<string>(requests[3].Body)))
        {
            Assert.Equal("agentweaver.knowledge-transfer.v1",
                importBody.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, importBody.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(decisionId,
                importBody.RootElement.GetProperty("records")[0].GetProperty("record")
                    .GetProperty("recordId").GetString());
        }
    }

    [Fact]
    public async Task AcceptedOnlySpawnPreservesOwnerAcceptanceWithoutClaimingCompletion()
    {
        await InitializeProtocolAsync();
        var list = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { },
        });
        var spawnTool = Assert.Single(
            list.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "agentweaver_spawnSession");
        Assert.Contains(
            "does not mean the requested work completed",
            spawnTool.GetProperty("description").GetString(),
            StringComparison.Ordinal);

        _gatewayFactory.EnqueueOrchestratorResponse(
            HttpStatusCode.Accepted,
            """
            {"node":{"identity":{"projectId":"project-a","runId":"run-a","sessionId":"child-a"},"parentSessionId":"parent-a","rootSessionId":"root-a","kind":"childWork","detached":false,"lifecycle":"active","executionFence":4,"logicalTurnOrdinal":0,"stateVersion":1,"createdAt":"2026-10-07T12:00:00Z","archivedAt":null},"pendingRequestId":"spawn-request-a","commandId":"11111111-1111-1111-1111-111111111111","dispatchState":"accepted"}
            """);

        var result = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_spawnSession",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    parentSessionId = "parent-a",
                    body = new
                    {
                        sessionId = "child-a",
                        kind = "childWork",
                        idempotencyKey = "spawn-key-1",
                        kickoff = "implement the approved work item",
                        userQuote = "implement the approved work item",
                        coordinatorInstructions = "keep the existing work fence",
                        workPlanItemId = "plan-item-a",
                    },
                },
            },
        });

        var toolResult = result.GetProperty("result");
        Assert.False(toolResult.GetProperty("isError").GetBoolean(), JsonSerializer.Serialize(result));
        var structured = toolResult.GetProperty("structuredContent");
        Assert.Equal((int)HttpStatusCode.Accepted, structured.GetProperty("status").GetInt32());
        Assert.Equal("spawn-request-a",
            structured.GetProperty("ownerResponse").GetProperty("pendingRequestId").GetString());
        Assert.Equal("accepted",
            structured.GetProperty("ownerResponse").GetProperty("dispatchState").GetString());
        Assert.Contains(
            "acceptance is not completion",
            toolResult.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);

        var request = Assert.Single(_gatewayFactory.OrchestratorRequests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(
            "/api/projects/project-a/runs/run-a/coordination/sessions/parent-a/spawn",
            request.PathAndQuery);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("child-a", body.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("childWork", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("spawn-key-1", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("implement the approved work item", body.RootElement.GetProperty("kickoff").GetString());
        Assert.Equal("keep the existing work fence",
            body.RootElement.GetProperty("coordinatorInstructions").GetString());
        Assert.Equal("plan-item-a", body.RootElement.GetProperty("workPlanItemId").GetString());
    }

    [Fact]
    public async Task GateAnswerPreservesRequestChoiceAndOwnerFence()
    {
        await InitializeProtocolAsync();
        _gatewayFactory.EnqueueOrchestratorResponse(
            HttpStatusCode.OK,
            """
            {"stateVersion":17,"executionFence":23,"outcomeConfirmed":false,"workflowConfirmed":false,"canDecompose":false,"canDispatch":false,"pendingGate":{"requestId":"gate-request-23","kind":"question","subjectId":"outcome-a","authorizedActorId":"actor-a","fence":23,"allowedChoices":["continue","revise"],"allowsFreeform":false,"prompt":"Choose how to proceed.","resolvesOutcomeClarification":false}}
            """);
        var decisions = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_readDecisions",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                },
            },
        });
        var pendingGate = decisions.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("ownerResponse")
            .GetProperty("pendingGate");
        Assert.Equal("gate-request-23", pendingGate.GetProperty("requestId").GetString());
        Assert.Equal(23, pendingGate.GetProperty("fence").GetInt32());
        Assert.Equal(
            new[] { "continue", "revise" },
            pendingGate.GetProperty("allowedChoices").EnumerateArray()
                .Select(choice => choice.GetString()!).ToArray());

        _gatewayFactory.EnqueueOrchestratorResponse(
            HttpStatusCode.OK,
            """
            {"decisionId":"22222222-2222-2222-2222-222222222222","stateVersion":18,"accepted":true,"executionFence":23,"pendingGate":null,"issues":[]}
            """);
        var answer = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_answerGate",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                    requestId = "gate-request-23",
                    body = new
                    {
                        expectedStateVersion = 17,
                        idempotencyKey = "gate-answer-23",
                        choiceId = "continue",
                        freeformAnswer = (string?)null,
                    },
                },
            },
        });
        Assert.False(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(23, answer.GetProperty("result").GetProperty("structuredContent")
            .GetProperty("ownerResponse").GetProperty("executionFence").GetInt32());

        var requests = _gatewayFactory.OrchestratorRequests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("GET", requests[0].Method);
        Assert.Equal(
            "/api/projects/project-a/runs/run-a/coordination/sessions/root-a/decisions",
            requests[0].PathAndQuery);
        Assert.Null(requests[0].Body);
        Assert.Equal("POST", requests[1].Method);
        Assert.Equal(
            "/api/projects/project-a/runs/run-a/coordination/sessions/root-a/decisions/gates/gate-request-23/answer",
            requests[1].PathAndQuery);
        using var answerBody = JsonDocument.Parse(requests[1].Body!);
        Assert.Equal(17, answerBody.RootElement.GetProperty("expectedStateVersion").GetInt64());
        Assert.Equal("gate-answer-23", answerBody.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("continue", answerBody.RootElement.GetProperty("choiceId").GetString());
        Assert.True(answerBody.RootElement.GetProperty("freeformAnswer").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GateAcknowledgementRemainsDistinctFromExplicitApproval()
    {
        await InitializeProtocolAsync();
        const string pendingGate =
            """{"requestId":"approval-request-a","kind":"approval","subjectId":"artifact-a","authorizedActorId":"actor-a","fence":31,"allowedChoices":[],"allowsFreeform":false,"prompt":"Approve artifact-a?","resolvesOutcomeClarification":false}""";
        _gatewayFactory.EnqueueOrchestratorResponse(
            HttpStatusCode.OK,
            $$"""{"decisionId":"33333333-3333-3333-3333-333333333333","stateVersion":18,"accepted":true,"executionFence":31,"pendingGate":{{pendingGate}},"issues":[]}""");

        var acknowledged = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_acknowledgeGate",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                    requestId = "approval-request-a",
                    body = new
                    {
                        expectedStateVersion = 17,
                        idempotencyKey = "gate-ack-a",
                    },
                },
            },
        });
        var acknowledgedResponse = acknowledged.GetProperty("result")
            .GetProperty("structuredContent").GetProperty("ownerResponse");
        Assert.True(acknowledgedResponse.GetProperty("accepted").GetBoolean());
        Assert.Equal("approval-request-a",
            acknowledgedResponse.GetProperty("pendingGate").GetProperty("requestId").GetString());
        var acknowledgementRequest = Assert.Single(_gatewayFactory.OrchestratorRequests);
        Assert.EndsWith("/decisions/gates/approval-request-a/acknowledge",
            acknowledgementRequest.PathAndQuery, StringComparison.Ordinal);
        using (var acknowledgementBody = JsonDocument.Parse(acknowledgementRequest.Body!))
        {
            Assert.Equal(17, acknowledgementBody.RootElement.GetProperty("expectedStateVersion").GetInt64());
            Assert.Equal("gate-ack-a",
                acknowledgementBody.RootElement.GetProperty("idempotencyKey").GetString());
        }

        _gatewayFactory.EnqueueOrchestratorResponse(
            HttpStatusCode.OK,
            """{"decisionId":"44444444-4444-4444-4444-444444444444","stateVersion":19,"accepted":true,"executionFence":32,"pendingGate":null,"issues":[]}""");
        var approved = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_approveGate",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                    requestId = "approval-request-a",
                    body = new
                    {
                        expectedStateVersion = 18,
                        idempotencyKey = "gate-approve-a",
                    },
                },
            },
        });
        Assert.False(approved.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Null(approved.GetProperty("result").GetProperty("structuredContent")
            .GetProperty("ownerResponse").GetProperty("pendingGate").GetString());

        var requests = _gatewayFactory.OrchestratorRequests.ToArray();
        Assert.Equal(2, requests.Length);
        var approvalRequest = requests[1];
        Assert.Equal("POST", approvalRequest.Method);
        Assert.EndsWith("/decisions/gates/approval-request-a/approve",
            approvalRequest.PathAndQuery, StringComparison.Ordinal);
        using var approvalBody = JsonDocument.Parse(approvalRequest.Body!);
        Assert.Equal(18, approvalBody.RootElement.GetProperty("expectedStateVersion").GetInt64());
        Assert.Equal("gate-approve-a", approvalBody.RootElement.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task MissingBearerIsRejectedBeforeMcpHandlersRun()
    {
        using var client = _mcpFactory.CreateClient();
        using var request = CreateRpcRequest(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "anonymous-test", version = "1" },
            },
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            $"resource_metadata=\"https://api.test/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
        Assert.Empty(_gatewayFactory.KnowledgeIdempotencyKeys);
    }

    [Fact]
    public async Task ProtectedResourceMetadataAdvertisesTheBrokerAndMcpResource()
    {
        using var client = _mcpFactory.CreateClient();

        using var response = await client.GetAsync(
            "/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            McpAudience,
            document.RootElement.GetProperty("resource").GetString());
        Assert.Equal(
            TestIssuer,
            Assert.Single(document.RootElement.GetProperty("authorization_servers")
                .EnumerateArray()
                .Select(value => value.GetString())));
    }

    [Fact]
    public async Task OpenIddictAcceptsBrokerResourceTokensAndRejectsInvalidTokens()
    {
        using var brokerRsa = RSA.Create(2048);
        using var upstreamRsa = RSA.Create(2048);
        var brokerKey = new RsaSecurityKey(brokerRsa) { KeyId = "broker-test-key" };
        var upstreamKey = new RsaSecurityKey(upstreamRsa) { KeyId = "upstream-test-key" };
        var configuration = new OpenIddictConfiguration
        {
            Issuer = new Uri(TestIssuer),
            SigningKeys = { brokerKey },
        };
        await using var factory = new McpFactory(
            _gatewayFactory,
            useTestAuthentication: false,
            configuration);
        using var client = factory.CreateClient();
        var validToken = CreateResourceToken(
            brokerKey,
            TestIssuer,
            McpAudience,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var invalidTokens = new[]
        {
            CreateResourceToken(
                brokerKey,
                TestIssuer,
                GatewayAudience,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            CreateResourceToken(
                brokerKey,
                "https://other-issuer.test/",
                McpAudience,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            CreateResourceToken(
                brokerKey,
                TestIssuer,
                McpAudience,
                DateTimeOffset.UtcNow.AddMinutes(-5)),
            CreateResourceToken(
                upstreamKey,
                TestIssuer,
                McpAudience,
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        using var validRequest = CreateRpcRequest(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "broker-token-test", version = "1" },
            },
        });
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
        using var validResponse = await client.SendAsync(validRequest);
        var validResponseBody = await validResponse.Content.ReadAsStringAsync();
        Assert.True(
            validResponse.StatusCode == HttpStatusCode.OK,
            $"{(int)validResponse.StatusCode} {validResponse.Headers.WwwAuthenticate}: {validResponseBody}");

        foreach (var invalidToken in invalidTokens)
        {
            using var invalidRequest = CreateRpcRequest(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "invalid-token-test", version = "1" },
                },
            });
            invalidRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", invalidToken);

            using var invalidResponse = await client.SendAsync(invalidRequest);

            Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        }

        Assert.Empty(_gatewayFactory.KnowledgeIdempotencyKeys);
    }

    [Fact]
    public async Task OwnerUnavailableRemainsAnExplicitMcpError()
    {
        await InitializeProtocolAsync();
        _gatewayFactory.KnowledgeUnavailable = true;

        var result = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_createKnowledgeRecord",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    agentId = "agent-a",
                    idempotencyKey = "unavailable-1",
                    body = new
                    {
                        kind = "memory",
                        type = "note",
                        content = "unavailable",
                        importance = "medium",
                        tags = new[] { "mcp" },
                    },
                },
            },
        });

        Assert.True(result.TryGetProperty("result", out var toolResult), JsonSerializer.Serialize(result));
        Assert.True(toolResult.GetProperty("isError").GetBoolean());
        Assert.Equal(502, toolResult.GetProperty("structuredContent").GetProperty("status").GetInt32());
        Assert.Equal(
            "owner_unavailable",
            toolResult.GetProperty("structuredContent").GetProperty("ownerResponse").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToolArgumentsAreValidatedAgainstTheAdvertisedSchema()
    {
        await InitializeProtocolAsync();
        var invalidArguments = new object[]
        {
            new
            {
                projectId = "project-a",
                runId = "run-a",
                agentId = "agent-a",
                idempotencyKey = "invalid-enum",
                body = new
                {
                    kind = "not-a-knowledge-kind",
                    type = "note",
                    content = "invalid enum",
                    importance = "medium",
                    tags = new[] { "mcp" },
                },
            },
            new
            {
                projectId = "project-a",
                runId = "run-a",
                agentId = "agent-a",
                idempotencyKey = "invalid-array-item",
                body = new
                {
                    kind = "memory",
                    type = "note",
                    content = "invalid array item",
                    importance = "medium",
                    tags = new object[] { "mcp", 1 },
                },
            },
            new
            {
                projectId = "project-a",
                runId = "run-a",
                agentId = "agent-a",
                idempotencyKey = "invalid key",
                body = new
                {
                    kind = "memory",
                    type = "note",
                    content = "invalid string constraint",
                    importance = "medium",
                    tags = new[] { "mcp" },
                },
            },
        };

        for (var index = 0; index < invalidArguments.Length; index++)
        {
            var result = await PostRpcAsync(new
            {
                jsonrpc = "2.0",
                id = 10 + index,
                method = "tools/call",
                @params = new
                {
                    name = "agentweaver_createKnowledgeRecord",
                    arguments = invalidArguments[index],
                },
            });
            var toolResult = result.GetProperty("result");
            Assert.True(toolResult.GetProperty("isError").GetBoolean());
            Assert.Equal(
                "invalid_arguments",
                toolResult.GetProperty("structuredContent").GetProperty("error").GetString());
        }

        var invalidIntegerFormat = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 20,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_proposeOutcome",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    sessionId = "root-a",
                    body = new
                    {
                        expectedStateVersion = ulong.MaxValue,
                        idempotencyKey = "version-overflow",
                        requestId = "request-overflow",
                        specification = new
                        {
                            id = "outcome-overflow",
                            goal = "complete the run",
                            desiredOutcome = "accepted",
                            scope = "project-a",
                            assumptions = "none",
                            clarifyingQuestions = Array.Empty<string>(),
                        },
                    },
                },
            },
        });
        var invalidIntegerResult = invalidIntegerFormat.GetProperty("result");
        Assert.True(invalidIntegerResult.GetProperty("isError").GetBoolean());
        Assert.Equal(
            "invalid_arguments",
            invalidIntegerResult.GetProperty("structuredContent").GetProperty("error").GetString());

        Assert.Empty(_gatewayFactory.KnowledgeIdempotencyKeys);
    }

    [Fact]
    public async Task GatewayBodyStallAfterHeadersReturnsMcpTimeoutError()
    {
        _client.Dispose();
        await _mcpFactory.DisposeAsync();
        _mcpFactory = new McpFactory(
            _gatewayFactory,
            gatewayApiHandler: new StallingGatewayHandler());
        _client = _mcpFactory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestBearer);
        _sessionId = null;
        _protocolVersion = null;
        await InitializeProtocolAsync();

        var result = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "tools/call",
            @params = new
            {
                name = "agentweaver_createKnowledgeRecord",
                arguments = new
                {
                    projectId = "project-a",
                    runId = "run-a",
                    agentId = "agent-a",
                    idempotencyKey = "body-timeout",
                    body = new
                    {
                        kind = "memory",
                        type = "note",
                        content = "body timeout",
                        importance = "medium",
                        tags = new[] { "mcp" },
                    },
                },
            },
        });

        var toolResult = result.GetProperty("result");
        Assert.True(toolResult.GetProperty("isError").GetBoolean());
        Assert.Equal(
            "gateway_timeout",
            toolResult.GetProperty("structuredContent").GetProperty("error").GetString());
    }

    private async Task InitializeProtocolAsync()
    {
        var initialized = await PostRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "agentweaver-mcp-tests", version = "1" },
            },
        }, includeSession: false);
        _protocolVersion = initialized.GetProperty("result").GetProperty("protocolVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(_protocolVersion));

        using var notification = CreateRpcRequest(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        });
        AddSessionHeaders(notification);
        using var response = await _client.SendAsync(notification);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        ReadSessionId(response);
    }

    private async Task<JsonElement> PostRpcAsync(object payload, bool includeSession = true)
    {
        using var request = CreateRpcRequest(payload);
        if (includeSession)
            AddSessionHeaders(request);
        using var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {raw}");
        ReadSessionId(response);
        var json = ExtractJsonResponse(raw, response.Content.Headers.ContentType?.MediaType);
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

    private void AddSessionHeaders(HttpRequestMessage request)
    {
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_protocolVersion is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", _protocolVersion);
    }

    private void ReadSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = Assert.Single(values);
    }

    private static string ExtractJsonResponse(string raw, string? mediaType)
    {
        if (mediaType == "text/event-stream")
        {
            var data = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].TrimStart());
            return Assert.Single(data);
        }
        return raw;
    }

    private sealed class GatewayFactory : WebApplicationFactory<GatewayProgram>
    {
        private readonly ConcurrentQueue<(HttpStatusCode StatusCode, string Body)> _orchestratorResponses = new();

        public ConcurrentQueue<string?> KnowledgeIdempotencyKeys { get; } = new();
        public ConcurrentQueue<string?> KnowledgeAuthorizationHeaders { get; } = new();
        public ConcurrentQueue<ObservedKnowledgeRequest> KnowledgeRequests { get; } = new();
        public ConcurrentQueue<ObservedOrchestratorRequest> OrchestratorRequests { get; } = new();
        public bool KnowledgeUnavailable { get; set; }

        public void EnqueueOrchestratorResponse(HttpStatusCode statusCode, string body) =>
            _orchestratorResponses.Enqueue((statusCode, body));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Identity:Issuer", TestIssuer);
            builder.UseSetting("Identity:Audience", GatewayAudience);
            builder.UseSetting("Gateway:Owners:Projects", "https://projects.test");
            builder.UseSetting("Gateway:Owners:Orchestrator", "https://orchestrator.test");
            builder.UseSetting("Gateway:Owners:Knowledge", "https://knowledge.test");
            builder.UseSetting("Gateway:Owners:Events", "https://events.test");
            builder.UseSetting("Gateway:Owners:IdentityBrokerAddress", "https://identity-broker.test");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthenticationHandler.TestScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.TestScheme,
                        _ => { });
                ConfigureOwner(services, "Knowledge", HandleKnowledgeAsync);
                ConfigureOwner(services, "Orchestrator", HandleOrchestratorAsync);
            });
        }

        private async Task<HttpResponseMessage> HandleKnowledgeAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var idempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? Assert.Single(values)
                : null;
            KnowledgeIdempotencyKeys.Enqueue(idempotencyKey);
            KnowledgeAuthorizationHeaders.Enqueue(request.Headers.Authorization?.ToString());
            KnowledgeRequests.Enqueue(new ObservedKnowledgeRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                idempotencyKey));
            if (KnowledgeUnavailable)
                throw new HttpRequestException("Simulated unavailable owner.");

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"recordId\":\"record-a\",\"revision\":1}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private async Task<HttpResponseMessage> HandleOrchestratorAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            OrchestratorRequests.Enqueue(new ObservedOrchestratorRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body));
            if (_orchestratorResponses.TryDequeue(out var response))
                return new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"accepted\":false,\"issues\":[{\"code\":\"stale_state\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private static void ConfigureOwner(
            IServiceCollection services,
            string clientName,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            services.Configure<HttpClientFactoryOptions>(
                clientName,
                options => options.HttpMessageHandlerBuilderActions.Add(
                    builder => builder.PrimaryHandler = new OwnerHandler(handler)));

        public sealed record ObservedOrchestratorRequest(string Method, string PathAndQuery, string? Body);
        public sealed record ObservedKnowledgeRequest(
            string Method,
            string PathAndQuery,
            string? Body,
            string? IdempotencyKey);
    }

    private static string CreateResourceToken(
        SecurityKey signingKey,
        string issuer,
        string audience,
        DateTimeOffset expiresAt)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "broker-user-a",
                ["scope"] = "api.read",
            },
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class McpFactory(
        GatewayFactory gateway,
        bool useTestAuthentication = true,
        OpenIddictConfiguration? validationConfiguration = null,
        HttpMessageHandler? gatewayApiHandler = null) : WebApplicationFactory<McpProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Identity:Issuer", TestIssuer);
            builder.UseSetting("Identity:Audience", McpAudience);
            builder.UseSetting("Gateway:BaseAddress", "https://gateway.test");
            builder.ConfigureTestServices(services =>
            {
                if (useTestAuthentication)
                {
                    services.AddAuthentication(TestAuthenticationHandler.TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                            TestAuthenticationHandler.TestScheme,
                            _ => { });
                }
                else if (validationConfiguration is not null)
                {
                    services.PostConfigureAll<OpenIddictValidationOptions>(options =>
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIddictConfiguration>(
                                validationConfiguration));
                }
                services.AddHttpClient<GatewayApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(
                        () => gatewayApiHandler ?? gateway.Server.CreateHandler());
                services.AddHttpClient<GatewayToolCatalog>()
                    .ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            });
        }
    }

    private sealed class OwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class StallingGatewayHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingReadStream()),
            });
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "McpTestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(
                    Request.Headers.Authorization,
                    out var bearer) ||
                !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(bearer.Parameter, TestBearer, StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new[]
            {
                new Claim("sub", "user-a"),
                new Claim("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
