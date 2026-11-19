using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed record PlatformPolicyProofHoldRequest(
    Guid HoldId,
    string TargetId,
    string PhysicalTarget,
    string Namespace,
    string NamespaceUid,
    long SealEpoch,
    string GuardDigest,
    string ProfileDigest,
    long RegistryRevision,
    string PreparationId,
    Guid ReservationId);

public sealed record PlatformPolicyProofHold(
    Guid HoldId,
    string TargetId,
    string PhysicalTarget,
    string Namespace,
    string NamespaceUid,
    long SealEpoch,
    string GuardDigest,
    string ProfileDigest,
    long RegistryRevision,
    string PreparationId,
    Guid ReservationId,
    string RequestFingerprint,
    DateTimeOffset CreatedAt,
    bool Replayed);

public sealed record PlatformPolicyProofReceiptRequest(
    Guid ReceiptId,
    Guid HoldId,
    string TargetId,
    Guid ReservationId,
    long SourceEpoch,
    long SourceRevision,
    string SourceId,
    string SourceHash,
    string IntentHash,
    string CompilerId,
    string EvaluatorVersion,
    string CeilingVersion,
    string ProfileDigest,
    string GuardDigest,
    string SnapshotId,
    string SpecDigest,
    string PostWriteResourceVersion,
    string ObjectUid,
    long PolicyGeneration,
    string SnapshotJson,
    DateTimeOffset EvidenceAt);

public sealed record PlatformPolicyProofReceipt(
    Guid ReceiptId,
    Guid HoldId,
    string TargetId,
    Guid ReservationId,
    long SourceEpoch,
    long SourceRevision,
    string SourceId,
    string SourceHash,
    string IntentHash,
    string CompilerId,
    string EvaluatorVersion,
    string CeilingVersion,
    string ProfileDigest,
    string GuardDigest,
    string SnapshotId,
    string SpecDigest,
    string PostWriteResourceVersion,
    string ObjectUid,
    long PolicyGeneration,
    string SnapshotJson,
    string RequestFingerprint,
    DateTimeOffset EvidenceAt,
    bool Replayed);

public sealed class PlatformPolicyProofStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
{
    private const string Holds = "\"environment\".\"platform_policy_proof_holds\"";
    private const string Receipts = "\"environment\".\"platform_policy_proof_receipts\"";
    private const int MaxSnapshotBytes = 1_048_576;

    public async Task<PlatformPolicyProofHold> AcquireHoldAsync(
        PlatformPolicyProofHoldRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AcquireHoldCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            (exception.SqlState == PostgresErrorCodes.CheckViolation &&
             exception.ConstraintName == "ck_platform_policy_hold_current_reservation") ||
            (exception.SqlState == PostgresErrorCodes.UniqueViolation &&
             exception.ConstraintName is "PK_platform_policy_proof_holds" or
                "ux_environment_platform_policy_proof_holds_target" or
                "ux_environment_platform_policy_proof_holds_reservation" or
                "ux_environment_platform_policy_proof_holds_physical_namespace"))
        {
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "Another policy proof bound this target or reservation.");
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformPolicyProofHold> AcquireHoldCoreAsync(
        PlatformPolicyProofHoldRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var fingerprint = Fingerprint(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockTargetAsync(connection, transaction, request.TargetId, cancellationToken).ConfigureAwait(false);
        var existing = await ReadHoldAsync(connection, transaction, request.TargetId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.HoldId != request.HoldId || existing.RequestFingerprint != fingerprint)
                throw new PlatformPolicyProofException(
                    "policy_fence_conflict", "The target already has a different durable policy hold.");
        }

        await using var latestQuery = new NpgsqlCommand(
            """
            SELECT reservation_id FROM environment.platform_policy_apply_reservations
            WHERE target_id = @target_id
            ORDER BY source_epoch DESC, generation DESC, created_at DESC, reservation_id DESC
            LIMIT 1
            """,
            connection, transaction);
        latestQuery.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, request.TargetId);
        if (await latestQuery.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid latest ||
            latest != request.ReservationId)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The hold does not match the latest reservation.");
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { Replayed = true };
        }

        var hold = new PlatformPolicyProofHold(
            request.HoldId,
            request.TargetId,
            request.PhysicalTarget,
            request.Namespace,
            request.NamespaceUid,
            request.SealEpoch,
            request.GuardDigest,
            request.ProfileDigest,
            request.RegistryRevision,
            request.PreparationId,
            request.ReservationId,
            fingerprint,
            timeProvider.GetUtcNow(),
            false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {Holds}
                (hold_id,target_id,physical_target,namespace,namespace_uid,seal_epoch,
                 guard_digest,profile_digest,registry_revision,preparation_id,reservation_id,
                 request_fingerprint,created_at)
            VALUES
                (@hold_id,@target_id,@physical_target,@namespace,@namespace_uid,@seal_epoch,
                 @guard_digest,@profile_digest,@registry_revision,@preparation_id,@reservation_id,
                 @request_fingerprint,@created_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("hold_id", NpgsqlDbType.Uuid, hold.HoldId);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, hold.TargetId);
        command.Parameters.AddWithValue("physical_target", NpgsqlDbType.Text, hold.PhysicalTarget);
        command.Parameters.AddWithValue("namespace", NpgsqlDbType.Text, hold.Namespace);
        command.Parameters.AddWithValue("namespace_uid", NpgsqlDbType.Text, hold.NamespaceUid);
        command.Parameters.AddWithValue("seal_epoch", NpgsqlDbType.Bigint, hold.SealEpoch);
        command.Parameters.AddWithValue("guard_digest", NpgsqlDbType.Text, hold.GuardDigest);
        command.Parameters.AddWithValue("profile_digest", NpgsqlDbType.Text, hold.ProfileDigest);
        command.Parameters.AddWithValue("registry_revision", NpgsqlDbType.Bigint, hold.RegistryRevision);
        command.Parameters.AddWithValue("preparation_id", NpgsqlDbType.Text, hold.PreparationId);
        command.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, hold.ReservationId);
        command.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, hold.RequestFingerprint);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, hold.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return hold;
    }

    public async Task<PlatformPolicyProofReceipt> PersistReceiptAsync(
        PlatformPolicyProofReceiptRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PersistReceiptCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformPolicyProofReceipt> PersistReceiptCoreAsync(
        PlatformPolicyProofReceiptRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var fingerprint = Fingerprint(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockTargetAsync(connection, transaction, request.TargetId, cancellationToken).ConfigureAwait(false);
        var hold = await ReadHoldByIdAsync(connection, transaction, request.HoldId, cancellationToken)
            .ConfigureAwait(false);
        if (hold is null || hold.TargetId != request.TargetId ||
            hold.ReservationId != request.ReservationId)
            throw new PlatformPolicyProofException(
                "policy_fence_conflict", "The receipt is not bound to the current durable hold.");
        var existing = await ReadReceiptAsync(
            connection, transaction, request.TargetId, request.HoldId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ReceiptId != request.ReceiptId ||
                existing.RequestFingerprint != fingerprint)
                throw new PlatformPolicyProofException(
                    "policy_fence_conflict", "The hold already has a different immutable receipt.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { Replayed = true };
        }

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {Receipts}
                (receipt_id,hold_id,target_id,reservation_id,source_epoch,source_revision,
                 source_id,source_hash,intent_hash,compiler_id,evaluator_version,ceiling_version,
                 profile_digest,guard_digest,snapshot_id,spec_digest,post_write_resource_version,
                 object_uid,policy_generation,snapshot_json,request_fingerprint,evidence_at)
            VALUES
                (@receipt_id,@hold_id,@target_id,@reservation_id,@source_epoch,@source_revision,
                 @source_id,@source_hash,@intent_hash,@compiler_id,@evaluator_version,@ceiling_version,
                 @profile_digest,@guard_digest,@snapshot_id,@spec_digest,@post_write_resource_version,
                 @object_uid,@policy_generation,@snapshot_json,@request_fingerprint,@evidence_at)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("receipt_id", NpgsqlDbType.Uuid, request.ReceiptId);
        insert.Parameters.AddWithValue("hold_id", NpgsqlDbType.Uuid, request.HoldId);
        insert.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, request.TargetId);
        insert.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, request.ReservationId);
        insert.Parameters.AddWithValue("source_epoch", NpgsqlDbType.Bigint, request.SourceEpoch);
        insert.Parameters.AddWithValue("source_revision", NpgsqlDbType.Bigint, request.SourceRevision);
        insert.Parameters.AddWithValue("source_id", NpgsqlDbType.Text, request.SourceId);
        insert.Parameters.AddWithValue("source_hash", NpgsqlDbType.Text, request.SourceHash);
        insert.Parameters.AddWithValue("intent_hash", NpgsqlDbType.Text, request.IntentHash);
        insert.Parameters.AddWithValue("compiler_id", NpgsqlDbType.Text, request.CompilerId);
        insert.Parameters.AddWithValue("evaluator_version", NpgsqlDbType.Text, request.EvaluatorVersion);
        insert.Parameters.AddWithValue("ceiling_version", NpgsqlDbType.Text, request.CeilingVersion);
        insert.Parameters.AddWithValue("profile_digest", NpgsqlDbType.Text, request.ProfileDigest);
        insert.Parameters.AddWithValue("guard_digest", NpgsqlDbType.Text, request.GuardDigest);
        insert.Parameters.AddWithValue("snapshot_id", NpgsqlDbType.Text, request.SnapshotId);
        insert.Parameters.AddWithValue("spec_digest", NpgsqlDbType.Text, request.SpecDigest);
        insert.Parameters.AddWithValue("post_write_resource_version", NpgsqlDbType.Text, request.PostWriteResourceVersion);
        insert.Parameters.AddWithValue("object_uid", NpgsqlDbType.Text, request.ObjectUid);
        insert.Parameters.AddWithValue("policy_generation", NpgsqlDbType.Bigint, request.PolicyGeneration);
        insert.Parameters.AddWithValue("snapshot_json", NpgsqlDbType.Jsonb, request.SnapshotJson);
        insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
        insert.Parameters.AddWithValue("evidence_at", NpgsqlDbType.TimestampTz, request.EvidenceAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            request.ReceiptId,
            request.HoldId,
            request.TargetId,
            request.ReservationId,
            request.SourceEpoch,
            request.SourceRevision,
            request.SourceId,
            request.SourceHash,
            request.IntentHash,
            request.CompilerId,
            request.EvaluatorVersion,
            request.CeilingVersion,
            request.ProfileDigest,
            request.GuardDigest,
            request.SnapshotId,
            request.SpecDigest,
            request.PostWriteResourceVersion,
            request.ObjectUid,
            request.PolicyGeneration,
            request.SnapshotJson,
            fingerprint,
            request.EvidenceAt,
            false);
    }

    public async Task<PlatformPolicyProofReceipt?> ReadCurrentReceiptAsync(
        string targetId,
        Guid holdId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCurrentReceiptCoreAsync(targetId, holdId, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformPolicyProofReceipt?> ReadCurrentReceiptCoreAsync(
        string targetId,
        Guid holdId,
        CancellationToken cancellationToken)
    {
        ValidateText(targetId, 256, "target ID");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT receipt_id,hold_id,target_id,reservation_id,source_epoch,source_revision,
                   source_id,source_hash,intent_hash,compiler_id,evaluator_version,ceiling_version,
                   profile_digest,guard_digest,snapshot_id,spec_digest,post_write_resource_version,
                   object_uid,policy_generation,snapshot_json,request_fingerprint,evidence_at
            FROM {Receipts}
            WHERE target_id=@target_id AND hold_id=@hold_id
            """,
            connection);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        command.Parameters.AddWithValue("hold_id", NpgsqlDbType.Uuid, holdId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReceipt(reader)
            : null;
    }

    public async Task RequireAllTargetsAsync(
        long registryRevision,
        string preparationId,
        IEnumerable<string> targetIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await RequireAllTargetsCoreAsync(
                registryRevision, preparationId, targetIds, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task RequireAllTargetsCoreAsync(
        long registryRevision,
        string preparationId,
        IEnumerable<string> targetIds,
        CancellationToken cancellationToken)
    {
        var required = targetIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (required.Length == 0)
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "A Platform Policy commit requires at least one proof target.");
        foreach (var targetId in required)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT EXISTS (
                    SELECT 1
                    FROM {Holds} h
                    JOIN {Receipts} r ON r.hold_id=h.hold_id AND r.target_id=h.target_id
                    WHERE h.target_id=@target_id
                      AND h.registry_revision=@registry_revision
                      AND h.preparation_id=@preparation_id
                )
                """,
                connection);
            command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
            command.Parameters.AddWithValue("registry_revision", NpgsqlDbType.Bigint, registryRevision);
            command.Parameters.AddWithValue("preparation_id", NpgsqlDbType.Text, preparationId);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new PlatformPolicyProofException(
                    "policy_fence_unavailable", $"Target '{targetId}' has no complete current proof receipt.");
        }
    }

    private static async Task LockTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target_id, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PlatformPolicyProofHold?> ReadHoldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            SelectHoldSql() + " WHERE target_id=@target_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHold(reader) : null;
    }

    private static async Task<PlatformPolicyProofHold?> ReadHoldByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid holdId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            SelectHoldSql() + " WHERE hold_id=@hold_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("hold_id", NpgsqlDbType.Uuid, holdId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHold(reader) : null;
    }

    private static string SelectHoldSql() =>
        $"""
        SELECT hold_id,target_id,physical_target,namespace,namespace_uid,seal_epoch,
               guard_digest,profile_digest,registry_revision,preparation_id,reservation_id,
               request_fingerprint,created_at
        FROM {Holds}
        """;

    private static async Task<PlatformPolicyProofReceipt?> ReadReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetId,
        Guid holdId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT receipt_id,hold_id,target_id,reservation_id,source_epoch,source_revision,
                   source_id,source_hash,intent_hash,compiler_id,evaluator_version,ceiling_version,
                   profile_digest,guard_digest,snapshot_id,spec_digest,post_write_resource_version,
                   object_uid,policy_generation,snapshot_json,request_fingerprint,evidence_at
            FROM {Receipts}
            WHERE target_id=@target_id AND hold_id=@hold_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        command.Parameters.AddWithValue("hold_id", NpgsqlDbType.Uuid, holdId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadReceipt(reader) : null;
    }

    private static PlatformPolicyProofHold ReadHold(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
            reader.GetInt64(8), reader.GetString(9), reader.GetGuid(10), reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12), false);

    private static PlatformPolicyProofReceipt ReadReceipt(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
            reader.GetString(16), reader.GetString(17), reader.GetInt64(18), reader.GetString(19),
            reader.GetString(20), reader.GetFieldValue<DateTimeOffset>(21), false);

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            .ToLowerInvariant();

    private static void Validate(PlatformPolicyProofHoldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.TargetId, 256, "target ID");
        ValidateText(request.PhysicalTarget, 512, "physical target");
        ValidateText(request.Namespace, 63, "namespace");
        ValidateText(request.NamespaceUid, 128, "namespace UID");
        ValidateText(request.GuardDigest, 64, "guard digest");
        ValidateText(request.ProfileDigest, 64, "profile digest");
        ValidateText(request.PreparationId, 256, "preparation ID");
        if (request.HoldId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.SealEpoch <= 0 || request.RegistryRevision <= 0)
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The durable policy hold is incomplete.");
        if (!IsSha256(request.GuardDigest) || !IsSha256(request.ProfileDigest))
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The enrollment or guard digest is malformed.");
    }

    private static void Validate(PlatformPolicyProofReceiptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.TargetId, 256, "target ID");
        ValidateText(request.SourceId, 256, "source ID");
        ValidateText(request.CompilerId, 128, "compiler ID");
        ValidateText(request.EvaluatorVersion, 128, "evaluator version");
        ValidateText(request.CeilingVersion, 128, "ceiling version");
        ValidateText(request.PostWriteResourceVersion, 256, "resource version");
        ValidateText(request.ObjectUid, 128, "object UID");
        if (Encoding.UTF8.GetByteCount(request.SnapshotJson) > MaxSnapshotBytes)
            throw new PlatformPolicyProofException(
                "policy_semantics_unsupported", "The retained policy snapshot exceeds the supported bound.");
        foreach (var hash in new[]
                 {
                     request.SourceHash, request.IntentHash, request.ProfileDigest, request.GuardDigest,
                     request.SnapshotId, request.SpecDigest,
                 })
            if (!IsSha256(hash))
                throw new PlatformPolicyProofException(
                    "policy_fence_unavailable", "A proof receipt hash is malformed.");
        try
        {
            using var snapshot = JsonDocument.Parse(request.SnapshotJson);
            if (snapshot.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new PlatformPolicyProofException(
                "policy_semantics_unsupported", "The retained policy snapshot is malformed.");
        }
        if (request.ReceiptId == Guid.Empty || request.HoldId == Guid.Empty ||
            request.ReservationId == Guid.Empty || request.SourceEpoch <= 0 ||
            request.SourceRevision <= 0 || request.PolicyGeneration <= 0)
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy proof receipt is incomplete.");
    }

    private static void ValidateText(string value, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maximum || value.Any(character => char.IsControl(character)))
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", $"The {name} is malformed.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
