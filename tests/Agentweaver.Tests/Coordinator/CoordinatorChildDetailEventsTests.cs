using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Integration tests for the Feature 008 child run-detail contract (Morpheus's wave):
/// <list type="bullet">
/// <item><c>GET /api/runs/{id}</c> must expose <c>parent_run_id</c> + <c>subtask_id</c> so the web
/// run-detail page can recognise a coordinator CHILD and render the TRIMMED pipeline instead of the
/// full 5-stage graph.</item>
/// <item><c>GET /api/runs/{id}/events</c> must return the persisted RunEvents (ordered by sequence)
/// for a run, so a finished child's execution log is non-empty and replayable after the in-memory
/// stream entry is evicted. The assemble-ready terminal persists the child's agent stream via
/// <see cref="RunWorkflowFactory.PersistRunEventsAsync"/>; this test drives that exact production
/// mechanism (real <see cref="RunStreamStore"/> + real <see cref="RunWorkflowFactory"/>) and then
/// reads the events back through the REST endpoint.</item>
/// </list>
///
/// Each test runs against a real in-process API host, real SQLite, and the real DI singletons baked
/// into <see cref="CoordinatorWebApplicationFactory"/> (no mocks, Principle VII).
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorChildDetailEventsTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public CoordinatorChildDetailEventsTests()
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

    // =========================================================================
    // GET /api/runs/{id} — child identity in RunResponse.
    // =========================================================================
    [Fact]
    public async Task GetRun_ForChild_IncludesParentRunIdAndSubtaskId()
    {
        var parentRunId = RunId.New().ToString();
        const string subtaskId = "7";
        var childRunId = await InsertChildRunAsync(CoordinatorWebApplicationFactory.OwnerUser, parentRunId, subtaskId);

        var resp = await _owner.GetAsync($"/api/runs/{childRunId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("parent_run_id").GetString().Should().Be(parentRunId,
            "the child run-detail contract must surface the coordinator parent id (snake_case)");
        body.GetProperty("subtask_id").GetString().Should().Be(subtaskId,
            "the child run-detail contract must surface the originating subtask id (snake_case)");
    }

    [Fact]
    public async Task GetRun_ForStandaloneRun_HasNullChildIdentity()
    {
        var standaloneRunId = await InsertChildRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser, parentRunId: null, subtaskId: null);

        var resp = await _owner.GetAsync($"/api/runs/{standaloneRunId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("parent_run_id").ValueKind.Should().Be(JsonValueKind.Null,
            "a non-child run must report a null parent_run_id");
        body.GetProperty("subtask_id").ValueKind.Should().Be(JsonValueKind.Null,
            "a non-child run must report a null subtask_id");
    }

    // =========================================================================
    // GET /api/runs/{id}/events — persisted execution log for a finished child.
    // =========================================================================
    [Fact]
    public async Task GetEvents_AfterChildAssembleReady_ReturnsPersistedAgentEvents()
    {
        var parentRunId = RunId.New().ToString();
        var childRunId = await InsertChildRunAsync(CoordinatorWebApplicationFactory.OwnerUser, parentRunId, "3");

        // Drive the SAME persistence mechanism the assemble-ready terminal uses
        // (RunWatchLoopService -> RunWorkflowFactory.PersistRunEventsAsync): record the child's
        // trimmed-pipeline stream (agent + assemble-ready), complete it, then persist.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var workflowFactory = _factory.Services.GetRequiredService<RunWorkflowFactory>();

        var entry = streamStore.Create(childRunId, CoordinatorWebApplicationFactory.OwnerUser);
        entry.RecordNext(EventTypes.RunStarted, new { runId = childRunId });
        entry.RecordNext(EventTypes.AgentMessage, new { messageId = "m1", content = "child agent did the subtask" });
        entry.RecordNext(EventTypes.RunAssembleReady, new { runId = childRunId, parentRunId, subtaskId = "3" });
        streamStore.Complete(childRunId);

        await workflowFactory.PersistRunEventsAsync(childRunId);

        var resp = await _owner.GetAsync($"/api/runs/{childRunId}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await resp.Content.ReadFromJsonAsync<List<EventDto>>();
        events.Should().NotBeNull();
        events!.Should().NotBeEmpty("a finished child's execution log must be non-empty after stream eviction");
        events.Should().BeInAscendingOrder(e => e.Sequence, "events must be ordered by sequence");
        events.Select(e => e.Type).Should().Contain(EventTypes.AgentMessage,
            "the persisted log must include the child's agent events");
        events.Select(e => e.Type).Should().NotContain(EventTypes.RaiVerdict,
            "child runs no longer launch a per-child RAI sub-stream");
        events.Select(e => e.Type).Should().Contain(EventTypes.RunAssembleReady,
            "the persisted log must include the child's assemble-ready terminal");
    }

    [Fact]
    public async Task GetEvents_NonOwner_Returns403()
    {
        var childRunId = await InsertChildRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser, RunId.New().ToString(), "1");

        var resp = await _other.GetAsync($"/api/runs/{childRunId}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "events are owner-scoped like the other run endpoints");
    }

    [Fact]
    public async Task GetEvents_UnknownRun_Returns404()
    {
        var resp = await _owner.GetAsync($"/api/runs/{RunId.New()}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEvents_InvalidId_Returns400()
    {
        var resp = await _owner.GetAsync("/api/runs/not-a-run-id/events");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Inserts a run owned by <paramref name="ownerUser"/>. When <paramref name="parentRunId"/> is
    /// set the run is a coordinator CHILD (carrying <paramref name="subtaskId"/>); otherwise it is a
    /// standalone run.
    /// </summary>
    private async Task<string> InsertChildRunAsync(string ownerUser, string? parentRunId, string? subtaskId)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child subtask",
            SubmittingUser = ownerUser,
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = parentRunId,
            SubtaskId = subtaskId,
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }

    private sealed record EventDto(int Sequence, string Type, JsonElement Payload);
}
