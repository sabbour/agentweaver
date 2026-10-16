using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public sealed record GrantMutationResult(string GrantId, long Revision);

public sealed class GrantConcurrencyException : Exception
{
    public GrantConcurrencyException() : base("The grant revision changed.") { }
}

public sealed class GrantIdempotencyConflictException : Exception
{
    public GrantIdempotencyConflictException() : base("The idempotency key was already used.") { }
}

/// <summary>
/// Identity's PostgreSQL-backed run-grant authority. Mutations use optimistic
/// compare-and-swap under a row lock; every successful replacement, renewal or
/// revocation appends a new immutable revision in the same transaction.
/// </summary>
public sealed class IdentityGrantAuthority(
    IdentityBrokerDbContext db,
    TimeProvider timeProvider) : IGrantAuthority
{
    public Task<bool> HasActiveRunBindingAsync(
        string actorId,
        string projectId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        return (
            from head in db.SecretGrantHeads.AsNoTracking()
            join revision in db.SecretGrantRevisions.AsNoTracking()
                on new { head.GrantId, Revision = head.CurrentRevision }
                equals new { revision.GrantId, revision.Revision }
            where revision.ActorId == actorId
                && revision.ProjectId == projectId
                && revision.RunId == runId
                && revision.State == GrantState.Active
                && revision.ExpiresAt > now
            select revision)
            .AnyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SecretRedemptionGrant>> FindGrantsAsync(
        TrustedActorContext actor,
        SecretRedemptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var matches = await (
            from head in db.SecretGrantHeads.AsNoTracking()
            join revision in db.SecretGrantRevisions.AsNoTracking()
                on new { head.GrantId, Revision = head.CurrentRevision }
                equals new { revision.GrantId, revision.Revision }
            where revision.ActorId == actor.ActorId
                && revision.ProjectId == actor.ProjectId
                && revision.RunId == actor.RunId
                && revision.Purpose == request.Purpose
                && revision.SecretId == request.Secret.Id
                && revision.SecretVersion == request.Secret.Version
                && revision.ExpiresAt > now
            select revision)
            .ToListAsync(cancellationToken);

        return matches.Select(snapshot => new SecretRedemptionGrant(
            snapshot.GrantId,
            snapshot.ActorId,
            snapshot.ProjectId,
            snapshot.RunId,
            snapshot.Purpose,
            new SecretRef(snapshot.SecretId, snapshot.SecretVersion),
            snapshot.State,
            snapshot.ExpiresAt,
            new SnapshotConstructionTimeProvider(now),
            snapshot.Revision.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    /// <summary>
    /// Creates a grant at expected revision zero or atomically replaces/renews the
    /// named grant at the supplied current revision. Replaying the same idempotency
    /// key and request returns the original receipt without another revision.
    /// </summary>
    public Task<GrantMutationResult> ReplaceAsync(
        SecretRedemptionGrant replacement,
        long expectedRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.State != GrantState.Active)
            throw new ArgumentException("A replacement grant must be active.", nameof(replacement));
        return MutateAsync(
            replacement.GrantId,
            expectedRevision,
            idempotencyKey,
            ReplacementHash(replacement, expectedRevision),
            (revision, _) =>
            {
                if (replacement.ExpiresAt <= timeProvider.GetUtcNow())
                    throw new ArgumentOutOfRangeException(nameof(replacement), "A grant must expire in the future.");
                var snapshot = ToRevision(replacement, revision);
                db.SecretGrantRevisions.Add(snapshot);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Appends a revoked snapshot using a compare-and-swap revision.</summary>
    public Task<GrantMutationResult> RevokeAsync(
        string grantId,
        long expectedRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateOpaqueIdentifier(grantId, nameof(grantId));
        return MutateAsync(
            grantId,
            expectedRevision,
            idempotencyKey,
            Hash("revoke", grantId, expectedRevision.ToString(CultureInfo.InvariantCulture)),
            async (revision, ct) =>
            {
                var current = await db.SecretGrantRevisions.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.GrantId == grantId && row.Revision == expectedRevision, ct)
                    ?? throw new GrantConcurrencyException();
                db.SecretGrantRevisions.Add(new SecretGrantRevision
                {
                    GrantId = current.GrantId,
                    Revision = revision,
                    ActorId = current.ActorId,
                    ProjectId = current.ProjectId,
                    RunId = current.RunId,
                    Purpose = current.Purpose,
                    SecretId = current.SecretId,
                    SecretVersion = current.SecretVersion,
                    State = GrantState.Revoked,
                    ExpiresAt = current.ExpiresAt,
                });
            },
            cancellationToken);
    }

    private async Task<GrantMutationResult> MutateAsync(
        string grantId,
        long expectedRevision,
        string idempotencyKey,
        string requestHash,
        Func<long, CancellationToken, Task> addSnapshot,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueIdentifier(grantId, nameof(grantId));
        ValidateOpaqueIdentifier(idempotencyKey, nameof(idempotencyKey));
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var operationInserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_broker.secret_grant_operations
                (idempotency_key, request_hash, grant_id, revision)
            VALUES ({idempotencyKey}, {requestHash}, {grantId}, 0)
            ON CONFLICT (idempotency_key) DO NOTHING
            """, cancellationToken);
        if (operationInserted == 0)
        {
            var prior = await db.SecretGrantOperations.AsNoTracking()
                .SingleAsync(operation => operation.IdempotencyKey == idempotencyKey, cancellationToken);
            if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                throw new GrantIdempotencyConflictException();
            return new GrantMutationResult(prior.GrantId, prior.Revision);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_broker.secret_grant_heads (grant_id, current_revision)
            VALUES ({grantId}, 0)
            ON CONFLICT (grant_id) DO NOTHING
            """, cancellationToken);

        var head = await db.SecretGrantHeads
            .FromSqlInterpolated($"""
                SELECT grant_id, current_revision
                FROM identity_broker.secret_grant_heads
                WHERE grant_id = {grantId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        if (head.CurrentRevision != expectedRevision)
            throw new GrantConcurrencyException();

        var nextRevision = checked(expectedRevision + 1);
        await addSnapshot(nextRevision, cancellationToken);
        var updatedHeads = await db.SecretGrantHeads
            .Where(item => item.GrantId == grantId && item.CurrentRevision == expectedRevision)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.CurrentRevision, nextRevision),
                cancellationToken);
        if (updatedHeads != 1)
            throw new GrantConcurrencyException();

        var operation = await db.SecretGrantOperations
            .SingleAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
        operation.Revision = nextRevision;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GrantMutationResult(grantId, nextRevision);
    }

    private static SecretGrantRevision ToRevision(SecretRedemptionGrant grant, long revision) => new()
    {
        GrantId = grant.GrantId,
        Revision = revision,
        ActorId = grant.ActorId,
        ProjectId = grant.ProjectId,
        RunId = grant.RunId,
        Purpose = grant.Purpose,
        SecretId = grant.Secret.Id,
        SecretVersion = grant.Secret.Version,
        State = grant.State,
        ExpiresAt = grant.ExpiresAt,
    };

    private static string ReplacementHash(SecretRedemptionGrant grant, long expectedRevision) => Hash(
        "replace",
        grant.GrantId,
        expectedRevision.ToString(CultureInfo.InvariantCulture),
        grant.ActorId,
        grant.ProjectId,
        grant.RunId,
        grant.Purpose,
        grant.Secret.Id,
        grant.Secret.Version,
        ((int)grant.State).ToString(CultureInfo.InvariantCulture),
        grant.ExpiresAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));

    private static string Hash(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));

    private static void ValidateOpaqueIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Expected a nonempty opaque identifier.", parameterName);
    }

    // Persisted grant snapshots may expire between the database read and materialization.
    // Validate them against the same timestamp used by the query; the authorization
    // primitive rechecks expiry against the live clock before backend contact.
    private sealed class SnapshotConstructionTimeProvider(DateTimeOffset snapshotTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => snapshotTime;
    }
}
