using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Persistence seam for Phase 3 collective-assembly state on the <see cref="WorkPlan"/> row. The
/// exactly-once claim (D4) is a DB-level compare-and-swap implemented with EF
/// <c>ExecuteUpdateAsync</c> (a single guarded <c>UPDATE … WHERE Status = 'awaiting_assembly'</c>),
/// which is the source of truth — an in-memory guard alone cannot prevent a double-start across the
/// dispatch/observe loop, a re-dispatch wave, and the HITL review resume. Uses a scoped
/// <see cref="MemoryDbContext"/> per call (the <see cref="IServiceScopeFactory"/> pattern) so it is
/// safe to call from the coordinator's background tasks and the HTTP review endpoint alike.
/// </summary>
public sealed class CoordinatorAssemblyStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorAssemblyStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// D4 exactly-once CAS. Atomically transitions <c>awaiting_assembly → assembling</c>, stamps
    /// <see cref="WorkPlan.AssemblyStartedAt"/>, and persists <paramref name="integrationBranch"/>.
    /// The stage is intentionally NOT set here — <see cref="CoordinatorAssemblyService"/> drives the
    /// stage explicitly as each collective node starts (so the eligibility/integration-build phase,
    /// which precedes RAI, shows no node live yet). Returns <c>true</c> for the single winner;
    /// <c>false</c> if the plan already moved past <c>awaiting_assembly</c>.
    /// </summary>
    public async Task<bool> TryStartAssemblyAsync(int workPlanId, string integrationBranch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkPlans
            .Where(w => w.Id == workPlanId && w.Status == WorkPlanStatus.AwaitingAssembly)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Status, WorkPlanStatus.Assembling)
                .SetProperty(w => w.IntegrationBranch, integrationBranch)
                .SetProperty(w => w.AssemblyStartedAt, now)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>Sets the work-plan <see cref="WorkPlan.Status"/> (e.g. in_review, complete, assembly_*).</summary>
    public async Task SetStatusAsync(int workPlanId, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Advances the collective-assembly stage (drives the coordinator graph node-flip).</summary>
    public async Task SetStageAsync(int workPlanId, string? stage, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.AssemblyStage, stage)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sets status and stage together (e.g. in_review/review, assembling/merge).</summary>
    public async Task SetStatusAndStageAsync(int workPlanId, string status, string? stage, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.AssemblyStage, stage)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the current assembly-relevant state of a work plan (null when not found).</summary>
    public async Task<WorkPlanAssemblyState?> GetAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new WorkPlanAssemblyState(w.Id, w.Status, w.AssemblyStage, w.IntegrationBranch))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Assembly-relevant projection of a <see cref="WorkPlan"/> row.</summary>
public sealed record WorkPlanAssemblyState(int Id, string Status, string? AssemblyStage, string? IntegrationBranch);
