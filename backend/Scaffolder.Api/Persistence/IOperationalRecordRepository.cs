using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

/// <summary>
/// Repository for the compliance/governance operational record.
/// One record per run, distinct from the append-only event log (FR-028).
/// </summary>
public interface IOperationalRecordRepository
{
    /// <summary>
    /// Creates or updates the operational record for a run.
    /// </summary>
    Task<OperationalRecordEntity> UpsertAsync(OperationalRecordEntity record, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the operational record for a run.
    /// </summary>
    Task<OperationalRecordEntity?> GetByRunIdAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Appends a governance policy decision entry to the policyTrace JSON array.
    /// Each entry is a JSON object with timestamp, policyType, decision, and details.
    /// </summary>
    Task AppendPolicyTraceEntryAsync(Guid runId, string entryJson, CancellationToken ct = default);
}
