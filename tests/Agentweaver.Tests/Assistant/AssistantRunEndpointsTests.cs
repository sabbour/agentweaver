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
    public async Task SendMessage_ConcurrentToolCallbacks_PersistWithoutSequenceGaps()
    {
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ConcurrentToolBurstCount = 10;
        factory.Agent.ToolName = "project_list";
        factory.Agent.ReplyText = "Burst complete.";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var turn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "burst tool callbacks" });
        turn.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await GetEventsAsync(client, runId);
        var burstToolCalls = events.Where(e =>
            e.Type == EventTypes.ToolCall
            && e.Payload.TryGetProperty("name", out var name)
            && name.GetString()!.StartsWith("project_list-", StringComparison.Ordinal))
            .ToList();
        var burstToolResults = events.Where(e =>
            e.Type == EventTypes.ToolResult
            && e.Payload.TryGetProperty("name", out var name)
            && name.GetString()!.StartsWith("project_list-", StringComparison.Ordinal))
            .ToList();

        burstToolCalls.Should().HaveCount(factory.Agent.ConcurrentToolBurstCount);
        burstToolResults.Should().HaveCount(factory.Agent.ConcurrentToolBurstCount);
        events.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, events.Count));
    }

    [Fact]
    public async Task SendMessage_GatedTool_EmitsApprovalRequired_AndRunsWhenApproved()
    {
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.EmitApproval = true;
        factory.Agent.ApprovalToolName = "coordinator_start";
        factory.Agent.ReplyText = "Started the coordinator.";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        // The turn blocks inside the gate until the operator approves, so post the message without
        // awaiting it, resolve the approval on the SAME generic endpoint the coordinator uses, then
        // await the turn.
        var turnTask = client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "start coordinator" });

        var requestId = await WaitForApprovalRequestIdAsync(client, runId);
        requestId.Should().NotBeNullOrWhiteSpace("the gated tool must raise a tool.approval_required event");

        var approve = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals", new { request_id = requestId, scope = "once" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, "the generic approve endpoint must resolve an operator run's approval");

        var turn = await turnTask;
        turn.StatusCode.Should().Be(HttpStatusCode.OK);
        (await turn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()
            .Should().Be("Started the coordinator.");

        factory.Agent.LastApprovalGranted.Should().BeTrue("the sink must report the operator approved the tool");

        var events = await GetEventsAsync(client, runId);
        var types = events.Select(e => e.Type).ToList();
        types.Should().Contain(EventTypes.ToolApprovalRequired);
        types.Should().Contain(EventTypes.ToolApprovalResolved);

        var required = events.First(e => e.Type == EventTypes.ToolApprovalRequired);
        required.Payload.GetProperty("requestId").GetString().Should().Be(requestId);
        required.Payload.GetProperty("toolName").GetString().Should().Be("coordinator_start");
    }

    [Fact]
    public async Task SendMessage_GatedTool_DeniedByOperator_ReturnsDeniedDecision()
    {
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.EmitApproval = true;
        factory.Agent.ApprovalToolName = "run_submit";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var turnTask = client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "submit a run" });

        var requestId = await WaitForApprovalRequestIdAsync(client, runId);
        requestId.Should().NotBeNullOrWhiteSpace();

        var deny = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-denials", new { request_id = requestId });
        deny.StatusCode.Should().Be(HttpStatusCode.OK);

        var turn = await turnTask;
        turn.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Agent.LastApprovalGranted.Should().BeFalse("a denied approval must surface as a false decision to the tool loop");

        var events = await GetEventsAsync(client, runId);
        events.Select(e => e.Type).Should().Contain(EventTypes.ToolApprovalResolved);
        // The frontend's derivePendingApprovals matches the resolution by the camelCase
        // `requestId`/`approved` fields emitted on the operator run's own stream by the sink.
        var resolved = events.First(e =>
            e.Type == EventTypes.ToolApprovalResolved &&
            e.Payload.TryGetProperty("approved", out _) &&
            e.Payload.TryGetProperty("requestId", out var rid) &&
            rid.GetString() == requestId);
        resolved.Payload.GetProperty("approved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ListRuns_ReturnsOnlyCallersOwnRuns_NewestFirst()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        var first = (await (await client.PostAsJsonAsync("/api/assistant/runs", new { message = "first convo" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        await Task.Delay(10);
        var second = (await (await client.PostAsJsonAsync("/api/assistant/runs", new { message = "second convo" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        // Seed an operator run owned by a DIFFERENT user directly in the store — it must never leak.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var otherUserRunId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = otherUserRunId,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "not your conversation",
            SubmittingUser = "someone-else",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);

        var response = await client.GetAsync("/api/assistant/runs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runs = body.GetProperty("runs").EnumerateArray()
            .Select(r => (Id: r.GetProperty("run_id").GetString()!, Title: r.GetProperty("title").GetString()!))
            .ToList();

        runs.Select(r => r.Id).Should().Contain(new[] { first, second });
        runs.Select(r => r.Id).Should().NotContain(otherUserRunId.ToString(), "another user's run must never leak");
        runs[0].Id.Should().Be(second, "runs are returned newest-first");
        runs.First(r => r.Id == first).Title.Should().Be("first convo");
    }

    [Fact]
    public async Task ListRuns_RequiresAuth_401WithoutToken()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/assistant/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendMessage_ToInProgressRun_CacheMiss_StillRehydrates()
    {
        // A genuine cache-miss on a run that is still InProgress and UNSEALED (a pod restart or
        // cross-replica route, NOT an idle closure) must still rehydrate and accept the turn — the
        // no-revive-sealed guard must only block runs whose stream is already terminal.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "Resumed.";
        var client = AuthedClient(factory);

        // Seed an InProgress operator run owned by the caller directly in the store, with NO terminal
        // event — it was never resident in this service instance, so the message forces rehydration.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "resumable conversation",
            SubmittingUser = AssistantWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);

        var turn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "are you still here?" });
        turn.StatusCode.Should().Be(HttpStatusCode.OK, "an unsealed InProgress run must rehydrate on a cache-miss");
        (await turn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Be("Resumed.");
    }

    [Fact]
    public async Task SendMessage_AfterIdleTimeout_WakesDormantRun_ContinuesSameConversation()
    {
        // Standing product directive: an Assistant/Operator conversation must NEVER die from human
        // wait time. Idle-timeout must PARK the conversation dormant (RunStatus.Idle) instead of ending
        // it, and the very next message must transparently un-sleep it and continue as the SAME run
        // with prior history intact — no error surfaced, no new run minted.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "First reply.";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var firstTurn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "remember the number 42" });
        firstTurn.StatusCode.Should().Be(HttpStatusCode.OK);

        // Force the 30-minute idle sweep. The run must be PARKED dormant, NOT sealed: status Idle,
        // ended_at still null, a NON-terminal run.idle marker on the stream (never run.completed), and
        // the SSE stream left open.
        var runService = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();
        runService.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var dormant = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        dormant!.Status.Should().Be(RunStatus.Idle, "idle-timeout must park the conversation dormant, not seal it");
        dormant.EndedAt.Should().BeNull("a dormant run is paused, not ended");

        var afterParkEvents = await GetEventsAsync(client, runId);
        afterParkEvents.Count(e => e.Type == EventTypes.RunIdle).Should().Be(1, "exactly one dormancy marker");
        afterParkEvents.Count(e => e.Type == EventTypes.RunCompleted).Should().Be(0, "dormancy must not seal the stream");

        // The next message transparently WAKES the dormant run and continues the SAME conversation.
        factory.Agent.ReplyText = "You said 42.";
        var secondTurn = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "what number did I say?" });
        secondTurn.StatusCode.Should().Be(HttpStatusCode.OK,
            "a dormant conversation must wake and continue with zero error surfaced");
        (await secondTurn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Be("You said 42.");

        // Woken back to InProgress on the SAME run id, still unsealed.
        var woken = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        woken!.Status.Should().Be(RunStatus.InProgress, "waking a dormant run returns it to active");
        woken.EndedAt.Should().BeNull();

        // Prior transcript was replayed to the model on the woken turn — same conversation, history
        // intact (the rebuild-from-events path seeds the woken run's history).
        var historyTexts = factory.Agent.LastRequest!.History.Select(h => h.Text).ToList();
        historyTexts.Should().Contain("remember the number 42", "the woken turn must carry the prior user message");
        historyTexts.Should().Contain("First reply.", "the woken turn must carry the prior assistant reply");

        // Still no terminal seal; the new user turn was appended to the SAME (never-sealed) stream.
        var finalEvents = await GetEventsAsync(client, runId);
        finalEvents.Count(e => e.Type == EventTypes.RunCompleted).Should().Be(0);
        finalEvents.Where(e => e.Type == EventTypes.AgentMessage && e.Payload.GetProperty("role").GetString() == "user")
            .Select(e => e.Payload.GetProperty("content").GetString())
            .Should().Contain("what number did I say?", "the woken turn is part of the same run's transcript");
    }

    [Fact]
    public async Task SendMessage_ToGenuinelyTerminalRun_Returns409_NotRehydrated()
    {
        // Contrast to dormancy: a GENUINELY terminal conversation — one whose durable stream carries a
        // real run.completed seal (reserved for a true end-of-conversation) — must still 409 and never
        // rehydrate. This is the zombie guard: only a REAL seal blocks resumption, idle dormancy does
        // not. Seed the sealed run directly in the store (never resident in the service) so the message
        // forces the rehydrate path where the seal guard lives.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "should not run";
        var client = AuthedClient(factory);

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var eventStream = factory.Services.GetRequiredService<IRunEventStream>();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "ended conversation",
            SubmittingUser = AssistantWebApplicationFactory.TestUser,
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow,
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);
        await eventStream.AppendAsync(runId.ToString(),
            new RunEvent(0, EventTypes.RunCompleted, new { runId = runId.ToString(), reason = "completed" }), CancellationToken.None);
        await eventStream.CompleteAsync(runId.ToString());

        var resume = await client.PostAsJsonAsync($"/api/assistant/runs/{runId}/messages", new { message = "you there?" });
        resume.StatusCode.Should().Be(HttpStatusCode.Conflict, "a genuinely-sealed conversation must not rehydrate");
        (await resume.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("operator_run_closed");

        var afterRun = await runStore.GetAsync(runId, CancellationToken.None);
        afterRun!.Status.Should().Be(RunStatus.Completed, "a rejected resume must never resurrect a sealed run");
        var events = await GetEventsAsync(client, runId.ToString());
        events.Where(e => e.Type == EventTypes.AgentMessage && e.Payload.GetProperty("role").GetString() == "user")
            .Select(e => e.Payload.GetProperty("content").GetString())
            .Should().NotContain("you there?", "a rejected resume must not append the new user turn");
    }

    [Fact]
    public async Task IdlePark_AcrossTwoReplicas_EmitsSingleRunIdle_NoDoublePark()
    {
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "hi";
        var client = AuthedClient(factory);

        // Replica A = the DI-hosted service. Start a run + opening turn so it is resident in A.
        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { message = "hello" });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var eventStream = factory.Services.GetRequiredService<IRunEventStream>();
        var gate = factory.Services.GetRequiredService<Agentweaver.Domain.IToolApprovalGate>();
        var serviceA = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();

        // Replica B = a second AssistantRunService over the SAME durable store + event stream. This
        // mirrors the live topology: k8s runs 2 API replicas, each with its own in-memory _runs map
        // and its own idle-sweep timer, and there is NO session affinity — so BOTH can independently
        // hold and then park the same run. Rehydrate the run into B via a normal cross-pod resume
        // (still InProgress and unsealed at this point, so legitimate).
        using var serviceB = new AssistantRunService(
            runStore, eventStream, factory.Agent, gate,
            Microsoft.Extensions.Options.Options.Create(new AssistantRunOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRunService>.Instance);
        var caller = new Agentweaver.Api.Security.CallerContext { User = AssistantWebApplicationFactory.TestUser };
        await serviceB.SendMessageAsync(caller, "token", runId, "still here?", CancellationToken.None);

        // Both replicas now hold the run in memory; each independently decides it is idle and parks
        // it against the shared store/stream (the exact race that once produced two idle_timeout
        // events ~99s apart live).
        serviceA.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));
        serviceB.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));

        // The compare-and-set park lets only ONE replica win, so the durable stream can never carry
        // two dormancy markers — and never a terminal seal.
        var events = await GetEventsAsync(client, runId);
        events.Count(e => e.Type == EventTypes.RunIdle).Should().Be(1,
            "only one replica's CAS park may win; the loser must be a no-op");
        events.Count(e => e.Type == EventTypes.RunCompleted).Should().Be(0, "dormancy never seals the stream");

        var parked = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        parked!.Status.Should().Be(RunStatus.Idle);
        parked.EndedAt.Should().BeNull("a dormant run is paused, not ended");
    }

    [Fact]
    public async Task WakeDormantRun_AcrossTwoReplicas_OnlyOneCasWins_NoDoubleWake()
    {
        // Mirror of the double-park race, in reverse: two replicas racing to WAKE the same dormant run
        // must transition it exactly once. The CAS Idle->InProgress is single-winner.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "hi";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { message = "hello" });
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        // Park the run dormant via the idle sweep.
        var runService = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();
        runService.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var parsed = RunId.Parse(runId);
        (await runStore.GetAsync(parsed, CancellationToken.None))!.Status.Should().Be(RunStatus.Idle);

        // Two replicas race to wake it. Exactly one observes Idle and flips it; the other gets false.
        var first = await runStore.TryWakeFromIdleAsync(parsed, CancellationToken.None);
        var second = await runStore.TryWakeFromIdleAsync(parsed, CancellationToken.None);
        first.Should().BeTrue("the first waker wins the CAS");
        second.Should().BeFalse("the second waker must be a no-op — no double-wake");

        (await runStore.GetAsync(parsed, CancellationToken.None))!.Status.Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task SweepIdleRuns_DoesNotClose_RunAwaitingHumanToolApproval()
    {
        // Standing rule: a run must never die from human-response wait time. A conversation parked on
        // a tool-approval card is actively blocked on the operator (who may have merely stepped away),
        // so the idle sweep must skip it — never seal the stream or clear the pending approval out from
        // under them. Mirrors the coordinator's AssemblyReviewGate indefinite-safe wait.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "hi";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { message = "start something" });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var gate = factory.Services.GetRequiredService<Agentweaver.Domain.IToolApprovalGate>();
        var runService = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();

        // Arm a pending tool-approval for this run (WaitForApprovalAsync registers the context
        // synchronously) and leave it UNRESOLVED — i.e. the operator has not yet clicked approve/deny.
        using var approvalCts = new CancellationTokenSource();
        var pendingApproval = gate.WaitForApprovalAsync(
            runId, "req-hitl", "coordinator_start", url: null, TimeSpan.FromMinutes(10), approvalCts.Token);
        gate.HasArmedApproval(runId).Should().BeTrue("the approval must be armed before the sweep runs");

        // Drive the idle sweep an hour past the 30-minute idle timeout — the armed approval must
        // protect the run from being idle-closed.
        runService.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var run = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        run!.Status.Should().Be(RunStatus.InProgress, "a run awaiting a human approval must not be idle-closed");
        run.EndedAt.Should().BeNull("the run must remain live/resumable, not sealed");
        gate.HasArmedApproval(runId).Should().BeTrue("the pending approval must not be cleared by the sweep");

        var events = await GetEventsAsync(client, runId);
        events.Count(e => e.Type == EventTypes.RunCompleted).Should().Be(0,
            "no idle_timeout close may fire while the run is awaiting the operator's decision");
        events.Count(e => e.Type == EventTypes.RunIdle).Should().Be(0,
            "the run must not even be parked dormant while an approval is armed — it is actively blocked on the human");

        // Cleanup: release the still-pending approval wait so the fire-and-forget task completes.
        await approvalCts.CancelAsync();
        try { await pendingApproval; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SweepIdleRuns_StillParks_RunWithNoArmedApproval()
    {
        // Regression contrast: a genuinely idle conversation with NO armed approval is still handled by
        // the idle sweep — but now PARKED dormant (RunStatus.Idle, resumable), not sealed. The HITL
        // guard must not disable ordinary idle handling.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "hi";
        var client = AuthedClient(factory);

        var start = await client.PostAsJsonAsync("/api/assistant/runs", new { message = "start something" });
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var runService = (AssistantRunService)factory.Services.GetRequiredService<IAssistantRunService>();
        runService.SweepIdleRuns(DateTimeOffset.UtcNow.AddHours(1));

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var run = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        run!.Status.Should().Be(RunStatus.Idle,
            "a run with no armed approval must still be idle-handled — now parked dormant, not sealed");
        run.EndedAt.Should().BeNull("a dormant run is paused, not ended");

        var events = await GetEventsAsync(client, runId);
        events.Count(e => e.Type == EventTypes.RunIdle).Should().Be(1, "the sweep must emit a single dormancy marker");
        events.Count(e => e.Type == EventTypes.RunCompleted).Should().Be(0, "idle dormancy must not seal the stream");
    }

    [Fact]
    public async Task SendMessage_ToRunOwnedByAnotherUser_ReturnsForbidden_NotSilentSuccess()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        // Seed a durable operator run owned by a DIFFERENT user (never touched via this service, so
        // it is guaranteed to be a cache-miss and must go through the rehydration path's ownership
        // check).
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var otherUsersRunId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = otherUsersRunId,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "someone else's conversation",
            SubmittingUser = "someone-else",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/assistant/runs/{otherUsersRunId}/messages", new { message = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "rehydration must never let a caller resume a run they don't own");
    }

    [Fact]
    public async Task SendMessage_ToNonexistentRun_Returns404()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/assistant/runs/{RunId.New()}/messages", new { message = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a genuinely nonexistent run must still 404, not attempt rehydration");
    }

    // --- resume_from_run_id ("auto-seed" resume, #347 follow-up) ---------------------------------

    [Fact]
    public async Task StartRun_ResumeFromSealedRun_MintsNewRun_SeedsHistory_LeavesOldRunUntouched()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        // Seed a GENUINELY sealed run A directly in the store (guaranteed cache-miss + a real
        // end-of-conversation terminal seal). Idle-timeout no longer seals — it parks a run dormant and
        // resumable — so to exercise the sealed-run resume path we build a true seal here: A's
        // transcript (two agent.message events), a terminal run.completed marker, and Completed status.
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var eventStream = factory.Services.GetRequiredService<IRunEventStream>();
        var runIdA = RunId.New();
        var keyA = runIdA.ToString();
        await runStore.InsertAsync(new Run
        {
            Id = runIdA,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "hello I'm Alice",
            SubmittingUser = AssistantWebApplicationFactory.TestUser,
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);
        await eventStream.AppendAsync(keyA,
            new RunEvent(0, EventTypes.AgentMessage, new { messageId = "m1", role = "user", content = "hello I'm Alice" }), CancellationToken.None);
        await eventStream.AppendAsync(keyA,
            new RunEvent(0, EventTypes.AgentMessage, new { messageId = "m2", role = "assistant", content = "First reply." }), CancellationToken.None);
        await eventStream.AppendAsync(keyA,
            new RunEvent(0, EventTypes.RunCompleted, new { runId = keyA, reason = "completed" }), CancellationToken.None);
        await eventStream.CompleteAsync(keyA);

        var sealedEndedAt = (await runStore.GetAsync(runIdA, CancellationToken.None))!.EndedAt;

        // A follow-up on the SEALED run A must 409, confirming it really is sealed (guard untouched)
        // before we exercise the resume path against it.
        var reviveAttempt = await client.PostAsJsonAsync($"/api/assistant/runs/{keyA}/messages", new { message = "still there?" });
        reviveAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        factory.Agent.ReplyText = "Continuing our chat.";
        var startB = await client.PostAsJsonAsync("/api/assistant/runs",
            new { message = "are you still there?", resume_from_run_id = keyA });
        startB.StatusCode.Should().Be(HttpStatusCode.Created);
        var bodyB = await startB.Content.ReadFromJsonAsync<JsonElement>();
        var runIdB = bodyB.GetProperty("run_id").GetString()!;

        runIdB.Should().NotBe(keyA, "resuming must mint a genuinely new run id");
        bodyB.GetProperty("status").GetString().Should().Be("in_progress");
        bodyB.GetProperty("message").GetString().Should().Be("Continuing our chat.");

        // Run A must be completely untouched by the resume: same terminal status, same ended_at.
        var afterRunA = await runStore.GetAsync(runIdA, CancellationToken.None);
        afterRunA!.Status.Should().Be(RunStatus.Completed);
        afterRunA.EndedAt.Should().Be(sealedEndedAt, "resuming must never touch the old sealed run");

        // The new run's model context was pre-loaded with A's prior transcript: the fake agent must
        // have received A's opening exchange as part of B's own opening-turn request history.
        factory.Agent.LastRequest.Should().NotBeNull();
        var historyTexts = factory.Agent.LastRequest!.History.Select(h => h.Text).ToList();
        historyTexts.Should().Contain("hello I'm Alice", "B's context must include A's user turn");
        historyTexts.Should().Contain("First reply.", "B's context must include A's assistant turn");

        // The new run's own event stream carries an observability marker linking it back to A.
        var eventsB = await GetEventsAsync(client, runIdB);
        var started = eventsB.First(e => e.Type == EventTypes.RunStarted);
        started.Payload.GetProperty("resumedFromRunId").GetString().Should().Be(keyA);
    }

    [Fact]
    public async Task StartRun_ResumeFromStillInProgressRun_StillSeedsHistory_NotRestrictedToSealedRuns()
    {
        // Resuming isn't restricted to sealed runs — it's the primary use case, not a hard requirement.
        await using var factory = new AssistantWebApplicationFactory();
        factory.Agent.ReplyText = "ok";
        var client = AuthedClient(factory);

        var startA = await client.PostAsJsonAsync("/api/assistant/runs", new { message = "keep this in mind: 42" });
        startA.StatusCode.Should().Be(HttpStatusCode.Created);
        var runIdA = (await startA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        (await runStore.GetAsync(RunId.Parse(runIdA), CancellationToken.None))!.Status.Should().Be(RunStatus.InProgress);

        factory.Agent.ReplyText = "sure";
        var startB = await client.PostAsJsonAsync("/api/assistant/runs",
            new { message = "hi again", resume_from_run_id = runIdA });

        startB.StatusCode.Should().Be(HttpStatusCode.Created,
            "resuming from a still-InProgress run must succeed, not be rejected");
        factory.Agent.LastRequest!.History.Select(h => h.Text).Should().Contain("keep this in mind: 42");
    }

    [Fact]
    public async Task StartRun_ResumeFromRunOwnedByAnotherUser_ReturnsForbidden_NoNewRunCreated()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var otherUsersRunId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = otherUsersRunId,
            RepositoryPath = string.Empty,
            OriginatingBranch = string.Empty,
            ModelSource = ModelSource.GitHubCopilot,
            Task = "someone else's conversation",
            SubmittingUser = "someone-else",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = AssistantRunService.OperatorAgentName,
        }, CancellationToken.None);

        var before = await runStore.GetRunsBySubmittingUserAsync(
            AssistantWebApplicationFactory.TestUser, AssistantRunService.OperatorAgentName, 200, CancellationToken.None);

        var response = await client.PostAsJsonAsync("/api/assistant/runs",
            new { message = "hi", resume_from_run_id = otherUsersRunId.ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "resuming from a run owned by a different user must never be allowed");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("error").GetString().Should().Be("forbidden");

        var after = await runStore.GetRunsBySubmittingUserAsync(
            AssistantWebApplicationFactory.TestUser, AssistantRunService.OperatorAgentName, 200, CancellationToken.None);
        after.Count.Should().Be(before.Count, "a forbidden resume must not create a new run for the caller");
    }

    [Fact]
    public async Task StartRun_ResumeFromNonexistentRunId_Returns404_NoNewRunCreated()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var before = await runStore.GetRunsBySubmittingUserAsync(
            AssistantWebApplicationFactory.TestUser, AssistantRunService.OperatorAgentName, 200, CancellationToken.None);

        var response = await client.PostAsJsonAsync("/api/assistant/runs",
            new { message = "hi", resume_from_run_id = RunId.New().ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("error").GetString().Should().Be("run_not_found");

        var after = await runStore.GetRunsBySubmittingUserAsync(
            AssistantWebApplicationFactory.TestUser, AssistantRunService.OperatorAgentName, 200, CancellationToken.None);
        after.Count.Should().Be(before.Count, "a 404 resume must not create a new run for the caller");
    }

    [Fact]
    public async Task StartRun_ResumeFromGarbageRunId_Returns404()
    {
        await using var factory = new AssistantWebApplicationFactory();
        var client = AuthedClient(factory);

        var response = await client.PostAsJsonAsync("/api/assistant/runs",
            new { message = "hi", resume_from_run_id = "not-a-real-run-id" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("error").GetString().Should().Be("run_not_found");
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
            e.TryGetProperty("sequence", out var seq) ? seq.GetInt32() : 0,
            e.GetProperty("type").GetString()!,
            e.GetProperty("payload"))).ToList();
    }

    /// <summary>Polls the run's event stream until a tool.approval_required event appears and returns
    /// its requestId, or null if none arrives within the timeout.</summary>
    private static async Task<string?> WaitForApprovalRequestIdAsync(HttpClient client, string runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var events = await GetEventsAsync(client, runId);
            var required = events.FirstOrDefault(e => e.Type == EventTypes.ToolApprovalRequired);
            if (required is not null &&
                required.Payload.TryGetProperty("requestId", out var id) &&
                id.GetString() is { Length: > 0 } requestId)
                return requestId;
            await Task.Delay(100);
        }
        return null;
    }

    private sealed record EventRow(int Sequence, string Type, JsonElement Payload);
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
                ["Auth:Mode"] = "GitHubLegacy",
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
    public int ConcurrentToolBurstCount { get; set; }

    /// <summary>When set, the fake drives one approval-gated tool call through the sink before
    /// replying: it emits a tool.call, asks the sink for approval, and emits a tool.result whose
    /// success reflects the decision. Mirrors what the real agent's approval-gating wrapper does.</summary>
    public bool EmitApproval { get; set; }
    public string ApprovalToolName { get; set; } = "coordinator_start";

    /// <summary>Captures the approval decision the sink returned, for test assertions.</summary>
    public bool? LastApprovalGranted { get; private set; }

    /// <summary>Captures the most recent request the fake was invoked with, so tests can assert on
    /// the conversation <see cref="OperatorAssistantRequest.History"/> the caller supplied (e.g. to
    /// verify a resumed run's opening turn was seeded with a prior conversation's transcript).</summary>
    public OperatorAssistantRequest? LastRequest { get; private set; }

    public async Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
    {
        LastRequest = request;

        if (sink is not null && EmitApproval)
        {
            var requestId = Guid.NewGuid().ToString("N");
            await sink.OnToolCallAsync(ApprovalToolName, "{}", ct);
            var approved = await sink.OnApprovalRequiredAsync(requestId, ApprovalToolName, "{}", ct);
            LastApprovalGranted = approved;
            await sink.OnToolResultAsync(ApprovalToolName, success: approved, ct);
        }

        if (sink is not null && EmitTool)
        {
            await sink.OnToolCallAsync(ToolName, "{}", ct);
            await sink.OnToolResultAsync(ToolName, success: true, ct);
        }

        if (sink is not null && ConcurrentToolBurstCount > 0)
        {
            var burst = Enumerable.Range(0, ConcurrentToolBurstCount)
                .Select(async i =>
                {
                    var burstTool = $"{ToolName}-{i}";
                    await sink.OnToolCallAsync(burstTool, "{}", ct);
                    await sink.OnToolResultAsync(burstTool, success: true, ct);
                });
            await Task.WhenAll(burst);
        }

        if (sink is not null)
            await sink.OnAssistantTextDeltaAsync(ReplyText, ct);

        return new OperatorAssistantResponse(ReplyText, ToolNamesInvoked);
    }
}
