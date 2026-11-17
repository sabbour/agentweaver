using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformEgressApplyReservationPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ReservationPersistsExactRequestAndReplaysAcrossStoreRestart()
    {
        var targetId = "test-" + Guid.NewGuid().ToString("N");
        var request = Request(targetId, "apply-1", sourceEpoch: 1, generation: 1);
        var store = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);

        var first = await store.ReserveAsync(request, CancellationToken.None);
        await using var restartedDataSource = fixture.CreateDataSource("egress-reservation-restart");
        var restartedStore = new PlatformEgressApplyReservationStore(restartedDataSource, TimeProvider.System);
        var recovered = await restartedStore.FindAsync(targetId, request.IdempotencyKey, CancellationToken.None);
        var replay = await restartedStore.ReserveAsync(request, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.True(recovered.Replayed);
        Assert.Equal(first.ReservationId, recovered.ReservationId);
        Assert.Equal(first.RequestFingerprint, recovered.RequestFingerprint);
        Assert.Equal(first.ObjectUid, recovered.ObjectUid);
        Assert.Equal(first.ResourceVersion, recovered.ResourceVersion);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ReservationId, replay.ReservationId);
        Assert.Equal(first.RequestFingerprint, replay.RequestFingerprint);
        Assert.Equal(first.SourceEpoch, replay.SourceEpoch);
        Assert.Equal(first.Generation, replay.Generation);
        Assert.Equal(first.ObjectUid, replay.ObjectUid);
        Assert.Equal(first.ResourceVersion, replay.ResourceVersion);
        Assert.Equal(first.Policy.IntentHash, replay.Policy.IntentHash);
        Assert.Equal(first.Policy.SourceHash, replay.Policy.SourceHash);
        Assert.Equal(
            first.Policy.Destinations.Select(destination => (destination.Host, destination.Port)),
            replay.Policy.Destinations.Select(destination => (destination.Host, destination.Port)));

        var refreshedRequest = request with
        {
            IdempotencyKey = "apply-retry",
            ResourceVersion = "resource-version-2",
        };
        var refreshed = await store.ReserveAsync(refreshedRequest, CancellationToken.None);
        var latest = await restartedStore.FindLatestAsync(targetId, CancellationToken.None);
        Assert.NotEqual(first.ReservationId, refreshed.ReservationId);
        Assert.Equal(first.SourceEpoch, refreshed.SourceEpoch);
        Assert.Equal(first.Generation, refreshed.Generation);
        Assert.Equal(first.Policy.IntentHash, refreshed.Policy.IntentHash);
        Assert.Equal("resource-version-2", refreshed.ResourceVersion);
        Assert.Equal(refreshed.ReservationId, latest?.ReservationId);

        await using var conflictingInsert = fixture.DataSource.CreateCommand(
            """
            INSERT INTO environment.platform_policy_apply_reservations
                (reservation_id, target_id, idempotency_key, request_fingerprint,
                 source_epoch, generation, source_id, source_revision, source_hash, intent_hash,
                 object_uid, resource_version, provider_request_json, created_at)
            SELECT @new_id, target_id, @new_key, @fingerprint,
                   source_epoch, generation, source_id, source_revision, @source_hash, intent_hash,
                   object_uid, resource_version, provider_request_json, created_at
            FROM environment.platform_policy_apply_reservations
            WHERE reservation_id = @id
            """);
        conflictingInsert.Parameters.AddWithValue("new_id", Guid.NewGuid());
        conflictingInsert.Parameters.AddWithValue("new_key", "apply-conflicting-content");
        conflictingInsert.Parameters.AddWithValue("fingerprint", PlatformEgressFixtures.Hash("fingerprint"));
        conflictingInsert.Parameters.AddWithValue("source_hash", PlatformEgressFixtures.Hash("different-source"));
        conflictingInsert.Parameters.AddWithValue("id", first.ReservationId);
        var contentConflict = await Assert.ThrowsAsync<PostgresException>(() => conflictingInsert.ExecuteNonQueryAsync());
        Assert.Equal("23514", contentConflict.SqlState);

        var idempotencyConflict = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            restartedStore.ReserveAsync(
                request with { Policy = Policy(1, "different-source") },
                CancellationToken.None));
        Assert.Equal("idempotency_conflict", idempotencyConflict.Code);

        var reusedVersion = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            restartedStore.ReserveAsync(
                request with
                {
                    IdempotencyKey = "apply-different-key",
                    Policy = Policy(1, "different-source"),
                },
                CancellationToken.None));
        Assert.Equal("source_epoch_conflict", reusedVersion.Code);

        await using var update = fixture.DataSource.CreateCommand(
            "UPDATE environment.platform_policy_apply_reservations SET source_hash = @hash WHERE reservation_id = @id");
        update.Parameters.AddWithValue("hash", PlatformEgressFixtures.Hash("changed"));
        update.Parameters.AddWithValue("id", first.ReservationId);
        var immutableError = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("55000", immutableError.SqlState);
        await using var truncate = fixture.DataSource.CreateCommand(
            "TRUNCATE environment.platform_policy_apply_reservations");
        var truncateError = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Equal("55000", truncateError.SqlState);
    }

    [Fact]
    public async Task ConcurrentDuplicateReservationsReturnOneDurableRequest()
    {
        var targetId = "test-" + Guid.NewGuid().ToString("N");
        var request = Request(targetId, "apply-concurrent", sourceEpoch: 1, generation: 1);
        var store = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.ReserveAsync(request, CancellationToken.None)));

        Assert.Single(results, result => !result.Replayed);
        Assert.All(results, result => Assert.Equal(results[0].ReservationId, result.ReservationId));
        Assert.Equal(3, results.Count(result => result.Replayed));
    }

    [Fact]
    public async Task ReservationRejectsMalformedDestinationsAsInvalidInput()
    {
        var targetId = "test-" + Guid.NewGuid().ToString("N");
        var request = Request(targetId, "invalid-destination", sourceEpoch: 1, generation: 1);
        var store = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        var malformedPolicies = new[]
        {
            request.Policy with { Destinations = request.Policy.Destinations.SetItem(0, null!) },
            request.Policy with { Destinations = [new PlatformEgressDestination("bad host", 443)] },
            UnorderedPolicy(),
            DuplicateDestinationPolicy(),
        };

        foreach (var policy in malformedPolicies)
        {
            var invalid = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
                store.ReserveAsync(request with { Policy = policy }, CancellationToken.None));
            Assert.Equal("invalid_apply_reservation", invalid.Code);
        }

        static PlatformEgressPolicy UnorderedPolicy()
        {
            var canonical = PlatformEgressPolicy.Create(
                1,
                PlatformEgressMode.Allowlist,
                [new PlatformEgressDestination("a.example.com", 443), new PlatformEgressDestination("b.example.com", 443)],
                PlatformEgressFixtures.Hash("unordered"));
            return canonical with { Destinations = [canonical.Destinations[1], canonical.Destinations[0]] };
        }

        static PlatformEgressPolicy DuplicateDestinationPolicy()
        {
            var canonical = PlatformEgressPolicy.Create(
                1,
                PlatformEgressMode.Allowlist,
                [new PlatformEgressDestination("a.example.com", 443)],
                PlatformEgressFixtures.Hash("duplicate"));
            return canonical with { Destinations = canonical.Destinations.Add(canonical.Destinations[0]) };
        }
    }

    [Fact]
    public async Task ReservationRejectsStaleEpochAndChangedPolicyWithinAnEpoch()
    {
        var targetId = "test-" + Guid.NewGuid().ToString("N");
        var store = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        await store.ReserveAsync(Request(targetId, "epoch-1", sourceEpoch: 1, generation: 1), CancellationToken.None);
        await store.ReserveAsync(Request(targetId, "epoch-2", sourceEpoch: 2, generation: 2), CancellationToken.None);

        var stale = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            store.ReserveAsync(
                Request(targetId, "stale-epoch", sourceEpoch: 1, generation: 3),
                CancellationToken.None));
        Assert.Equal("stale_source_epoch", stale.Code);

        var changedPolicy = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            store.ReserveAsync(
                Request(targetId, "changed-policy", sourceEpoch: 2, generation: 3) with
                {
                    Policy = Policy(3, "different-source"),
                },
                CancellationToken.None));
        Assert.Equal("source_epoch_conflict", changedPolicy.Code);
    }

    private static PlatformEgressApplyReservationRequest Request(
        string targetId,
        string idempotencyKey,
        long sourceEpoch,
        long generation) =>
        new(
            targetId,
            idempotencyKey,
            sourceEpoch,
            sourceEpoch,
            "projects-config:platform:default:test",
            "object-" + Guid.NewGuid().ToString("N"),
            "resource-version-1",
            Policy(generation, "source"));

    private static PlatformEgressPolicy Policy(long generation, string source) =>
        PlatformEgressPolicy.Create(
            generation,
            PlatformEgressMode.Allowlist,
            [new PlatformEgressDestination("api.example.com", 443)],
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
}
