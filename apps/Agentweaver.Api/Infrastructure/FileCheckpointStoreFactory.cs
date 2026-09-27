using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
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
    // Recovery must scan the directory the store actually opened, including replica/temp fallback.
    private readonly ConcurrentDictionary<string, (string Directory, JsonCheckpointStore Store)> _stores = new(StringComparer.Ordinal);

    public bool IsDatabaseBacked => false;

    public JsonCheckpointStore Create(string storeName, string fallbackFileDir, ILogger logger)
    {
        var store = ResilientCheckpointStore.Create(fallbackFileDir, logger, out var selectedDirectory);
        _stores[storeName] = (selectedDirectory, store);
        return store;
    }

    /// <summary>
    /// MAF's file-store index enumerates checkpoints in creation order. Use the already-open
    /// store to read it: index.jsonl is exclusively locked while that store is alive.
    /// File modification times can tie or be reordered independently of checkpoint order.
    /// </summary>
    public async Task<CheckpointInfo?> GetLatestCheckpointAsync(string storeName, string sessionId, CancellationToken ct = default)
    {
        if (!_stores.TryGetValue(storeName, out var selected))
            return null;

        if (!Directory.Exists(selected.Directory))
            return null;

        ct.ThrowIfCancellationRequested();
        var index = await selected.Store.RetrieveIndexAsync(sessionId).ConfigureAwait(false);
        return index.LastOrDefault();
    }

    // The file store is GC'd by directory sweeping in CheckpointGcService, not here.
    public Task<int> PurgeTerminalAsync(
        string storeName,
        Func<string, CancellationToken, ValueTask<bool>> isTerminalSession,
        CancellationToken ct) => Task.FromResult(0);
}
