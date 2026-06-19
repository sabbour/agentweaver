using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Integration tests for the Feature 008 Phase 1 coordinator outcome-spec flow.
///
/// Every test runs against a real in-process API host, a real SQLite database, the real
/// <see cref="CoordinatorRunService"/> + <c>CoordinatorWorkflowFactory</c>, and the real MAF
/// request-port suspend/resume machinery. There are no mocks (Principle VII): the only seam is a
/// signed-out <see cref="SignedOutGitHubTokenStore"/> so the drafting agent turn fails closed and
/// the workflow uses its built-in deterministic draft — a real component, exercised exactly as it
/// is in production when Copilot is unavailable.
///
/// Coverage:
///   - StartCoordinatorRunAsync drafts a spec (awaiting_confirmation), persists it, emits
///     coordinator.outcome_spec, and SUSPENDS at the confirmation gate with no dispatch.
///   - The three CoordinatorGateOutcome branches: Accepted (200), RunNotActive (409),
///     NoPendingGate (409) — at both the service and HTTP-mapping levels.
///   - Confirm advances the spec to confirmed and records the caller as ConfirmedBy; the gate is
///     consumed atomically (no double-consume).
///   - Revise re-drafts and re-suspends (status back to awaiting_confirmation) with no dispatch.
///   - Owner-scoping: non-owner 403, missing run 404, missing/invalid project 404/400.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorOutcomeSpecTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public CoordinatorOutcomeSpecTests()
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
    // Start: draft + persist + emit + suspend at the gate, no dispatch.
    // =========================================================================
    [Fact]
    public async Task Start_DraftsSpec_PersistsAwaitingConfirmation_EmitsEvent_SuspendsAtGate()
    {
        var projectId = await CreateProjectAsync();

        var runId = await StartOrchestrationAsync(projectId, "Build a deterministic outcome spec for testing");

        // The run suspends at the confirmation gate: the watch loop registers the pending request.
        await WaitForGateAsync(runId);

        // The drafted spec is persisted and surfaced as awaiting_confirmation with all fields set.
        var spec = await GetOutcomeSpecAsync(_owner, runId);
        spec.Should().NotBeNull("the coordinator must persist a draft before suspending");
        spec!.Status.Should().Be("awaiting_confirmation");
        spec.Goal.Should().Be("Build a deterministic outcome spec for testing");
        spec.DesiredOutcome.Should().NotBeNullOrWhiteSpace();
        spec.Scope.Should().NotBeNullOrWhiteSpace();
        spec.Assumptions.Should().NotBeNullOrWhiteSpace();
        spec.ConfirmedBy.Should().BeNull("no one has confirmed an awaiting_confirmation spec");

        // The coordinator.outcome_spec event is emitted on the run stream, carrying the goal so the
        // UI populates the GOAL field live even when its GET snapshot raced ahead of the draft.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(runId);
        entry.Should().NotBeNull();
        var specEvent = entry!.GetSnapshotSince(0).Events.FirstOrDefault(
            e => e.Type == EventTypes.CoordinatorOutcomeSpec);
        specEvent.Should().NotBeNull(
            "the draft executor must emit coordinator.outcome_spec before suspending");
        var specPayload = JsonSerializer.SerializeToElement(specEvent!.Payload);
        specPayload.GetProperty("goal").GetString().Should().Be(
            "Build a deterministic outcome spec for testing",
            "the outcome_spec event must carry the goal for the UI GOAL field");

        // No dispatch in Phase 1: the run stays in_progress (suspended), not terminal, and no
        // child run was created.
        var run = await GetRunAsync(_owner, runId);
        run!.Status.Should().Be("in_progress",
            "the coordinator run must remain suspended at the gate, not dispatch or terminate");

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var stored = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        stored!.AgentName.Should().Be("Coordinator");
        stored.ParentRunId.Should().BeNull("the coordinator run is the parent, it has no parent");
        stored.SubtaskId.Should().BeNull("Phase 1 does not decompose into subtasks");
    }

    // =========================================================================
    // Confirm (Accepted): advances to confirmed, records caller as ConfirmedBy.
    // =========================================================================
    [Fact]
    public async Task Confirm_OnPendingGate_AdvancesToConfirmed_RecordsCaller()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Confirm advances the spec to confirmed");
        await WaitForGateAsync(runId);

        var confirmResp = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK, "Accepted must map to 200");

        // Finalize runs asynchronously; poll until the persisted spec reaches confirmed.
        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull("confirm must advance the spec to confirmed");
        spec!.ConfirmedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser,
            "the confirming caller's user must be recorded as ConfirmedBy");

        // The gate has been consumed: no pending request remains for this run.
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        pendingStore.Get(runId).Should().BeNull("confirm must atomically consume the pending gate");
    }

    // =========================================================================
    // Confirm (RunNotActive at HTTP layer): an existing run with no live workflow -> 409.
    // =========================================================================
    [Fact]
    public async Task Confirm_RunExistsButNotActive_Returns409_RunNotActive()
    {
        // A coordinator run owned by the owner, persisted but never started (no live workflow).
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "RunNotActive must map to 409");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("run_not_active");
    }

    // =========================================================================
    // Confirm idempotency / no double-consume: the second confirm cannot also succeed.
    // After the first confirm the gate is consumed and the run finalizes, so the second
    // confirm returns 409 (RunNotActive or NoPendingGate) — never a second 200.
    // =========================================================================
    [Fact]
    public async Task Confirm_Twice_SecondIsRejected_NoDoubleConsume()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Double confirm must not double consume");
        await WaitForGateAsync(runId);

        var first = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "the first confirm consumes the gate");

        // Ensure the first decision was fully processed and the run finalized.
        await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");

        var second = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the gate was already consumed, so a second confirm must be rejected with 409");
    }

    // =========================================================================
    // NoPendingGate (service level): the run is active/registered but the gate has been
    // drained, so a confirm returns NoPendingGate rather than RunNotActive.
    // =========================================================================
    [Fact]
    public async Task Confirm_ActiveRunWithDrainedGate_ReturnsNoPendingGate()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Drained gate yields NoPendingGate");
        await WaitForGateAsync(runId);

        // Drain the pending request directly, leaving the run live in the registry but with no
        // gate to consume. This is the precise condition the NoPendingGate branch guards.
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        pendingStore.TryRemove(runId).Should().NotBeNull("the gate must be pending before draining");

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            runId, CoordinatorWebApplicationFactory.OwnerUser, CancellationToken.None);

        outcome.Should().Be(CoordinatorGateOutcome.NoPendingGate,
            "an active run whose gate has been consumed must report NoPendingGate, not RunNotActive");
    }

    // =========================================================================
    // Confirm-gate ordering race (regression for the revise -> confirm bug):
    // after a re-draft the spec is persisted/emitted as awaiting_confirmation (UI enables
    // Confirm) BEFORE the MAF runtime suspends and the watch loop arms the pending gate. A fast
    // confirm in that window finds an empty gate. The fix is a bounded wait: while the spec is
    // still awaiting_confirmation, confirm must WAIT for the gate to arm and then SUCCEED — never
    // return NoPendingGate prematurely.
    // =========================================================================
    [Fact]
    public async Task Confirm_GateArmsAfterClick_WaitsAndSucceeds_NotNoPendingGate()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Confirm must wait for the gate to arm");
        await WaitForGateAsync(runId);

        // The persisted spec is awaiting_confirmation at this point.
        (await GetOutcomeSpecAsync(_owner, runId))!.Status.Should().Be("awaiting_confirmation");

        // Simulate the not-yet-armed window: drain the real pending entry, then re-arm it after a
        // short delay (the watch loop would do this once the MAF runtime suspends). We re-arm with
        // the very same ExternalRequest so SendResponseAsync drives a real confirmation.
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        var drained = pendingStore.TryRemove(runId);
        drained.Should().NotBeNull("the gate must be pending before draining");

        const int reArmDelayMs = 350;
        _ = Task.Run(async () =>
        {
            await Task.Delay(reArmDelayMs);
            pendingStore.Set(runId, drained!.Request, drained.OwnerUser);
        });

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var sw = Stopwatch.StartNew();
        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            runId, CoordinatorWebApplicationFactory.OwnerUser, CancellationToken.None);
        sw.Stop();

        outcome.Should().Be(CoordinatorGateOutcome.Accepted,
            "confirm must wait for the imminent gate to arm and then succeed, not return NoPendingGate");
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(reArmDelayMs - 100,
            "confirm must have actually waited for the gate to arm rather than consuming it instantly");

        // The confirmation went through end to end.
        await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
    }

    // =========================================================================
    // Fast path: when the gate is empty AND the spec is NOT awaiting_confirmation (already
    // confirmed/declined — a genuine double-submit or a drained gate after dispatch hand-off),
    // confirm must return NoPendingGate PROMPTLY without burning the bounded wait. This preserves
    // replay/double-POST protection.
    // =========================================================================
    [Fact]
    public async Task Confirm_DrainedGate_SpecNotAwaiting_ReturnsNoPendingGatePromptly()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Drained gate with non-awaiting spec is fast");
        await WaitForGateAsync(runId);

        // Drain the gate (no re-arm) and advance the persisted spec out of awaiting_confirmation,
        // exactly as a completed confirm / dispatch hand-off would leave it.
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        pendingStore.TryRemove(runId).Should().NotBeNull("the gate must be pending before draining");

        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var spec = await db.OutcomeSpecs.FirstAsync(s => s.CoordinatorRunId == runId);
            spec.Status = "confirmed";
            await db.SaveChangesAsync();
        }

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var sw = Stopwatch.StartNew();
        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            runId, CoordinatorWebApplicationFactory.OwnerUser, CancellationToken.None);
        sw.Stop();

        outcome.Should().Be(CoordinatorGateOutcome.NoPendingGate,
            "a drained gate whose spec already advanced past awaiting_confirmation must report NoPendingGate");
        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "the fast path must not burn the bounded gate-arm wait when the spec is not awaiting_confirmation");
    }

    // =========================================================================
    // RunNotActive (service level): an unknown/unregistered run id.
    // =========================================================================
    [Fact]
    public async Task Confirm_UnknownRun_ReturnsRunNotActive()
    {
        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();

        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            RunId.New().ToString(), CoordinatorWebApplicationFactory.OwnerUser, CancellationToken.None);

        outcome.Should().Be(CoordinatorGateOutcome.RunNotActive,
            "a run that was never registered has no live workflow and must be RunNotActive");
    }

    // =========================================================================
    // Revise: re-drafts and re-suspends (status back to awaiting_confirmation), no dispatch.
    // =========================================================================
    [Fact]
    public async Task Revise_ReDraftsAndReSuspends_NoDispatch()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Revise re-drafts then re-suspends at the gate");
        await WaitForGateAsync(runId);

        const string feedback = "Please narrow the scope to the API layer only";
        var reviseResp = await _owner.PostAsJsonAsync(
            $"/api/runs/{runId}/outcome-spec/revise", new { feedback });
        reviseResp.StatusCode.Should().Be(HttpStatusCode.OK, "Accepted must map to 200");

        // The re-draft incorporates the feedback (the deterministic draft surfaces it in the
        // clarifying questions) and re-suspends with status back to awaiting_confirmation.
        var spec = await PollOutcomeSpecUntilAsync(
            runId,
            s => s.Status == "awaiting_confirmation"
                 && s.ClarifyingQuestions != null
                 && s.ClarifyingQuestions.Contains(feedback, StringComparison.Ordinal));
        spec.Should().NotBeNull("revise must re-persist an awaiting_confirmation spec that reflects the feedback");

        // The run re-suspends at the gate (a fresh pending request) and still has not dispatched.
        await WaitForGateAsync(runId);
        var run = await GetRunAsync(_owner, runId);
        run!.Status.Should().Be("in_progress", "revise must re-suspend, not dispatch or terminate");
    }

    // =========================================================================
    // Owner-scoping: a non-owner cannot read, confirm, or revise another user's run.
    // =========================================================================
    [Fact]
    public async Task NonOwner_CannotAccessOutcomeSpec_Returns403()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Owner scoping forbids other users");
        await WaitForGateAsync(runId);

        (await _other.GetAsync($"/api/runs/{runId}/outcome-spec"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner GET must be 403");

        (await _other.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner confirm must be 403");

        (await _other.PostAsJsonAsync($"/api/runs/{runId}/outcome-spec/revise", new { feedback = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner revise must be 403");

        // The owner's gate is still intact after the rejected attempts.
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        pendingStore.Get(runId).Should().NotBeNull("a forbidden request must not consume the gate");
    }

    // =========================================================================
    // Missing / invalid identifiers.
    // =========================================================================
    [Fact]
    public async Task OutcomeSpec_UnknownRun_Returns404()
    {
        var unknown = RunId.New().ToString();

        (await _owner.GetAsync($"/api/runs/{unknown}/outcome-spec"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _owner.PostAsync($"/api/runs/{unknown}/outcome-spec/confirm", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartOrchestration_MissingProject_Returns404()
    {
        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{ProjectId.New()}/orchestrations", new { goal = "no such project" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartOrchestration_MissingGoal_Returns400()
    {
        var projectId = await CreateProjectAsync();
        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations", new { goal = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<string> StartOrchestrationAsync(string projectId, string goal)
    {
        var resp = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/orchestrations", new { goal });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "starting a coordinator run must return 201");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runId").GetString()!;
    }

    /// <summary>
    /// Polls the in-process <see cref="PendingRequestStore"/> until the coordinator run has
    /// suspended at the confirmation gate (the watch loop has captured the request port event).
    /// </summary>
    private async Task WaitForGateAsync(string runId, int timeoutSeconds = 20)
    {
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (pendingStore.Get(runId) is not null) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Coordinator run {runId} did not suspend at the confirmation gate in time.");
    }

    private async Task<OutcomeSpecResponse?> GetOutcomeSpecAsync(HttpClient client, string runId)
    {
        var resp = await client.GetAsync($"/api/runs/{runId}/outcome-spec");
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<OutcomeSpecResponse>();
    }

    private async Task<OutcomeSpecResponse?> PollOutcomeSpecUntilAsync(
        string runId, Func<OutcomeSpecResponse, bool> predicate, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var spec = await GetOutcomeSpecAsync(_owner, runId);
            if (spec is not null && predicate(spec)) return spec;
            await Task.Delay(50);
        }

        return null;
    }

    private async Task<RunResponse?> GetRunAsync(HttpClient client, string runId)
    {
        var resp = await client.GetAsync($"/api/runs/{runId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<RunResponse>();
    }

    /// <summary>
    /// Inserts a coordinator-style run directly into the store with no live workflow, so the
    /// confirm/revise resume seam reports RunNotActive. Mirrors the shape produced by
    /// StartCoordinatorRunAsync (AgentName "Coordinator", null parent/subtask).
    /// </summary>
    private async Task<string> InsertInactiveCoordinatorRunAsync(string ownerUser)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "inactive coordinator run",
            SubmittingUser = ownerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
            ParentRunId = null,
            SubtaskId = null,
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }
}
