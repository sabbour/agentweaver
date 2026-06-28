using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// Postgres-backed MAF checkpoint store. A drop-in for <see cref="FileSystemJsonCheckpointStore"/>:
/// it derives from the same MAF <see cref="JsonCheckpointStore"/> base (i.e.
/// <c>ICheckpointStore&lt;JsonElement&gt;</c>) and so plugs straight into
/// <c>CheckpointManager.CreateJson(store)</c>.
/// <para>
/// Unlike the file store — which takes an EXCLUSIVE process lock on its directory, so under
/// <c>replicas:2</c> on a shared RWX volume only ONE pod can ever own it — this store keeps every
/// checkpoint as an independent, unique-PK row in the <c>workflow_checkpoints</c> table. Concurrent
/// writes from different replicas are plain INSERTs that never contend (no global lock), and Postgres
/// MVCC makes each committed checkpoint immediately visible to the other replica. That gives genuine
/// concurrency-safe, cross-pod checkpoint sharing and resume.
/// </para>
/// Uses <see cref="IDbContextFactory{TContext}"/> so a single registered instance can serve many
/// concurrent runs (a fresh context per call), matching the other EF-backed singleton stores.
/// </summary>
public sealed class PostgresJsonCheckpointStore : JsonCheckpointStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly string _storeName;
    private readonly ILogger? _logger;

    public PostgresJsonCheckpointStore(
        IDbContextFactory<MemoryDbContext> factory,
        string storeName,
        ILogger? logger = null)
    {
        _factory = factory;
        _storeName = storeName;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        // Each checkpoint gets a fresh unique id, so this is always an INSERT — two replicas writing
        // concurrently produce different PKs and never collide (no exclusive lock required).
        var checkpointId = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;

        await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
        db.WorkflowCheckpoints.Add(new WorkflowCheckpointRecord
        {
            StoreName = _storeName,
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ParentCheckpointId = parent?.CheckpointId,
            HasParentMetadata = true,
            Payload = value.GetRawText(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new CheckpointInfo(sessionId, checkpointId);
    }

    /// <inheritdoc />
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
        var rec = await db.WorkflowCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.StoreName == _storeName && r.SessionId == sessionId && r.CheckpointId == key.CheckpointId)
            .ConfigureAwait(false);

        if (rec is null)
            throw new KeyNotFoundException(
                $"Checkpoint '{key.CheckpointId}' was not found for session '{sessionId}' in store '{_storeName}'.");

        using var doc = JsonDocument.Parse(rec.Payload);
        return doc.RootElement.Clone();
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId, CheckpointInfo? withParent = null)
    {
        await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
        var recs = await db.WorkflowCheckpoints.AsNoTracking()
            .Where(r => r.StoreName == _storeName && r.SessionId == sessionId)
            .ToListAsync()
            .ConfigureAwait(false);

        // Mirror FileSystemJsonCheckpointStore: with a parent filter, include entries that either have
        // no parent metadata or whose parent matches the requested checkpoint.
        IEnumerable<WorkflowCheckpointRecord> filtered = recs;
        if (withParent is not null)
            filtered = recs.Where(r => !r.HasParentMetadata || r.ParentCheckpointId == withParent.CheckpointId);

        return filtered
            .Select(r => new CheckpointInfo(r.SessionId, r.CheckpointId))
            .ToList();
    }

    /// <summary>
    /// Deletes every checkpoint row for a session in this store. Used by the checkpoint GC to reclaim
    /// rows for terminal runs (the Postgres equivalent of deleting a per-run checkpoint directory).
    /// </summary>
    public async Task<int> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.WorkflowCheckpoints
            .Where(r => r.StoreName == _storeName && r.SessionId == sessionId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
