using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

public interface IRunRepository
{
    Task<RunEntity> CreateAsync(RunEntity run, CancellationToken ct = default);
    Task<RunEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<RunEntity> UpdateStatusAsync(Guid id, RunStatus status, CancellationToken ct = default);
    Task<RunEntity> UpdateDiffSummaryAsync(Guid id, string diffSummary, CancellationToken ct = default);
    Task<RunEntity> UpdateFailureReasonAsync(Guid id, string failureReason, CancellationToken ct = default);
    Task<RunEntity> UpdateSessionIdAsync(Guid id, Guid sessionId, CancellationToken ct = default);
}
