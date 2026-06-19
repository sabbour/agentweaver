using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression test for the Feature 009 backlog-pickup identity bug: a Ready task picked up by the
/// coordinator heartbeat produced a coordinator run whose detail page 404'd on EVERYTHING (and whose
/// outcome spec only "looked" missing), because the pickup path stamped a DISTINCT
/// <c>workflow_run_id</c> guid and the board navigated by it, while every coordinator-run detail
/// endpoint resolves the run by <c>run_id</c> (SqliteRunStore.GetAsync — WHERE run_id = $id).
///
/// The fix makes a pickup coordinator run identity-shaped EXACTLY like an INTERACTIVE coordinator run
/// (workflow_run_id IS NULL, no workflow_runs envelope), so the board navigates by run_id and the run
/// resolves. This test drives a real pickup against the hermetic
/// <see cref="CoordinatorWebApplicationFactory"/> (FakeCoordinatorSpecDrafter seam, heartbeat timer
/// disabled — the pickup is invoked directly, mirroring CoordinatorHeartbeatService.RunTickAsync) and
/// asserts the run is reachable by run_id, the board's navigable id equals run_id (never a third
/// guid), and the outcome spec was drafted under run_id.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorPickupRunIdTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;

    public CoordinatorPickupRunIdTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task BacklogPickup_CoordinatorRun_IsResolvableByRunId_AndBoardNavigatesByRunId()
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);

        // Seed a Ready, unclaimed task captured by the OWNER (so the owner client owns the resulting
        // coordinator run and can read its detail endpoints — IsOwner compares run.SubmittingUser).
        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        var task = new BacklogTask
        {
            Id          = BacklogTaskId.New(),
            ProjectId   = pid,
            Title       = "Pickup must resolve by run_id",
            Description = "deterministic pickup",
            State       = BacklogTaskState.Ready,
            OrderKey    = "n",
            CapturedBy  = CoordinatorWebApplicationFactory.OwnerUser,
            CreatedAt   = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
            ClaimedAt   = null,
            RunId       = null,
        };
        await backlogStore.InsertAsync(task);

        // Drive the pickup directly (the heartbeat BackgroundService is disabled in this hermetic
        // host), exactly as CoordinatorHeartbeatService.RunTickAsync does for one project+task.
        var projectStore = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await projectStore.GetAsync(pid);
        project.Should().NotBeNull();

        var candidates = await backlogStore.ListReadyForClaimAsync(pid, project!.MaxReadyPerHeartbeat);
        candidates.Should().ContainSingle().Which.Id.Should().Be(task.Id);

        var pickupService = _factory.Services.GetRequiredService<CoordinatorPickupService>();
        await pickupService.TryPickupAsync(project, candidates[0], CancellationToken.None);

        // The claim bound the coordinator run_id to the task; that run_id is the canonical detail key.
        var claimed = await backlogStore.GetAsync(pid, task.Id);
        claimed!.State.Should().Be(BacklogTaskState.Claimed);
        claimed.RunId.Should().NotBeNull("a won pickup reserves a coordinator run and binds its run_id");
        var runId = claimed.RunId!.Value.ToString();

        // (1) The reserved coordinator run is retrievable by run_id — 200, NOT the 404 the bug caused.
        var runResp = await _owner.GetAsync($"/api/runs/{runId}");
        runResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the coordinator-run detail endpoint resolves by run_id; pickup must not 404");
        var run = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        run.GetProperty("status").GetString().Should().Be("in_progress");
        // Identity parity with interactive coordinator runs: no distinct workflow_run_id.
        var wf = run.GetProperty("workflow_run_id");
        (wf.ValueKind == JsonValueKind.Null || wf.GetString() == runId)
            .Should().BeTrue("a pickup run must carry workflow_run_id null or == run_id, never a third guid");

        // (2) The board card the RunCard would navigate to uses run_id (== GetAsync key), never a
        //     third distinct guid. Find the coordinator run card across all workflow columns.
        var board = await _owner.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/board");
        var runCard = board.GetProperty("columns").EnumerateArray()
            .SelectMany(c => c.GetProperty("cards").EnumerateArray())
            .FirstOrDefault(card =>
                card.TryGetProperty("kind", out var kind) && kind.GetString() == "run"
                && card.TryGetProperty("run_id", out var rid) && rid.GetString() == runId);
        runCard.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "the pickup coordinator run must surface as a run card on the board");
        runCard.GetProperty("run_id").GetString().Should().Be(runId);
        var cardWf = runCard.GetProperty("workflow_run_id");
        (cardWf.ValueKind == JsonValueKind.Null || cardWf.GetString() == runId)
            .Should().BeTrue("RunCard's navigable id must resolve via GetAsync (run_id), never a third guid");

        // (3) The outcome spec was drafted under run_id (the bug made it "look" missing because the
        //     page polled the distinct workflow_run_id). Poll the run_id-keyed endpoint until 200.
        var specOk = await PollUntilAsync(async () =>
            (await _owner.GetAsync($"/api/runs/{runId}/outcome-spec")).StatusCode == HttpStatusCode.OK);
        specOk.Should().BeTrue("the coordinator drafts the outcome spec under run_id, so it must be reachable there");

        // (4) The work plan is only created AFTER the spec is CONFIRMED. A pickup confirms unattended
        //     (ScheduleUnattendedConfirm), so OrchestrateAsync runs for real and persists the plan +
        //     subtasks under run_id (the deterministic fallback decomposition — no live model needed,
        //     because the decompose agent is signed out). This proves the END-TO-END retrievability the
        //     user doubted: GET /work-plan must resolve by run_id (NOT 404), with the decomposed subtasks.
        //     PersistPlanAsync commits the WorkPlan row before its Subtask rows, so poll until the plan
        //     is reachable AND has surfaced its subtasks (avoids the brief plan-without-subtasks window).
        JsonElement plan = default;
        var planOk = await PollUntilAsync(async () =>
        {
            var resp = await _owner.GetAsync($"/api/runs/{runId}/work-plan");
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            plan = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return plan.TryGetProperty("subtasks", out var st)
                && st.ValueKind == JsonValueKind.Array
                && st.GetArrayLength() > 0;
        }, timeoutSeconds: 40);
        planOk.Should().BeTrue(
            "a picked-up run auto-confirms, so its work plan + subtasks are orchestrated and persisted under run_id");

        plan.GetProperty("status").GetString().Should().Be("planned");
        var subtasks = plan.GetProperty("subtasks").EnumerateArray().ToList();
        subtasks.Should().NotBeEmpty("the orchestrator's deterministic fallback always decomposes into >= 1 subtask");

        // The run-detail endpoint also resolves by run_id post-confirm (no 404 cascade).
        var finalRun = await _owner.GetAsync($"/api/runs/{runId}");
        finalRun.StatusCode.Should().Be(HttpStatusCode.OK, "the coordinator run stays resolvable by run_id end-to-end");
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Pickup Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private static async Task<bool> PollUntilAsync(Func<Task<bool>> predicate, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return true;
            await Task.Delay(50);
        }
        return false;
    }
}
