using System.Security.Cryptography;
using System.Text;
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
        var request = Hold("target-" + Guid.NewGuid().ToString("N"));

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

        var alias = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.AcquireHoldAsync(request with
            {
                TargetId = "alias-" + Guid.NewGuid().ToString("N"),
                HoldId = Guid.NewGuid(),
                ReservationId = Guid.NewGuid(),
            }, CancellationToken.None));
        Assert.Equal("policy_fence_conflict", alias.Code);
    }

    [Fact]
    public async Task ReceiptReplayIsExactAndRowsAreAppendOnly()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var holdRequest = Hold("target-" + Guid.NewGuid().ToString("N"));
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
        var firstHold = Hold("target-a-" + Guid.NewGuid().ToString("N"));
        var secondHold = Hold("target-b-" + Guid.NewGuid().ToString("N")) with
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
        var request = Hold("target-" + Guid.NewGuid().ToString("N"));

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.AcquireHoldAsync(request, CancellationToken.None)));

        Assert.Single(results, result => !result.Replayed);
        Assert.All(results, result => Assert.Equal(results[0].HoldId, result.HoldId));
    }

    [Fact]
    public async Task MissingTargetSetCannotCommitPlatformPolicy()
    {
        var store = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
        var error = await Assert.ThrowsAsync<PlatformPolicyProofException>(() =>
            store.RequireAllTargetsAsync(1, "preparation", [], CancellationToken.None));
        Assert.Equal("policy_fence_unavailable", error.Code);
    }

    private static PlatformPolicyProofHoldRequest Hold(string targetId) =>
        new(
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
            Guid.NewGuid());

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
}
