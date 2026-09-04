using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
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
        var events = entry!.GetSnapshotSince(0).Events.ToList();
        var draftingEvent = events.FirstOrDefault(e => e.Type == EventTypes.CoordinatorOutcomeSpecDrafting);
        draftingEvent.Should().NotBeNull(
            "the coordinator must emit an active drafting event before the confirmable draft is ready");
        var draftingPayload = JsonSerializer.SerializeToElement(draftingEvent!.Payload);
        draftingPayload.GetProperty("status").GetString().Should().Be("drafting");
        draftingPayload.GetProperty("goal").GetString().Should().Be(
            "Build a deterministic outcome spec for testing");

        var specEvent = events.FirstOrDefault(e => e.Type == EventTypes.CoordinatorOutcomeSpec);
        specEvent.Should().NotBeNull(
            "the draft executor must emit coordinator.outcome_spec before suspending");
        draftingEvent.Sequence.Should().BeLessThan(specEvent!.Sequence,
            "the active drafting projection must precede the ready-for-confirmation projection");
        var specPayload = JsonSerializer.SerializeToElement(specEvent!.Payload);
        specPayload.GetProperty("goal").GetString().Should().Be(
            "Build a deterministic outcome spec for testing",
            "the outcome_spec event must carry the goal for the UI GOAL field");

        // No dispatch in Phase 1: the run stays in_progress (suspended), not terminal, and no
        // child run was created.
        var run = await GetRunAsync(_owner, runId);
        run!.Status.Should().Be("in_progress",
            "the coordinator run must remain suspended at the gate, not dispatch or terminate");
        run.CoordinatorStatus.Should().Be("awaiting_confirmation",
            "run detail should replay the pre-work-plan outcome-spec lifecycle after drafting completes");

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var stored = await runStore.GetAsync(RunId.Parse(runId), CancellationToken.None);
        stored!.AgentName.Should().Be("Coordinator");
        stored.ParentRunId.Should().BeNull("the coordinator run is the parent, it has no parent");
        stored.SubtaskId.Should().BeNull("Phase 1 does not decompose into subtasks");
    }

    [Fact]
    public async Task Start_UsesProjectOutcomeSpecGenerationModel()
    {
        var projectId = await CreateProjectAsync();
        var update = await _owner.PutAsJsonAsync(
            $"/api/projects/{projectId}/provider-settings",
            new { outcome_spec_generation_model = "gpt-5-mini" });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var runId = await StartOrchestrationAsync(projectId, "Draft with project-specific outcome spec model");
        await WaitForGateAsync(runId);

        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.LastInput.Should().NotBeNull();
        drafter.LastInput!.OutcomeSpecGenerationModel.Should().Be("gpt-5-mini");
    }

    [Fact]
    public async Task Start_DraftExceedsDeadline_FailsRunAndEmitsTypedTerminal()
    {
        var projectId = await CreateProjectAsync();
        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.BlockUntilCancelled = true;

        var runId = await StartOrchestrationAsync(
            projectId,
            "A provider startup that never completes must not strand outcome planning");

        RunResponse? run = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            run = await GetRunAsync(_owner, runId);
            if (run?.Status == "failed")
                break;
            await Task.Delay(50);
        }

        run.Should().NotBeNull();
        run!.Status.Should().Be("failed");
        var cancellationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!drafter.CancellationObserved && DateTime.UtcNow < cancellationDeadline)
            await Task.Delay(25);
        drafter.CancellationObserved.Should().BeTrue(
            "the coordinator deadline must still cancel provider setup/session work after failing the run");

        var spec = await GetOutcomeSpecAsync(_owner, runId);
        spec.Should().NotBeNull();
        spec!.Status.Should().Be("drafting",
            "the persisted drafting row should remain diagnostic evidence rather than masquerade as a completed plan");

        var eventsResponse = await _owner.GetAsync($"/api/runs/{runId}/events");
        eventsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await eventsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var failedEvent = events.Should().NotBeNull().And.Subject
            .Single(e => e.GetProperty("type").GetString() == EventTypes.RunFailed);
        failedEvent.GetProperty("payload").GetProperty("reason").GetString()
            .Should().Be("outcome_spec_draft_timeout");
    }

    [Fact]
    public async Task Start_DraftCancellationCallbackBlocks_FailsPromptlyAtDeadlineAndCleansUp()
    {
        var projectId = await CreateProjectAsync();
        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.BlockCancellationCallback = true;

        var deadlineStopwatch = Stopwatch.StartNew();
        var runId = await StartOrchestrationAsync(
            projectId,
            "A blocked provider cancellation callback must not delay terminal failure");

        try
        {
            await drafter.BlockedCancellationStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var terminalizationStopwatch = Stopwatch.StartNew();

            RunResponse? run = null;
            var terminalDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < terminalDeadline)
            {
                run = await GetRunAsync(_owner, runId);
                if (run?.Status == "failed")
                    break;
                await Task.Delay(25);
            }

            run.Should().NotBeNull();
            run!.Status.Should().Be("failed",
                "terminalization must not await a provider cancellation callback that is still blocked");
            terminalizationStopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            deadlineStopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4),
                "the test host configures a one-second drafting deadline");
        }
        finally
        {
            drafter.ReleaseBlockedCancellation();
        }

        await drafter.BlockedDraftCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        drafter.CancellationObserved.Should().BeTrue();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        List<RunEventRecord> durableFailures = [];
        var persistenceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < persistenceDeadline)
        {
            durableFailures = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId && e.EventType == EventTypes.RunFailed)
                .OrderBy(e => e.Sequence)
                .ToListAsync();
            if (durableFailures.Count > 0)
                break;
            await Task.Delay(50);
        }

        var failedEvent = durableFailures.Should().ContainSingle(
            "deadline cleanup must not emit a second terminal event")
            .Subject;
        var payload = JsonSerializer.Deserialize<JsonElement>(failedEvent.PayloadJson);
        payload.GetProperty("reason").GetString()
            .Should().Be("outcome_spec_draft_timeout");
    }

    [Fact]
    public async Task Start_CopilotProviderFailure_PreservesTypedDurableTerminalWithoutDuplicate()
    {
        var projectId = await CreateProjectAsync();
        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.ProviderFailureToThrow = new AgentProviderException(
            ModelSource.GitHubCopilot,
            AgentProviderFailureKind.ProviderUnavailable,
            "github_copilot_models_unavailable",
            "GitHub Copilot could not list available models.",
            isRetryable: false);

        var runId = await StartOrchestrationAsync(
            projectId,
            "A typed Copilot provider failure must remain the coordinator terminal");

        RunResponse? run = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            run = await GetRunAsync(_owner, runId);
            if (run?.Status == "failed")
                break;
            await Task.Delay(50);
        }

        run.Should().NotBeNull();
        run!.Status.Should().Be("failed");
        run.Result.Should().Be("github_copilot_models_unavailable");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        List<RunEventRecord> durableFailures = [];
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            durableFailures = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId && e.EventType == EventTypes.RunFailed)
                .OrderBy(e => e.Sequence)
                .ToListAsync();
            if (durableFailures.Count > 0)
                break;
            await Task.Delay(50);
        }

        var durableFailure = durableFailures.Should().ContainSingle(
            "CopilotAIAgent already emitted the provider terminal before MAF surfaced ExecutorFailedEvent")
            .Subject;
        var payload = JsonSerializer.Deserialize<JsonElement>(durableFailure.PayloadJson);
        payload.GetProperty("errorCode").GetString().Should().Be("github_copilot_models_unavailable");
        payload.GetProperty("message").GetString().Should().Be("GitHub Copilot could not list available models.");
        payload.GetProperty("category").GetString().Should().Be(
            AgentProviderFailureKind.ProviderUnavailable.ToString());
        payload.GetProperty("retryable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Start_DrafterThrowsTimeout_DoesNotMislabelCoordinatorDeadline()
    {
        var projectId = await CreateProjectAsync();
        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.ExceptionToThrow = new TimeoutException("simulated provider timeout");

        var runId = await StartOrchestrationAsync(
            projectId,
            "An immediate provider timeout must keep its executor-failure classification");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        RunResponse? run = null;
        while (DateTime.UtcNow < deadline)
        {
            run = await GetRunAsync(_owner, runId);
            if (run?.Status == "failed")
                break;
            await Task.Delay(50);
        }

        run.Should().NotBeNull();
        run!.Status.Should().Be("failed");

        var events = await _owner.GetFromJsonAsync<JsonElement[]>($"/api/runs/{runId}/events");
        var failedEvent = events.Should().NotBeNull().And.Subject
            .Single(e => e.GetProperty("type").GetString() == EventTypes.RunFailed);
        failedEvent.GetProperty("payload").GetProperty("reason").GetString()
            .Should().Be("coordinator_executor_failed:coordinator-draft");
    }

    [Fact]
    public async Task Start_DefineOutcomeMode_DraftsSpecAndSuspendsAtGate()
    {
        var projectId = await CreateProjectAsync();

        var runId = await StartOrchestrationAsync(
            projectId,
            "Explicit define-outcome mode should preserve the existing flow",
            startMode: "defineOutcome");

        await WaitForGateAsync(runId);
        var spec = await GetOutcomeSpecAsync(_owner, runId);
        spec.Should().NotBeNull();
        spec!.Status.Should().Be("awaiting_confirmation");

        var workPlan = await GetWorkPlanAsync(runId);
        workPlan.Should().BeNull("defineOutcome must not decompose before the human confirms the spec");
    }

    [Fact]
    public async Task Start_DirectMode_SkipsOutcomeGate_OrchestratesFromPrompt()
    {
        var projectId = await CreateProjectAsync();
        const string goal = "Direct mode should plan from this prompt without drafting an outcome spec";

        var runId = await StartOrchestrationAsync(projectId, goal, startMode: "direct");

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull("direct mode persists a confirmed prompt-backed spec for the existing work-plan FK");
        spec!.Goal.Should().Be(goal);
        spec.DesiredOutcome.Should().Be(goal);
        spec.ConfirmedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser);

        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        (await pendingStore.GetAsync(runId)).Should().BeNull("direct mode skips the outcome-spec confirmation gate");

        var workPlan = await PollWorkPlanAsync(runId);
        workPlan.Should().NotBeNull("direct mode must enter the same orchestration path as a confirmed spec");
        workPlan!.OutcomeSpecId.Should().BeGreaterThan(0);
        workPlan.Status.Should().Be("planned");

        var subtaskCount = await PollSubtaskCountAsync(workPlan.Id);
        subtaskCount.Should().BeGreaterThan(0,
            "direct mode must decompose the prompt into dispatchable subtasks");
    }

    [Fact]
    public async Task RunDetail_NoWorkPlan_ReplaysPersistedDraftingStatus()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.OutcomeSpecs.Add(new OutcomeSpec
            {
                ProjectId = Guid.NewGuid().ToString(),
                CoordinatorRunId = runId,
                Goal = "Drafting should survive replay",
                DesiredOutcome = string.Empty,
                Scope = string.Empty,
                Assumptions = string.Empty,
                Status = "drafting",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var run = await GetRunAsync(_owner, runId);

        run!.CoordinatorStatus.Should().Be("drafting",
            "persisted/replayed coordinator state must distinguish active outcome-plan drafting from pending/not-started");
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
        (await pendingStore.GetAsync(runId)).Should().BeNull("confirm must atomically consume the pending gate");
    }

    [Fact]
    public async Task Confirm_RequestCanOptIntoTaskPromotion_BeforeFinalizingSpec()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Confirm stores promotion opt-in");
        await WaitForGateAsync(runId);

        var confirmResp = await _owner.PostAsJsonAsync(
            $"/api/runs/{runId}/outcome-spec/confirm",
            new ConfirmOutcomeSpecRequest { AllowTaskPromotion = true });
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull();
        spec!.AllowTaskPromotion.Should().BeTrue();
    }

    // =========================================================================
    // Autopilot: define-outcome runs auto-confirm the spec unattended, with no manual
    // confirm POST, and record the submitting user as ConfirmedBy (#228). Off-by-default
    // stays parked at the gate.
    // =========================================================================
    [Fact]
    public async Task Start_AutopilotDefineOutcome_AutoConfirmsSpec_WithoutManualConfirm()
    {
        var projectId = await CreateProjectAsync();

        // Start with autopilot on and the default define-outcome mode. No manual confirm POST is sent:
        // the unattended-confirm loop must advance the spec on the submitting user's behalf.
        var runId = await StartOrchestrationAsync(
            projectId, "Autopilot should auto-confirm the outcome spec", autopilot: true);

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed", timeoutSeconds: 30);
        spec.Should().NotBeNull("autopilot must auto-confirm the outcome spec without a human POST (#228)");
        spec!.ConfirmedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser,
            "the accountable submitting user must be recorded as ConfirmedBy for the unattended confirm");
    }

    [Fact]
    public async Task Start_AutopilotOff_DefineOutcome_ParksAtGate()
    {
        var projectId = await CreateProjectAsync();

        var runId = await StartOrchestrationAsync(
            projectId, "Without autopilot the run must wait for a human to confirm");

        await WaitForGateAsync(runId);

        var spec = await GetOutcomeSpecAsync(_owner, runId);
        spec.Should().NotBeNull();
        spec!.Status.Should().Be("awaiting_confirmation",
            "with autopilot off the run must park at the confirmation gate until a human confirms");
        spec.ConfirmedBy.Should().BeNull("no one has confirmed a run that is parked at the gate");
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
        (await pendingStore.TryRemoveAsync(runId)).Should().NotBeNull("the gate must be pending before draining");

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            runId, CoordinatorWebApplicationFactory.OwnerUser, allowTaskPromotion: false, CancellationToken.None);

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
        var drained = await pendingStore.TryRemoveAsync(runId);
        drained.Should().NotBeNull("the gate must be pending before draining");

        const int reArmDelayMs = 350;
        _ = Task.Run(async () =>
        {
            await Task.Delay(reArmDelayMs);
            await pendingStore.SetAsync(runId, drained!.Request, drained.OwnerUser);
        });

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var sw = Stopwatch.StartNew();
        var outcome = await coordinator.ConfirmOutcomeSpecAsync(
            runId, CoordinatorWebApplicationFactory.OwnerUser, allowTaskPromotion: false, CancellationToken.None);
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
        (await pendingStore.TryRemoveAsync(runId)).Should().NotBeNull("the gate must be pending before draining");

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
            runId, CoordinatorWebApplicationFactory.OwnerUser, allowTaskPromotion: false, CancellationToken.None);
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
            RunId.New().ToString(), CoordinatorWebApplicationFactory.OwnerUser, allowTaskPromotion: false, CancellationToken.None);

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
    // Regression (#315): a revision must carry the already-reviewed prior draft forward to the
    // drafter so its established requirements are preserved instead of being silently re-generated
    // (and potentially regressed) from goal + feedback alone. The first draft must NOT receive a
    // prior draft; the revision MUST receive one that mirrors the persisted awaiting_confirmation
    // spec.
    // =========================================================================
    [Fact]
    public async Task Revise_CarriesPriorDraftForwardToDrafter_ToPreserveEstablishedRequirements()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(
            projectId, "Build and publish the image to an Azure-accessible registry, then smoke-test it live");
        await WaitForGateAsync(runId);

        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;

        // The FIRST draft is not a revision: no prior draft, no feedback.
        drafter.LastInput.Should().NotBeNull();
        drafter.LastInput!.PriorDraft.Should().BeNull("the first draft has no established prior draft to preserve");
        drafter.LastInput!.ReviseFeedback.Should().BeNullOrEmpty();

        // Capture the established (prior) draft the human reviewed before pushing back.
        var priorSpec = await GetOutcomeSpecAsync(_owner, runId);
        priorSpec.Should().NotBeNull();
        priorSpec!.Status.Should().Be("awaiting_confirmation");

        const string feedback = "The smoke-test proof is too vague; require a concrete verification command";
        var reviseResp = await _owner.PostAsJsonAsync(
            $"/api/runs/{runId}/outcome-spec/revise", new { feedback });
        reviseResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-drafts and re-suspends at a fresh gate.
        await PollOutcomeSpecUntilAsync(
            runId,
            s => s.Status == "awaiting_confirmation"
                 && s.ClarifyingQuestions != null
                 && s.ClarifyingQuestions.Contains(feedback, StringComparison.Ordinal));
        await WaitForGateAsync(runId);

        // The REVISION carried the prior draft forward verbatim, so the drafter can lock the
        // already-established requirements (e.g. "publish ... to an Azure-accessible registry")
        // as invariants rather than regenerating them from scratch.
        drafter.LastInput!.ReviseFeedback.Should().Be(feedback);
        drafter.LastInput!.PriorDraft.Should().NotBeNull(
            "a revision must carry the already-reviewed prior draft forward (#315)");
        drafter.LastInput!.PriorDraft!.DesiredOutcome.Should().Be(priorSpec.DesiredOutcome);
        drafter.LastInput!.PriorDraft!.Scope.Should().Be(priorSpec.Scope);
        drafter.LastInput!.PriorDraft!.Assumptions.Should().Be(priorSpec.Assumptions);
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
        (await pendingStore.GetAsync(runId)).Should().NotBeNull("a forbidden request must not consume the gate");
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

    [Fact]
    public async Task StartOrchestration_TeamlessProject_Returns409NoTeam_AndCreatesNoRun()
    {
        var projectId = await CreateProjectAsync(seedTeam: false);
        var pid = ProjectId.Parse(projectId);
        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        (await runStore.GetRunsByProjectAsync(pid)).Should().BeEmpty("precondition: project starts with no runs");

        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations", new { goal = "build without a team" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_team");
        body.GetProperty("message").GetString()
            .Should().Be("This project has no team. Cast a team before starting an orchestration.");
        (await runStore.GetRunsByProjectAsync(pid)).Should().BeEmpty(
            "the start guard must reject before creating a misleading coordinator run");
    }

    [Fact]
    public async Task StartOrchestration_UnreadableTeamRoster_Returns422InvalidTeam_AndCreatesNoRun()
    {
        var projectId = await CreateProjectAsync(seedTeam: false);
        var pid = ProjectId.Parse(projectId);
        var project = await _factory.Services.GetRequiredService<IProjectStore>().GetAsync(pid);
        project.Should().NotBeNull();

        var squadDir = Path.Combine(project!.WorkingDirectory, ".squad");
        Directory.CreateDirectory(Path.Combine(squadDir, "casting"));
        await File.WriteAllTextAsync(Path.Combine(squadDir, "casting", "registry.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(squadDir, "casting-registry.json"), "{\"members\":{\"x\":{}}}");

        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations", new { goal = "build with a corrupt team layout" });

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_team");
        body.GetProperty("message").GetString()
            .Should().Be("The project team roster could not be read. Fix the team before starting an orchestration.");
        (await runStore.GetRunsByProjectAsync(pid)).Should().BeEmpty(
            "roster read failures must reject before creating a coordinator run");
    }

    [Fact]
    public async Task StartOrchestration_WithDispatchableTeam_Returns201()
    {
        var projectId = await CreateProjectAsync(seedTeam: true);

        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations", new { goal = "build with a cast team" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("runId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync(bool seedTeam = true)
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        if (seedTeam)
            SquadTestFixtureHelper.CreateMinimalSquad(dir, "Coordinator Test");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<string> StartOrchestrationAsync(
        string projectId, string goal, string? startMode = null, bool autopilot = false)
    {
        object request = (startMode, autopilot) switch
        {
            (null, false) => new { goal },
            (null, true) => new { goal, autopilot },
            (_, false) => new { goal, start_mode = startMode },
            (_, true) => new { goal, start_mode = startMode, autopilot },
        };
        var resp = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/orchestrations", request);
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
            if (await pendingStore.GetAsync(runId) is not null) return;
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

    private async Task<WorkPlan?> GetWorkPlanAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == runId);
    }

    private async Task<WorkPlan?> PollWorkPlanAsync(string runId, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var plan = await GetWorkPlanAsync(runId);
            if (plan is not null) return plan;
            await Task.Delay(50);
        }

        return null;
    }

    private async Task<int> CountSubtasksAsync(int workPlanId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().CountAsync(s => s.WorkPlanId == workPlanId);
    }

    private async Task<int> PollSubtaskCountAsync(int workPlanId, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var count = await CountSubtasksAsync(workPlanId);
            if (count > 0) return count;
            await Task.Delay(50);
        }

        return 0;
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
