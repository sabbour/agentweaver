using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the ask_question bubbling foundation:
/// <list type="bullet">
/// <item>POST /api/runs/{id}/questions/{requestId}/answer resolves a pending
/// <see cref="IQuestionGate"/> wait (pending -> answered), and returns 404/409 for the
/// wrong state.</item>
/// <item><see cref="CoordinatorDispatchService.BubbleChildInteraction"/> re-projects a child's
/// <see cref="EventTypes.AgentQuestionAsked"/> and <see cref="EventTypes.ToolApprovalRequired"/>
/// onto the COORDINATOR run stream as <see cref="EventTypes.CoordinatorChildQuestion"/> /
/// <see cref="EventTypes.CoordinatorChildApprovalRequired"/>.</item>
/// </list>
/// Real in-process API host, real SQLite, real DI singletons (no mocks).
/// </summary>
public sealed class AskQuestionBubblingTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public AskQuestionBubblingTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
        _other = _factory.CreateOtherClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _other.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task AnswerEndpoint_PendingQuestion_ResolvesGate_And200()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        var gate = _factory.Services.GetRequiredService<IQuestionGate>();
        const string requestId = "q-1";

        // Suspend on the gate exactly as the ask_question tool would.
        var askTask = gate.AskAsync(runId, requestId, "Which framework?", TimeSpan.FromMinutes(5), CancellationToken.None);
        askTask.IsCompleted.Should().BeFalse();

        var resp = await _owner.PostAsJsonAsync(
            $"/api/runs/{runId}/questions/{requestId}/answer", new { answer = "Use xUnit" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await askTask).Should().Be("Use xUnit", "the answer must flow back to the suspended ask_question call");
    }

    [Fact]
    public async Task AnswerEndpoint_NoPendingQuestion_Returns409()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync(
            $"/api/runs/{runId}/questions/does-not-exist/answer", new { answer = "anything" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "answering a request that is not pending must be a 409");
    }

    [Fact]
    public async Task AnswerEndpoint_UnknownRun_Returns404()
    {
        var resp = await _owner.PostAsJsonAsync(
            $"/api/runs/{RunId.New()}/questions/q-1/answer", new { answer = "anything" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnswerEndpoint_NonOwner_Returns403()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        var gate = _factory.Services.GetRequiredService<IQuestionGate>();
        _ = gate.AskAsync(runId, "q-1", "Q?", TimeSpan.FromMinutes(5), CancellationToken.None);

        var resp = await _other.PostAsJsonAsync(
            $"/api/runs/{runId}/questions/q-1/answer", new { answer = "anything" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void BubbleChildInteraction_ChildQuestion_ReProjectsOntoCoordinatorStream()
    {
        const string coordinatorRunId = "coord-bubble-1";
        const string childRunId = "child-bubble-1";
        const int subtaskId = 7;

        var streamStore = new RunStreamStore();
        streamStore.Create(coordinatorRunId, "alice");
        var sut = NewDispatchService(streamStore);

        var evt = new RunEvent(1, EventTypes.AgentQuestionAsked, new { requestId = "rq-1", question = "Proceed with renaming?" });
        sut.BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);

        var events = streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events;
        var bubbled = events.Should().ContainSingle(e => e.Type == EventTypes.CoordinatorChildQuestion).Subject;

        var payload = JsonSerializer.SerializeToElement(bubbled.Payload);
        payload.GetProperty("childRunId").GetString().Should().Be(childRunId);
        payload.GetProperty("subtaskId").GetInt32().Should().Be(subtaskId);
        payload.GetProperty("requestId").GetString().Should().Be("rq-1");
        payload.GetProperty("question").GetString().Should().Be("Proceed with renaming?");
    }

    [Fact]
    public void BubbleChildInteraction_ChildApprovalRequired_ReProjectsOntoCoordinatorStream()
    {
        const string coordinatorRunId = "coord-bubble-2";
        const string childRunId = "child-bubble-2";
        const int subtaskId = 3;

        var streamStore = new RunStreamStore();
        streamStore.Create(coordinatorRunId, "alice");
        var sut = NewDispatchService(streamStore);

        var evt = new RunEvent(1, EventTypes.ToolApprovalRequired, new
        {
            requestId = "tr-1",
            toolName = "web_fetch",
            url = "https://example.com",
            message = "needs approval",
        });
        sut.BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);

        var events = streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events;
        var bubbled = events.Should().ContainSingle(e => e.Type == EventTypes.CoordinatorChildApprovalRequired).Subject;

        var payload = JsonSerializer.SerializeToElement(bubbled.Payload);
        payload.GetProperty("childRunId").GetString().Should().Be(childRunId);
        payload.GetProperty("subtaskId").GetInt32().Should().Be(subtaskId);
        payload.GetProperty("requestId").GetString().Should().Be("tr-1");
        payload.GetProperty("toolName").GetString().Should().Be("web_fetch");
    }

    [Fact]
    public void BubbleChildInteraction_NonInteractionEvent_DoesNotEmit()
    {
        const string coordinatorRunId = "coord-bubble-3";
        var streamStore = new RunStreamStore();
        streamStore.Create(coordinatorRunId, "alice");
        var sut = NewDispatchService(streamStore);

        var evt = new RunEvent(1, EventTypes.AgentMessage, new { content = "working" });
        sut.BubbleChildInteraction(coordinatorRunId, 1, "child-3", evt);

        streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Should().NotContain(e => e.Type == EventTypes.CoordinatorChildQuestion
                || e.Type == EventTypes.CoordinatorChildApprovalRequired);
    }

    private static CoordinatorDispatchService NewDispatchService(RunStreamStore streamStore) =>
        new(
            runStore: null!,
            streamStore,
            orchestrator: null!,
            worktreeManager: null!,
            steering: null!,
            assembly: null!,
            scopeFactory: null!,
            lifetime: new StubLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance);

    private sealed class StubLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private async Task<string> InsertInProgressRunAsync(string ownerUser)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "asking a question",
            SubmittingUser = ownerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }
}
