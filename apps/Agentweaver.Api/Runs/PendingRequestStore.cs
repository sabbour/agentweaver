using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Replica-safe store mapping <c>runId → pending ExternalRequest</c> plus fenced delivery state for
/// the human-in-the-loop (HITL) review/confirmation gate.
///
/// State lives in <see cref="MemoryDbContext"/> (Postgres in prod, SQLite in dev) rather than per-pod
/// memory: the background watch loop arms the gate on one pod while a later HTTP review/confirm
/// request may be served by a different pod (at <c>replicas:2</c>). A submitted decision/result is
/// persisted against the exact MAF request id and claimed through waiting → ready → delivering →
/// delivered transitions so a crash before send does not destroy the resume handoff.
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
        var requestId = request.RequestId;
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
                RequestId = requestId,
                OwnerUser = ownerUser,
                DeliveryState = PendingRequestDeliveryStates.Waiting,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            if (existing.RequestId == requestId
                && (existing.DeliveryState == PendingRequestDeliveryStates.Ready
                    || existing.DeliveryState == PendingRequestDeliveryStates.Delivering
                    || existing.DeliveryState == PendingRequestDeliveryStates.Delivered))
                return;

            existing.RequestJson = json;
            existing.RequestId = requestId;
            existing.OwnerUser = ownerUser;
            existing.DeliveryState = PendingRequestDeliveryStates.Waiting;
            existing.DeliveryKind = null;
            existing.DecisionIdentity = null;
            existing.ResponseJson = null;
            existing.DeliveryClaimOwner = null;
            existing.DeliveryClaimedAt = null;
            existing.DeliveredAt = null;
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
            .FirstOrDefaultAsync(p => p.RunId == runId
                && p.DeliveryState != PendingRequestDeliveryStates.Delivered
                && p.DeliveryState != PendingRequestDeliveryStates.Ready
                && p.DeliveryState != PendingRequestDeliveryStates.Delivering, ct)
            .ConfigureAwait(false);
        return row is null ? null : new PendingEntry(DeserializeRequest(row.RequestJson), row.OwnerUser);
    }

    public async Task<bool> ExistsForRequestAsync(string runId, string requestId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.PendingRequests.AsNoTracking()
            .AnyAsync(p => p.RunId == runId
                && p.RequestId == requestId, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsUndeliveredAsync(string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.PendingRequests.AsNoTracking()
            .AnyAsync(p => p.RunId == runId
                && p.DeliveryState != PendingRequestDeliveryStates.Delivered, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryQueueDeliveryAsync<TResponse>(
        string runId,
        string deliveryKind,
        string decisionIdentity,
        TResponse response,
        string ownerUser,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var row = await db.PendingRequests
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);
        if (row is null || row.DeliveryState == PendingRequestDeliveryStates.Delivered)
            return false;
        if (!string.Equals(row.OwnerUser, ownerUser, StringComparison.Ordinal))
            return false;

        var request = DeserializeRequest(row.RequestJson);
        var expectedIdentity = CreateDecisionIdentity(request.RequestId, response);
        if (!string.Equals(expectedIdentity, decisionIdentity, StringComparison.Ordinal))
            return false;

        if (row.DecisionIdentity is not null)
            return string.Equals(row.DecisionIdentity, decisionIdentity, StringComparison.Ordinal);

        var responseJson = JsonSerializer.Serialize(response, JsonDefaults.Options);
        var queued = await db.PendingRequests
            .Where(p => p.RunId == runId
                && (p.RequestId == request.RequestId || p.RequestId == null)
                && p.OwnerUser == ownerUser
                && p.DeliveryState == PendingRequestDeliveryStates.Waiting
                && p.DecisionIdentity == null)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(p => p.RequestId, request.RequestId)
                .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Ready)
                .SetProperty(p => p.DeliveryKind, deliveryKind)
                .SetProperty(p => p.DecisionIdentity, decisionIdentity)
                .SetProperty(p => p.ResponseJson, responseJson)
                .SetProperty(p => p.DeliveryClaimOwner, (string?)null)
                .SetProperty(p => p.DeliveryClaimedAt, (DateTimeOffset?)null)
                .SetProperty(p => p.DeliveredAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
        if (queued == 1)
            return true;

        var current = await db.PendingRequests.AsNoTracking()
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);
        return current is not null
            && string.Equals(current.RequestId, request.RequestId, StringComparison.Ordinal)
            && string.Equals(current.DecisionIdentity, decisionIdentity, StringComparison.Ordinal);
    }

    public async Task<PendingDelivery?> TryClaimDeliveryAsync(
        string runId,
        string claimOwner,
        TimeSpan staleAfter,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - staleAfter;

        var row = await db.PendingRequests
            .FirstOrDefaultAsync(p => p.RunId == runId, ct)
            .ConfigureAwait(false);
        if (row is null
            || row.DeliveryState == PendingRequestDeliveryStates.Delivered
            || string.IsNullOrEmpty(row.ResponseJson)
            || string.IsNullOrEmpty(row.DecisionIdentity)
            || string.IsNullOrEmpty(row.DeliveryKind))
            return null;
        if (row.DeliveryState == PendingRequestDeliveryStates.Delivering
            && row.DeliveryClaimedAt is not null
            && row.DeliveryClaimedAt > staleBefore)
            return null;
        if (row.DeliveryState != PendingRequestDeliveryStates.Ready
            && row.DeliveryState != PendingRequestDeliveryStates.Delivering)
            return null;

        int claimed;
        var previousOwner = row.DeliveryClaimOwner;
        var previousClaimedAt = row.DeliveryClaimedAt;
        if (row.DeliveryState == PendingRequestDeliveryStates.Ready)
        {
            claimed = await db.PendingRequests
                .Where(p => p.RunId == runId
                    && p.DecisionIdentity == row.DecisionIdentity
                    && p.DeliveryState == PendingRequestDeliveryStates.Ready)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Delivering)
                    .SetProperty(p => p.DeliveryClaimOwner, claimOwner)
                    .SetProperty(p => p.DeliveryClaimedAt, now), ct)
                .ConfigureAwait(false);
        }
        else
        {
            if (previousClaimedAt is not null && previousClaimedAt > staleBefore)
                claimed = 0;
            else if (previousClaimedAt is null)
            {
                claimed = await db.PendingRequests
                    .Where(p => p.RunId == runId
                        && p.DecisionIdentity == row.DecisionIdentity
                        && p.DeliveryState == PendingRequestDeliveryStates.Delivering
                        && p.DeliveryClaimOwner == previousOwner
                        && p.DeliveryClaimedAt == null)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(p => p.DeliveryClaimOwner, claimOwner)
                        .SetProperty(p => p.DeliveryClaimedAt, now), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                claimed = await db.PendingRequests
                    .Where(p => p.RunId == runId
                        && p.DecisionIdentity == row.DecisionIdentity
                        && p.DeliveryState == PendingRequestDeliveryStates.Delivering
                        && p.DeliveryClaimOwner == previousOwner
                        && p.DeliveryClaimedAt == previousClaimedAt)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(p => p.DeliveryClaimOwner, claimOwner)
                        .SetProperty(p => p.DeliveryClaimedAt, now), ct)
                    .ConfigureAwait(false);
            }
        }
        if (claimed == 0)
            return null;

        return new PendingDelivery(
            DeserializeRequest(row.RequestJson),
            row.OwnerUser,
            row.DeliveryKind,
            row.DecisionIdentity,
            row.ResponseJson,
            claimOwner,
            now);
    }

    public async Task<bool> MarkDeliveredAsync(
        string runId,
        string decisionIdentity,
        string claimOwner,
        DateTimeOffset claimedAt,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var delivered = await db.PendingRequests
            .Where(p => p.RunId == runId
                && p.DecisionIdentity == decisionIdentity
                && p.DeliveryState == PendingRequestDeliveryStates.Delivering
                && p.DeliveryClaimOwner == claimOwner
                && p.DeliveryClaimedAt == claimedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Delivered)
                .SetProperty(p => p.DeliveredAt, DateTimeOffset.UtcNow)
                .SetProperty(p => p.DeliveryClaimOwner, (string?)null)
                .SetProperty(p => p.DeliveryClaimedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
        return delivered == 1;
    }

    public async Task<bool> MarkObservedWorkflowAdvanceAsync(
        string runId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var delivered = await db.PendingRequests
            .Where(p => p.RunId == runId
                && p.DeliveryState == PendingRequestDeliveryStates.Delivering)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Delivered)
                .SetProperty(p => p.DeliveredAt, DateTimeOffset.UtcNow)
                .SetProperty(p => p.DeliveryClaimOwner, (string?)null)
                .SetProperty(p => p.DeliveryClaimedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
        return delivered > 0;
    }

    public async Task<bool> ReleaseDeliveryAsync(
        string runId,
        string decisionIdentity,
        string claimOwner,
        DateTimeOffset claimedAt,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var released = await db.PendingRequests
            .Where(p => p.RunId == runId
                && p.DecisionIdentity == decisionIdentity
                && p.DeliveryState == PendingRequestDeliveryStates.Delivering
                && p.DeliveryClaimOwner == claimOwner
                && p.DeliveryClaimedAt == claimedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(p => p.DeliveryState, PendingRequestDeliveryStates.Ready)
                .SetProperty(p => p.DeliveryClaimOwner, (string?)null)
                .SetProperty(p => p.DeliveryClaimedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
        return released == 1;
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
            .Where(p => p.RunId == runId
                && p.DeliveryState == PendingRequestDeliveryStates.Waiting)
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

    public static string CreateDecisionIdentity<TResponse>(string requestId, TResponse response)
    {
        var payload = JsonSerializer.Serialize(response, JsonDefaults.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId}\n{payload}")))
            .ToLowerInvariant();
        return $"{requestId}:{hash}";
    }

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

public sealed record PendingDelivery(
    ExternalRequest Request,
    string OwnerUser,
    string DeliveryKind,
    string DecisionIdentity,
    string ResponseJson,
    string ClaimOwner,
    DateTimeOffset ClaimedAt)
{
    public TResponse GetResponse<TResponse>() =>
        JsonSerializer.Deserialize<TResponse>(ResponseJson, JsonDefaults.Options)
        ?? throw new InvalidOperationException("Stored pending delivery response could not be deserialized.");
}
