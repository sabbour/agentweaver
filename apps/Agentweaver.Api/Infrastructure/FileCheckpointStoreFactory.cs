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
    // The base directory is only known per-Create call (different per logical store), so remember it
    // keyed by storeName. Recovery's GetLatestCheckpointAsync then resolves the right directory to scan.
    private readonly ConcurrentDictionary<string, string> _baseDirs = new(StringComparer.Ordinal);

    public bool IsDatabaseBacked => false;

    public JsonCheckpointStore Create(string storeName, string fallbackFileDir, ILogger logger)
    {
        _baseDirs[storeName] = fallbackFileDir;
        return ResilientCheckpointStore.Create(fallbackFileDir, logger);
    }

    /// <summary>
    /// Scans the per-session checkpoint directory and returns the most recently written checkpoint
    /// (the file store equivalent of the DB "latest by CreatedAt" query).
    /// </summary>
    public Task<CheckpointInfo?> GetLatestCheckpointAsync(string storeName, string sessionId, CancellationToken ct = default)
    {
        if (!_baseDirs.TryGetValue(storeName, out var baseDir))
            return Task.FromResult<CheckpointInfo?>(null);

        var dir = Path.Combine(baseDir, sessionId);
        if (!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0)
            return Task.FromResult<CheckpointInfo?>(null);

        var latestFile = Directory.GetFiles(dir)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latestFile is null)
            return Task.FromResult<CheckpointInfo?>(null);

        var checkpointId = Path.GetFileNameWithoutExtension(latestFile);
        return Task.FromResult<CheckpointInfo?>(new CheckpointInfo(sessionId, checkpointId));
    }

    // The file store is GC'd by directory sweeping in CheckpointGcService, not here.
    public Task<int> PurgeTerminalAsync(
        string storeName,
        Func<string, CancellationToken, ValueTask<bool>> isTerminalSession,
        CancellationToken ct) => Task.FromResult(0);
}
