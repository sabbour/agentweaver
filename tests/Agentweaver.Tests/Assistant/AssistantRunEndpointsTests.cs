using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Assistant;

/// <summary>
/// Integration tests for the operator assistant backend (#346): the two new endpoints
/// (POST /api/assistant/runs, POST /api/assistant/runs/{id}/messages) plus AssistantRunService.
///
/// The in-API Copilot loop (<see cref="IOperatorAssistantAgent"/>) is replaced with a deterministic
/// fake — the same hermetic-seam pattern the coordinator suite uses — so no live model call is made.
/// Everything else (auth middleware, run store, run event stream, endpoint wiring) is the real
/// production host. This exercises: run persistence, a full message round-trip producing RunEvents on
/// the existing stream, the per-user concurrency bound, and the auth requirement.
/// </summary>
public sealed class AssistantRunEndpointsTests
{
    [Fact]
    public async Task StartRun_PersistsOperatorRun()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        var response = await client.PostAsJsonAsync("/api/assistant/runs", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = body.GetProperty("run_id").GetString();
        runId.Should().NotBeNullOrWhiteSpace();
        body.GetProperty("status").GetString().Should().Be("in_progress");

        // The conversation is persisted as a lightweight operator run in the real run store.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var run = await runStore.GetAsync(RunId.Parse(runId!), CancellationToken.None);
        run.Should().NotBeNull();
        run!.AgentName.Should().Be(AssistantRunService.OperatorAgentName);
        run.SubmittingUser.Should().Be(AgentweaverWebApplicationFactory.TestUser);
        run.ParentRunId.Should().BeNull("an operator run has no parent and no work plan");
        run.Status.Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task SendMessage_RoundTrips_ProducesRunEventsOnStream()
    {
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.EmitTool = true;
        factory.Agent.ToolName = "project_list";
        factory.Agent.ReplyText = "Here are your projects.";
        factory.Agent.ToolNamesInvoked = new[] { "project_list" };
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var turn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "list my projects" });
        turn.StatusCode.Should().Be(HttpStatusCode.OK);
        var turnBody = await turn.Content.ReadFromJsonAsync<JsonElement>();
        turnBody.GetProperty("message").GetString().Should().Be("Here are your projects.");
        turnBody.GetProperty("tools_invoked").EnumerateArray()
            .Select(t => t.GetString()).Should().Contain("project_list");

        // The same GET /api/runs/{id}/events the frontend seeds from must now carry the transcript
        // as RunEvents: run.started, the user + assistant messages, and the tool call/result steps.
        var events = await GetEventsAsync(client, runId);
        var types = events.Select(e => e.Type).ToList();
        types.Should().Contain(EventTypes.RunStarted);
        types.Should().Contain(EventTypes.ToolCall);
        types.Should().Contain(EventTypes.ToolResult);
        types.Count(t => t == EventTypes.AgentMessage).Should().BeGreaterThanOrEqualTo(2, "one user turn + one assistant turn");

        var assistantMessage = events.Last(e => e.Type == EventTypes.AgentMessage);
        assistantMessage.Payload.GetProperty("role").GetString().Should().Be("assistant");
        assistantMessage.Payload.GetProperty("content").GetString().Should().Be("Here are your projects.");

        var toolCall = events.First(e => e.Type == EventTypes.ToolCall);
        toolCall.Payload.GetProperty("name").GetString().Should().Be("project_list");
    }

    [Fact]
    public async Task StartRun_EnforcesPerUserConcurrencyBound()
    {
        await using var factory = new AssistantWebApplicationFactory { MaxConcurrentRunsPerUser = 1 };
        var client = AuthedClient(factory);

        var first = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("error").GetString().Should().Be("operator_run_limit");
    }

    [Fact]
    public async Task StartRun_RequiresAuth_401WithoutToken()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = factory.CreateClient(); // no Authorization header

        var response = await client.PostAsJsonAsync("/api/assistant/runs", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendMessage_RequiresAuth_401WithoutToken()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/assistant/runs/{Guid.NewGuid()}/messages", new { message = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient AuthedClient(AssistantWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    private static async Task<List<EventRow>> GetEventsAsync(HttpClient client, string runId)
    {
        var response = await client.GetAsync($"/api/runs/{runId}/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        return raw!.Select(e => new EventRow(
            e.GetProperty("type").GetString()!,
            e.GetProperty("payload"))).ToList();
    }

    private sealed record EventRow(string Type, JsonElement Payload);
}

/// <summary>
/// Integration host for the operator-assistant endpoints: production wiring with a temp SQLite DB and
/// the test bearer-auth bypass, but with the Copilot operator loop swapped for a deterministic fake and
/// a configurable per-user concurrency bound. Mirrors <see cref="AgentweaverWebApplicationFactory"/>'s
/// config (that type is sealed, so this is a sibling rather than a subclass).
/// </summary>
public sealed class AssistantWebApplicationFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public const string TestApiKey = AgentweaverWebApplicationFactory.TestApiKey;
    public const string TestUser = AgentweaverWebApplicationFactory.TestUser;

    public FakeOperatorAssistantAgent Agent { get; } = new();
    public int MaxConcurrentRunsPerUser { get; set; } = 3;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-{Guid.NewGuid():N}.db");
    private readonly string _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-wt-{Guid.NewGuid():N}");
    private readonly string _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-cp-{Guid.NewGuid():N}");
    private readonly string _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-assistant-ccp-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Worktrees:BasePath"] = _worktreesPath,
                ["Checkpoints:Path"] = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                ["Testing:BypassGitHubTokenAuth"] = "true",
                ["Auth:ApiKey"] = TestApiKey,
                ["Auth:User"] = TestUser,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10",
                ["Assistant:MaxConcurrentRunsPerUser"] = MaxConcurrentRunsPerUser.ToString(),
                // Present so DI validation is happy; the fake agent replaces the real path that would
                // connect here, so no MCP connection is ever attempted in tests.
                ["Assistant:McpEndpoint"] = "http://127.0.0.1:59999/mcp",
            });
        });

        builder.ConfigureServices(services =>
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IOperatorAssistantAgent));
            if (existing is not null) services.Remove(existing);
            services.AddSingleton<IOperatorAssistantAgent>(Agent);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
        foreach (var d in new[] { _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Deterministic stand-in for the in-API Copilot operator loop. Optionally drives the turn sink with
/// a single tool call + result so tests can assert the tool-step events reach the run stream, then
/// returns a fixed assistant message. No network, no model.
/// </summary>
public sealed class FakeOperatorAssistantAgent : IOperatorAssistantAgent
{
    public string ReplyText { get; set; } = "ok";
    public bool EmitTool { get; set; }
    public string ToolName { get; set; } = "tool";
    public IReadOnlyList<string> ToolNamesInvoked { get; set; } = Array.Empty<string>();

    public async Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
    {
        if (sink is not null && EmitTool)
        {
            await sink.OnToolCallAsync(ToolName, "{}", ct);
            await sink.OnToolResultAsync(ToolName, success: true, ct);
        }

        if (sink is not null)
            await sink.OnAssistantTextDeltaAsync(ReplyText, ct);

        return new OperatorAssistantResponse(ReplyText, ToolNamesInvoked);
    }
}
