using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// PostgreSQL-backed run lease store using atomic CAS UPDATE.
/// Claim: UPDATE runs SET owner_id=@me, lease_expires_at=@deadline, fencing_token=fencing_token+1, attempt=attempt+1
///        WHERE run_id=@runId AND (owner_id IS NULL OR lease_expires_at < now())
/// Renew: UPDATE runs SET lease_expires_at=@deadline, heartbeat_at=now()
///        WHERE run_id=@runId AND owner_id=@me AND fencing_token=@token
/// Release: UPDATE runs SET owner_id=NULL, lease_expires_at=NULL
///          WHERE run_id=@runId AND owner_id=@me AND fencing_token=@token
/// </summary>
public sealed class PostgresRunLeaseStore : IRunLeaseStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public PostgresRunLeaseStore(IDbContextFactory<MemoryDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<(bool Claimed, long FencingToken)> TryClaimAsync(
        string runId, string ownerId, TimeSpan leaseTtl, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var deadline = DateTimeOffset.UtcNow.Add(leaseTtl);
        var now = DateTimeOffset.UtcNow;

        var rows = await db.Runs
            .Where(r => r.RunId == runId &&
                        (r.OwnerId == null || r.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.OwnerId, ownerId)
                .SetProperty(r => r.LeaseExpiresAt, deadline)
                .SetProperty(r => r.HeartbeatAt, now)
                .SetProperty(r => r.FencingToken, r => r.FencingToken + 1)
                .SetProperty(r => r.Attempt, r => r.Attempt + 1),
            ct);

        if (rows == 0)
            return (false, 0);

        var token = await db.Runs
            .Where(r => r.RunId == runId && r.OwnerId == ownerId)
            .Select(r => (long?)r.FencingToken)
            .FirstOrDefaultAsync(ct);

        return token.HasValue ? (true, token.Value) : (false, 0);
    }

    public async Task<bool> TryRenewAsync(
        string runId, string ownerId, long fencingToken, TimeSpan leaseTtl, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var deadline = DateTimeOffset.UtcNow.Add(leaseTtl);
        var now = DateTimeOffset.UtcNow;

        var rows = await db.Runs
            .Where(r => r.RunId == runId && r.OwnerId == ownerId && r.FencingToken == fencingToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LeaseExpiresAt, deadline)
                .SetProperty(r => r.HeartbeatAt, now),
            ct);

        return rows > 0;
    }

    public async Task ReleaseAsync(string runId, string ownerId, long fencingToken, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Runs
            .Where(r => r.RunId == runId && r.OwnerId == ownerId && r.FencingToken == fencingToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.OwnerId, (string?)null)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null),
            ct);
    }

    public async Task<bool> IsLeaseOwnerAsync(
        string runId, string ownerId, long fencingToken, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.Runs
            .AnyAsync(r => r.RunId == runId &&
                           r.OwnerId == ownerId &&
                           r.FencingToken == fencingToken &&
                           r.LeaseExpiresAt > now,
                      ct);
    }
}
