using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Selects the MAF checkpoint store backend at startup. Production (Postgres) gets a shared,
/// concurrency-safe <c>PostgresJsonCheckpointStore</c>; local/dev (sqlite or no DB) falls back to the
/// per-pod file store via <see cref="ResilientCheckpointStore"/>.
/// <para>
/// The store the factory returns derives from MAF's <see cref="JsonCheckpointStore"/>, so callers can
/// pass it straight to <c>CheckpointManager.CreateJson(store)</c>.
/// </para>
/// </summary>
public interface ICheckpointStoreFactory
{
    /// <summary>True when the backing store is the shared database (Postgres) rather than the file store.</summary>
    bool IsDatabaseBacked { get; }

    /// <summary>
    /// Creates the checkpoint store for a logical store (<c>"runs"</c> or <c>"coordinator"</c>).
    /// <paramref name="fallbackFileDir"/> is only used by the file backend.
    /// </summary>
    JsonCheckpointStore Create(string storeName, string fallbackFileDir, ILogger logger);

    /// <summary>
    /// Reclaims checkpoints whose session is terminal (DB-backed stores only). The file store returns 0
    /// and relies on directory sweeping instead. Returns the number of checkpoint rows deleted.
    /// </summary>
    Task<int> PurgeTerminalAsync(
        string storeName,
        Func<string, CancellationToken, ValueTask<bool>> isTerminalSession,
        CancellationToken ct);
}
