using System.Security.Cryptography;
using System.Text;
using System.Data;
using System.Net;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformPolicyProofPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task HoldPersistsAcrossRestartAndRejectsCompetingTargetBinding()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var request = await HoldAsync("target-" + Guid.NewGuid().ToString("N"));

        var first = await store.AcquireHoldAsync(request, CancellationToken.None);
        var restarted = new PlatformPolicyProofStore(
            fixture.CreateDataSource("policy-proof-restart"), TimeProvider.System);
        var replay = await restarted.AcquireHoldAsync(request, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.HoldId, replay.HoldId);
        Assert.Equal(first.RequestFingerprint, replay.RequestFingerprint);

        var conflict = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.AcquireHoldAsync(request with { HoldId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", conflict.Code);

        var aliasRequest = await HoldAsync("alias-" + Guid.NewGuid().ToString("N"));
        var alias = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.AcquireHoldAsync(aliasRequest with
            {
                PhysicalTarget = request.PhysicalTarget,
                NamespaceUid = request.NamespaceUid,
            }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", alias.Code);
    }

    [Fact]
    public async Task ReceiptReplayIsExactAndRowsAreAppendOnly()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var holdRequest = await HoldAsync("target-" + Guid.NewGuid().ToString("N"));
        var hold = await store.AcquireHoldAsync(holdRequest, CancellationToken.None);
        var receipt = Receipt(hold);

        var first = await store.PersistReceiptAsync(receipt, CancellationToken.None);
        var replay = await store.PersistReceiptAsync(receipt, CancellationToken.None);
        var read = await store.ReadCurrentReceiptAsync(hold.TargetId, hold.HoldId, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ReceiptId, replay.ReceiptId);
        Assert.Equal(first.SnapshotId, read?.SnapshotId);

        var changed = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.PersistReceiptAsync(receipt with
            {
                ReceiptId = Guid.NewGuid(),
                SnapshotId = Hash("changed"),
            }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", changed.Code);

        await using var update = fixture.DataSource.CreateCommand(
            "UPDATE environment.platform_policy_proof_receipts SET source_hash=@hash WHERE receipt_id=@id");
        update.Parameters.AddWithValue("hash", Hash("tampered"));
        update.Parameters.AddWithValue("id", first.ReceiptId);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("55000", immutable.SqlState);

        await using var mismatch = fixture.DataSource.CreateCommand(
            """
            INSERT INTO environment.platform_policy_proof_receipts
                (receipt_id,hold_id,target_id,reservation_id,source_epoch,source_revision,
                 source_id,source_hash,intent_hash,compiler_id,evaluator_version,ceiling_version,
                 profile_digest,guard_digest,snapshot_id,spec_digest,post_write_resource_version,
                 object_uid,policy_generation,snapshot_json,request_fingerprint,evidence_at)
            SELECT @new_id,hold_id,@wrong_target,reservation_id,source_epoch,source_revision,
                   source_id,source_hash,intent_hash,compiler_id,evaluator_version,ceiling_version,
                   profile_digest,guard_digest,snapshot_id,spec_digest,post_write_resource_version,
                   object_uid,policy_generation,snapshot_json,request_fingerprint,evidence_at
            FROM environment.platform_policy_proof_receipts
            WHERE receipt_id=@id
            """);
        mismatch.Parameters.AddWithValue("new_id", Guid.NewGuid());
        mismatch.Parameters.AddWithValue("wrong_target", "wrong-target");
        mismatch.Parameters.AddWithValue("id", first.ReceiptId);
        var binding = await Assert.ThrowsAsync<PostgresException>(() => mismatch.ExecuteNonQueryAsync());
        Assert.Equal("23514", binding.SqlState);
    }

    [Fact]
    public async Task CommitGateRequiresEveryTargetAtTheFrozenRegistryRevision()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var firstHold = await HoldAsync("target-a-" + Guid.NewGuid().ToString("N"));
        var secondHold = (await HoldAsync("target-b-" + Guid.NewGuid().ToString("N"))) with
        {
            PhysicalTarget = "https://cluster-b",
            NamespaceUid = "namespace-" + Guid.NewGuid().ToString("N"),
            RegistryRevision = firstHold.RegistryRevision,
            PreparationId = firstHold.PreparationId,
        };
        var first = await store.AcquireHoldAsync(firstHold, CancellationToken.None);
        await store.PersistReceiptAsync(Receipt(first), CancellationToken.None);

        var missing = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.RequireAllTargetsAsync(
                firstHold.RegistryRevision,
                firstHold.PreparationId,
                [firstHold.TargetId, secondHold.TargetId],
                CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", missing.Code);

        var second = await store.AcquireHoldAsync(secondHold, CancellationToken.None);
        await store.PersistReceiptAsync(Receipt(second), CancellationToken.None);
        await store.RequireAllTargetsAsync(
            firstHold.RegistryRevision,
            firstHold.PreparationId,
            [firstHold.TargetId, secondHold.TargetId],
            CancellationToken.None);

        var stale = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.RequireAllTargetsAsync(
                firstHold.RegistryRevision + 1,
                firstHold.PreparationId,
                [firstHold.TargetId, secondHold.TargetId],
                CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", stale.Code);
    }

    [Fact]
    public async Task ConcurrentExactHoldsReturnOneDurableBinding()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var request = await HoldAsync("target-" + Guid.NewGuid().ToString("N"));

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.AcquireHoldAsync(request, CancellationToken.None)));

        Assert.Single(results, result => !result.Replayed);
        Assert.All(results, result => Assert.Equal(results[0].HoldId, result.HoldId));
    }

    [Fact]
    public async Task DifferentSuppliedPhysicalAliasesAreNotRecognizedAsOneCluster()
    {
        var first = await HoldAsync("logical-a-" + Guid.NewGuid().ToString("N"));
        var second = (await HoldAsync("logical-b-" + Guid.NewGuid().ToString("N"))) with
        {
            PhysicalTarget = "https://other-origin",
            NamespaceUid = first.NamespaceUid,
        };
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        await store.AcquireHoldAsync(first, CancellationToken.None);
        var accepted = await store.AcquireHoldAsync(second, CancellationToken.None);
        Assert.Equal(second.HoldId, accepted.HoldId);
    }

    [Fact]
    public async Task MissingTargetSetCannotCommitPlatformPolicy()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.RequireAllTargetsAsync(1, "preparation", [], CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", error.Code);
    }

    [Theory]
    [InlineData("mismatch", "policy_fence_conflict")]
    [InlineData("unavailable", "policy_fence_unavailable")]
    [InlineData("namespace", "policy_fence_conflict")]
    public async Task CoordinatorRejectsProviderIdentityBeforeHoldOrKubernetes(
        string scenario, string expectedCode)
    {
        var target = scenario == "namespace"
            ? "test:agentweaver:" + CiliumPlatformEgressProvider.PolicyName
            : "identity-" + Guid.NewGuid().ToString("N");
        var request = await HoldAsync(target);
        var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        var reservation = await reservations.FindLatestAsync(target, CancellationToken.None);
        Assert.NotNull(reservation);
        ICiliumPolicyResourceStore resourceStore = scenario == "unavailable"
            ? new UnavailableIdentityStore()
            : new FakeCiliumPolicyStore();
        var provider = PlatformEgressFixtures.Cilium(resourceStore, exactHost: true, reservationReader: reservations);
        var api = new CountingApi();
        using var http = new HttpClient(api) { BaseAddress = new Uri("https://kubernetes.test/") };
        var coordinator = new PlatformPolicyProofCoordinator(
            new KubernetesPlatformPolicyInventoryReader(http), provider,
            new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System), TimeProvider.System);
        var preparation = new PlatformPolicyProofPreparation(
            request.PreparationId, request.RegistryRevision, reservation.SourceId, reservation.SourceRevision,
            reservation.Policy.SourceHash, PlatformEgressAcsCompiler.CompilerVersion,
            reservation.Policy.Destinations);
        var frozen = new PlatformPolicyProofFrozenTarget(
            request.HoldId, target, request.PhysicalTarget,
            scenario == "namespace" ? "other" : request.Namespace, request.NamespaceUid,
            request.SealEpoch, request.GuardDigest, request.ProfileDigest);

        var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, api.Calls);
        if (resourceStore is FakeCiliumPolicyStore fake)
        {
            Assert.Equal(0, fake.Reads);
            Assert.Equal(0, fake.Writes);
        }
        await using var query = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE target_id=@target");
        query.Parameters.AddWithValue("target", target);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedPreflightKeepsHoldAndRejectsSupersession()
    {
        var api = new CountingApi();
        using var http = new HttpClient(api)
        {
            BaseAddress = new Uri("https://cluster-" + Guid.NewGuid().ToString("N") + ".test/"),
        };
        var resourceStore = new KubernetesCiliumPolicyResourceStore(http);
        var target = resourceStore.GetTargetId("agentweaver", CiliumPlatformEgressProvider.PolicyName);
        var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        var reservation = await reservations.ReserveAsync(Request(target, "initial", 1), CancellationToken.None);
        var proof = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var provider = PlatformEgressFixtures.Cilium(resourceStore, exactHost: true, reservationReader: reservations);
        var coordinator = new PlatformPolicyProofCoordinator(
            new KubernetesPlatformPolicyInventoryReader(http), provider, proof, TimeProvider.System);
        var frozen = new PlatformPolicyProofFrozenTarget(
            Guid.NewGuid(), target, "cluster", "agentweaver", Guid.NewGuid().ToString("N"),
            1, Hash("guard"), Hash("profile"));
        var preparation = new PlatformPolicyProofPreparation(
            "preparation", 1, reservation.SourceId, reservation.SourceRevision, reservation.Policy.SourceHash,
            PlatformEgressAcsCompiler.CompilerVersion, reservation.Policy.Destinations);
        var failure = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            coordinator.ProveAsync(preparation, frozen, reservation, CancellationToken.None));
        Assert.Equal("policy_inventory_unavailable", failure.Code);
        Assert.True(api.Calls > 0);
        Assert.Equal(0, api.PutCalls);
        var blocked = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(Request(target, "new", 2), CancellationToken.None));
        Assert.Equal("policy_fence_conflict", blocked.Code);
        var replay = await proof.AcquireHoldAsync(
            new(frozen.HoldId, frozen.TargetId, frozen.PhysicalTarget, frozen.Namespace, frozen.NamespaceUid,
                frozen.SealEpoch, frozen.GuardDigest, frozen.ProfileDigest, preparation.RegistryRevision,
                preparation.PreparationId, reservation.ReservationId), CancellationToken.None);
        Assert.True(replay.Replayed);
    }

    [Theory]
    [InlineData("uid", "stale_object_uid")]
    [InlineData("version", "stale_resource_version")]
    [InlineData("readback", "readback_mismatch")]
    public async Task FailedReservedProviderWriteRetainsHold(string failure, string expectedMessage)
    {
        var target = "provider-" + Guid.NewGuid().ToString("N");
        var resourceStore = new ControlledPolicyStore(target, failure == "readback");
        var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        var request = Request(target, "initial", 1) with
        {
            ObjectUid = failure == "uid" ? "wrong-uid" : resourceStore.Current.Metadata.Uid!,
            ResourceVersion = failure == "version" ? "wrong-version" : resourceStore.Current.Metadata.ResourceVersion!,
        };
        var reservation = await reservations.ReserveAsync(request, CancellationToken.None);
        var hold = new PlatformPolicyProofHoldRequest(
            Guid.NewGuid(), target, "cluster", "agentweaver", Guid.NewGuid().ToString("N"),
            1, Hash("guard"), Hash("profile"), 1, "preparation", reservation.ReservationId);
        var proof = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        await proof.AcquireHoldAsync(hold, CancellationToken.None);
        var provider = PlatformEgressFixtures.Cilium(resourceStore, exactHost: true, reservationReader: reservations);
        var status = await provider.ApplyReservedAsync(reservation, CancellationToken.None);
        Assert.Equal(expectedMessage, status.Message);
        Assert.Equal(failure == "readback" ? 1 : 0, resourceStore.Writes);
        var blocked = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(Request(target, "new", 2), CancellationToken.None));
        Assert.Equal("policy_fence_conflict", blocked.Code);
        Assert.True((await proof.AcquireHoldAsync(hold, CancellationToken.None)).Replayed);
    }

    [Fact]
    public async Task HoldRequiresLatestReservationAndBlocksNewRowsButAllowsExactReplay()
    {
        var target = "target-" + Guid.NewGuid().ToString("N");
        var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
        var proof = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var firstHold = await HoldAsync(target);
        var first = await reservations.FindLatestAsync(target, CancellationToken.None);
        Assert.NotNull(first);
        var newer = Request(target, "newer", 2);
        var second = await reservations.ReserveAsync(newer, CancellationToken.None);
        var stale = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            proof.AcquireHoldAsync(firstHold, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", stale.Code);
        var foreign = await HoldAsync("foreign-" + Guid.NewGuid().ToString("N"));
        var foreignError = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            proof.AcquireHoldAsync(firstHold with { ReservationId = foreign.ReservationId }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", foreignError.Code);
        var missing = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            proof.AcquireHoldAsync(firstHold with { ReservationId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", missing.Code);

        var bound = await proof.AcquireHoldAsync(
            firstHold with { ReservationId = second.ReservationId }, CancellationToken.None);
        await proof.PersistReceiptAsync(Receipt(bound), CancellationToken.None);
        await proof.RequireAllTargetsAsync(bound.RegistryRevision, bound.PreparationId, [target], CancellationToken.None);
        Assert.Equal(second.ReservationId, (await reservations.ReserveAsync(newer, CancellationToken.None)).ReservationId);
        var oldReplay = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(Request(target, "initial", 1), CancellationToken.None));
        Assert.Equal("policy_fence_conflict", oldReplay.Code);
        var changed = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(newer with { ResourceVersion = "changed" }, CancellationToken.None));
        Assert.Equal("idempotency_conflict", changed.Code);
        var next = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(Request(target, "old-generation", 1), CancellationToken.None));
        Assert.Equal("policy_fence_conflict", next.Code);
        var changedContent = Request(target, "changed-content", 2) with
        {
            Policy = PlatformEgressPolicy.Create(2, PlatformEgressMode.Allowlist,
                [new PlatformEgressDestination("api.example.com", 443)], Hash("different")),
        };
        var heldConflict = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(changedContent, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", heldConflict.Code);
    }

    [Fact]
    public async Task RestrictedInsertChecksHoldBeforeContentAndHigherIsolationBeforeLock()
    {
        var target = "target-" + Guid.NewGuid().ToString("N");
        var hold = await HoldAsync(target);
        var role = "fence_" + Guid.NewGuid().ToString("N");
        await using (var setup = fixture.DataSource.CreateCommand(
            $"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT USAGE ON SCHEMA environment TO "{role}";
            GRANT SELECT, INSERT ON environment.platform_policy_apply_reservations,
                environment.platform_policy_proof_holds TO "{role}";
            REVOKE EXECUTE ON FUNCTION environment.enforce_platform_policy_reservation_hold_fence(),
                environment.enforce_platform_policy_hold_reservation_fence(),
                environment.enforce_platform_policy_apply_reservation_identity() FROM PUBLIC;
            """))
            await setup.ExecuteNonQueryAsync();
        await using (var grants = fixture.DataSource.CreateCommand(
            """
            SELECT NOT pg_catalog.has_schema_privilege(@role, 'environment', 'CREATE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_apply_reservations', 'UPDATE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_apply_reservations', 'DELETE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_apply_reservations', 'TRUNCATE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_proof_holds', 'UPDATE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_proof_holds', 'DELETE')
               AND NOT pg_catalog.has_table_privilege(@role, 'environment.platform_policy_proof_holds', 'TRUNCATE')
               AND NOT pg_catalog.has_function_privilege(@role,
                   'environment.enforce_platform_policy_reservation_hold_fence()', 'EXECUTE')
            """))
        {
            grants.Parameters.AddWithValue("role", role);
            Assert.Equal(true, await grants.ExecuteScalarAsync());
        }

        async Task<PostgresException> InsertAsync(IsolationLevel isolation)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(isolation);
            await using var command = new NpgsqlCommand(
                $"""
                SET LOCAL ROLE "{role}";
                INSERT INTO environment.platform_policy_apply_reservations
                    (reservation_id,target_id,idempotency_key,request_fingerprint,source_epoch,generation,
                     source_id,source_revision,source_hash,intent_hash,object_uid,resource_version,
                     provider_request_json,created_at)
                SELECT @new_id,target_id,@key,request_fingerprint,source_epoch,generation,
                       source_id,source_revision,@hash,intent_hash,object_uid,resource_version,
                       provider_request_json,created_at
                FROM environment.platform_policy_apply_reservations WHERE reservation_id=@id;
                """, connection, transaction);
            command.Parameters.AddWithValue("new_id", Guid.NewGuid());
            command.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("hash", Hash("changed"));
            command.Parameters.AddWithValue("id", hold.ReservationId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(timeout.Token));
        }

        var unheld = await InsertAsync(IsolationLevel.ReadCommitted);
        Assert.Equal("23514", unheld.SqlState);
        Assert.Null(unheld.ConstraintName);
        await new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System)
            .AcquireHoldAsync(hold, CancellationToken.None);
        var held = await InsertAsync(IsolationLevel.ReadCommitted);
        Assert.Equal("23514", held.SqlState);
        Assert.Equal("ck_platform_policy_reservation_held", held.ConstraintName);

        await using var locker = await fixture.DataSource.OpenConnectionAsync();
        await using var locked = await locker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target, 0))", locker, locked))
        {
            command.Parameters.AddWithValue("target", target);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal("PF001", (await InsertAsync(IsolationLevel.RepeatableRead)).SqlState);
        Assert.Equal("PF001", (await InsertAsync(IsolationLevel.Serializable)).SqlState);
        await locked.RollbackAsync();

        async Task<PostgresException> InsertHoldAsync(IsolationLevel isolation)
        {
            await using var holdConnection = await fixture.DataSource.OpenConnectionAsync();
            await using var holdTransaction = await holdConnection.BeginTransactionAsync(isolation);
            await using var insert = new NpgsqlCommand(
                $"""
                SET LOCAL ROLE "{role}";
                INSERT INTO environment.platform_policy_proof_holds
                    (hold_id,target_id,physical_target,namespace,namespace_uid,seal_epoch,guard_digest,
                     profile_digest,registry_revision,preparation_id,reservation_id,request_fingerprint,created_at)
                SELECT @id,@foreign_target,physical_target,namespace,@uid,seal_epoch,guard_digest,
                       profile_digest,registry_revision,preparation_id,reservation_id,request_fingerprint,created_at
                FROM environment.platform_policy_proof_holds WHERE hold_id=@original;
                """, holdConnection, holdTransaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("foreign_target", "foreign-" + Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("uid", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("original", hold.HoldId);
            return await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        }
        var foreignHold = await InsertHoldAsync(IsolationLevel.ReadCommitted);
        Assert.Equal("23514", foreignHold.SqlState);
        Assert.Equal("ck_platform_policy_hold_current_reservation", foreignHold.ConstraintName);
        Assert.Equal("PF001", (await InsertHoldAsync(IsolationLevel.RepeatableRead)).SqlState);
        Assert.Equal("PF001", (await InsertHoldAsync(IsolationLevel.Serializable)).SqlState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedStoreSeesTheCommittedWinner(bool reservationWins)
    {
        var target = "race-" + Guid.NewGuid().ToString("N");
        var hold = await HoldAsync(target);
        var application = "fence-race-" + Guid.NewGuid().ToString("N");
        var role = "fence_" + Guid.NewGuid().ToString("N");
        await using (var setup = fixture.DataSource.CreateCommand(
            $"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT USAGE ON SCHEMA environment TO "{role}";
            GRANT SELECT, INSERT ON environment.platform_policy_apply_reservations,
                environment.platform_policy_proof_holds TO "{role}";
            """))
            await setup.ExecuteNonQueryAsync();
        await using var waitingDataSource = fixture.CreateDataSource(application);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target, 0))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("target", target);
            await acquire.ExecuteNonQueryAsync();
        }
        await using (var restrict = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\"", connection, transaction))
            await restrict.ExecuteNonQueryAsync();

        Task waiter = reservationWins
            ? new PlatformPolicyProofStore(waitingDataSource, TimeProvider.System)
                .AcquireHoldAsync(hold, CancellationToken.None)
            : new PlatformEgressApplyReservationStore(waitingDataSource, TimeProvider.System)
                .ReserveAsync(Request(target, "new", 2), CancellationToken.None);
        await WaitForAdvisoryWaitAsync(application);

        await using var insert = reservationWins
            ? DirectReservation(connection, transaction, hold)
            : DirectHold(connection, transaction, hold);
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        if (reservationWins)
        {
            var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() => waiter);
            Assert.Equal("policy_fence_conflict", error.Code);
        }
        else
        {
            var error = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() => waiter);
            Assert.Equal("policy_fence_conflict", error.Code);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedRestrictedInsertSeesTheCommittedWinner(bool reservationWins)
    {
        var target = "direct-race-" + Guid.NewGuid().ToString("N");
        var hold = await HoldAsync(target);
        var role = "fence_" + Guid.NewGuid().ToString("N");
        var application = "fence-direct-" + Guid.NewGuid().ToString("N");
        await using (var setup = fixture.DataSource.CreateCommand(
            $"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT USAGE ON SCHEMA environment TO "{role}";
            GRANT SELECT, INSERT ON environment.platform_policy_apply_reservations,
                environment.platform_policy_proof_holds TO "{role}";
            """))
            await setup.ExecuteNonQueryAsync();
        await using var winnerConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var winner = await winnerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var waitingDataSource = fixture.CreateDataSource(application);
        await using var waitingConnection = await waitingDataSource.OpenConnectionAsync();
        await using var waitingTransaction = await waitingConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var restrict = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\"", waitingConnection, waitingTransaction))
            await restrict.ExecuteNonQueryAsync();

        await using var winnerCommand = reservationWins
            ? DirectReservation(winnerConnection, winner, hold)
            : DirectHold(winnerConnection, winner, hold);
        await winnerCommand.ExecuteNonQueryAsync();
        await using var waitingCommand = reservationWins
            ? DirectHold(waitingConnection, waitingTransaction, hold)
            : DirectReservation(waitingConnection, waitingTransaction, hold);
        var waiting = waitingCommand.ExecuteNonQueryAsync();
        await WaitForAdvisoryWaitAsync(application);
        await winner.CommitAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => waiting);
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(reservationWins
            ? "ck_platform_policy_hold_current_reservation"
            : "ck_platform_policy_reservation_held", error.ConstraintName);
    }

    private static NpgsqlCommand DirectReservation(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PlatformPolicyProofHoldRequest hold)
    {
        var command = new NpgsqlCommand(
            """
            INSERT INTO environment.platform_policy_apply_reservations
                (reservation_id,target_id,idempotency_key,request_fingerprint,source_epoch,generation,
                 source_id,source_revision,source_hash,intent_hash,object_uid,resource_version,
                 provider_request_json,created_at)
            SELECT @new_id,target_id,@key,request_fingerprint,source_epoch+100,generation+1,
                   source_id,source_revision,source_hash,intent_hash,object_uid,resource_version,
                   provider_request_json,created_at
            FROM environment.platform_policy_apply_reservations WHERE reservation_id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("new_id", Guid.NewGuid());
        command.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("id", hold.ReservationId);
        return command;
    }

    private static NpgsqlCommand DirectHold(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PlatformPolicyProofHoldRequest hold)
    {
        var command = new NpgsqlCommand(
            """
            INSERT INTO environment.platform_policy_proof_holds
                (hold_id,target_id,physical_target,namespace,namespace_uid,seal_epoch,guard_digest,
                 profile_digest,registry_revision,preparation_id,reservation_id,request_fingerprint,created_at)
            VALUES (@id,@target,@physical,@namespace,@uid,@epoch,@guard,@profile,@revision,@preparation,
                    @reservation,@fingerprint,now())
            """, connection, transaction);
        command.Parameters.AddWithValue("id", hold.HoldId);
        command.Parameters.AddWithValue("target", hold.TargetId);
        command.Parameters.AddWithValue("physical", hold.PhysicalTarget);
        command.Parameters.AddWithValue("namespace", hold.Namespace);
        command.Parameters.AddWithValue("uid", hold.NamespaceUid);
        command.Parameters.AddWithValue("epoch", hold.SealEpoch);
        command.Parameters.AddWithValue("guard", hold.GuardDigest);
        command.Parameters.AddWithValue("profile", hold.ProfileDigest);
        command.Parameters.AddWithValue("revision", hold.RegistryRevision);
        command.Parameters.AddWithValue("preparation", hold.PreparationId);
        command.Parameters.AddWithValue("reservation", hold.ReservationId);
        command.Parameters.AddWithValue("fingerprint", Hash("direct"));
        return command;
    }

    [Fact]
    public async Task CancellationRollsBackBlockedReservationAndOtherTargetsProceed()
    {
        var target = "cancel-" + Guid.NewGuid().ToString("N");
        await HoldAsync(target);
        var other = "independent-" + Guid.NewGuid().ToString("N");
        var application = "fence-cancel-" + Guid.NewGuid().ToString("N");
        await using var waitingDataSource = fixture.CreateDataSource(application);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target, 0))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("target", target);
            await acquire.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var store = new PlatformEgressApplyReservationStore(waitingDataSource, TimeProvider.System);
        var request = Request(target, "new", 2);
        var waiting = store.ReserveAsync(request, cancellation.Token);
        await WaitForAdvisoryWaitAsync(application);
        var independent = await store.ReserveAsync(Request(other, "new", 1), CancellationToken.None);
        Assert.Equal(other, independent.TargetId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await transaction.RollbackAsync();
        Assert.Null(await store.FindAsync(target, "new", CancellationToken.None));
        Assert.False((await store.ReserveAsync(request, CancellationToken.None)).Replayed);
    }

    [Fact]
    public async Task CancelledHoldLeavesNoRowAndCanRetry()
    {
        var target = "cancel-hold-" + Guid.NewGuid().ToString("N");
        var hold = await HoldAsync(target);
        var application = "fence-cancel-hold-" + Guid.NewGuid().ToString("N");
        await using var waitingDataSource = fixture.CreateDataSource(application);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target, 0))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("target", target);
            await acquire.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var store = new PlatformPolicyProofStore(waitingDataSource, TimeProvider.System);
        var waiting = store.AcquireHoldAsync(hold, cancellation.Token);
        await WaitForAdvisoryWaitAsync(application);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await transaction.RollbackAsync();
        await using var query = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM environment.platform_policy_proof_holds WHERE target_id=@target");
        query.Parameters.AddWithValue("target", target);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
        Assert.False((await store.AcquireHoldAsync(hold, CancellationToken.None)).Replayed);
    }

    [Fact]
    public async Task DatabaseErrorsAreSanitizedAndCancellationPropagates()
    {
        var target = "unavailable-" + Guid.NewGuid().ToString("N");
        var hold = await HoldAsync(target);
        var missingDatabase = "missing_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(fixture.DataSource.ConnectionString)
        {
            Database = missingDatabase,
        };
        await using var missing = NpgsqlDataSource.Create(builder.ConnectionString);
        var proof = new PlatformPolicyProofStore(missing, TimeProvider.System);
        var reservations = new PlatformEgressApplyReservationStore(missing, TimeProvider.System);
        var holdError = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            proof.AcquireHoldAsync(hold, CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", holdError.Code);
        Assert.DoesNotContain(missingDatabase, holdError.Message);
        var reservationError = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
            reservations.ReserveAsync(Request(target, "new", 2), CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", reservationError.Code);
        Assert.DoesNotContain(missingDatabase, reservationError.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proof.AcquireHoldAsync(hold, cancellation.Token));
    }

    [Fact]
    public async Task ReservationMapsOnlyTheNamedHeldConstraintToFenceConflict()
    {
        var id = Guid.NewGuid().ToString("N");
        var target = "mapping-" + id;
        var function = "test_fence_" + id;
        var trigger = "aaa_test_fence_" + id;
        await using (var setup = fixture.DataSource.CreateCommand(
            $"""
            CREATE FUNCTION environment."{function}"() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.target_id = TG_ARGV[0] THEN
                    IF NEW.idempotency_key = 'held' THEN
                        RAISE EXCEPTION 'private detail' USING ERRCODE = '23514',
                            CONSTRAINT = 'ck_platform_policy_reservation_held';
                    ELSE
                        RAISE EXCEPTION 'private detail' USING ERRCODE = '23514',
                            CONSTRAINT = 'unrelated_constraint';
                    END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER "{trigger}" BEFORE INSERT ON environment.platform_policy_apply_reservations
                FOR EACH ROW EXECUTE FUNCTION environment."{function}"('{target}');
            """))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var store = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
            var held = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
                store.ReserveAsync(Request(target, "held", 1), CancellationToken.None));
            Assert.Equal("policy_fence_conflict", held.Code);
            var unrelated = await Assert.ThrowsAsync<PlatformEgressApplyReservationException>(() =>
                store.ReserveAsync(Request(target, "unrelated", 1), CancellationToken.None));
            Assert.Equal("policy_fence_unavailable", unrelated.Code);
            Assert.DoesNotContain("private detail", unrelated.Message);
        }
        finally
        {
            await using var cleanup = fixture.DataSource.CreateCommand(
                $"""
                DROP TRIGGER "{trigger}" ON environment.platform_policy_apply_reservations;
                DROP FUNCTION environment."{function}"();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task WaitForAdvisoryWaitAsync(string application)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = fixture.DataSource.CreateCommand(
                """
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity
                               WHERE application_name=@application AND wait_event='advisory')
                """);
            command.Parameters.AddWithValue("application", application);
            if (await command.ExecuteScalarAsync(timeout.Token) is true)
                return;
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static PlatformEgressApplyReservationRequest Request(string targetId, string key, long generation)
    {
        var policy = PlatformEgressPolicy.Create(generation, PlatformEgressMode.Allowlist,
            [new PlatformEgressDestination("api.example.com", 443)], Hash("source"));
        return new(targetId, key, generation, generation, "source", "uid", "rv", policy);
    }

    private async Task<PlatformPolicyProofHoldRequest> HoldAsync(string targetId)
    {
        var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
            .ReserveAsync(Request(targetId, "initial", 1), CancellationToken.None);
        return new(
            Guid.NewGuid(),
            targetId,
            "https://cluster-a",
            "agentweaver",
            "namespace-" + Guid.NewGuid().ToString("N"),
            1,
            Hash("guard"),
            Hash("profile"),
            10,
            "preparation-" + Guid.NewGuid().ToString("N"),
            reservation.ReservationId);
    }

    private static PlatformPolicyProofReceiptRequest Receipt(PlatformPolicyProofHold hold) =>
        new(
            hold.HoldId,
            hold.HoldId,
            hold.TargetId,
            hold.ReservationId,
            2,
            3,
            "projects-config:platform:default:test",
            Hash("source"),
            Hash("intent"),
            "compiler-v1",
            SystemOnlyPolicySetEvaluation.Version,
            SystemOnlyPolicyCeiling.Version,
            hold.ProfileDigest,
            hold.GuardDigest,
            Hash("snapshot"),
            Hash("spec"),
            "post-write-rv",
            "policy-uid",
            4,
            """{"schemaVersion":1}""",
            DateTimeOffset.UtcNow);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class CountingApi : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int PutCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Put)
                PutCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class UnavailableIdentityStore : ICiliumPolicyResourceStore
    {
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string @namespace, string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string expectedResourceVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public Task DeleteAsync(string @namespace, string name, string expectedResourceVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class ControlledPolicyStore(string target, bool corruptReadback) : ICiliumPolicyResourceStore
    {
        private readonly FakeCiliumPolicyStore inner = new();
        public CiliumNetworkPolicyDocument Current => inner.Current!;
        public int Writes => inner.Writes;
        public string GetTargetId(string @namespace, string name) => target;
        public Task<CiliumNetworkPolicyDocument?> GetAsync(
            string @namespace, string name, CancellationToken cancellationToken) =>
            inner.GetAsync(@namespace, name, cancellationToken);
        public Task<CiliumNetworkPolicyDocument> CreateAsync(
            CiliumNetworkPolicyDocument policy, CancellationToken cancellationToken) =>
            inner.CreateAsync(policy, cancellationToken);
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string expectedResourceVersion, CancellationToken cancellationToken) =>
            ReplaceAsync(policy, policy.Metadata.Uid!, expectedResourceVersion, cancellationToken);
        public async Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string expectedUid, string expectedResourceVersion,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReplaceAsync(policy, expectedUid, expectedResourceVersion, cancellationToken);
            return corruptReadback
                ? result with { Metadata = result.Metadata with { Uid = "changed-uid" } }
                : result;
        }
        public Task DeleteAsync(
            string @namespace, string name, string expectedResourceVersion, CancellationToken cancellationToken) =>
            inner.DeleteAsync(@namespace, name, expectedResourceVersion, cancellationToken);
    }
}
