using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Provider-neutral run revision audit store interface. Both SqliteRunRevisionStore and
/// EfRunRevisionStore implement this. Register the correct implementation via
/// Database:Provider in DI so consumers never bind to a concrete SQLite type.
/// </summary>
public interface IRunRevisionStore
{
    Task InsertRevisionAsync(
        RunId runId,
        int revisionNumber,
        string reviewerUser,
        string rawComment,
        string sanitizedComment,
        string previousTreeHash,
        CancellationToken ct = default);

    Task<int> GetMaxRevisionNumberAsync(RunId runId, CancellationToken ct = default);
}
