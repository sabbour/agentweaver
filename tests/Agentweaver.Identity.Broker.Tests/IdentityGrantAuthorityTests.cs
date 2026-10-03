using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class IdentityGrantAuthorityTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MutationsAreCasIdempotentAppendOnlyAndDurableAcrossRestart()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);

        var first = CreateGrant("grant:durable", "secret-a", "version-1");
        GrantMutationResult created;
        await using (var db = new IdentityBrokerDbContext(options))
        {
            var authority = new IdentityGrantAuthority(db, TimeProvider.System);
            created = await authority.ReplaceAsync(first, 0, "create-operation");
            Assert.Equal(1, created.Revision);
            Assert.Equal(created, await authority.ReplaceAsync(first, 0, "create-operation"));

            await Assert.ThrowsAsync<GrantIdempotencyConflictException>(() =>
                authority.ReplaceAsync(CreateGrant("grant:durable", "secret-b", "version-2"), 0, "create-operation"));
            await Assert.ThrowsAsync<GrantConcurrencyException>(() =>
                authority.ReplaceAsync(first, 0, "stale-create"));
        }

        var replacement = CreateGrant("grant:durable", "secret-b", "version-2");
        GrantMutationResult renewed;
        await using (var db = new IdentityBrokerDbContext(options))
        {
            var authority = new IdentityGrantAuthority(db, TimeProvider.System);
            var replaced = await authority.ReplaceAsync(replacement, 1, "replace-operation");
            Assert.Equal(2, replaced.Revision);
            renewed = await authority.ReplaceAsync(
                CreateGrant("grant:durable", "secret-b", "version-2", DateTimeOffset.UtcNow.AddHours(2)),
                2,
                "renew-operation");
            Assert.Equal(3, renewed.Revision);
            var revoked = await authority.RevokeAsync("grant:durable", 3, "revoke-operation");
            Assert.Equal(4, revoked.Revision);
            Assert.Equal(revoked, await authority.RevokeAsync("grant:durable", 3, "revoke-operation"));

            var actor = new TrustedActorContext("actor-1", "project-1", "run-1");
            var request = new SecretRedemptionRequest(new SecretRef("secret-b", "version-2"), "configure", "run-1");
            var current = Assert.Single(await authority.FindGrantsAsync(actor, request, CancellationToken.None));
            Assert.Equal("4", current.Revision);
            Assert.Equal(GrantState.Revoked, current.State);
        }

        await using (var restarted = new IdentityBrokerDbContext(options))
        {
            var history = await restarted.SecretGrantRevisions.AsNoTracking()
                .Where(snapshot => snapshot.GrantId == "grant:durable")
                .OrderBy(snapshot => snapshot.Revision)
                .ToArrayAsync();
            Assert.Equal(new long[] { 1, 2, 3, 4 }, history.Select(snapshot => snapshot.Revision));
            Assert.Equal("secret-a", history[0].SecretId);
            Assert.Equal("secret-b", history[1].SecretId);
            Assert.Equal(GrantState.Revoked, history[3].State);
            var credentialValueColumnExists = await restarted.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'identity_broker'
                      AND table_name IN ('secret_grant_heads', 'secret_grant_revisions', 'secret_grant_operations')
                      AND column_name = 'value'
                ) AS "Value"
                """).SingleAsync();
            Assert.False(credentialValueColumnExists);

            const string changedSecretId = "changed";
            const string durableGrantId = "grant:durable";
            var immutableSnapshotError = await Assert.ThrowsAsync<PostgresException>(() =>
                restarted.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE identity_broker.secret_grant_revisions SET secret_id = {changedSecretId} WHERE grant_id = {durableGrantId} AND revision = {1}"));
            Assert.Contains("append-only", immutableSnapshotError.MessageText, StringComparison.Ordinal);
            var skippedRevisionError = await Assert.ThrowsAsync<PostgresException>(() =>
                restarted.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE identity_broker.secret_grant_heads SET current_revision = {6} WHERE grant_id = {durableGrantId}"));
            Assert.Contains("advance by one", skippedRevisionError.MessageText, StringComparison.Ordinal);
            const string creationOperation = "create-operation";
            var immutableReceiptError = await Assert.ThrowsAsync<PostgresException>(() =>
                restarted.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE identity_broker.secret_grant_operations SET revision = {9} WHERE idempotency_key = {creationOperation}"));
            Assert.Contains("receipts are immutable", immutableReceiptError.MessageText, StringComparison.Ordinal);

            var schemaState = await restarted.Database.SqlQueryRaw<string>(
                "SELECT to_regclass('identity_broker.secret_grant_revisions')::text AS \"Value\"")
                .SingleAsync();
            var publicState = await restarted.Database.SqlQueryRaw<string?>(
                "SELECT to_regclass('public.secret_grant_revisions')::text AS \"Value\"")
                .SingleAsync();
            Assert.Equal("identity_broker.secret_grant_revisions", schemaState);
            Assert.Null(publicState);
        }
    }

    [Fact]
    public async Task ConcurrentCasWritersProduceOneNextRevisionAndReadersSeeCoherentSnapshots()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);

        await using (var db = new IdentityBrokerDbContext(options))
            await new IdentityGrantAuthority(db, TimeProvider.System)
                .ReplaceAsync(CreateGrant("grant:concurrent", "secret-a", "version-1"), 0, "create-concurrent");

        var writers = new[]
        {
            ReplaceInNewContextAsync(options, DateTimeOffset.UtcNow.AddHours(2), "writer-b"),
            ReplaceInNewContextAsync(options, DateTimeOffset.UtcNow.AddHours(3), "writer-c"),
        };
        var readers = Enumerable.Range(0, 8)
            .Select(_ => ReadInNewContextAsync(options))
            .ToArray();
        await Task.WhenAll(writers.Cast<Task>().Concat(readers));
        var outcomes = writers.Select(task => task.Result).ToArray();
        Assert.Equal(1, outcomes.Count(result => result is not null));
        var snapshots = readers.Select(task => task.Result).ToArray();
        Assert.All(snapshots, current =>
        {
            var snapshot = Assert.Single(current);
            Assert.InRange(long.Parse(snapshot.Revision, System.Globalization.CultureInfo.InvariantCulture), 1, 2);
            Assert.Equal("secret-a", snapshot.Secret.Id);
        });

        await using var verification = new IdentityBrokerDbContext(options);
        Assert.Equal(2, await verification.SecretGrantRevisions.CountAsync(snapshot => snapshot.GrantId == "grant:concurrent"));

        var idempotentCandidate = CreateGrant("grant:idempotent-concurrent", "secret-a", "version-1");
        var duplicateMutations = await Task.WhenAll(
            CreateWithIdempotencyKeyAsync(options, idempotentCandidate),
            CreateWithIdempotencyKeyAsync(options, idempotentCandidate));
        Assert.All(duplicateMutations, result => Assert.Equal(1, result.Revision));
        Assert.Equal(1, await verification.SecretGrantRevisions.CountAsync(
            snapshot => snapshot.GrantId == "grant:idempotent-concurrent"));

        async Task<GrantMutationResult> CreateWithIdempotencyKeyAsync(
            DbContextOptions<IdentityBrokerDbContext> dbOptions,
            SecretRedemptionGrant grant)
        {
            await using var db = new IdentityBrokerDbContext(dbOptions);
            return await new IdentityGrantAuthority(db, TimeProvider.System).ReplaceAsync(
                grant,
                0,
                "shared-create-operation");
        }

        async Task<GrantMutationResult?> ReplaceInNewContextAsync(
            DbContextOptions<IdentityBrokerDbContext> dbOptions, DateTimeOffset expiresAt, string operation)
        {
            await using var db = new IdentityBrokerDbContext(dbOptions);
            try
            {
                return await new IdentityGrantAuthority(db, TimeProvider.System).ReplaceAsync(
                    CreateGrant("grant:concurrent", "secret-a", "version-1", expiresAt), 1, operation);
            }
            catch (GrantConcurrencyException)
            {
                return null;
            }
        }

        async Task<IReadOnlyList<SecretRedemptionGrant>> ReadInNewContextAsync(
            DbContextOptions<IdentityBrokerDbContext> dbOptions)
        {
            await using var db = new IdentityBrokerDbContext(dbOptions);
            return await new IdentityGrantAuthority(db, TimeProvider.System).FindGrantsAsync(
                new TrustedActorContext("actor-1", "project-1", "run-1"),
                new SecretRedemptionRequest(new SecretRef("secret-a", "version-1"), "configure", "run-1"),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReusedContextLocksAndMutatesTheLatestGrantRevision()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);

        await using var firstContext = new IdentityBrokerDbContext(options);
        var firstAuthority = new IdentityGrantAuthority(firstContext, TimeProvider.System);
        await firstAuthority.ReplaceAsync(
            CreateGrant("grant:reused-context", "secret-a", "version-1"),
            0,
            "create-reused-context");

        await using (var secondContext = new IdentityBrokerDbContext(options))
        {
            var secondAuthority = new IdentityGrantAuthority(secondContext, TimeProvider.System);
            var replacement = CreateGrant(
                "grant:reused-context",
                "secret-a",
                "version-1",
                DateTimeOffset.UtcNow.AddHours(2));
            Assert.Equal(2, (await secondAuthority.ReplaceAsync(
                replacement, 1, "replace-from-second-context")).Revision);
        }

        var revoked = await firstAuthority.RevokeAsync(
            "grant:reused-context", 2, "revoke-from-reused-context");
        Assert.Equal(3, revoked.Revision);

        await using var verification = new IdentityBrokerDbContext(options);
        Assert.Equal(3, await verification.SecretGrantRevisions.CountAsync(
            snapshot => snapshot.GrantId == "grant:reused-context"));
        Assert.Equal(3, await verification.SecretGrantHeads
            .Where(head => head.GrantId == "grant:reused-context")
            .Select(head => head.CurrentRevision)
            .SingleAsync());
        Assert.Equal(GrantState.Revoked, await verification.SecretGrantRevisions
            .Where(snapshot => snapshot.GrantId == "grant:reused-context" && snapshot.Revision == 3)
            .Select(snapshot => snapshot.State)
            .SingleAsync());
    }

    [Fact]
    public async Task IdempotentReplayReturnsCommittedReceiptAfterGrantExpiry()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = CreateOptions(dataSource);
        await IdentityBrokerMigrator.MigrateAsync(dataSource, options);

        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var replacement = new SecretRedemptionGrant(
            "grant:expired-replay",
            "actor-1",
            "project-1",
            "run-1",
            "configure",
            new SecretRef("secret-a", "version-1"),
            GrantState.Active,
            clock.GetUtcNow().AddMinutes(1),
            clock,
            "draft");

        await using var db = new IdentityBrokerDbContext(options);
        var authority = new IdentityGrantAuthority(db, clock);
        var created = await authority.ReplaceAsync(replacement, 0, "create-before-expiry");
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(created, await authority.ReplaceAsync(replacement, 0, "create-before-expiry"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            authority.ReplaceAsync(replacement, 1, "new-operation-after-expiry"));

        Assert.Equal(1, await db.SecretGrantRevisions.CountAsync(
            snapshot => snapshot.GrantId == "grant:expired-replay"));
        Assert.Equal(1, await db.SecretGrantOperations.CountAsync(
            operation => operation.GrantId == "grant:expired-replay"));
    }

    private static DbContextOptions<IdentityBrokerDbContext> CreateOptions(NpgsqlDataSource dataSource) =>
        new DbContextOptionsBuilder<IdentityBrokerDbContext>()
            .UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityBrokerDbContext.Schema))
            .UseOpenIddict()
            .Options;

    internal static SecretRedemptionGrant CreateGrant(
        string grantId,
        string secretId,
        string version,
        DateTimeOffset? expiresAt = null) =>
        new(
            grantId,
            "actor-1",
            "project-1",
            "run-1",
            "configure",
            new SecretRef(secretId, version),
            GrantState.Active,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30),
            revision: "draft");

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }
}
