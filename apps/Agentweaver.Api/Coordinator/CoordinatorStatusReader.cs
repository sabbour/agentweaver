using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Read-only lookup of a coordinator run's work-plan orchestration status, used by the general
/// run and project endpoints to surface "Dispatching" / "Awaiting assembly" / "Failed: &lt;reason&gt;"
/// alongside the bare run status.
///
/// This is kept separate from <see cref="CoordinatorRunService"/> on purpose: the hot-path run-list
/// and run-detail endpoints must not pull the full coordinator orchestration graph (dispatch,
/// workflow factory, watch-loop wiring) into their dependency closure. This reader depends only on
/// <see cref="IServiceScopeFactory"/> and reads the lifecycle projection tables.
/// </summary>
public sealed class CoordinatorStatusReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorStatusReader(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// Returns the current coordinator lifecycle for each supplied coordinator run id. Once a work
    /// plan exists, <c>WorkPlan.Status</c> is authoritative. Before decomposition, the outcome-spec
    /// status (<c>drafting</c> / <c>awaiting_confirmation</c>) is used so replayed pages do not
    /// collapse active outcome planning back to "not started".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetCoordinatorStatusesAsync(
        IReadOnlyCollection<string> coordinatorRunIds, CancellationToken ct)
    {
        if (coordinatorRunIds.Count == 0)
            return new Dictionary<string, string>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var result = await db.WorkPlans.AsNoTracking()
            .Where(w => coordinatorRunIds.Contains(w.CoordinatorRunId))
            .ToDictionaryAsync(w => w.CoordinatorRunId, w => w.Status, ct)
            .ConfigureAwait(false);

        var missing = coordinatorRunIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0)
            return result;

        var specs = await db.OutcomeSpecs.AsNoTracking()
            .Where(s => missing.Contains(s.CoordinatorRunId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var spec in specs
                     .GroupBy(s => s.CoordinatorRunId)
                     .Select(g => g.OrderByDescending(s => s.UpdatedAt).First()))
        {
            result[spec.CoordinatorRunId] = spec.Status;
        }

        return result;
    }
}
