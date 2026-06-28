using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Postgres-backed checkpoint store factory (production default when Database:Provider = postgres).
/// Hands out <see cref="PostgresJsonCheckpointStore"/> instances that share the same
/// <c>workflow_checkpoints</c> table across all replicas, so checkpoints are concurrency-safe and
/// resumable cross-pod with no file lock and no shared-volume permission dependency.
/// </summary>
public sealed class PostgresCheckpointStoreFactory : ICheckpointStoreFactory
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public PostgresCheckpointStoreFactory(IDbContextFactory<MemoryDbContext> factory)
    {
        _factory = factory;
    }

    public bool IsDatabaseBacked => true;

    public JsonCheckpointStore Create(string storeName, string fallbackFileDir, ILogger logger)
        => new PostgresJsonCheckpointStore(_factory, storeName, logger);

    public async Task<int> PurgeTerminalAsync(
        string storeName,
        Func<string, CancellationToken, ValueTask<bool>> isTerminalSession,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var sessions = await db.WorkflowCheckpoints.AsNoTracking()
            .Where(c => c.StoreName == storeName)
            .Select(c => c.SessionId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deleted = 0;
        foreach (var sessionId in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (!await isTerminalSession(sessionId, ct).ConfigureAwait(false))
                continue;

            deleted += await db.WorkflowCheckpoints
                .Where(c => c.StoreName == storeName && c.SessionId == sessionId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return deleted;
    }
}
