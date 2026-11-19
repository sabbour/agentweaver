using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed record PlatformEgressApplyReservationRequest(
    string TargetId,
    string IdempotencyKey,
    long SourceEpoch,
    long SourceRevision,
    string SourceId,
    string ObjectUid,
    string ResourceVersion,
    PlatformEgressPolicy Policy);

public sealed record PlatformEgressApplyReservation(
    Guid ReservationId,
    string TargetId,
    string IdempotencyKey,
    long SourceEpoch,
    long Generation,
    long SourceRevision,
    string SourceId,
    string ObjectUid,
    string ResourceVersion,
    PlatformEgressPolicy Policy,
    string RequestFingerprint,
    DateTimeOffset CreatedAt,
    bool Replayed);

public sealed class PlatformEgressApplyReservationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IPlatformEgressApplyReservationReader
{
    Task<PlatformEgressApplyReservation?> FindAsync(
        string targetId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PlatformEgressApplyReservation?> FindLatestAsync(
        string targetId,
        CancellationToken cancellationToken);
}

/// <summary>Persists immutable provider requests before a caller issues a Kubernetes write.</summary>
public sealed class PlatformEgressApplyReservationStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : IPlatformEgressApplyReservationReader
{
    private const string Table = "\"environment\".\"platform_policy_apply_reservations\"";
    private const int MaxRequestBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<PlatformEgressApplyReservation?> FindAsync(
        string targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FindCoreAsync(targetId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Conflict("policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformEgressApplyReservation?> FindCoreAsync(
        string targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateText(targetId, 256, "target ID");
        ValidateText(idempotencyKey, 128, "idempotency key");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var reservation = await ReadByIdempotencyAsync(
            connection, transaction, targetId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation is null ? null : reservation with { Replayed = true };
    }

    public async Task<PlatformEgressApplyReservation?> FindLatestAsync(
        string targetId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FindLatestCoreAsync(targetId, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Conflict("policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformEgressApplyReservation?> FindLatestCoreAsync(
        string targetId,
        CancellationToken cancellationToken)
    {
        ValidateText(targetId, 256, "target ID");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var reservation = await ReadLatestAsync(connection, transaction, targetId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation is null ? null : reservation with { Replayed = true };
    }

    public async Task<PlatformEgressApplyReservation> ReserveAsync(
        PlatformEgressApplyReservationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReserveCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation &&
            exception.ConstraintName == "ck_platform_policy_reservation_held")
        {
            throw Conflict("policy_fence_conflict", "The target has a durable policy hold.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "ux_environment_platform_policy_apply_reservations_idempotency")
        {
            throw Conflict("reservation_conflict", "The target already has a reservation with this idempotency key.");
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Conflict("policy_fence_unavailable", "The policy fence store is unavailable.");
        }
    }

    private async Task<PlatformEgressApplyReservation> ReserveCoreAsync(
        PlatformEgressApplyReservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var policyJson = JsonSerializer.Serialize(request.Policy, JsonOptions);
        if (Encoding.UTF8.GetByteCount(policyJson) > MaxRequestBytes)
            throw Invalid("The provider request exceeds the supported size.");
        var fingerprint = Fingerprint(request);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@target_id, 0))",
            connection,
            transaction))
        {
            acquire.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, request.TargetId);
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var replay = await ReadByIdempotencyAsync(
            connection, transaction, request.TargetId, request.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.RequestFingerprint != fingerprint)
                throw Conflict("idempotency_conflict", "The idempotency key was already used for a different provider request.");
        }

        await using var heldQuery = new NpgsqlCommand(
            "SELECT reservation_id FROM environment.platform_policy_proof_holds WHERE target_id = @target_id",
            connection, transaction);
        heldQuery.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, request.TargetId);
        var held = await heldQuery.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (held is Guid heldReservation && (replay is null || replay.ReservationId != heldReservation))
            throw Conflict("policy_fence_conflict", "The target has a durable policy hold.");
        var latest = await ReadLatestAsync(connection, transaction, request.TargetId, cancellationToken)
            .ConfigureAwait(false);
        if (held is Guid heldId && latest?.ReservationId != heldId)
            throw Conflict("policy_fence_conflict", "The hold does not match the latest reservation.");
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay with { Replayed = true };
        }

        if (latest is not null)
            EnsureNextReservation(request, latest);

        var createdAt = timeProvider.GetUtcNow();
        if (latest is not null && createdAt <= latest.CreatedAt)
            createdAt = latest.CreatedAt.AddTicks(10);

        var reservation = new PlatformEgressApplyReservation(
            Guid.NewGuid(),
            request.TargetId,
            request.IdempotencyKey,
            request.SourceEpoch,
            request.Policy.Generation,
            request.SourceRevision,
            request.SourceId,
            request.ObjectUid,
            request.ResourceVersion,
            request.Policy,
            fingerprint,
            createdAt,
            Replayed: false);

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {Table}
                (reservation_id, target_id, idempotency_key, request_fingerprint,
                 source_epoch, generation, source_id, source_revision, source_hash, intent_hash,
                 object_uid, resource_version, provider_request_json, created_at)
            VALUES
                (@reservation_id, @target_id, @idempotency_key, @request_fingerprint,
                 @source_epoch, @generation, @source_id, @source_revision, @source_hash, @intent_hash,
                 @object_uid, @resource_version, @provider_request_json, @created_at)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("reservation_id", NpgsqlDbType.Uuid, reservation.ReservationId);
        insert.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, reservation.TargetId);
        insert.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, reservation.IdempotencyKey);
        insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, reservation.RequestFingerprint);
        insert.Parameters.AddWithValue("source_epoch", NpgsqlDbType.Bigint, reservation.SourceEpoch);
        insert.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, reservation.Generation);
        insert.Parameters.AddWithValue("source_id", NpgsqlDbType.Text, reservation.SourceId);
        insert.Parameters.AddWithValue("source_revision", NpgsqlDbType.Bigint, reservation.SourceRevision);
        insert.Parameters.AddWithValue("source_hash", NpgsqlDbType.Text, reservation.Policy.SourceHash);
        insert.Parameters.AddWithValue("intent_hash", NpgsqlDbType.Text, reservation.Policy.IntentHash);
        insert.Parameters.AddWithValue("object_uid", NpgsqlDbType.Text, reservation.ObjectUid);
        insert.Parameters.AddWithValue("resource_version", NpgsqlDbType.Text, reservation.ResourceVersion);
        insert.Parameters.AddWithValue("provider_request_json", NpgsqlDbType.Jsonb, policyJson);
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, reservation.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation;
    }

    private static async Task<PlatformEgressApplyReservation?> ReadByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT reservation_id, target_id, idempotency_key, request_fingerprint,
                   source_epoch, generation, source_id, source_revision, source_hash, intent_hash,
                   object_uid, resource_version, provider_request_json, created_at
            FROM {Table}
            WHERE target_id = @target_id AND idempotency_key = @idempotency_key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader)
            : null;
    }

    private static async Task<PlatformEgressApplyReservation?> ReadLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT reservation_id, target_id, idempotency_key, request_fingerprint,
                   source_epoch, generation, source_id, source_revision, source_hash, intent_hash,
                   object_uid, resource_version, provider_request_json, created_at
            FROM {Table}
            WHERE target_id = @target_id
            ORDER BY source_epoch DESC, generation DESC, created_at DESC, reservation_id DESC
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("target_id", NpgsqlDbType.Text, targetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader)
            : null;
    }

    private static PlatformEgressApplyReservation ReadReservation(NpgsqlDataReader reader)
    {
        var policy = JsonSerializer.Deserialize<PlatformEgressPolicy>(reader.GetString(12), JsonOptions)
            ?? throw new InvalidOperationException("The saved provider request is invalid.");
        if (policy.Generation != reader.GetInt64(5) ||
            policy.SourceHash != reader.GetString(8) ||
            policy.IntentHash != reader.GetString(9))
            throw new InvalidOperationException("The saved provider request does not match its reservation.");

        return new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(7),
            reader.GetString(6),
            reader.GetString(10),
            reader.GetString(11),
            policy,
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(13),
            Replayed: false);
    }

    private static void EnsureNextReservation(
        PlatformEgressApplyReservationRequest request,
        PlatformEgressApplyReservation latest)
    {
        if (request.SourceEpoch < latest.SourceEpoch)
            throw Conflict("stale_source_epoch", "The provider request uses an older source epoch.");
        if (request.Policy.Generation < latest.Generation)
            throw Conflict("stale_generation", "The provider request uses an older or reused generation.");
        if (request.Policy.Generation == latest.Generation)
        {
            if (request.SourceEpoch != latest.SourceEpoch)
                throw Conflict("stale_generation", "A new source epoch must advance the provider generation.");
            EnsureSameSource(request, latest);
            return;
        }
        if (request.SourceEpoch == latest.SourceEpoch)
            EnsureSameSource(request, latest);
    }

    private static void EnsureSameSource(
        PlatformEgressApplyReservationRequest request,
        PlatformEgressApplyReservation latest)
    {
        if (request.SourceId != latest.SourceId ||
            request.SourceRevision != latest.SourceRevision ||
            request.Policy.SourceHash != latest.Policy.SourceHash ||
            request.Policy.IntentHash != latest.Policy.IntentHash ||
            !SamePolicy(request.Policy, latest.Policy))
            throw Conflict("source_epoch_conflict", "One source epoch cannot identify different policy content.");
    }

    private static bool SamePolicy(PlatformEgressPolicy left, PlatformEgressPolicy right) =>
        left.Mode == right.Mode &&
        left.IntentHash == right.IntentHash &&
        left.SourceHash == right.SourceHash &&
        left.Destinations.SequenceEqual(right.Destinations);

    private static void Validate(PlatformEgressApplyReservationRequest request)
    {
        ValidateText(request.TargetId, 256, "target ID");
        ValidateText(request.IdempotencyKey, 128, "idempotency key");
        ValidateText(request.SourceId, 256, "source ID");
        ValidateText(request.ObjectUid, 128, "Kubernetes object UID");
        ValidateText(request.ResourceVersion, 256, "Kubernetes resourceVersion");
        ArgumentOutOfRangeException.ThrowIfLessThan(request.SourceEpoch, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.SourceRevision, 1);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Policy.Generation, 1);
        if (!Enum.IsDefined(request.Policy.Mode) ||
            !IsSha256(request.Policy.SourceHash) ||
            !IsSha256(request.Policy.IntentHash) ||
            request.Policy.Destinations.IsDefault ||
            (request.Policy.Mode == PlatformEgressMode.PublicHttps && !request.Policy.Destinations.IsEmpty))
            throw Invalid("The provider policy is incomplete or invalid.");
        foreach (var destination in request.Policy.Destinations)
            if (destination is null ||
                PlatformEgressHostValidator.Validate(destination.Host, destination.Port) is not null)
                throw Invalid("The provider policy contains an invalid destination.");
        var canonical = PlatformEgressPolicy.Create(
            request.Policy.Generation,
            request.Policy.Mode,
            request.Policy.Destinations,
            request.Policy.SourceHash);
        if (!request.Policy.Destinations.SequenceEqual(canonical.Destinations) ||
            request.Policy.IntentHash != canonical.IntentHash)
            throw Invalid("The provider policy is incomplete or invalid.");
    }

    private static string Fingerprint(PlatformEgressApplyReservationRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static void ValidateText(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maxLength || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw Invalid($"The {name} is invalid.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static PlatformEgressApplyReservationException Invalid(string message) =>
        new("invalid_apply_reservation", message);

    private static PlatformEgressApplyReservationException Conflict(string code, string message) =>
        new(code, message);

}
