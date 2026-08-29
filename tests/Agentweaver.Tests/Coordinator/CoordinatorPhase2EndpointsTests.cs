using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Integration tests for the Feature 008 Phase 2 coordinator HTTP endpoints (Tank's wave):
/// <c>GET /api/runs/{coordinatorRunId}/work-plan</c>, <c>GET .../children</c>, and
/// <c>POST .../steer</c>.
///
/// Each test runs against a real in-process API host, a real SQLite database, and the real
/// <see cref="CoordinatorRunService"/>/<see cref="Agentweaver.Api.Coordinator.CoordinatorSteeringService"/>;
/// the only seam is the signed-out <see cref="SignedOutGitHubTokenStore"/> baked into
/// <see cref="CoordinatorWebApplicationFactory"/> (no mocks, Principle VII). Auto-dispatch is off in
/// the harness, so children stays empty and the work plan is deterministic.
///
/// Coverage:
///   - work-plan: 200 + camelCase shape after confirm decomposes a plan; 404 when the run has no
///     plan (not a coordinator run); 404 unknown run; 400 invalid id; 403 non-owner.
///   - children: 200 empty array when nothing is dispatched; 404 unknown run; 403 non-owner.
///   - steer: 400 on the descoped 'pause' verb; 400 on an unknown verb; 400 when a redirect/amend
///     omits the instruction; 201 + camelCase view on a valid stop; 403 non-owner; 404 unknown run.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorPhase2EndpointsTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public CoordinatorPhase2EndpointsTests()
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
    // work-plan: 200 + shape after confirm decomposes a plan.
    // =========================================================================
    [Fact]
    public async Task WorkPlan_AfterConfirm_Returns200_WithCamelCaseShape()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Build a deterministic work plan for the work-plan endpoint");
        await WaitForGateAsync(runId);

        var confirm = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Orchestration persists the plan asynchronously; poll the endpoint until it materializes.
        var plan = await PollWorkPlanAsync(runId);
        plan.Should().NotBeNull("confirm must route to orchestration and the endpoint must surface the plan");
        plan!.WorkPlanId.Should().BeGreaterThan(0);
        plan.CoordinatorRunId.Should().Be(runId);
        plan.OutcomeSpecId.Should().BeGreaterThan(0);
        plan.Status.Should().Be("planned");
        plan.Subtasks.Should().NotBeEmpty("the plan must decompose into at least one subtask");
        plan.Subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.AssignedAgent));
        plan.Subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SelectedModelId));
        plan.Subtasks.Should().OnlyContain(s => s.ChildRunId == null,
            "no child run is dispatched while auto-dispatch is off");
        plan.Dependencies.Should().OnlyContain(d => d.SubtaskId != d.DependsOnSubtaskId);
    }

    // =========================================================================
    // work-plan: 404 when the run exists but has no plan (not a coordinator plan yet).
    // =========================================================================
    [Fact]
    public async Task WorkPlan_RunWithoutPlan_Returns404()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.GetAsync($"/api/runs/{runId}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a run with no persisted work plan must 404 from the work-plan endpoint");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("work_plan_not_found",
            "the pre-decomposition state is typed so clients can distinguish it from a missing run");
    }

    [Fact]
    public async Task WorkPlan_UnknownRun_Returns404()
    {
        var resp = await _owner.GetAsync($"/api/runs/{RunId.New()}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WorkPlan_InvalidRunId_Returns400()
    {
        var resp = await _owner.GetAsync("/api/runs/not-a-guid/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WorkPlan_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.GetAsync($"/api/runs/{runId}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner work-plan reads must be 403");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("forbidden");
    }

    // =========================================================================
    // children: 200 empty array when nothing is dispatched.
    // =========================================================================
    [Fact]
    public async Task Children_NothingDispatched_Returns200_EmptyArray()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.GetAsync($"/api/runs/{runId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "children is always 200 for an owned run");
        var children = await resp.Content.ReadFromJsonAsync<List<CoordinatorChildResponse>>();
        children.Should().NotBeNull();
        children!.Should().BeEmpty("auto-dispatch is off, so no child runs exist");
    }

    [Fact]
    public async Task Children_UnknownRun_Returns404()
    {
        var resp = await _owner.GetAsync($"/api/runs/{RunId.New()}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Children_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.GetAsync($"/api/runs/{runId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // =========================================================================
    // steer: validation -> 400.
    // =========================================================================
    [Fact]
    public async Task Steer_PauseVerb_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "pause", instruction = "hold on" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the descoped 'pause' verb maps to 400");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("steering_invalid");
    }

    [Fact]
    public async Task Steer_UnknownVerb_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "explode", instruction = "boom" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown verb maps to 400");
    }

    [Fact]
    public async Task Steer_RedirectWithoutInstruction_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "redirect" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "redirect requires a non-empty instruction");
    }

    [Fact]
    public async Task Steer_MissingKind_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { instruction = "no verb here" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "kind is required");
    }

    // =========================================================================
    // steer: a valid stop (instruction may be omitted) -> 201 + camelCase view.
    // =========================================================================
    [Fact]
    public async Task Steer_Stop_NoInstruction_Returns201_WithDirectiveView()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer", new { kind = "stop" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created, "a valid stop creates a directive (201)");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Id.Should().BeGreaterThan(0);
        directive.CoordinatorRunId.Should().Be(runId);
        directive.Kind.Should().Be("stop");
        directive.Status.Should().Be("applied", "stop collapses to applied immediately");
        directive.CreatedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser,
            "createdBy must be the authenticated caller");
    }

    [Fact]
    public async Task Steer_Send_WithTargetChild_Returns201_WithTargetInDirectiveView()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", target_child_run_id = "child-selected", instruction = "focus on the parser edge case" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "a selected-child coordinator message creates a queued steering directive");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Kind.Should().Be("send");
        directive.Status.Should().Be("queued");
        directive.TargetChildRunId.Should().Be("child-selected",
            "the API response must preserve the selected child context for UI success state");
    }

    [Fact]
    public async Task Steer_Send_AwaitingOutcomeSpec_WithAffirmativeChat_ConfirmsSpec()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Let the operator confirm this outcome plan from chat");
        await WaitForGateAsync(runId);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = "yes, go ahead" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "an obvious affirmative chat reply at the outcome-spec gate should be accepted immediately");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Kind.Should().Be("send");
        directive.Status.Should().Be(SteeringStatus.Applied,
            "the chat reply should route through the existing confirm seam, not queue for a missing dispatch boundary");

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull();
        spec!.ConfirmedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser);
    }

    // #272 regression: the live API harness used the multi-clause phrase
    // "yes, looks good, please proceed". This natural affirmative must confirm the spec (route through
    // the confirm seam), not redraft it.
    [Fact]
    public async Task Steer_Send_AwaitingOutcomeSpec_WithMultiClauseAffirmativeChat_ConfirmsSpec()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Confirm this outcome plan with a natural multi-clause reply");
        await WaitForGateAsync(runId);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = "yes, looks good, please proceed" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Status.Should().Be(SteeringStatus.Applied,
            "a natural multi-clause affirmative ('yes, looks good, please proceed') must route through the confirm seam");

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull("the exact harness confirm phrase must confirm the spec, not redraft it");
        spec!.ConfirmedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser);
    }

    // The classification decision must come from the (LLM) classifier, not from keyword matching:
    // an ambiguous phrase the classifier rules a confirm must confirm the spec.
    [Fact]
    public async Task Steer_Send_AwaitingOutcomeSpec_UsesClassifierDecision_ToConfirm()
    {
        _factory.ReplyClassifier.Override = _ => OutcomeSpecReplyKind.Confirm;

        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Confirm via a classifier decision, not keywords");
        await WaitForGateAsync(runId);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = "hmm ok I suppose that will do then" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>())!
            .Status.Should().Be(SteeringStatus.Applied);

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull("the classifier's confirm decision must drive the confirm seam");

        _factory.ReplyClassifier.CallCount.Should().BeGreaterThan(0, "the classifier must be consulted");
        _factory.ReplyClassifier.LastContext!.Instruction.Should().Be("hmm ok I suppose that will do then");
        _factory.ReplyClassifier.LastContext.SubmittingUser.Should().Be(CoordinatorWebApplicationFactory.OwnerUser);
        _factory.ReplyClassifier.LastContext.DesiredOutcome.Should().NotBeNullOrWhiteSpace(
            "the classifier must receive the proposed spec as grounding context");
    }

    // Safety: if the classifier cannot produce a decision (model outage → null), an obvious
    // affirmative must NOT be silently confirmed — the steering service fails closed to revise.
    [Fact]
    public async Task Steer_Send_AwaitingOutcomeSpec_WhenClassifierUnavailable_FailsClosedToRevise()
    {
        _factory.ReplyClassifier.Override = _ => null;

        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Fail closed to revise when the classifier is unavailable");
        await WaitForGateAsync(runId);

        const string reply = "yes, looks good, please proceed";
        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = reply });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>())!
            .Status.Should().Be(SteeringStatus.Applied,
                "the reply is still handled via the gate — as a revise, since classification was unavailable");

        // Fail closed: the spec must be re-drafted (still awaiting_confirmation, reply surfaced as
        // feedback), NEVER confirmed, when the classifier returns no decision.
        var spec = await PollOutcomeSpecUntilAsync(
            runId,
            s => s.Status == "awaiting_confirmation"
                 && s.ClarifyingQuestions != null
                 && s.ClarifyingQuestions.Contains(reply, StringComparison.Ordinal));
        spec.Should().NotBeNull("an unavailable classifier must fail closed to revise, not confirm");
        spec!.ConfirmedBy.Should().BeNull("a fail-closed revise must never confirm the spec");
        await WaitForGateAsync(runId);
    }

    [Fact]
    public async Task Steer_Send_AwaitingOutcomeSpec_WithClarificationChat_RedraftsSpec()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Let the operator clarify this outcome plan from chat");
        await WaitForGateAsync(runId);

        const string feedback = "Actually, also include rollback criteria in the plan";
        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = feedback });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "a non-affirmative chat reply at the outcome-spec gate should be treated as clarification feedback");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Kind.Should().Be("send");
        directive.Status.Should().Be(SteeringStatus.Applied,
            "clarification chat should route through revise immediately, not queue");

        var spec = await PollOutcomeSpecUntilAsync(
            runId,
            s => s.Status == "awaiting_confirmation"
                 && s.ClarifyingQuestions != null
                 && s.ClarifyingQuestions.Contains(feedback, StringComparison.Ordinal));
        spec.Should().NotBeNull("the revised outcome spec should carry the chat feedback into the re-draft");
        await WaitForGateAsync(runId);
    }

    // =========================================================================
    // #272 — outcome-spec confirm/revise via chat must actually advance the run,
    // even when the coordinator has no resident watch loop (pod-per-run: its
    // reasoning ran in a reaped AgentHost pod, so the confirm/revise decision is
    // deferred to the DB and must be drained by the heartbeat watchdog).
    // =========================================================================

    [Fact]
    public async Task DeferredOutcomeSpecDecision_IsAppliedByPoller_ConfirmsSpec()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "A deferred confirm decision must advance the outcome spec");
        await WaitForGateAsync(runId);

        // A deferred confirm decision (what SubmitDecisionAsync writes when it cannot apply the
        // decision synchronously) must be drained and applied — the run must reach 'confirmed'.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.DeferredDecisions.Add(new CoordinatorDeferredDecisionRecord
            {
                RunId = runId,
                DecisionJson = JsonSerializer.Serialize(
                    new CoordinatorOutcomeSpecDecision(Confirmed: true, Revise: false,
                        ConfirmedBy: CoordinatorWebApplicationFactory.OwnerUser),
                    JsonDefaults.Options),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        // Exercise the poller/watchdog application path without relying on its fire-and-forget cadence.
        await coordinator.ApplyDeferredDecisionAsync(runId, CancellationToken.None);

        var spec = await PollOutcomeSpecUntilAsync(runId, s => s.Status == "confirmed");
        spec.Should().NotBeNull("a deferred confirm decision must be applied so the run leaves awaiting_confirmation");
    }

    [Fact]
    public async Task DrainOrphanedSpecDeferrals_StaleDecisionForNonGateRun_IsDiscarded()
    {
        // A coordinator run that is NOT parked at the confirmation gate (no outcome spec) with a
        // lingering deferred decision must have that stale row discarded, not retried forever.
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.DeferredDecisions.Add(new CoordinatorDeferredDecisionRecord
            {
                RunId = runId,
                DecisionJson = JsonSerializer.Serialize(
                    new CoordinatorOutcomeSpecDecision(Confirmed: true), JsonDefaults.Options),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var acted = await coordinator.DrainOrphanedSpecDeferralsAsync(CancellationToken.None);

        acted.Should().Be(1, "the stale deferral for a non-gate run should be acted on (discarded)");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.DeferredDecisions.AnyAsync(d => d.RunId == runId))
                .Should().BeFalse("the stale deferral must be discarded so it is not retried forever");
        }
    }

    [Fact]
    public async Task DrainOrphanedSpecDeferrals_NoDeferrals_IsNoOp()
    {
        var coordinator = _factory.Services.GetRequiredService<CoordinatorRunService>();
        var acted = await coordinator.DrainOrphanedSpecDeferralsAsync(CancellationToken.None);
        acted.Should().Be(0, "with no deferred decisions the drain must be a safe no-op");
    }

    [Fact]
    public async Task Steer_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.PostAsJsonAsync($"/api/runs/{runId}/steer", new { kind = "stop" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner steering must be 403");
    }

    [Fact]
    public async Task Steer_UnknownRun_Returns404()
    {
        var resp = await _owner.PostAsJsonAsync($"/api/runs/{RunId.New()}/steer", new { kind = "stop" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // assembly review: stale/no-gate POSTs must not leave durable decisions behind.
    // =========================================================================
    [Fact]
    public async Task AssemblyReview_NoPendingGate_Returns409_AndDoesNotPersistDecision()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/assembly/review", new { approved = true });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_assembly_review_pending");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.AssemblyReviews.CountAsync(r => r.CoordinatorRunId == runId))
            .Should().Be(0, "stale assembly review submissions must not persist decisions");
    }

    [Fact]
    public async Task AssemblyReview_PendingDurableGateWithoutLocalGate_Returns202_AndPersistsDeferredDecision()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        await SeedWorkPlanAsync(runId, WorkPlanStatus.InReview, AssemblyStage.Review);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            runId,
            CoordinatorWebApplicationFactory.OwnerUser,
            $"agentweaver/integration/{runId}",
            "tree-hash",
            CancellationToken.None);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/assembly/review",
            new { approved = true, feedback = "looks good" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "a non-owner replica can durably defer a decision only for a validated in-review gate");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetBoolean().Should().BeFalse();
        body.GetProperty("deferred").GetBoolean().Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking()
            .SingleAsync(r => r.CoordinatorRunId == runId);
        record.DecisionJson.Should().Contain("\"Approved\":true");
        record.DecisionJson.Should().Contain("looks good");
        record.DecisionSubmittedAt.Should().NotBeNull();
    }

    // =========================================================================
    // steer: redirect with snake_case target_child_run_id is deserialized correctly.
    // Regression guard: frontend sends target_child_run_id (snake_case); the DTO must
    // bind it so the targeted force-complete path receives the child run id.
    // =========================================================================
    [Fact]
    public async Task Steer_Redirect_SnakeCaseTargetChildRunId_IsDeserializedAndReturned()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        const string childRunId = "child-abc-123";

        var json = $$"""{"kind":"redirect","target_child_run_id":"{{childRunId}}","instruction":"use the v2 API"}""";
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await _owner.PostAsync($"/api/runs/{runId}/steer", content);

        resp.StatusCode.Should().Be(HttpStatusCode.Created, "a valid redirect with target_child_run_id must succeed");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.TargetChildRunId.Should().Be(childRunId,
            "the snake_case target_child_run_id from the request body must be bound to TargetChildRunId");
    }

    [Fact]
    public async Task Steer_Send_AwaitingAssemblyReviewCoordinator_DeliveredAsAdvisory_NotQueued()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser,
            RunStatus.AwaitingReview);
        await SeedWorkPlanAsync(runId, WorkPlanStatus.InReview, AssemblyStage.Review);

        var detail = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        detail.GetProperty("status").GetString().Should().Be("awaiting_review");
        detail.GetProperty("coordinator_steerable").GetBoolean().Should().BeTrue(
            "an assembly human-review gate is parked but still operator-addressable");

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "send", instruction = "Please explain the assembly risk before I approve." });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "awaiting_review coordinator runs must accept steering messages during human review");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Kind.Should().Be("send");
        // #226 (Q4/N3): a send at the review gate has no running child and no change request, so it is
        // delivered as an ADVISORY timeline note (settled `applied`) rather than left `queued` forever.
        directive.Status.Should().Be(SteeringStatus.Applied,
            "a send at the assembly review gate is an advisory note, not a queued directive that never drains");
        directive.Status.Should().NotBe(SteeringStatus.Queued);

        // The advisory send must NOT be turned into a review decision (no request-changes/approve).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.AssemblyReviews.CountAsync(r => r.CoordinatorRunId == runId && r.DecisionJson != null))
            .Should().Be(0, "an advisory send must not submit an assembly review decision");
    }

    // =========================================================================
    // #226: a human /steer redirect|amend at the assembly human-review gate must DRAIN (be delivered
    // into the review gate through the same mechanism /assembly/review uses), never persist a `queued`
    // directive that nothing drains. On a non-owning replica (no locally-armed gate) it is durably
    // deferred (202), mirroring /assembly/review. Q1 default scoping = broad all-contributors fallback
    // (TargetFiles null); an optional target_child_run_id narrows to that subtask's touched files.
    // =========================================================================
    [Fact]
    public async Task Steer_Redirect_AtAssemblyReviewGate_NoLocalGate_Returns202_PersistsDeferredRequestChanges()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser, RunStatus.AwaitingReview);
        await SeedWorkPlanAsync(runId, WorkPlanStatus.InReview, AssemblyStage.Review);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            runId,
            CoordinatorWebApplicationFactory.OwnerUser,
            $"agentweaver/integration/{runId}",
            "tree-hash",
            CancellationToken.None);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "redirect", instruction = "Rework the signup validation." });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "a human redirect at the review gate on a replica without the armed gate is durably deferred, mirroring /assembly/review");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive!.Kind.Should().Be("redirect");
        directive.Status.Should().Be(SteeringStatus.Deferred);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking().SingleAsync(r => r.CoordinatorRunId == runId);
        record.DecisionJson.Should().Contain("\"RequestChanges\":true",
            "a redirect maps to request_changes at the gate");
        record.DecisionJson.Should().Contain("Rework the signup validation.");
        record.DecisionJson.Should().NotContain("TargetFiles",
            "Q1 default: a bare redirect uses the broad all-contributors fallback; the null TargetFiles field is omitted by the serializer");
        record.DecisionSubmittedAt.Should().NotBeNull();

        (await db.SteeringDirectives.CountAsync(d =>
                d.CoordinatorRunId == runId && d.Status == SteeringStatus.Queued))
            .Should().Be(0, "a redirect delivered to the review gate must never persist a queued directive (N2/Q5)");
    }

    [Fact]
    public async Task Steer_Amend_AtAssemblyReviewGate_NoLocalGate_Returns202_PersistsDeferredRequestChanges()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser, RunStatus.AwaitingReview);
        await SeedWorkPlanAsync(runId, WorkPlanStatus.InReview, AssemblyStage.Review);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            runId,
            CoordinatorWebApplicationFactory.OwnerUser,
            $"agentweaver/integration/{runId}",
            "tree-hash",
            CancellationToken.None);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "amend", instruction = "Also cover the empty-email edge case." });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive!.Kind.Should().Be("amend");
        directive.Status.Should().Be(SteeringStatus.Deferred);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking().SingleAsync(r => r.CoordinatorRunId == runId);
        // N1: amend also maps to request_changes; "never discard completed work" softens to the decider
        // preferring in-place — we do NOT force-pin amend→InPlaceSteer here (the decider chooses).
        record.DecisionJson.Should().Contain("\"RequestChanges\":true");
        record.DecisionJson.Should().Contain("Also cover the empty-email edge case.");
    }

    [Fact]
    public async Task Steer_Redirect_TargetChildRunId_AtReviewGate_NarrowsTargetFilesFromSubtaskDiff()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(
            CoordinatorWebApplicationFactory.OwnerUser, RunStatus.AwaitingReview);

        var childRunId = await SeedAssembleReadyChildRunAsync(
            "diff --git a/src/api/Signup.cs b/src/api/Signup.cs\n+++ b/src/api/Signup.cs\n@@ -1 +1 @@\n");
        await SeedWorkPlanWithChildAsync(runId, childRunId, WorkPlanStatus.InReview, AssemblyStage.Review);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            runId,
            CoordinatorWebApplicationFactory.OwnerUser,
            $"agentweaver/integration/{runId}",
            "tree-hash",
            CancellationToken.None);

        var json = $$"""{"kind":"redirect","target_child_run_id":"{{childRunId}}","instruction":"fix the signup path"}""";
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await _owner.PostAsync($"/api/runs/{runId}/steer", content);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking().SingleAsync(r => r.CoordinatorRunId == runId);
        // Optional clean narrowing: the targeted subtask's touched files flow through as TargetFiles so the
        // SAME ScopeImplicatedSubtasks reverse-map narrows scope to that subtask (∪ co-touching subtasks).
        record.DecisionJson.Should().Contain("src/api/Signup.cs");
        record.DecisionJson.Should().Contain("\"TargetFiles\"",
            "a targeted child run narrows TargetFiles instead of the broad fallback");
    }

    // =========================================================================
    // Feature 008: coordinator orchestration status + failure reason surfacing.
    // A coordinator run parked at a terminal assembly status must expose the work-plan status on the
    // run detail (coordinator_status) and the failure reason on the work-plan (statusReason), so the
    // UI never shows a bare "Failed" the user can't act on.
    // =========================================================================
    [Fact]
    public async Task RunDetail_And_WorkPlan_SurfaceCoordinatorStatusAndReason_ForBlockedAssembly()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        // Park the run at a terminal blocked assembly: run Failed + reason, work plan assembly_blocked.
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.UpdateResultAsync(
            RunId.Parse(runId), RunStatus.Failed, "assembly_blocked: integration_conflict",
            DateTimeOffset.UtcNow, CancellationToken.None);
        await SeedWorkPlanAsync(runId, "assembly_blocked");

        var detail = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        detail.GetProperty("status").GetString().Should().Be("failed");
        detail.GetProperty("coordinator_status").GetString().Should().Be("assembly_blocked");
        detail.GetProperty("result").GetString().Should().Be("assembly_blocked: integration_conflict");
        detail.GetProperty("coordinator_status_reason").GetString().Should().Be("assembly_blocked: integration_conflict");

        var plan = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/work-plan");
        plan.GetProperty("status").GetString().Should().Be("assembly_blocked");
        plan.GetProperty("statusReason").GetString().Should().Be("assembly_blocked: integration_conflict");
    }

    [Fact]
    public async Task WorkPlan_SurfaceAssemblyStageTruth_ForTerminalAssembly()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);
        const string reason = "assembly_merge_failed: merge_error";

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.UpdateResultAsync(
            RunId.Parse(runId), RunStatus.MergeFailed, reason,
            DateTimeOffset.UtcNow, CancellationToken.None);
        await SeedWorkPlanAsync(
            runId,
            "assembly_failed",
            assemblyStage: "scribe",
            assemblyTerminalStage: "merge",
            assemblyStatusReason: reason);

        var plan = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/work-plan");

        plan.GetProperty("status").GetString().Should().Be("assembly_failed");
        plan.GetProperty("assemblyStage").GetString().Should().Be("scribe");
        plan.GetProperty("assemblyTerminalStage").GetString().Should().Be("merge");
        plan.GetProperty("statusReason").GetString().Should().Be(reason);
    }

    /// <summary>Seeds an OutcomeSpec + WorkPlan (with the given status) + one subtask for a run.</summary>
    private async Task SeedWorkPlanAsync(
        string coordinatorRunId,
        string status,
        string? assemblyStage = null,
        string? assemblyTerminalStage = null,
        string? assemblyStatusReason = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Agentweaver.Api.Memory.MemoryDbContext>();

        var spec = new Agentweaver.Api.Memory.OutcomeSpec
        {
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new Agentweaver.Api.Memory.WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Status = status,
            AssemblyStage = assemblyStage,
            AssemblyTerminalStage = assemblyTerminalStage,
            AssemblyStatusReason = assemblyStatusReason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        db.Subtasks.Add(new Agentweaver.Api.Memory.Subtask
        {
            WorkPlanId = plan.Id,
            Title = "t",
            Scope = "s",
            AssignedAgent = "morpheus",
            SelectedModelId = "gpt",
            Phase = "execution",
            IsolationStrategy = "worktree",
            Status = Agentweaver.Api.Coordinator.SubtaskStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a child (subtask) run in <c>assemble_ready</c> with the given unified diff so its touched
    /// files can be reverse-mapped by #226's <c>ResolveTargetFilesForChildAsync</c> when a steer targets it.
    /// </summary>
    private async Task<string> SeedAssembleReadyChildRunAsync(string diff)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child subtask run",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = null,
            SubtaskId = null,
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        await runStore.SetAssembleReadyAsync(
            runId,
            treeHash: "child-tree",
            worktreeBranch: $"agentweaver/wt/{runId}",
            diff: diff,
            stepCount: 1,
            endedAt: DateTimeOffset.UtcNow,
            CancellationToken.None);
        return runId.ToString();
    }

    /// <summary>
    /// Seeds an OutcomeSpec + WorkPlan + one Completed subtask whose <c>ChildRunId</c> points at
    /// <paramref name="childRunId"/>, so a steer's <c>target_child_run_id</c> resolves to that subtask.
    /// </summary>
    private async Task SeedWorkPlanWithChildAsync(
        string coordinatorRunId,
        string childRunId,
        string status,
        string? assemblyStage = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Agentweaver.Api.Memory.MemoryDbContext>();

        var spec = new Agentweaver.Api.Memory.OutcomeSpec
        {
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new Agentweaver.Api.Memory.WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Status = status,
            AssemblyStage = assemblyStage,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        db.Subtasks.Add(new Agentweaver.Api.Memory.Subtask
        {
            WorkPlanId = plan.Id,
            Title = "t",
            Scope = "s",
            AssignedAgent = "morpheus",
            SelectedModelId = "gpt",
            Phase = "execution",
            IsolationStrategy = "worktree",
            Status = Agentweaver.Api.Coordinator.SubtaskStatus.Completed,
            ChildRunId = childRunId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator P2 {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        SquadTestFixtureHelper.CreateMinimalSquad(dir, "Coordinator P2");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<string> StartOrchestrationAsync(string projectId, string goal)
    {
        var resp = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/orchestrations", new { goal });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runId").GetString()!;
    }

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

    private async Task<WorkPlanResponse?> PollWorkPlanAsync(string runId, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _owner.GetAsync($"/api/runs/{runId}/work-plan");
            if (resp.StatusCode == HttpStatusCode.OK)
                return await resp.Content.ReadFromJsonAsync<WorkPlanResponse>();
            await Task.Delay(50);
        }

        return null;
    }

    private async Task<OutcomeSpecResponse?> PollOutcomeSpecUntilAsync(
        string runId,
        Func<OutcomeSpecResponse, bool> predicate,
        int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _owner.GetAsync($"/api/runs/{runId}/outcome-spec");
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var spec = await resp.Content.ReadFromJsonAsync<OutcomeSpecResponse>();
                if (spec is not null && predicate(spec))
                    return spec;
            }

            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// Inserts a coordinator-style run owned by <paramref name="ownerUser"/> with no live workflow
    /// and no work plan, mirroring the shape produced by StartCoordinatorRunAsync.
    /// </summary>
    private async Task<string> InsertInactiveCoordinatorRunAsync(
        string ownerUser,
        RunStatus status = RunStatus.InProgress)
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
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
            ParentRunId = null,
            SubtaskId = null,
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }
}
