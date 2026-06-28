using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Replica-safe store mapping <c>runId → pending ExternalRequest</c> for the human-in-the-loop
/// (HITL) review/confirmation gate.
///
/// State lives in <see cref="MemoryDbContext"/> (Postgres in prod, SQLite in dev) rather than per-pod
/// memory: the background watch loop arms the gate on one pod while a later HTTP review/confirm
/// request may be served by a different pod (at <c>replicas:2</c>). <see cref="TryRemoveAsync"/> is an
/// atomic single-consume (read-then-conditional <c>ExecuteDeleteAsync</c> on the unique run id), so two
/// pods can never both consume the same gate — preserving at-most-once delivery (replay / double-POST
/// protection).
///
/// Registered as a singleton because it is consumed by singleton background services
/// (<c>RunWatchLoopService</c>, <c>CoordinatorRunService</c>) as well as scoped HTTP endpoints; it
/// opens a fresh <see cref="MemoryDbContext"/> per call via <see cref="IServiceScopeFactory"/>, the same
/// pattern as <c>CoordinatorAssemblyStore</c>.
/// </summary>
public sealed class PendingRequestStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PendingRequestStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>Arms (or re-arms) the pending gate for a run. Upserts by the unique run id.</summary>
    public async Task SetAsync(string runId, ExternalRequest request, string ownerUser, CancellationToken ct = default)
    {
        var json = SerializeRequest(request);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var existing = await db.PendingRequests
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.PendingRequests.Add(new PendingRequestRecord
            {
                RunId = runId,
                RequestJson = json,
                OwnerUser = ownerUser,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.RequestJson = json;
            existing.OwnerUser = ownerUser;
            existing.CreatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the pending gate for a run without consuming it. Null when no gate is armed.</summary>
    public async Task<PendingEntry?> GetAsync(string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.PendingRequests.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);
        return row is null ? null : new PendingEntry(DeserializeRequest(row.RequestJson), row.OwnerUser);
    }

    /// <summary>
    /// Atomically removes and returns the pending gate, guaranteeing at-most-once delivery across
    /// replicas. Reads the row, then conditionally deletes it by run id: the caller whose
    /// <c>ExecuteDeleteAsync</c> affected the row wins; zero rows affected (already consumed on this or
    /// another pod, or never armed) yields <c>null</c>.
    /// </summary>
    public async Task<PendingEntry?> TryRemoveAsync(string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var row = await db.PendingRequests.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);
        if (row is null)
            return null;

        var deleted = await db.PendingRequests
            .Where(p => p.RunId == runId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Lost the race to another consumer (this or another replica) — at-most-once preserved.
        if (deleted == 0)
            return null;

        return new PendingEntry(DeserializeRequest(row.RequestJson), row.OwnerUser);
    }

    // ── Serialization ──────────────────────────────────────────────────────────
    // Only PortInfo + RequestId are persisted: these are all that CreateResponse needs to build the
    // response and resume the suspended workflow. The original request Data (PortableValue) is not
    // round-tripped — it is never read after the gate is armed, and PortableValue requires MAF's
    // checkpoint converter to deserialize faithfully.

    private sealed record PendingRequestEnvelope(RequestPortInfo PortInfo, string RequestId);

    private static string SerializeRequest(ExternalRequest request) =>
        JsonSerializer.Serialize(
            new PendingRequestEnvelope(request.PortInfo, request.RequestId), JsonDefaults.Options);

    private static ExternalRequest DeserializeRequest(string json)
    {
        var env = JsonSerializer.Deserialize<PendingRequestEnvelope>(json, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Stored pending request could not be deserialized.");
        // Data is unused by CreateResponse; supply a placeholder PortableValue to satisfy the ctor.
        return new ExternalRequest(env.PortInfo, env.RequestId, new PortableValue(env.RequestId));
    }
}

/// <summary>Pending request entry with owner for IDOR defense.</summary>
public sealed record PendingEntry(ExternalRequest Request, string OwnerUser);
