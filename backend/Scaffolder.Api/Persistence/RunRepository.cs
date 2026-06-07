using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

internal sealed class RunRepository : IRunRepository
{
    private readonly ScaffolderDbContext _db;

    public RunRepository(ScaffolderDbContext db)
    {
        _db = db;
    }

    public async Task<RunEntity> CreateAsync(RunEntity run, CancellationToken ct = default)
    {
        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<RunEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Runs
            .Include(r => r.Session)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<RunEntity> UpdateStatusAsync(Guid id, RunStatus status, CancellationToken ct = default)
    {
        var run = await GetRequiredAsync(id, ct);
        run.Status = status;
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<RunEntity> UpdateDiffSummaryAsync(Guid id, string diffSummary, CancellationToken ct = default)
    {
        var run = await GetRequiredAsync(id, ct);
        run.DiffSummary = diffSummary;
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<RunEntity> UpdateFailureReasonAsync(Guid id, string failureReason, CancellationToken ct = default)
    {
        var run = await GetRequiredAsync(id, ct);
        run.FailureReason = failureReason;
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<RunEntity> UpdateSessionIdAsync(Guid id, Guid sessionId, CancellationToken ct = default)
    {
        var run = await GetRequiredAsync(id, ct);
        run.SessionId = sessionId;
        await _db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<RunEntity> GetRequiredAsync(Guid id, CancellationToken ct)
    {
        return await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"Run {id} not found.");
    }
}
