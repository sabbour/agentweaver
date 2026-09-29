using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class CoordinatorChildReservationPostgresTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task ConcurrentReservations_CommitOneCanonicalChildIdentityAndLease()
    {
        var (planId, subtaskId, parentId, child) = await SeedAsync();
        var store = new EfRunStore(pg.Factory);
        var competing = child with { Id = RunId.New() };
        var results = await Task.WhenAll(
            store.TryReserveCoordinatorChildAsync(planId, subtaskId, "pod-a", 1, child, "worker-a", TimeSpan.FromMinutes(1)),
            store.TryReserveCoordinatorChildAsync(planId, subtaskId, "pod-a", 1, competing, "worker-b", TimeSpan.FromMinutes(1)));

        results.Count(r => r.State == ChildDispatchReservationState.Claimed).Should().Be(1);
        results.Count(r => r.State == ChildDispatchReservationState.LeaseHeld).Should().Be(1);
        results.Select(r => r.ChildRunId).Distinct().Should().ContainSingle();
        await using var db = await pg.CreateDbContextAsync();
        var canonical = await db.Subtasks.Where(s => s.Id == subtaskId).Select(s => s.ChildRunId).SingleAsync();
        canonical.Should().Be(results[0].ChildRunId);
        var runs = await db.Runs.Where(r => r.ParentRunId == parentId && r.SubtaskId == subtaskId.ToString()).ToListAsync();
        runs.Should().ContainSingle().Which.Status.Should().Be("pending");
        runs[0].FencingToken.Should().Be(1);
        (await db.ExecutionIdentities.CountAsync(i => i.RunId == canonical)).Should().Be(1);
    }

    [PostgresFact]
    public async Task StaleOwnerOrGenerationAndCancellation_CannotReserve()
    {
        var (planId, subtaskId, _, child) = await SeedAsync();
        var store = new EfRunStore(pg.Factory);
        foreach (var (pod, generation) in new[] { ("other-pod", 1), ("pod-a", 2) })
        {
            var rejected = await store.TryReserveCoordinatorChildAsync(
                planId, subtaskId, pod, generation, child, "worker-a", TimeSpan.FromMinutes(1));
            rejected.State.Should().Be(ChildDispatchReservationState.NotOwner);
        }
        await using (var db = await pg.CreateDbContextAsync())
        {
            await db.WorkPlans.Where(w => w.Id == planId).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.CoordinatorCancellationRequestedAt, DateTimeOffset.UtcNow));
        }
        (await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child, "worker-a", TimeSpan.FromMinutes(1)))
            .State.Should().Be(ChildDispatchReservationState.NotOwner);
        await using var verify = await pg.CreateDbContextAsync();
        (await verify.Subtasks.SingleAsync(s => s.Id == subtaskId)).ChildRunId.Should().BeNull();
        (await verify.Runs.AnyAsync(r => r.RunId == child.Id.ToString())).Should().BeFalse();
    }

    [PostgresFact]
    public async Task StoppedPlanOrParent_CannotCreateChild()
    {
        var (planId, subtaskId, parentId, child) = await SeedAsync();
        var store = new EfRunStore(pg.Factory);
        await using (var db = await pg.CreateDbContextAsync())
            await db.WorkPlans.Where(w => w.Id == planId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "awaiting_assembly"));
        (await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child, "worker-a", TimeSpan.FromMinutes(1)))
            .State.Should().Be(ChildDispatchReservationState.NotOwner);

        await using (var db = await pg.CreateDbContextAsync())
        {
            await db.WorkPlans.Where(w => w.Id == planId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "dispatching"));
            await db.Runs.Where(r => r.RunId == parentId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "failed"));
        }
        (await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child, "worker-a", TimeSpan.FromMinutes(1)))
            .State.Should().Be(ChildDispatchReservationState.NotOwner);
        await using var verify = await pg.CreateDbContextAsync();
        (await verify.Subtasks.SingleAsync(s => s.Id == subtaskId)).ChildRunId.Should().BeNull();
    }

    [PostgresFact]
    public async Task ExpiredLeaseReclaimsPending_ActiveChildIsNeverRelaunched()
    {
        var (planId, subtaskId, _, child) = await SeedAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new EfRunStore(pg.Factory, clock: clock);
        var first = await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child, "worker-a", TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child with { Id = RunId.New() }, "worker-b", TimeSpan.FromMinutes(1));
        reclaimed.State.Should().Be(ChildDispatchReservationState.Claimed);
        reclaimed.ChildRunId.Should().Be(first.ChildRunId);
        reclaimed.FencingToken.Should().Be(first.FencingToken + 1);
        await using (var db = await pg.CreateDbContextAsync())
        {
            await db.Runs.Where(r => r.RunId == first.ChildRunId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "in_progress"));
        }

        (await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, child, "worker-c", TimeSpan.FromMinutes(1)))
            .State.Should().Be(ChildDispatchReservationState.ExistingActive);
    }

    [PostgresFact]
    public async Task WorkerAndApiPlanTakeover_FencesPendingLaunchAndReclaimsSameChild()
    {
        var (planId, subtaskId, parentId, proposed) = await SeedAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new EfRunStore(pg.Factory, clock: clock);
        var worker = await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, proposed, "pod-a/child-dispatch/worker", TimeSpan.FromMinutes(1));
        worker.State.Should().Be(ChildDispatchReservationState.Claimed);

        await using (var db = await pg.CreateDbContextAsync())
            await db.WorkPlans.Where(w => w.Id == planId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.CoordinatorPodId, "api-pod"));

        (await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "pod-a", 1, proposed with { Id = RunId.New() },
            "stale-worker", TimeSpan.FromMinutes(1))).State.Should().Be(ChildDispatchReservationState.NotOwner);
        var api = await store.TryReserveCoordinatorChildAsync(
            planId, subtaskId, "api-pod", 1, proposed with { Id = RunId.New() },
            "api-pod/child-dispatch/worker", TimeSpan.FromMinutes(1));
        api.State.Should().Be(ChildDispatchReservationState.Claimed);
        api.ChildRunId.Should().Be(worker.ChildRunId);
        api.FencingToken.Should().Be(worker.FencingToken + 1);
        (await store.IsCoordinatorChildLaunchAuthorizedAsync(
            planId, subtaskId, "pod-a", 1, worker.ChildRunId!,
            "pod-a/child-dispatch/worker", worker.FencingToken, CancellationToken.None)).Should().BeFalse();
        await using var verify = await pg.CreateDbContextAsync();
        (await verify.Runs.CountAsync(r => r.ParentRunId == parentId
            && r.SubtaskId == subtaskId.ToString())).Should().Be(1);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private async Task<(int PlanId, int SubtaskId, string ParentId, Run Child)> SeedAsync()
    {
        var parentId = RunId.New().ToString();
        var projectId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var db = await pg.CreateDbContextAsync();
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = projectId, Name = "reservation", OriginKind = "local",
            WorkingDirectory = "/repo", Owner = "user", DefaultProvider = "github_copilot",
            CreatedAt = now, UpdatedAt = now,
        });
        db.Runs.Add(new RunRecord
        {
            RunId = parentId, RepositoryPath = "/repo", OriginatingBranch = "main",
            ModelSource = "github_copilot", Task = "parent", SubmittingUser = "user",
            Status = "in_progress", StartedAt = now,
        });
        var outcome = new OutcomeSpec
        {
            ProjectId = projectId, CoordinatorRunId = parentId,
            Goal = "test", DesiredOutcome = "test", Scope = "test", Assumptions = "test",
            Status = "confirmed", CreatedAt = now, UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(outcome);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = outcome.Id, ProjectId = projectId, CoordinatorRunId = parentId,
            CoordinatorPodId = "pod-a", Status = "dispatching", CreatedAt = now, UpdatedAt = now,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        var subtask = new Subtask
        {
            WorkPlanId = plan.Id, Title = "task", Scope = "test", AssignedAgent = "agent",
            SelectedModelId = "model", Phase = "execution", IsolationStrategy = "worktree",
            Status = "pending", CreatedAt = now, UpdatedAt = now,
        };
        db.Subtasks.Add(subtask);
        await db.SaveChangesAsync();
        return (plan.Id, subtask.Id, parentId, new Run
        {
            Id = RunId.New(), RepositoryPath = "/repo", OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot, Task = "child", SubmittingUser = "user",
            Status = RunStatus.Pending, StartedAt = now, ParentRunId = parentId,
            SubtaskId = subtask.Id.ToString(),
        });
    }
}
