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
/// <see cref="IServiceScopeFactory"/> and reads a single table.
/// </summary>
public sealed class CoordinatorStatusReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorStatusReader(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// Returns the current <c>WorkPlan.Status</c> for each supplied coordinator run id that has a
    /// work plan. Run ids with no work plan are omitted. Returns an empty map for an empty input.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetCoordinatorStatusesAsync(
        IReadOnlyCollection<string> coordinatorRunIds, CancellationToken ct)
    {
        if (coordinatorRunIds.Count == 0)
            return new Dictionary<string, string>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(w => coordinatorRunIds.Contains(w.CoordinatorRunId))
            .ToDictionaryAsync(w => w.CoordinatorRunId, w => w.Status, ct)
            .ConfigureAwait(false);
    }
}
