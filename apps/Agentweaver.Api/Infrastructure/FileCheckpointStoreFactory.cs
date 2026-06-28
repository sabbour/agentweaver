using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// File-backed checkpoint store factory for local/dev (sqlite or no database). Delegates to
/// <see cref="ResilientCheckpointStore"/>, which keeps the per-pod fallback and corrupt-index safety
/// net. This is NOT the production default — Postgres deployments use
/// <c>PostgresCheckpointStoreFactory</c> for a shared, replica-safe store.
/// </summary>
public sealed class FileCheckpointStoreFactory : ICheckpointStoreFactory
{
    public bool IsDatabaseBacked => false;

    public JsonCheckpointStore Create(string storeName, string fallbackFileDir, ILogger logger)
        => ResilientCheckpointStore.Create(fallbackFileDir, logger);

    // The file store is GC'd by directory sweeping in CheckpointGcService, not here.
    public Task<int> PurgeTerminalAsync(
        string storeName,
        Func<string, CancellationToken, ValueTask<bool>> isTerminalSession,
        CancellationToken ct) => Task.FromResult(0);
}
