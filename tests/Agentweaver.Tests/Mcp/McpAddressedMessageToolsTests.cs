using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Mcp;

public sealed class McpAddressedMessageToolsTests
{
    [Fact]
    public async Task SendListDeliverAndAcknowledge_ForwardTheSameApiContract()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var tools = new AddressedMessageTools(new AgentweaverApiClient(
            client, new McpConfig("http://localhost", "test-key")));

        (await tools.SendAsync("project", "Link", "run-2", "Please check", "retry-1"))
            .Should().Contain("accepted");
        (await tools.ListAsync("project", "run-2")).Should().Contain("accepted");
        (await tools.GetAsync("project", "message-1")).Should().Contain("accepted");
        (await tools.RetryAsync("project", "message-1", "retry-2", "run-3")).Should().Contain("accepted");
        (await tools.ClaimAsync("project", "worker-1")).Should().Contain("accepted");
        (await tools.DeliverAsync("project", "message-1", "worker-1", 7)).Should().Contain("accepted");
        (await tools.AcknowledgeAsync("project", "message-1")).Should().Contain("accepted");

        handler.Requests.Select(x => x.Path).Should().Equal(
            "/api/projects/project/agent-messages",
            "/api/projects/project/agent-messages?run_id=run-2",
            "/api/projects/project/agent-messages/message-1",
            "/api/projects/project/agent-messages/message-1/retry",
            "/api/projects/project/agent-messages/claim",
            "/api/projects/project/agent-messages/message-1/deliver",
            "/api/projects/project/agent-messages/message-1/acknowledge");
        using var sent = JsonDocument.Parse(handler.Requests[0].Body!);
        sent.RootElement.GetProperty("target_run_id").GetString().Should().Be("run-2");
        sent.RootElement.GetProperty("idempotency_key").GetString().Should().Be("retry-1");
        using var retried = JsonDocument.Parse(handler.Requests[3].Body!);
        retried.RootElement.GetProperty("idempotency_key").GetString().Should().Be("retry-2");
        retried.RootElement.GetProperty("target_run_id").GetString().Should().Be("run-3");
        using var delivered = JsonDocument.Parse(handler.Requests[5].Body!);
        delivered.RootElement.GetProperty("fence").GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task ApiConflict_IsExposedAsMcpError_NotSuccess()
    {
        using var handler = new RecordingHandler { Conflict = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var tools = new AddressedMessageTools(new AgentweaverApiClient(
            client, new McpConfig("http://localhost", "test-key")));
        var action = () => tools.SendAsync("project", "Link", "run-2", "text", "retry-1");
        (await action.Should().ThrowAsync<McpApiException>()).Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public void ApiSendContract_AcceptsMcpSnakeCaseFields()
    {
        const string payload = """
            {"recipient":"Link","target_run_id":"run-2","content":"Please check",
             "idempotency_key":"retry-1","reply_to_id":"message-1"}
            """;
        var request = JsonSerializer.Deserialize<SendAddressedMessage>(
            payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        request!.TargetRunId.Should().Be("run-2");
        request.IdempotencyKey.Should().Be("retry-1");
        request.ReplyToId.Should().Be("message-1");
    }

    [Fact]
    public async Task VerifiedBrokerRunIdentity_IsForwardedOnlyForMessageRoutes()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "agent")], "Bearer")),
        };
        context.Items["mcp.validated_broker_token"] = "verified-token";
        context.Request.Headers["X-Agentweaver-Run-Id"] = "run-1";
        context.Request.Headers["X-Agentweaver-Run-Token"] = "capability-1";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var api = new AgentweaverApiClient(client, new McpConfig("http://localhost", "fallback-token"), accessor);
        var tools = new AddressedMessageTools(api);

        await tools.ListAsync("project");
        handler.Requests[0].RunId.Should().Be("run-1");
        handler.Requests[0].RunToken.Should().Be("capability-1");
        await api.GetAsync<object>("api/projects/project/team");
        handler.Requests[1].RunId.Should().BeNull();
        handler.Requests[1].RunToken.Should().BeNull();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Conflict { get; set; }
        public List<(string Path, string? Body, string? RunId, string? RunToken)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Headers.TryGetValues("X-Agentweaver-Run-Id", out var ids) ? ids.Single() : null,
                request.Headers.TryGetValues("X-Agentweaver-Run-Token", out var tokens) ? tokens.Single() : null));
            return new HttpResponseMessage(Conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Conflict ? """{"error":"idempotency_conflict"}""" : """{"status":"accepted"}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
