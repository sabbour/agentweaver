using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Coordinator;

/// <summary>
/// Integration test for the Feature 008 Phase 2 coordinator ORCHESTRATOR (decompose + persist).
///
/// Runs against a real in-process API host, a real SQLite + EF <see cref="MemoryDbContext"/>, and
/// the real coordinator MAF workflow with its request-port suspend/resume. The only seam is the
/// signed-out <see cref="SignedOutGitHubTokenStore"/> baked into
/// <see cref="CoordinatorWebApplicationFactory"/>: the decomposition agent turn fails closed and
/// the orchestrator uses its built-in DETERMINISTIC fallback (a real component, exercised exactly
/// as in production when Copilot is unavailable) — no mocks (Principle VII).
///
/// Asserts the wave's contract: confirming a spec routes the run to orchestration, which persists
/// one WorkPlan (planned), the Subtask rows (pending, with a real assigned agent + selected model),
/// any SubtaskDependency edges, and emits a single coordinator.work_plan snapshot event.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorOrchestratorTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;

    public CoordinatorOrchestratorTests()
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
    public async Task Confirm_DecomposesAndPersistsWorkPlanSubtasksAndDependencies()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Build a deterministic work plan for testing");
        await WaitForGateAsync(runId);

        // Confirm -> the run finalizes the spec then runs orchestration (decompose + persist).
        var confirm = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Orchestration runs asynchronously after confirm; poll EF until the work plan is persisted.
        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        workPlan.Should().NotBeNull("confirm must route to orchestration and persist a work plan");
        workPlan!.Status.Should().Be("planned");
        workPlan.ProjectId.Should().Be(projectId);
        workPlan.OutcomeSpecId.Should().BeGreaterThan(0, "the work plan must link to the confirmed outcome spec");

        // Subtasks are persisted pending, with a real assigned agent and a selected Copilot model.
        var subtasks = await PollAsync(async db =>
        {
            var rows = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == workPlan.Id).ToListAsync();
            return rows.Count > 0 ? rows : null;
        });
        subtasks.Should().NotBeNull("the work plan must decompose into at least one subtask");
        subtasks!.Should().OnlyContain(s => s.Status == "pending",
            "this wave persists subtasks pending and does not dispatch any child run");
        subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.AssignedAgent),
            "each subtask must be assigned a roster agent (FR-011)");
        subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SelectedModelId),
            "each subtask must have a selected Copilot model (FR-012)");
        subtasks.Should().OnlyContain(s => s.ChildRunId == null,
            "no child run is dispatched in the decompose + persist wave");

        // Dependency edges, when present, must reference subtasks of THIS plan and be acyclic.
        var subtaskIds = subtasks.Select(s => s.Id).ToHashSet();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var edges = await db.SubtaskDependencies.AsNoTracking()
                .Where(d => subtaskIds.Contains(d.SubtaskId))
                .ToListAsync();
            edges.Should().OnlyContain(e => subtaskIds.Contains(e.DependsOnSubtaskId),
                "every dependency edge must reference a subtask in the same work plan");
            edges.Should().OnlyContain(e => e.SubtaskId != e.DependsOnSubtaskId, "no self-dependencies");
        }

        // A single coordinator.work_plan snapshot event is emitted on the coordinator run stream.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(runId);
        entry.Should().NotBeNull();
        var planEvents = entry!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorWorkPlan).ToList();
        planEvents.Should().HaveCount(1, "exactly one plan-time snapshot event is emitted");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator Orchestrate {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
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
            if (pendingStore.Get(runId) is not null) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Coordinator run {runId} did not suspend at the confirmation gate in time.");
    }

    private async Task<T?> PollAsync<T>(Func<MemoryDbContext, Task<T?>> query, int timeoutSeconds = 20) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var result = await query(db);
                if (result is not null) return result;
            }
            await Task.Delay(50);
        }

        return null;
    }
}
