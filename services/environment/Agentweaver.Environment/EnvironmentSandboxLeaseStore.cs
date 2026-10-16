using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

/// <summary>
/// Persists Sandbox leases, provider attempts, and late-resource cleanup claims under Environment owner locks.
/// Lease revisions and fencing generations prevent stale provider results from becoming current.
/// </summary>
public sealed class EnvironmentSandboxLeaseStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : ISandboxLeaseStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LateCleanupClaimDuration = TimeSpan.FromMinutes(2);
    private const string Leases = "\"environment\".\"sandbox_leases\"";
    private const string LateResourceCleanups = "\"environment\".\"sandbox_late_resource_cleanups\"";
    private const string ProviderAttempts = "\"environment\".\"sandbox_provider_attempts\"";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Reserves one provision generation, reusing only a matching idempotency key and immutable intent.</summary>
    public async Task<SandboxLeaseReservation> ReserveProvisionAsync(
        EnvironmentGenerationFence fence,
        string idempotencyKey,
        SandboxLeaseProvisionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Sandbox idempotency keys must be bounded.", nameof(idempotencyKey));
        intent = (intent ?? throw new ArgumentNullException(nameof(intent))).Validate();
        var fingerprint = ProvisionFingerprint(fence, intent);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);

        var replay = await ReadByIdempotencyAsync(
            connection, transaction, fence.Owner, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "sandbox_idempotency_conflict",
                    "The sandbox idempotency key was already used for a different provision intent.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(replay.Value.Lease, Replayed: true);
        }

        await EnsureNoUnresolvedSandboxWorkAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);

        var current = await ReadCurrentAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        if (current is { State: not (SandboxLeaseState.Released or SandboxLeaseState.Failed) })
            throw new EnvironmentLifecycleException(
                "sandbox_lease_capacity_exceeded",
                "This Environment already has a Sandbox lease that is active or requires reconciliation.");
        if (current is not null)
        {
            await using var clearCurrent = new NpgsqlCommand($"""
                UPDATE {Leases}
                SET is_current = FALSE,
                    lease_revision = lease_revision + 1,
                    lease_expires_at = NULL,
                    updated_at = @updated_at
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND resource_generation = @resource_generation AND is_current = TRUE
                  AND lease_revision = @expected_lease_revision
                """, connection, transaction);
            AddOwnerParameters(clearCurrent, fence.Owner);
            clearCurrent.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, current.ResourceGeneration);
            clearCurrent.Parameters.AddWithValue(
                "expected_lease_revision",
                NpgsqlDbType.Bigint,
                current.LeaseRevision);
            clearCurrent.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            if (await clearCurrent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_lease_revision_stale",
                    "The terminal Sandbox lease changed before the next generation was reserved.");
        }

        var (resourceGeneration, fencingGeneration) = await ReadNextGenerationsAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {Leases}
                (tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 resource_generation, provider_fencing_generation, current_fencing_generation,
                 operation_id, provision_idempotency_key, request_fingerprint,
                 provider_id, adapter_version, options_schema_version, options_revision,
                 options_snapshot_json, selection_snapshot_json, provider_request_json,
                 lease_state, is_current, created_at, updated_at, lease_revision, lease_expires_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 @resource_generation, @provider_fencing_generation, @provider_fencing_generation,
                 @operation_id, @idempotency_key, @request_fingerprint,
                 @provider_id, @adapter_version, @options_schema_version, @options_revision,
                 @options_snapshot_json, @selection_snapshot_json, @provider_request_json,
                 'Provisioning', TRUE, @created_at, @updated_at, 1, @lease_expires_at)
            """, connection, transaction))
        {
            AddOwnerParameters(insert, fence.Owner);
            insert.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, fence.LifecycleGeneration);
            insert.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, resourceGeneration);
            insert.Parameters.AddWithValue("provider_fencing_generation", NpgsqlDbType.Bigint, fencingGeneration);
            insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            insert.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
            insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
            insert.Parameters.AddWithValue("provider_id", NpgsqlDbType.Text, intent.ProviderId);
            insert.Parameters.AddWithValue("adapter_version", NpgsqlDbType.Text, intent.AdapterVersion);
            insert.Parameters.AddWithValue("options_schema_version", NpgsqlDbType.Integer, intent.OptionsSchemaVersion);
            insert.Parameters.AddWithValue("options_revision", NpgsqlDbType.Text, intent.OptionsRevision);
            insert.Parameters.AddWithValue("options_snapshot_json", NpgsqlDbType.Jsonb, intent.OptionsSnapshot.GetRawText());
            insert.Parameters.AddWithValue("selection_snapshot_json", NpgsqlDbType.Jsonb, intent.SelectionSnapshot.GetRawText());
            insert.Parameters.AddWithValue("provider_request_json", NpgsqlDbType.Jsonb, intent.ProviderRequest.GetRawText());
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
            insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            insert.Parameters.AddWithValue(
                "lease_expires_at",
                NpgsqlDbType.TimestampTz,
                now + LeaseDuration);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The sandbox lease reservation was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(lease, Replayed: false);
    }

    /// <summary>Reads the current lease while holding the owner lock and requiring an active lifecycle fence.</summary>
    public async Task<SandboxLeaseSnapshot?> GetCurrentAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadCurrentAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    /// <summary>Runs a callback with the current lease while its owner transaction remains open.</summary>
    public Task<TResult> GetCurrentAsync<TResult>(
        EnvironmentGenerationFence fence,
        Func<SandboxLeaseSnapshot?, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return GetCurrentCoreAsync(
            fence, (lease, _, _, token) => callback(lease, token), cancellationToken);
    }

    /// <summary>Runs a callback with the current lease and workspace snapshot in one owner-locked transaction.</summary>
    public Task<TResult> GetCurrentWithWorkspaceAsync<TResult>(
        EnvironmentGenerationFence fence,
        string volumeId,
        Func<SandboxLeaseSnapshot?, EnvironmentWorkspaceVolumeSnapshot?, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        ArgumentNullException.ThrowIfNull(callback);
        return GetCurrentCoreAsync(fence, async (lease, connection, transaction, token) =>
        {
            var workspace = await EnvironmentLifecycleStore.ReadWorkspaceVolumeInTransactionAsync(
                connection, transaction, fence, volumeId, token).ConfigureAwait(false);
            return await callback(lease, workspace, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<TResult> GetCurrentCoreAsync<TResult>(
        EnvironmentGenerationFence fence,
        Func<SandboxLeaseSnapshot?, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(callback);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadCurrentAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var result = await callback(lease, connection, transaction, cancellationToken).ConfigureAwait(false);
        var currentLease = await ReadCurrentAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        if (!SameLeaseVersion(lease, currentLease))
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox lease changed during the owner-scoped read.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reads one Sandbox resource generation for the supplied Environment owner.</summary>
    public async Task<SandboxLeaseSnapshot?> GetAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (resourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByGenerationAsync(
            connection, transaction, fence.Owner, resourceGeneration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    /// <summary>Persists the exact provider request before an external Sandbox provisioning attempt.</summary>
    public async Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        JsonElement providerRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (operationId == Guid.Empty ||
            providerRequest.ValueKind != JsonValueKind.Object ||
            providerRequest.GetRawText().Length > 32_768)
            throw new ArgumentException("The complete Sandbox provider request is invalid.", nameof(providerRequest));
        var fingerprint = Fingerprint(string.Join('\0',
            "sandbox-provider-request",
            operationId.ToString("N"),
            fence.Owner.TenantId,
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            fence.Owner.EnvironmentId,
            fence.LifecycleGeneration,
            providerRequest.GetRawText()));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox provision operation is not recorded.");
        if (lease.Fence != fence || lease.State != SandboxLeaseState.Provisioning || !lease.IsCurrent ||
            lease.CurrentFencingGeneration != lease.ProviderFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox provider request cannot change after its lease has been retired or fenced.");
        if (lease.ProviderRequestFingerprint is not null)
        {
            if (!string.Equals(lease.ProviderRequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_request_conflict",
                    "The operation is already bound to a different immutable provider request.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }

        var updatedAt = timeProvider.GetUtcNow();
        await using (var update = new NpgsqlCommand($"""
            UPDATE {Leases}
            SET provider_request_json = @provider_request_json,
                provider_request_fingerprint = @provider_request_fingerprint,
                lease_revision = lease_revision + 1,
                lease_expires_at = @lease_expires_at,
                updated_at = @updated_at
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND lease_state = 'Provisioning' AND is_current = TRUE
              AND provider_fencing_generation = current_fencing_generation
              AND lease_revision = @expected_lease_revision
              AND provider_request_fingerprint IS NULL
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            update.Parameters.AddWithValue("expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
            update.Parameters.AddWithValue(
                "provider_request_json", NpgsqlDbType.Jsonb, providerRequest.GetRawText());
            update.Parameters.AddWithValue("provider_request_fingerprint", NpgsqlDbType.Text, fingerprint);
            update.Parameters.AddWithValue(
                "lease_expires_at",
                NpgsqlDbType.TimestampTz,
                updatedAt + LeaseDuration);
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_fence_stale",
                    "The Sandbox provider request changed before it could be pinned to the lease.");
        }
        var result = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox provider request was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reserves a provider attempt with a stable operation key and the current lease fence.</summary>
    public Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
        SandboxProviderAttemptRequest request,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        return BeginProviderAttemptAsync(request, request.Fence, cancellationToken);
    }

    /// <summary>Starts or replays a durable provider attempt under the owner lock.</summary>
    public async Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
        SandboxProviderAttemptRequest request,
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(currentFence);
        if (request.Fence.Owner != currentFence.Owner)
            throw new EnvironmentLifecycleException(
                "environment_owner_mismatch",
                "The provider attempt source and current authorization fences have different owners.");
        if ((request.EffectKind != SandboxProviderEffectKind.LateResourceRelease &&
                request.Fence != currentFence) ||
            (request.EffectKind == SandboxProviderEffectKind.LateResourceRelease &&
                request.Fence.LifecycleGeneration > currentFence.LifecycleGeneration))
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "Only late-resource cleanup can use an older source fence under the current Environment fence.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, currentFence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, currentFence, cancellationToken).ConfigureAwait(false);

        var lease = await ReadByOperationAsync(
            connection, transaction, request.OperationId, request.Fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox provider operation is not recorded.");
        ValidateProviderAttemptRequestForLease(request, lease);
        var previous = await ReadLatestProviderAttemptAsync(
            connection, transaction, request, cancellationToken).ConfigureAwait(false);
        if (previous is { State: SandboxProviderAttemptState.ResultRecorded or SandboxProviderAttemptState.Completed })
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(previous, Replayed: true);
        }
        if (previous is { State: SandboxProviderAttemptState.InProgress or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.LegacyUnknown })
            throw ProviderAttemptUnresolved(previous);

        await EnsureProviderEffectDoesNotOvertakeLateCleanupAsync(
            connection, transaction, request, cancellationToken).ConfigureAwait(false);

        var unresolved = await ReadAnyUnresolvedProviderAttemptAsync(
            connection, transaction, request.Fence.Owner, cancellationToken).ConfigureAwait(false);
        if (unresolved is not null)
            throw ProviderAttemptUnresolved(unresolved);

        var attemptId = Guid.NewGuid();
        var startedAt = timeProvider.GetUtcNow();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {ProviderAttempts}
                (attempt_id, tenant_id, project_id, run_id, environment_id,
                 lifecycle_generation, resource_generation, provider_fencing_generation,
                 operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                 attempt_state, started_at, updated_at)
            VALUES
                (@attempt_id, @tenant_id, @project_id, @run_id, @environment_id,
                 @lifecycle_generation, @resource_generation, @provider_fencing_generation,
                 @operation_id, @effect_kind, @request_fingerprint, @late_resource_fingerprint,
                 'InProgress', @started_at, @updated_at)
            """, connection, transaction))
        {
            AddOwnerParameters(insert, request.Fence.Owner);
            insert.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Uuid, attemptId);
            insert.Parameters.AddWithValue(
                "lifecycle_generation", NpgsqlDbType.Bigint, request.Fence.LifecycleGeneration);
            insert.Parameters.AddWithValue(
                "resource_generation", NpgsqlDbType.Bigint, request.ResourceGeneration);
            insert.Parameters.AddWithValue(
                "provider_fencing_generation", NpgsqlDbType.Bigint, request.ProviderFencingGeneration);
            insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.OperationId);
            insert.Parameters.AddWithValue("effect_kind", NpgsqlDbType.Text, request.EffectKind.ToString());
            insert.Parameters.AddWithValue(
                "request_fingerprint", NpgsqlDbType.Text, request.RequestFingerprint);
            insert.Parameters.AddWithValue(
                "late_resource_fingerprint",
                NpgsqlDbType.Text,
                request.LateResourceFingerprint is null ? DBNull.Value : request.LateResourceFingerprint);
            insert.Parameters.AddWithValue("started_at", NpgsqlDbType.TimestampTz, startedAt);
            insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, startedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.EffectKind == SandboxProviderEffectKind.LateResourceRelease)
        {
            await using var linkReleaseAttempt = new NpgsqlCommand($"""
                UPDATE {LateResourceCleanups}
                SET release_attempt_id = @release_attempt_id,
                    updated_at = @updated_at
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND resource_generation = @resource_generation
                  AND provider_resource_fingerprint = @late_resource_fingerprint
                  AND release_receipt_json IS NULL
                  AND claim_token IS NOT NULL
                  AND claim_expires_at > @updated_at
                """, connection, transaction);
            AddOwnerParameters(linkReleaseAttempt, request.Fence.Owner);
            linkReleaseAttempt.Parameters.AddWithValue(
                "resource_generation", NpgsqlDbType.Bigint, request.ResourceGeneration);
            linkReleaseAttempt.Parameters.AddWithValue(
                "late_resource_fingerprint", NpgsqlDbType.Text, request.LateResourceFingerprint!);
            linkReleaseAttempt.Parameters.AddWithValue(
                "release_attempt_id", NpgsqlDbType.Uuid, attemptId);
            linkReleaseAttempt.Parameters.AddWithValue(
                "updated_at", NpgsqlDbType.TimestampTz, startedAt);
            if (await linkReleaseAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_claim_stale",
                    "The late Sandbox cleanup claim changed before its release attempt was linked.");
        }

        var attempt = new SandboxProviderAttemptSnapshot(
            attemptId,
            request,
            SandboxProviderAttemptState.InProgress,
            Outcome: null,
            FailureCode: null,
            startedAt,
            OutcomeRecordedAt: null,
            ResolvedAt: null).Validate();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(attempt, Replayed: false);
    }

    /// <summary>Records a provider attempt result so uncertain calls can be reconciled without losing evidence.</summary>
    public async Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
        Guid attemptId,
        EnvironmentGenerationFence fence,
        JsonElement outcome,
        bool effectMayHaveApplied,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (attemptId == Guid.Empty ||
            outcome.ValueKind != JsonValueKind.Object ||
            outcome.GetRawText().Length > 65_536 ||
            (failureCode is null && !effectMayHaveApplied) ||
            failureCode is { } code &&
                (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl)))
            throw new ArgumentException("The Sandbox provider attempt outcome is invalid.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var attempt = await ReadProviderAttemptByIdAsync(
            connection, transaction, attemptId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unknown",
                "The exact Sandbox provider attempt is not recorded.");
        if (attempt.Request.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Sandbox provider attempt belongs to a stale Environment lifecycle fence.");
        var lease = await ReadByOperationAsync(
            connection, transaction, attempt.Request.OperationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox operation for the provider attempt is not recorded.");
        if (lease.Fence != fence || lease.ResourceGeneration != attempt.Request.ResourceGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The provider attempt does not match the exact retained Sandbox operation.");

        var targetState = failureCode is null
            ? SandboxProviderAttemptState.ResultRecorded
            : effectMayHaveApplied
                ? SandboxProviderAttemptState.Unknown
                : SandboxProviderAttemptState.FailedNoEffect;
        if (attempt.State != SandboxProviderAttemptState.InProgress)
        {
            if (attempt.State == targetState &&
                JsonNode.DeepEquals(
                    JsonNode.Parse(attempt.Outcome?.GetRawText() ?? "null"),
                    JsonNode.Parse(outcome.GetRawText())) &&
                string.Equals(attempt.FailureCode, failureCode, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return attempt;
            }
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_outcome_conflict",
                "The Sandbox provider attempt already has a different durable outcome.",
                SandboxProviderAttemptReceipt.From(attempt));
        }

        var recordedAt = timeProvider.GetUtcNow();
        var resolvedAt = targetState == SandboxProviderAttemptState.FailedNoEffect
            ? recordedAt
            : (DateTimeOffset?)null;
        await UpdateProviderAttemptAsync(
            connection,
            transaction,
            attempt,
            targetState,
            outcome,
            failureCode,
            recordedAt,
            resolvedAt,
            cancellationToken).ConfigureAwait(false);
        var updated = attempt with
        {
            State = targetState,
            Outcome = outcome.Clone(),
            FailureCode = failureCode,
            OutcomeRecordedAt = recordedAt,
            ResolvedAt = resolvedAt
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated.Validate();
    }

    /// <summary>Reads provider attempt history for the exact Environment lease generation.</summary>
    public async Task<ImmutableArray<SandboxProviderAttemptSnapshot>> GetProviderAttemptsAsync(
        EnvironmentGenerationFence currentFence,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentFence);
        if (operationId == Guid.Empty)
            throw new ArgumentException("A Sandbox operation ID is required.", nameof(operationId));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, currentFence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, currentFence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, currentFence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox provider operation is not recorded.");
        var attempts = ImmutableArray.CreateBuilder<SandboxProviderAttemptSnapshot>();
        await using (var select = new NpgsqlCommand($"""
            SELECT attempt_id, lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                   attempt_state, outcome_json, failure_code, started_at, outcome_recorded_at, resolved_at
            FROM {ProviderAttempts}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND operation_id = @operation_id
            ORDER BY started_at, attempt_id
            """, connection, transaction))
        {
            AddOwnerParameters(select, currentFence.Owner);
            select.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                attempts.Add(ReadProviderAttempt(reader, currentFence.Owner));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _ = lease;
        return attempts.ToImmutable();
    }

    /// <summary>Lists bounded late-resource cleanup records and indicates when additional records remain.</summary>
    public async Task<(ImmutableArray<SandboxLateResourceCleanupRecord> Items, bool HasMore)>
        GetLateResourceCleanupRecordsAsync(
            EnvironmentGenerationFence currentFence,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentFence);
        const int pageSize = 100;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, currentFence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, currentFence, cancellationToken).ConfigureAwait(false);

        var rows = new List<CleanupReadRow>(pageSize + 1);
        await using (var select = new NpgsqlCommand($"""
            SELECT resource_generation, provider_resource_fingerprint, resource_json,
                   claim_expires_at, release_receipt_json, created_at, updated_at,
                   provision_attempt_id, release_attempt_id
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            ORDER BY CASE WHEN release_receipt_json IS NULL THEN 0 ELSE 1 END,
                     created_at, resource_generation, provider_resource_fingerprint
            LIMIT @limit
            """, connection, transaction))
        {
            AddOwnerParameters(select, currentFence.Owner);
            select.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        }

        var hasMore = rows.Count > pageSize;
        var output = ImmutableArray.CreateBuilder<SandboxLateResourceCleanupRecord>(
            Math.Min(rows.Count, pageSize));
        foreach (var row in rows.Take(pageSize))
        {
            var lease = await ReadByGenerationAsync(
                connection, transaction, currentFence.Owner, row.ResourceGeneration, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_unknown",
                    "The late Sandbox cleanup does not have its retained lease history.");
            var provisionedResource = JsonSerializer.Deserialize<SandboxProvisionedResource>(
                row.ResourceJson, JsonOptions)
                ?? throw new InvalidOperationException("The stored late Sandbox resource is empty.");
            ValidateProvisionedResource(lease, provisionedResource);
            var releaseReceipt = row.ReleaseReceiptJson is null
                ? null
                : JsonSerializer.Deserialize<SandboxReleaseReceipt>(row.ReleaseReceiptJson, JsonOptions)
                  ?? throw new InvalidOperationException("The stored late Sandbox release receipt is empty.");
            var provisionAttempt = row.ProvisionAttemptId is { } provisionAttemptId
                ? await ReadProviderAttemptByIdAsync(
                    connection, transaction, provisionAttemptId, currentFence.Owner, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var releaseAttempt = row.ReleaseAttemptId is { } releaseAttemptId
                ? await ReadProviderAttemptByIdAsync(
                    connection, transaction, releaseAttemptId, currentFence.Owner, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var item = new SandboxLateResourceCleanupReadItem(
                lease.Fence,
                lease.OperationId,
                lease.ResourceGeneration,
                lease.ProviderFencingGeneration,
                lease.CurrentFencingGeneration,
                row.Fingerprint,
                provisionedResource.Resource,
                provisionAttempt is null ? null : SandboxProviderAttemptReceipt.From(provisionAttempt),
                releaseAttempt is null ? null : SandboxProviderAttemptReceipt.From(releaseAttempt),
                row.CreatedAt,
                row.UpdatedAt,
                row.ClaimExpiresAt,
                releaseReceipt,
                ProviderObservation: null);
            output.Add(new(lease, provisionedResource, item));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (output.ToImmutable(), hasMore);
    }

    /// <summary>Completes provisioning only when the provider result matches the reserved lease generation and fence.</summary>
    public async Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource? provisionedResource,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null,
        string? providerErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (operationId == Guid.Empty)
            throw new ArgumentException("A sandbox operation ID is required.", nameof(operationId));
        if (provisionedResource is not null)
        {
            provisionedResource = provisionedResource.Validate();
            if (!effectMayHaveApplied)
                throw new ArgumentException("A provisioned resource proves the provider effect was applied.",
                    nameof(effectMayHaveApplied));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox provision operation is not recorded.");
        if (lease.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Sandbox operation belongs to a stale Environment lifecycle fence.");
        var attempt = providerAttemptId is { } exactAttemptId
            ? await ReadProviderAttemptByIdAsync(
                connection, transaction, exactAttemptId, fence.Owner, cancellationToken).ConfigureAwait(false)
            : null;
        var knownNoEffectBeforeCall =
            attempt is null &&
            providerAttemptId is null &&
            provisionedResource is null &&
            !effectMayHaveApplied;
        if (!knownNoEffectBeforeCall &&
            (attempt is null ||
             attempt.Request != SandboxProviderAttemptRequest.ForProvision(lease)))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The provision result must identify its exact durable provider attempt.");
        if (provisionedResource is not null)
            ValidateProvisionedResource(lease, provisionedResource);
        var ownerFenceIsActive = await IsOwnerFenceActiveAsync(
            connection, transaction, fence, cancellationToken).ConfigureAwait(false);

        var outcome = JsonSerializer.SerializeToElement(new
        {
            provisionedResource,
            effectMayHaveApplied,
            providerErrorCode
        }, JsonOptions);
        var targetAttemptState = provisionedResource is not null
            ? SandboxProviderAttemptState.Completed
            : effectMayHaveApplied
                ? SandboxProviderAttemptState.Unknown
                : SandboxProviderAttemptState.FailedNoEffect;
        if (attempt is not null &&
            (attempt.State is SandboxProviderAttemptState.ResultRecorded or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.FailedNoEffect or
                SandboxProviderAttemptState.Completed or SandboxProviderAttemptState.LegacyUnknown))
        {
            if (attempt.State != targetAttemptState &&
                !(attempt.State == SandboxProviderAttemptState.Unknown &&
                  targetAttemptState == SandboxProviderAttemptState.Completed) &&
                !(attempt.State == SandboxProviderAttemptState.LegacyUnknown &&
                  targetAttemptState == SandboxProviderAttemptState.Completed) &&
                !(attempt.State == SandboxProviderAttemptState.ResultRecorded &&
                  targetAttemptState is SandboxProviderAttemptState.Completed or
                      SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.FailedNoEffect))
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_outcome_conflict",
                    "The provision result conflicts with the durable provider attempt outcome.",
                    SandboxProviderAttemptReceipt.From(attempt));
            if (attempt.State is not (SandboxProviderAttemptState.Unknown or
                    SandboxProviderAttemptState.ResultRecorded or SandboxProviderAttemptState.LegacyUnknown) &&
                attempt.Outcome is { } existingOutcome &&
                !JsonNode.DeepEquals(
                    JsonNode.Parse(existingOutcome.GetRawText()),
                    JsonNode.Parse(outcome.GetRawText())))
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_outcome_conflict",
                    "The provision result conflicts with the durable provider attempt outcome.",
                    SandboxProviderAttemptReceipt.From(attempt));
        }
        else if (attempt is not null && attempt.State != SandboxProviderAttemptState.InProgress)
        {
            throw ProviderAttemptUnresolved(attempt);
        }

        var samePersistedResource =
            lease.ProvisionedResource is not null &&
            provisionedResource is not null &&
            SameProvisionedResource(lease.ProvisionedResource, provisionedResource);
        if (samePersistedResource &&
            (lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed ||
             ownerFenceIsActive && lease.State == SandboxLeaseState.Active))
        {
            if (attempt!.State != SandboxProviderAttemptState.Completed)
                await UpdateProviderAttemptAsync(
                    connection,
                    transaction,
                    attempt,
                    SandboxProviderAttemptState.Completed,
                    outcome,
                    providerErrorCode,
                    attempt.OutcomeRecordedAt ?? timeProvider.GetUtcNow(),
                    attempt.ResolvedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if (provisionedResource is not null &&
            lease.CurrentFencingGeneration != lease.ProviderFencingGeneration &&
            lease.State is not (SandboxLeaseState.Released or SandboxLeaseState.Failed))
        {
            await RecordLateResourceCleanupAsync(
                connection,
                transaction,
                lease,
                provisionedResource,
                attempt!.AttemptId,
                cancellationToken).ConfigureAwait(false);
            var lateUpdatedAt = timeProvider.GetUtcNow();
            await using (var retireLate = new NpgsqlCommand($"""
                UPDATE {Leases}
                SET lease_state = 'Released',
                    resource_json = COALESCE(@resource_json, resource_json),
                    is_current = FALSE,
                    lease_revision = lease_revision + 1,
                    lease_expires_at = NULL,
                    updated_at = @updated_at
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND resource_generation = @resource_generation
                  AND operation_id = @operation_id
                  AND lease_revision = @expected_lease_revision
                """, connection, transaction))
            {
                AddOwnerParameters(retireLate, fence.Owner);
                retireLate.Parameters.AddWithValue(
                    "resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
                retireLate.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
                retireLate.Parameters.AddWithValue(
                    "expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
                retireLate.Parameters.AddWithValue(
                    "resource_json",
                    NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(provisionedResource, JsonOptions));
                retireLate.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, lateUpdatedAt);
                if (await retireLate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new EnvironmentLifecycleException(
                        "sandbox_operation_state_conflict",
                        "The retired Sandbox operation changed before its late resource was recorded.");
            }
            await CompleteProviderAttemptAsync(
                connection,
                transaction,
                attempt!,
                outcome,
                providerErrorCode,
                cancellationToken).ConfigureAwait(false);
            var lateRetiredLease = await ReadByOperationAsync(
                connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The late Sandbox resource cleanup was not persisted.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lateRetiredLease;
        }
        if (!ownerFenceIsActive)
        {
            if (provisionedResource is not null)
                await RecordLateResourceCleanupAsync(
                    connection,
                    transaction,
                    lease,
                    provisionedResource,
                    attempt!.AttemptId,
                    cancellationToken).ConfigureAwait(false);

            var lateState = provisionedResource is not null
                ? SandboxLeaseState.Released
                : effectMayHaveApplied
                    ? SandboxLeaseState.ReconciliationRequired
                    : SandboxLeaseState.Failed;
            var lateUpdatedAt = timeProvider.GetUtcNow();
            await using (var updateLate = new NpgsqlCommand($"""
                UPDATE {Leases}
                SET lease_state = @lease_state,
                    resource_json = COALESCE(@resource_json, resource_json),
                    is_current = FALSE,
                    lease_revision = lease_revision + 1,
                    lease_expires_at = NULL,
                    updated_at = @updated_at
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND resource_generation = @resource_generation
                  AND operation_id = @operation_id
                  AND lease_revision = @expected_lease_revision
                """, connection, transaction))
            {
                AddOwnerParameters(updateLate, fence.Owner);
                updateLate.Parameters.AddWithValue(
                    "resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
                updateLate.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
                updateLate.Parameters.AddWithValue(
                    "expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
                updateLate.Parameters.AddWithValue("lease_state", NpgsqlDbType.Text, lateState.ToString());
                updateLate.Parameters.AddWithValue(
                    "resource_json",
                    NpgsqlDbType.Jsonb,
                    provisionedResource is null
                        ? DBNull.Value
                        : JsonSerializer.Serialize(provisionedResource, JsonOptions));
                updateLate.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, lateUpdatedAt);
                if (await updateLate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new EnvironmentLifecycleException(
                        "sandbox_operation_state_conflict",
                        "The retained Sandbox operation changed before its late provider result was recorded.");
            }

            if (targetAttemptState == SandboxProviderAttemptState.Completed)
            {
                await CompleteProviderAttemptAsync(
                    connection,
                    transaction,
                    attempt!,
                    outcome,
                    providerErrorCode,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (targetAttemptState == SandboxProviderAttemptState.FailedNoEffect && attempt is not null)
            {
                if (attempt.State != SandboxProviderAttemptState.FailedNoEffect)
                    await UpdateProviderAttemptAsync(
                        connection,
                        transaction,
                        attempt,
                        targetAttemptState,
                        outcome,
                        providerErrorCode ?? "provider_no_effect",
                        attempt.OutcomeRecordedAt ?? lateUpdatedAt,
                        attempt.ResolvedAt ?? lateUpdatedAt,
                        cancellationToken).ConfigureAwait(false);
            }
            else if (attempt is not null)
            {
                await UpdateProviderAttemptAsync(
                    connection,
                    transaction,
                    attempt,
                    targetAttemptState,
                    outcome,
                    providerErrorCode,
                    attempt.OutcomeRecordedAt ?? lateUpdatedAt,
                    resolvedAt: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var lateResult = await ReadByOperationAsync(
                connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The late Sandbox provider result was not persisted.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lateResult;
        }
        if ((lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed) &&
            provisionedResource is null &&
            lease.ProvisionedResource is null &&
            !effectMayHaveApplied)
        {
            if (attempt is not null && attempt.State != SandboxProviderAttemptState.FailedNoEffect)
                await UpdateProviderAttemptAsync(
                    connection,
                    transaction,
                    attempt,
                    SandboxProviderAttemptState.FailedNoEffect,
                    outcome,
                    providerErrorCode ?? "provider_no_effect",
                    attempt.OutcomeRecordedAt ?? timeProvider.GetUtcNow(),
                    attempt.ResolvedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if ((lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed) &&
            provisionedResource is not null)
        {
            await RecordLateResourceCleanupAsync(
                connection,
                transaction,
                lease,
                provisionedResource,
                attempt!.AttemptId,
                cancellationToken).ConfigureAwait(false);
            await CompleteProviderAttemptAsync(
                connection,
                transaction,
                attempt,
                outcome,
                providerErrorCode,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if (lease.State == SandboxLeaseState.Releasing &&
            lease.ProvisionedResource is not null &&
            provisionedResource is not null &&
            !SameProvisionedResource(lease.ProvisionedResource, provisionedResource))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_result_conflict",
                "The retiring Sandbox operation returned a different provider resource.");

        var nextState = lease.State switch
        {
            SandboxLeaseState.Provisioning when provisionedResource is not null => SandboxLeaseState.Active,
            SandboxLeaseState.Provisioning when effectMayHaveApplied => SandboxLeaseState.ReconciliationRequired,
            SandboxLeaseState.Provisioning => SandboxLeaseState.Failed,
            SandboxLeaseState.Releasing when provisionedResource is not null => SandboxLeaseState.Releasing,
            SandboxLeaseState.Releasing when effectMayHaveApplied => SandboxLeaseState.ReconciliationRequired,
            SandboxLeaseState.Releasing => SandboxLeaseState.Released,
            SandboxLeaseState.ReconciliationRequired when lease.IsCurrent &&
                                                          lease.RetirementReason is null &&
                                                          lease.CurrentFencingGeneration ==
                                                          lease.ProviderFencingGeneration &&
                                                          provisionedResource is not null =>
                SandboxLeaseState.Active,
            SandboxLeaseState.ReconciliationRequired when lease.RetirementReason is not null &&
                                                          provisionedResource is not null =>
                SandboxLeaseState.Releasing,
            SandboxLeaseState.ReconciliationRequired => SandboxLeaseState.ReconciliationRequired,
            SandboxLeaseState.Active when lease.ProvisionedResource is not null &&
                                          provisionedResource is not null &&
                                          SameProvisionedResource(
                                              lease.ProvisionedResource,
                                              provisionedResource) =>
                SandboxLeaseState.Active,
            SandboxLeaseState.Released or SandboxLeaseState.Failed when provisionedResource is not null =>
                SandboxLeaseState.ReconciliationRequired,
            _ => throw new EnvironmentLifecycleException(
                "sandbox_operation_state_conflict",
                "The Sandbox provision operation cannot complete from its current durable state.")
        };
        var current = lease.IsCurrent &&
            nextState is not (SandboxLeaseState.Released or SandboxLeaseState.Failed);
        var updatedAt = timeProvider.GetUtcNow();
        await using (var update = new NpgsqlCommand($"""
            UPDATE {Leases}
            SET lease_state = @lease_state,
                resource_json = COALESCE(@resource_json, resource_json),
                is_current = @is_current,
                lease_revision = lease_revision + 1,
                lease_expires_at = @lease_expires_at,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND operation_id = @operation_id
              AND lease_revision = @expected_lease_revision
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            update.Parameters.AddWithValue("expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
            update.Parameters.AddWithValue("lease_state", NpgsqlDbType.Text, nextState.ToString());
            update.Parameters.AddWithValue(
                "resource_json",
                NpgsqlDbType.Jsonb,
                provisionedResource is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(provisionedResource, JsonOptions));
            update.Parameters.AddWithValue("is_current", NpgsqlDbType.Boolean, current);
            update.Parameters.AddWithValue(
                "lease_expires_at",
                NpgsqlDbType.TimestampTz,
                nextState is SandboxLeaseState.Released or SandboxLeaseState.Failed
                    ? DBNull.Value
                    : updatedAt + LeaseDuration);
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_operation_state_conflict",
                    "The Sandbox operation changed while its provider result was being recorded.");
        }

        if (attempt is not null && targetAttemptState == SandboxProviderAttemptState.Unknown)
        {
            await UpdateProviderAttemptAsync(
                connection,
                transaction,
                attempt,
                targetAttemptState,
                outcome,
                providerErrorCode,
                attempt.OutcomeRecordedAt ?? timeProvider.GetUtcNow(),
                resolvedAt: null,
                cancellationToken).ConfigureAwait(false);
        }
        else if (attempt is not null && targetAttemptState == SandboxProviderAttemptState.FailedNoEffect)
        {
            if (attempt.State != SandboxProviderAttemptState.FailedNoEffect)
                await UpdateProviderAttemptAsync(
                    connection,
                    transaction,
                    attempt,
                    targetAttemptState,
                    outcome,
                    providerErrorCode ?? "provider_no_effect",
                    attempt.OutcomeRecordedAt ?? timeProvider.GetUtcNow(),
                    attempt.ResolvedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
        }
        else if (attempt is not null)
        {
            await CompleteProviderAttemptAsync(
                connection,
                transaction,
                attempt,
                outcome,
                providerErrorCode,
                cancellationToken).ConfigureAwait(false);
        }

        var result = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox provision result was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Fences the current lease for retirement before any external Sandbox deletion begins.</summary>
    public async Task<SandboxLeaseSnapshot> BeginRetirementAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        long providerFencingGeneration,
        SandboxRetirementReason reason,
        string idempotencyKey,
        SandboxRetirementAuthorization? authorization,
        SandboxTerminalEvidence? terminalEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (resourceGeneration < 1 || providerFencingGeneration < 1 ||
            !Enum.IsDefined(reason) ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128 ||
            idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("The Sandbox retirement request is invalid.");
        if (reason == SandboxRetirementReason.AuthorizedAbandon)
        {
            authorization = (authorization ?? throw new ArgumentNullException(nameof(authorization))).Validate();
            if (terminalEvidence is not null)
                throw new ArgumentException("Abandonment cannot include provider terminal evidence.",
                    nameof(terminalEvidence));
        }
        else
        {
            terminalEvidence = (terminalEvidence ?? throw new ArgumentNullException(nameof(terminalEvidence))).Validate();
            if (authorization is not null)
                throw new ArgumentException("Terminal retirement does not accept caller-supplied authorization evidence.",
                    nameof(authorization));
        }
        var fingerprint = RetirementFingerprint(
            reason, authorization, terminalEvidence, resourceGeneration, providerFencingGeneration);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByGenerationAsync(
            connection, transaction, fence.Owner, resourceGeneration, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_lease_unknown",
                "The exact Sandbox resource generation is not recorded.");
        if (lease.State == SandboxLeaseState.Released &&
            lease.ProviderFencingGeneration == providerFencingGeneration &&
            string.Equals(lease.ReleaseIdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
            string.Equals(lease.RetirementFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if (!lease.IsCurrent || lease.ProviderFencingGeneration != providerFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease is no longer current at the requested provider fence.");

        if (lease.State == SandboxLeaseState.Releasing)
        {
            if (!string.Equals(lease.ReleaseIdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(lease.RetirementFingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "sandbox_idempotency_conflict",
                    "The Sandbox lease is already retiring under a different authorized operation.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if (lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_not_active",
                "A released or failed Sandbox lease cannot be retired again.");
        if (reason == SandboxRetirementReason.Finished &&
            (lease.ProvisionedResource is null ||
             !string.Equals(
                 terminalEvidence!.ClaimUid,
                 lease.ProvisionedResource.Resource.ResourceId,
                 StringComparison.Ordinal) ||
             terminalEvidence.FencingGeneration != lease.ProviderFencingGeneration))
            throw new EnvironmentLifecycleException(
                "sandbox_terminal_evidence_mismatch",
                "Terminal evidence does not identify the exact recorded Sandbox claim and fence.");

        var nextFence = checked(lease.CurrentFencingGeneration + 1);
        var updatedAt = timeProvider.GetUtcNow();
        await using (var update = new NpgsqlCommand($"""
            UPDATE {Leases}
            SET current_fencing_generation = @current_fencing_generation,
                lease_state = 'Releasing',
                retirement_reason = @retirement_reason,
                terminal_evidence_json = @terminal_evidence_json,
                retiring_issuer = @retiring_issuer,
                retiring_actor_id = @retiring_actor_id,
                retiring_membership_revision = @retiring_membership_revision,
                release_idempotency_key = @release_idempotency_key,
                retirement_fingerprint = @retirement_fingerprint,
                lease_revision = lease_revision + 1,
                lease_expires_at = @lease_expires_at,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND operation_id = @operation_id
              AND current_fencing_generation = @expected_current_fence
              AND lease_revision = @expected_lease_revision
              AND lease_state IN ('Provisioning', 'Active', 'ReconciliationRequired')
              AND is_current = TRUE
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, resourceGeneration);
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, lease.OperationId);
            update.Parameters.AddWithValue("current_fencing_generation", NpgsqlDbType.Bigint, nextFence);
            update.Parameters.AddWithValue("expected_current_fence", NpgsqlDbType.Bigint, lease.CurrentFencingGeneration);
            update.Parameters.AddWithValue("expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
            update.Parameters.AddWithValue("retirement_reason", NpgsqlDbType.Text, reason.ToString());
            update.Parameters.AddWithValue(
                "terminal_evidence_json",
                NpgsqlDbType.Jsonb,
                terminalEvidence is null ? DBNull.Value : JsonSerializer.Serialize(terminalEvidence, JsonOptions));
            update.Parameters.AddWithValue(
                "retiring_issuer",
                NpgsqlDbType.Text,
                authorization?.Issuer is null ? DBNull.Value : authorization.Issuer);
            update.Parameters.AddWithValue(
                "retiring_actor_id",
                NpgsqlDbType.Text,
                authorization?.ActorId is null ? DBNull.Value : authorization.ActorId);
            update.Parameters.AddWithValue(
                "retiring_membership_revision",
                NpgsqlDbType.Bigint,
                authorization is null ? DBNull.Value : authorization.MembershipRevision);
            update.Parameters.AddWithValue("release_idempotency_key", NpgsqlDbType.Text, idempotencyKey);
            update.Parameters.AddWithValue("retirement_fingerprint", NpgsqlDbType.Text, fingerprint);
            update.Parameters.AddWithValue(
                "lease_expires_at",
                NpgsqlDbType.TimestampTz,
                updatedAt + LeaseDuration);
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_fence_stale",
                    "The Sandbox lease changed before retirement could be fenced.");
        }

        var result = await ReadByOperationAsync(
            connection, transaction, lease.OperationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox retirement was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Records full release after the Sandbox resource is confirmed deleted or absent.</summary>
    public async Task<SandboxLeaseSnapshot> CompleteReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        long providerFencingGeneration,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null,
        Guid? providerAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(receipt);
        if (operationId == Guid.Empty || providerFencingGeneration < 1)
            throw new ArgumentException("The Sandbox release identity is invalid.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox release operation is not recorded.");
        var attempt = providerAttemptId is { } exactAttemptId
            ? await ReadProviderAttemptByIdAsync(
                connection, transaction, exactAttemptId, fence.Owner, cancellationToken).ConfigureAwait(false)
            : null;
        var expectedAttempt = lease.ProvisionedResource is null
            ? null
            : SandboxProviderAttemptRequest.ForRelease(lease, lease.ProvisionedResource);
        var receiptOutcome = JsonSerializer.SerializeToElement(receipt, JsonOptions);
        if (attempt is null || expectedAttempt is null || attempt.Request != expectedAttempt)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The release result must identify its exact durable provider attempt.");
        if ((attempt.State is SandboxProviderAttemptState.ResultRecorded or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.Completed) &&
            attempt.Outcome is { } storedOutcome &&
            attempt.State != SandboxProviderAttemptState.Unknown &&
            !JsonNode.DeepEquals(
                JsonNode.Parse(storedOutcome.GetRawText()),
                JsonNode.Parse(receiptOutcome.GetRawText())))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_outcome_conflict",
                "The release receipt conflicts with the durable provider attempt outcome.",
                SandboxProviderAttemptReceipt.From(attempt));
        if (lease.State == SandboxLeaseState.Released &&
            attempt.State == SandboxProviderAttemptState.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if (lease.Fence != fence ||
            lease.State != SandboxLeaseState.Releasing ||
            lease.ProviderFencingGeneration != providerFencingGeneration ||
            lease.ProvisionedResource is null ||
            lease.ReleaseIdempotencyKey is null ||
            receipt.Resource != lease.ProvisionedResource.Resource ||
            !string.Equals(receipt.IdempotencyKey, lease.ReleaseIdempotencyKey, StringComparison.Ordinal) ||
            !Enum.IsDefined(receipt.Disposition))
            throw new EnvironmentLifecycleException(
                "sandbox_release_receipt_mismatch",
                "The Sandbox release receipt does not match the exact fenced provider resource.");
        if (beforeCommitCheck is not null)
            await beforeCommitCheck(cancellationToken).ConfigureAwait(false);

        var updatedAt = timeProvider.GetUtcNow();
        await using (var update = new NpgsqlCommand($"""
            UPDATE {Leases}
            SET lease_state = 'Released',
                is_current = FALSE,
                lease_revision = lease_revision + 1,
                lease_expires_at = NULL,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND operation_id = @operation_id
              AND current_fencing_generation = @current_fencing_generation
              AND lease_revision = @expected_lease_revision
              AND lease_state = 'Releasing' AND is_current = TRUE
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            update.Parameters.AddWithValue("current_fencing_generation", NpgsqlDbType.Bigint, lease.CurrentFencingGeneration);
            update.Parameters.AddWithValue("expected_lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_fence_stale",
                    "The Sandbox lease changed before release completion could be recorded.");
        }
        await CompleteProviderAttemptAsync(
            connection,
            transaction,
            attempt,
            receiptOutcome,
            failureCode: null,
            cancellationToken).ConfigureAwait(false);
        var result = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox release completion was not persisted.");
        if (beforeCommitCheck is not null)
            await beforeCommitCheck(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Records partial release and preserves unresolved resource cleanup for later reconciliation.</summary>
    public async Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
        SandboxPartialReleaseCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Fence);
        ArgumentNullException.ThrowIfNull(request.Receipt);
        var fence = request.Fence;
        var receipt = request.Receipt;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, receipt.OperationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox partial-release operation is not recorded.");
        if (lease.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Sandbox partial-release operation belongs to a stale Environment lifecycle fence.");
        var attempt = request.ProviderAttemptId is { } exactAttemptId
            ? await ReadProviderAttemptByIdAsync(
                connection, transaction, exactAttemptId, fence.Owner, cancellationToken).ConfigureAwait(false)
            : null;
        var expectedAttempt = SandboxProviderAttemptRequest.ForPartialRelease(lease);
        var receiptOutcome = JsonSerializer.SerializeToElement(receipt, JsonOptions);
        if (attempt is null || attempt.Request != expectedAttempt)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The partial-release result must identify its exact durable provider attempt.");
        if ((attempt.State is SandboxProviderAttemptState.ResultRecorded or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.Completed) &&
            attempt.Outcome is { } storedOutcome &&
            attempt.State != SandboxProviderAttemptState.Unknown &&
            !JsonNode.DeepEquals(
                JsonNode.Parse(storedOutcome.GetRawText()),
                JsonNode.Parse(receiptOutcome.GetRawText())))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_outcome_conflict",
                "The partial-release receipt conflicts with the durable provider attempt outcome.",
                SandboxProviderAttemptReceipt.From(attempt));
        if (lease.State == SandboxLeaseState.Released &&
            lease.PartialReleaseReceipt == receipt &&
            attempt.State == SandboxProviderAttemptState.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }

        var partialRequest = new SandboxPartialReleaseRequest(fence, lease);
        try
        {
            _ = receipt.ValidateFor(partialRequest);
        }
        catch (ArgumentException)
        {
            throw new EnvironmentLifecycleException(
                "sandbox_partial_release_receipt_mismatch",
                "The partial Sandbox release receipt does not match the current retired lease.");
        }

        var updatedAt = timeProvider.GetUtcNow();
        await using (var update = new NpgsqlCommand($"""
            UPDATE {Leases}
            SET lease_state = 'Released',
                is_current = FALSE,
                lease_revision = lease_revision + 1,
                lease_expires_at = NULL,
                partial_release_receipt_json = @partial_release_receipt_json,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND operation_id = @operation_id
              AND provider_fencing_generation = @provider_fencing_generation
              AND current_fencing_generation = @current_fencing_generation
              AND lease_revision = @expected_lease_revision
              AND lease_state IN ('Releasing', 'ReconciliationRequired')
              AND retirement_reason = 'AuthorizedAbandon'
              AND provider_request_fingerprint IS NOT NULL
              AND release_idempotency_key = @release_idempotency_key
              AND resource_json IS NULL AND is_current = TRUE
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, lease.OperationId);
            update.Parameters.AddWithValue(
                "provider_fencing_generation",
                NpgsqlDbType.Bigint,
                lease.ProviderFencingGeneration);
            update.Parameters.AddWithValue(
                "current_fencing_generation",
                NpgsqlDbType.Bigint,
                lease.CurrentFencingGeneration);
            update.Parameters.AddWithValue(
                "expected_lease_revision",
                NpgsqlDbType.Bigint,
                lease.LeaseRevision);
            update.Parameters.AddWithValue(
                "release_idempotency_key",
                NpgsqlDbType.Text,
                lease.ReleaseIdempotencyKey!);
            update.Parameters.AddWithValue(
                "partial_release_receipt_json",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(receipt, JsonOptions));
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_fence_stale",
                    "The partial Sandbox lease changed before retirement could be committed.");
        }

        var result = await ReadByOperationAsync(
            connection, transaction, lease.OperationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The partial Sandbox release was not persisted.");
        await CompleteProviderAttemptAsync(
            connection,
            transaction,
            attempt,
            receiptOutcome,
            failureCode: null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Claims the next due late-resource cleanup with a bounded lease for one worker.</summary>
    public async Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        long resourceGeneration = 0;
        string resourceFingerprint = string.Empty;
        string resourceJson = string.Empty;
        string releaseIdempotencyKey = string.Empty;
        Guid? provisionAttemptId = null;
        Guid? releaseAttemptId = null;
        Guid? existingClaimToken = null;
        DateTimeOffset? existingClaimExpiresAt = null;
        var hasCandidate = false;
        await using (var select = new NpgsqlCommand($"""
            SELECT resource_generation, provider_resource_fingerprint, resource_json,
                   release_idempotency_key, provision_attempt_id, release_attempt_id,
                   claim_token, claim_expires_at
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND release_receipt_json IS NULL
            ORDER BY created_at, resource_generation, provider_resource_fingerprint
            LIMIT 1
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwnerParameters(select, fence.Owner);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resourceGeneration = reader.GetInt64(0);
                resourceFingerprint = reader.GetString(1);
                resourceJson = reader.GetString(2);
                releaseIdempotencyKey = reader.GetString(3);
                provisionAttemptId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
                releaseAttemptId = reader.IsDBNull(5) ? null : reader.GetGuid(5);
                existingClaimToken = reader.IsDBNull(6) ? null : reader.GetGuid(6);
                existingClaimExpiresAt = reader.IsDBNull(7)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(7);
                hasCandidate = true;
            }
        }
        if (!hasCandidate)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (existingClaimToken is not null &&
            existingClaimExpiresAt is { } activeClaimExpiry &&
            activeClaimExpiry > now)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (releaseAttemptId is { } existingReleaseAttemptId)
        {
            var previousReleaseAttempt = await ReadProviderAttemptByIdAsync(
                connection,
                transaction,
                existingReleaseAttemptId,
                fence.Owner,
                cancellationToken).ConfigureAwait(false)
                ?? throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_unknown",
                    "The late Sandbox cleanup references a missing release attempt.");
            if (previousReleaseAttempt.State is SandboxProviderAttemptState.InProgress or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.LegacyUnknown)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (previousReleaseAttempt.State == SandboxProviderAttemptState.Completed)
                throw new InvalidOperationException(
                    "A completed late Sandbox release attempt has no cleanup receipt.");
        }
        if (provisionAttemptId is not { } sourceAttemptId)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_attempt_unknown",
                "The late Sandbox cleanup has no linked provision attempt.");
        var sourceAttempt = await ReadProviderAttemptByIdAsync(
            connection,
            transaction,
            sourceAttemptId,
            fence.Owner,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unknown",
                "The late Sandbox cleanup references a missing provision attempt.");
        if (sourceAttempt.State != SandboxProviderAttemptState.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var claimToken = Guid.NewGuid();
        var claimExpiresAt = now + LateCleanupClaimDuration;
        await using (var update = new NpgsqlCommand($"""
            UPDATE {LateResourceCleanups}
            SET claim_token = @claim_token,
                claim_expires_at = @claim_expires_at,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND provider_resource_fingerprint = @provider_resource_fingerprint
              AND release_receipt_json IS NULL
              AND (claim_expires_at IS NULL OR claim_expires_at <= @updated_at)
            RETURNING claim_expires_at
            """, connection, transaction))
        {
            AddOwnerParameters(update, fence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, resourceGeneration);
            update.Parameters.AddWithValue("provider_resource_fingerprint", NpgsqlDbType.Text, resourceFingerprint);
            update.Parameters.AddWithValue("claim_token", NpgsqlDbType.Uuid, claimToken);
            update.Parameters.AddWithValue("claim_expires_at", NpgsqlDbType.TimestampTz, claimExpiresAt);
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_claim_conflict",
                    "The late Sandbox resource cleanup changed before it could be claimed.");
            claimExpiresAt = reader.GetFieldValue<DateTimeOffset>(0);
        }

        var lease = await ReadByGenerationAsync(
            connection, transaction, fence.Owner, resourceGeneration, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The late Sandbox cleanup lease was not found.");
        var provisionedResource = JsonSerializer.Deserialize<SandboxProvisionedResource>(
            resourceJson,
            JsonOptions) ?? throw new InvalidOperationException("The late Sandbox resource is empty.");
        ValidateProvisionedResource(lease, provisionedResource);
        if (!string.Equals(
                LateResourceFingerprint(lease, provisionedResource.Resource),
                resourceFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                LateResourceIdempotencyKey(resourceFingerprint),
                releaseIdempotencyKey,
                StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_identity_mismatch",
                "The persisted late Sandbox cleanup identity is inconsistent.");
        var claim = new SandboxLateResourceCleanupClaim(
            lease,
            provisionedResource,
            releaseIdempotencyKey,
            claimToken,
            claimExpiresAt)
        {
            ResourceFingerprint = resourceFingerprint,
            ProvisionAttemptId = provisionAttemptId,
            ReleaseAttemptId = releaseAttemptId
        }.Validate();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claim;
    }

    /// <summary>Completes a cleanup claim or records its retry state after provider work.</summary>
    public async Task CompleteLateResourceCleanupAsync(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim claim,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(currentFence);
        claim = (claim ?? throw new ArgumentNullException(nameof(claim))).Validate();
        ArgumentNullException.ThrowIfNull(receipt);
        if (claim.Lease.Fence.Owner != currentFence.Owner)
            throw new EnvironmentLifecycleException(
                "environment_owner_mismatch",
                "The late Sandbox cleanup belongs to a different Environment owner.");
        var releaseRequest = new SandboxReleaseRequest(
            claim.Lease.Fence,
            claim.ProvisionedResource.Resource,
            claim.Lease.ProviderFencingGeneration,
            claim.ProvisionedResource.ProviderBinding,
            claim.IdempotencyKey).Validate();
        try
        {
            _ = receipt.ValidateFor(releaseRequest);
        }
        catch (ArgumentException)
        {
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_receipt_mismatch",
                "The late Sandbox cleanup receipt does not identify the exact provider resource.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, currentFence.Owner, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByGenerationAsync(
            connection,
            transaction,
            currentFence.Owner,
            claim.Lease.ResourceGeneration,
            cancellationToken).ConfigureAwait(false);
        if (lease is null ||
            lease.Fence != claim.Lease.Fence ||
            lease.OperationId != claim.Lease.OperationId ||
            lease.State is not (SandboxLeaseState.Released or SandboxLeaseState.Failed) ||
            lease.IsCurrent)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_fence_stale",
                "The terminal Sandbox lease changed before late cleanup could be committed.");
        ValidateProvisionedResource(lease, claim.ProvisionedResource);
        var fingerprint = LateResourceFingerprint(lease, claim.ProvisionedResource.Resource);
        if (!string.Equals(claim.IdempotencyKey, LateResourceIdempotencyKey(fingerprint), StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_identity_mismatch",
                "The late Sandbox cleanup claim does not match its persisted identity.");

        string? persistedReceiptJson;
        Guid? persistedClaimToken;
        DateTimeOffset? persistedClaimExpiresAt;
        Guid? persistedReleaseAttemptId;
        await using (var select = new NpgsqlCommand($"""
            SELECT claim_token, claim_expires_at, release_receipt_json, release_attempt_id
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND provider_resource_fingerprint = @provider_resource_fingerprint
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwnerParameters(select, currentFence.Owner);
            select.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            select.Parameters.AddWithValue("provider_resource_fingerprint", NpgsqlDbType.Text, fingerprint);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_unknown",
                    "The exact late Sandbox cleanup claim is not recorded.");
            persistedClaimToken = reader.IsDBNull(0) ? null : reader.GetGuid(0);
            persistedClaimExpiresAt = reader.IsDBNull(1)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(1);
            persistedReceiptJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            persistedReleaseAttemptId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        }

        var attempt = providerAttemptId is { } exactAttemptId
            ? await ReadProviderAttemptByIdAsync(
                connection, transaction, exactAttemptId, currentFence.Owner, cancellationToken).ConfigureAwait(false)
            : null;
        var expectedAttempt = SandboxProviderAttemptRequest.ForLateResourceRelease(claim);
        var receiptOutcome = JsonSerializer.SerializeToElement(receipt, JsonOptions);
        if (attempt is null ||
            attempt.Request != expectedAttempt ||
            persistedReleaseAttemptId != attempt.AttemptId)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The late cleanup result must identify its exact durable provider attempt.");
        if ((attempt.State is SandboxProviderAttemptState.ResultRecorded or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.Completed) &&
            attempt.Outcome is { } storedOutcome &&
            attempt.State != SandboxProviderAttemptState.Unknown &&
            !JsonNode.DeepEquals(
                JsonNode.Parse(storedOutcome.GetRawText()),
                JsonNode.Parse(receiptOutcome.GetRawText())))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_outcome_conflict",
                "The late cleanup receipt conflicts with the durable provider attempt outcome.",
                SandboxProviderAttemptReceipt.From(attempt));

        if (persistedReceiptJson is not null)
        {
            var persistedReceipt = JsonSerializer.Deserialize<SandboxReleaseReceipt>(
                persistedReceiptJson,
                JsonOptions) ?? throw new InvalidOperationException("The stored late Sandbox receipt is empty.");
            if (persistedReceipt != receipt)
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_receipt_conflict",
                    "The late Sandbox cleanup already has a different durable receipt.");
            if (attempt.State != SandboxProviderAttemptState.Completed)
                throw new InvalidOperationException(
                    "A persisted late Sandbox cleanup receipt has an unresolved provider attempt.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (persistedClaimToken != claim.ClaimToken)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_claim_stale",
                "The late Sandbox cleanup claim was superseded before its receipt could be committed.");

        var updatedAt = timeProvider.GetUtcNow();
        if (persistedClaimExpiresAt != claim.ClaimExpiresAt)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_claim_stale",
                "The late Sandbox cleanup claim expiry changed before its receipt could be committed.");
        if (persistedClaimExpiresAt is not { } claimExpiresAt)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_claim_stale",
                "The late Sandbox cleanup no longer has its original durable claim.");
        await using (var update = new NpgsqlCommand($"""
            UPDATE {LateResourceCleanups}
            SET claim_token = NULL,
                claim_expires_at = NULL,
                release_receipt_json = @release_receipt_json,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND provider_resource_fingerprint = @provider_resource_fingerprint
              AND claim_token = @claim_token
              AND claim_expires_at = @claim_expires_at
              AND release_receipt_json IS NULL
            """, connection, transaction))
        {
            AddOwnerParameters(update, currentFence.Owner);
            update.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
            update.Parameters.AddWithValue("provider_resource_fingerprint", NpgsqlDbType.Text, fingerprint);
            update.Parameters.AddWithValue("claim_token", NpgsqlDbType.Uuid, claim.ClaimToken);
            update.Parameters.AddWithValue("claim_expires_at", NpgsqlDbType.TimestampTz, claimExpiresAt);
            update.Parameters.AddWithValue(
                "release_receipt_json",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(receipt, JsonOptions));
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, updatedAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_claim_stale",
                    "The late Sandbox cleanup claim changed before its receipt could be committed.");
        }

        await CompleteProviderAttemptAsync(
            connection,
            transaction,
            attempt,
            receiptOutcome,
            failureCode: null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordLateResourceCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource provisionedResource,
        Guid provisionAttemptId,
        CancellationToken cancellationToken)
    {
        var fingerprint = LateResourceFingerprint(lease, provisionedResource.Resource);
        var now = timeProvider.GetUtcNow();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {LateResourceCleanups}
                (tenant_id, project_id, run_id, environment_id, resource_generation,
                 provider_resource_fingerprint, resource_json, release_idempotency_key,
                 provision_attempt_id, created_at, updated_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, @resource_generation,
                 @provider_resource_fingerprint, @resource_json, @release_idempotency_key,
                 @provision_attempt_id, @created_at, @updated_at)
            ON CONFLICT
                (tenant_id, project_id, run_id, environment_id, resource_generation,
                 provider_resource_fingerprint)
            DO NOTHING
            """, connection, transaction);
        AddOwnerParameters(insert, lease.Fence.Owner);
        insert.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
        insert.Parameters.AddWithValue("provider_resource_fingerprint", NpgsqlDbType.Text, fingerprint);
        insert.Parameters.AddWithValue(
            "resource_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(provisionedResource, JsonOptions));
        insert.Parameters.AddWithValue(
            "release_idempotency_key",
            NpgsqlDbType.Text,
            LateResourceIdempotencyKey(fingerprint));
        insert.Parameters.AddWithValue(
            "provision_attempt_id",
            NpgsqlDbType.Uuid,
            provisionAttemptId);
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var verify = new NpgsqlCommand($"""
            SELECT provision_attempt_id
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
              AND provider_resource_fingerprint = @provider_resource_fingerprint
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(verify, lease.Fence.Owner);
        verify.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
        verify.Parameters.AddWithValue("provider_resource_fingerprint", NpgsqlDbType.Text, fingerprint);
        var persistedAttemptId = await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (persistedAttemptId is not Guid exactAttemptId || exactAttemptId != provisionAttemptId)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_attempt_mismatch",
                "The late Sandbox cleanup is not linked to the exact provision attempt.");
    }

    private static void ValidateProviderAttemptRequestForLease(
        SandboxProviderAttemptRequest request,
        SandboxLeaseSnapshot lease)
    {
        if (request.Fence != lease.Fence ||
            request.ResourceGeneration != lease.ResourceGeneration ||
            request.ProviderFencingGeneration != lease.ProviderFencingGeneration ||
            request.OperationId != lease.OperationId)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The Sandbox provider attempt does not identify the exact saved operation and resource fence.");

        var expected = request.EffectKind switch
        {
            SandboxProviderEffectKind.Provision
                when lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.ReconciliationRequired =>
                SandboxProviderAttemptRequest.ForProvision(lease),
            SandboxProviderEffectKind.Release
                when lease.State == SandboxLeaseState.Releasing && lease.ProvisionedResource is not null =>
                SandboxProviderAttemptRequest.ForRelease(lease, lease.ProvisionedResource),
            SandboxProviderEffectKind.PartialRelease
                when (lease.State is SandboxLeaseState.Releasing or SandboxLeaseState.ReconciliationRequired) &&
                     lease.RetirementReason == SandboxRetirementReason.AuthorizedAbandon &&
                     lease.ProvisionedResource is null =>
                SandboxProviderAttemptRequest.ForPartialRelease(lease),
            SandboxProviderEffectKind.LateResourceRelease
                when !lease.IsCurrent &&
                     (lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed) =>
                null,
            _ => throw new EnvironmentLifecycleException(
                "sandbox_provider_effect_invalid",
                "The provider effect is not valid for the exact Sandbox lease state.")
        };
        if (expected is not null && expected != request)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The Sandbox provider attempt fingerprint does not match the saved provider request.");
    }

    private static async Task<SandboxProviderAttemptSnapshot?> ReadLatestProviderAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxProviderAttemptRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT attempt_id, lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                   attempt_state, outcome_json, failure_code, started_at, outcome_recorded_at, resolved_at
            FROM {ProviderAttempts}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND operation_id = @operation_id
              AND resource_generation = @resource_generation
              AND effect_kind = @effect_kind
              AND request_fingerprint = @request_fingerprint
            ORDER BY started_at DESC, attempt_id DESC
            LIMIT 1
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, request.Fence.Owner);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.OperationId);
        command.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, request.ResourceGeneration);
        command.Parameters.AddWithValue("effect_kind", NpgsqlDbType.Text, request.EffectKind.ToString());
        command.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, request.RequestFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProviderAttempt(reader, request.Fence.Owner)
            : null;
    }

    private static async Task<SandboxProviderAttemptSnapshot?> ReadAnyUnresolvedProviderAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT attempt_id, lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                   attempt_state, outcome_json, failure_code, started_at, outcome_recorded_at, resolved_at
            FROM {ProviderAttempts}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND attempt_state IN ('InProgress', 'ResultRecorded', 'Unknown', 'LegacyUnknown')
            ORDER BY started_at, attempt_id
            LIMIT 1
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProviderAttempt(reader, owner)
            : null;
    }

    private static async Task EnsureNoUnresolvedSandboxWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        var unresolved = await ReadAnyUnresolvedProviderAttemptAsync(
            connection, transaction, owner, cancellationToken).ConfigureAwait(false);
        if (unresolved is not null)
            throw ProviderAttemptUnresolved(unresolved);

        await using var command = new NpgsqlCommand($"""
            SELECT resource_generation
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND release_receipt_json IS NULL
            ORDER BY created_at, resource_generation, provider_resource_fingerprint
            LIMIT 1
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_pending",
                "A late Sandbox resource must be cleaned up before another Sandbox generation can be reserved.");
    }

    private static async Task<SandboxProviderAttemptSnapshot?> ReadProviderAttemptByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid attemptId,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT attempt_id, lifecycle_generation, resource_generation, provider_fencing_generation,
                   operation_id, effect_kind, request_fingerprint, late_resource_fingerprint,
                   attempt_state, outcome_json, failure_code, started_at, outcome_recorded_at, resolved_at
            FROM {ProviderAttempts}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND attempt_id = @attempt_id
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Uuid, attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProviderAttempt(reader, owner)
            : null;
    }

    private static SandboxProviderAttemptSnapshot ReadProviderAttempt(
        NpgsqlDataReader reader,
        EnvironmentOwnerIdentity owner)
    {
        var attemptId = reader.GetGuid(0);
        var fence = new EnvironmentGenerationFence(owner, reader.GetInt64(1));
        var resourceGeneration = reader.GetInt64(2);
        var providerFencingGeneration = reader.GetInt64(3);
        var operationId = reader.GetGuid(4);
        var effectKind = Enum.Parse<SandboxProviderEffectKind>(reader.GetString(5), ignoreCase: false);
        var request = new SandboxProviderAttemptRequest(
            fence,
            resourceGeneration,
            providerFencingGeneration,
            operationId,
            effectKind,
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7)).Validate();
        var state = Enum.Parse<SandboxProviderAttemptState>(reader.GetString(8), ignoreCase: false);
        JsonElement? outcome = null;
        if (!reader.IsDBNull(9))
        {
            using var document = JsonDocument.Parse(reader.GetString(9));
            outcome = document.RootElement.Clone();
        }
        var failureCode = reader.IsDBNull(10) ? null : reader.GetString(10);
        var startedAt = reader.GetFieldValue<DateTimeOffset>(11);
        DateTimeOffset? outcomeRecordedAt = reader.IsDBNull(12)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(12);
        DateTimeOffset? resolvedAt = reader.IsDBNull(13)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(13);
        return new SandboxProviderAttemptSnapshot(
            attemptId,
            request,
            state,
            outcome,
            failureCode,
            startedAt,
            outcomeRecordedAt,
            resolvedAt).Validate();
    }

    private async Task UpdateProviderAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxProviderAttemptSnapshot current,
        SandboxProviderAttemptState state,
        JsonElement outcome,
        string? failureCode,
        DateTimeOffset outcomeRecordedAt,
        DateTimeOffset? resolvedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {ProviderAttempts}
            SET attempt_state = @attempt_state,
                outcome_json = @outcome_json,
                failure_code = @failure_code,
                outcome_recorded_at = @outcome_recorded_at,
                resolved_at = @resolved_at,
                updated_at = @updated_at
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND attempt_id = @attempt_id AND attempt_state = @expected_state
            """, connection, transaction);
        AddOwnerParameters(command, current.Request.Fence.Owner);
        command.Parameters.AddWithValue("attempt_id", NpgsqlDbType.Uuid, current.AttemptId);
        command.Parameters.AddWithValue("expected_state", NpgsqlDbType.Text, current.State.ToString());
        command.Parameters.AddWithValue("attempt_state", NpgsqlDbType.Text, state.ToString());
        command.Parameters.AddWithValue("outcome_json", NpgsqlDbType.Jsonb, outcome.GetRawText());
        command.Parameters.AddWithValue(
            "failure_code",
            NpgsqlDbType.Text,
            failureCode is null ? DBNull.Value : failureCode);
        command.Parameters.AddWithValue(
            "outcome_recorded_at", NpgsqlDbType.TimestampTz, outcomeRecordedAt);
        command.Parameters.AddWithValue(
            "resolved_at",
            NpgsqlDbType.TimestampTz,
            resolvedAt is null ? DBNull.Value : resolvedAt.Value);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_conflict",
                "The Sandbox provider attempt changed before its outcome could be recorded.");
    }

    private async Task CompleteProviderAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxProviderAttemptSnapshot attempt,
        JsonElement outcome,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (attempt.State == SandboxProviderAttemptState.Completed)
            return;
        if (attempt.State == SandboxProviderAttemptState.FailedNoEffect ||
            attempt.State == SandboxProviderAttemptState.LegacyUnknown &&
                attempt.Request.EffectKind != SandboxProviderEffectKind.Provision)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_outcome_conflict",
                "The provider result cannot resolve this attempt state.",
                SandboxProviderAttemptReceipt.From(attempt));
        var recordedAt = attempt.OutcomeRecordedAt ?? timeProvider.GetUtcNow();
        var resolvedAt = attempt.ResolvedAt ?? timeProvider.GetUtcNow();
        await UpdateProviderAttemptAsync(
            connection,
            transaction,
            attempt,
            SandboxProviderAttemptState.Completed,
            outcome,
            failureCode,
            recordedAt,
            resolvedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private static EnvironmentLifecycleException ProviderAttemptUnresolved(
        SandboxProviderAttemptSnapshot attempt) =>
        new(
            "sandbox_provider_attempt_unresolved",
            "A prior Sandbox provider attempt has no durable resolution; another provider effect is blocked.",
            SandboxProviderAttemptReceipt.From(attempt));

    private async Task EnsureProviderEffectDoesNotOvertakeLateCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxProviderAttemptRequest request,
        CancellationToken cancellationToken)
    {
        long resourceGeneration = 0;
        string? fingerprint = null;
        string? resourceJson = null;
        string? idempotencyKey = null;
        Guid? provisionAttemptId = null;
        Guid? claimToken = null;
        DateTimeOffset? claimExpiresAt = null;
        var found = false;
        await using (var command = new NpgsqlCommand($"""
            SELECT resource_generation, provider_resource_fingerprint, resource_json,
                   release_idempotency_key, provision_attempt_id, claim_token, claim_expires_at
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND release_receipt_json IS NULL
            ORDER BY created_at, resource_generation, provider_resource_fingerprint
            LIMIT 1
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwnerParameters(command, request.Fence.Owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resourceGeneration = reader.GetInt64(0);
                fingerprint = reader.GetString(1);
                resourceJson = reader.GetString(2);
                idempotencyKey = reader.GetString(3);
                provisionAttemptId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
                claimToken = reader.IsDBNull(5) ? null : reader.GetGuid(5);
                claimExpiresAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6);
                found = true;
            }
        }

        if (request.EffectKind != SandboxProviderEffectKind.LateResourceRelease)
        {
            if (found)
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_pending",
                    "A late Sandbox resource must be cleaned up before another provider effect can begin.");
            return;
        }

        if (!found ||
            resourceGeneration != request.ResourceGeneration ||
            !string.Equals(fingerprint, request.LateResourceFingerprint, StringComparison.Ordinal) ||
            provisionAttemptId is null)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_unknown",
                "The exact late Sandbox resource is not the next pending cleanup.");
        if (claimToken is null || claimExpiresAt is not { } expiresAt || expiresAt <= timeProvider.GetUtcNow())
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_claim_expired",
                "A current late Sandbox cleanup claim is required before its provider effect can begin.");

        var sourceAttempt = await ReadProviderAttemptByIdAsync(
            connection, transaction, provisionAttemptId.Value, request.Fence.Owner, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unknown",
                "The late Sandbox resource has no durable provision attempt.");
        if (sourceAttempt.State != SandboxProviderAttemptState.Completed)
            throw ProviderAttemptUnresolved(sourceAttempt);

        var lease = await ReadByGenerationAsync(
            connection,
            transaction,
            request.Fence.Owner,
            resourceGeneration,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_unknown",
                "The late Sandbox resource does not have its retained lease history.");
        var provisionedResource = JsonSerializer.Deserialize<SandboxProvisionedResource>(
            resourceJson!, JsonOptions)
            ?? throw new InvalidOperationException("The stored late Sandbox resource is empty.");
        if (sourceAttempt.Request.EffectKind != SandboxProviderEffectKind.Provision ||
            sourceAttempt.Request.OperationId != lease.OperationId ||
            sourceAttempt.Request.ResourceGeneration != lease.ResourceGeneration ||
            sourceAttempt.Request.Fence != lease.Fence)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_attempt_mismatch",
                "The late Sandbox cleanup does not reference the exact source provision attempt.");
        var expectedRequest = SandboxProviderAttemptRequest.ForLateResourceRelease(
            lease, provisionedResource, idempotencyKey!, fingerprint!);
        if (expectedRequest != request)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_mismatch",
                "The late Sandbox provider attempt does not match the exact pending resource.");
    }

    /// <summary>Database projection of a late-resource cleanup item while it is being claimed or read.</summary>
    private sealed record CleanupReadRow(
        long ResourceGeneration,
        string Fingerprint,
        string ResourceJson,
        DateTimeOffset? ClaimExpiresAt,
        string? ReleaseReceiptJson,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        Guid? ProvisionAttemptId,
        Guid? ReleaseAttemptId);

    private static async Task RequireActiveOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT lifecycle_generation, state
            FROM "environment"."owners"
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwnerParameters(command, fence.Owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        var generation = reader.GetInt64(0);
        var state = reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (state != EnvironmentLifecycleState.Active.ToString())
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot accept Sandbox operations.");
        if (generation != fence.LifecycleGeneration)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Environment lifecycle fence is stale.");
    }

    private static async Task<bool> IsOwnerFenceActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT lifecycle_generation, state
            FROM "environment"."owners"
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwnerParameters(command, fence.Owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        return reader.GetInt64(0) == fence.LifecycleGeneration &&
               string.Equals(
                   reader.GetString(1),
                   EnvironmentLifecycleState.Active.ToString(),
                   StringComparison.Ordinal);
    }

    private static async Task<SandboxLeaseSnapshot?> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, resource_generation, provider_fencing_generation,
                   current_fencing_generation, operation_id, lease_state,
                   provider_id, adapter_version, options_schema_version, options_revision,
                   options_snapshot_json, selection_snapshot_json, provider_request_json,
                   provider_request_fingerprint, resource_json, retirement_reason, terminal_evidence_json, retiring_issuer,
                   retiring_actor_id, retiring_membership_revision, release_idempotency_key,
                   retirement_fingerprint, is_current, updated_at, lease_revision, lease_expires_at,
                   partial_release_receipt_json, created_at
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND is_current = TRUE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLease(reader, owner)
            : null;
    }

    private static bool SameLeaseVersion(
        SandboxLeaseSnapshot? left,
        SandboxLeaseSnapshot? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Fence == right.Fence &&
              left.ResourceGeneration == right.ResourceGeneration &&
              left.OperationId == right.OperationId &&
              left.LeaseRevision == right.LeaseRevision &&
              left.ProviderFencingGeneration == right.ProviderFencingGeneration &&
              left.CurrentFencingGeneration == right.CurrentFencingGeneration &&
              left.State == right.State &&
              left.IsCurrent == right.IsCurrent &&
              left.LeaseExpiresAt == right.LeaseExpiresAt &&
              left.CreatedAt == right.CreatedAt &&
              left.UpdatedAt == right.UpdatedAt;

    private static async Task<SandboxLeaseSnapshot?> ReadByGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        long resourceGeneration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, resource_generation, provider_fencing_generation,
                   current_fencing_generation, operation_id, lease_state,
                   provider_id, adapter_version, options_schema_version, options_revision,
                   options_snapshot_json, selection_snapshot_json, provider_request_json,
                   provider_request_fingerprint, resource_json, retirement_reason, terminal_evidence_json, retiring_issuer,
                   retiring_actor_id, retiring_membership_revision, release_idempotency_key,
                   retirement_fingerprint, is_current, updated_at, lease_revision, lease_expires_at,
                   partial_release_receipt_json, created_at
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, resourceGeneration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLease(reader, owner)
            : null;
    }

    private static async Task<SandboxLeaseSnapshot?> ReadByOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, resource_generation, provider_fencing_generation,
                   current_fencing_generation, operation_id, lease_state,
                   provider_id, adapter_version, options_schema_version, options_revision,
                   options_snapshot_json, selection_snapshot_json, provider_request_json,
                   provider_request_fingerprint, resource_json, retirement_reason, terminal_evidence_json, retiring_issuer,
                   retiring_actor_id, retiring_membership_revision, release_idempotency_key,
                   retirement_fingerprint, is_current, updated_at, lease_revision, lease_expires_at,
                   partial_release_receipt_json, created_at
            FROM {Leases}
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLease(reader, owner)
            : null;
    }

    private static async Task<(string Fingerprint, SandboxLeaseSnapshot Lease)?> ReadByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_fingerprint, lifecycle_generation, resource_generation,
                   provider_fencing_generation, current_fencing_generation, operation_id, lease_state,
                   provider_id, adapter_version, options_schema_version, options_revision,
                   options_snapshot_json, selection_snapshot_json, provider_request_json,
                   provider_request_fingerprint, resource_json, retirement_reason, terminal_evidence_json, retiring_issuer,
                   retiring_actor_id, retiring_membership_revision, release_idempotency_key,
                   retirement_fingerprint, is_current, updated_at, lease_revision, lease_expires_at,
                   partial_release_receipt_json, created_at
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND provision_idempotency_key = @idempotency_key
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), ReadLease(reader, owner, columnOffset: 1))
            : null;
    }

    private static SandboxLeaseSnapshot ReadLease(
        NpgsqlDataReader reader,
        EnvironmentOwnerIdentity owner,
        int columnOffset = 0)
    {
        var offset = columnOffset;
        var fence = new EnvironmentGenerationFence(owner, reader.GetInt64(offset++));
        var resourceGeneration = reader.GetInt64(offset++);
        var providerFence = reader.GetInt64(offset++);
        var currentFence = reader.GetInt64(offset++);
        var operationId = reader.GetGuid(offset++);
        var state = Enum.Parse<SandboxLeaseState>(reader.GetString(offset++), ignoreCase: false);
        var providerId = reader.GetString(offset++);
        var adapterVersion = reader.GetString(offset++);
        var optionsSchemaVersion = reader.GetInt32(offset++);
        var optionsRevision = reader.GetString(offset++);
        using var options = JsonDocument.Parse(reader.GetString(offset++));
        using var selection = JsonDocument.Parse(reader.GetString(offset++));
        using var request = JsonDocument.Parse(reader.GetString(offset++));
        var providerRequestFingerprint = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var resourceJson = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        SandboxRetirementReason? retirementReason = reader.IsDBNull(offset)
            ? null
            : Enum.Parse<SandboxRetirementReason>(reader.GetString(offset), ignoreCase: false);
        offset++;
        var terminalJson = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var issuer = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var actor = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        long? membershipRevision = reader.IsDBNull(offset) ? null : reader.GetInt64(offset);
        offset++;
        var releaseKey = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var retirementFingerprint = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var current = reader.GetBoolean(offset++);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(offset);
        offset++;
        var leaseRevision = reader.GetInt64(offset++);
        DateTimeOffset? leaseExpiresAt = reader.IsDBNull(offset)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(offset);
        offset++;
        var partialReleaseJson = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        offset++;
        var createdAt = reader.GetFieldValue<DateTimeOffset>(offset);
        var intent = new SandboxLeaseProvisionIntent(
            providerId,
            adapterVersion,
            optionsSchemaVersion,
            optionsRevision,
            options.RootElement.Clone(),
            selection.RootElement.Clone(),
            request.RootElement.Clone());
        var provisionedResource = resourceJson is null
            ? null
            : JsonSerializer.Deserialize<SandboxProvisionedResource>(resourceJson, JsonOptions)
              ?? throw new InvalidOperationException("The stored Sandbox provider resource is empty.");
        var terminalEvidence = terminalJson is null
            ? null
            : JsonSerializer.Deserialize<SandboxTerminalEvidence>(terminalJson, JsonOptions)
              ?? throw new InvalidOperationException("The stored Sandbox terminal evidence is empty.");
        var partialReleaseReceipt = partialReleaseJson is null
            ? null
            : JsonSerializer.Deserialize<SandboxPartialReleaseReceipt>(partialReleaseJson, JsonOptions)
              ?? throw new InvalidOperationException("The stored partial Sandbox release receipt is empty.");
        return new SandboxLeaseSnapshot(
            fence,
            resourceGeneration,
            providerFence,
            currentFence,
            operationId,
            state,
            intent,
            provisionedResource,
            retirementReason,
            terminalEvidence,
            actor,
            membershipRevision,
            releaseKey,
            current,
            updatedAt)
        {
            RetirementFingerprint = retirementFingerprint,
            RetiringIssuer = issuer,
            ProviderRequestFingerprint = providerRequestFingerprint,
            LeaseRevision = leaseRevision,
            LeaseExpiresAt = leaseExpiresAt,
            PartialReleaseReceipt = partialReleaseReceipt,
            CreatedAt = createdAt
        }.Validate();
    }

    private static async Task<(long ResourceGeneration, long FencingGeneration)> ReadNextGenerationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(MAX(resource_generation), 0) + 1,
                   COALESCE(MAX(current_fencing_generation), 0) + 1
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Sandbox generation counters could not be read.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static void ValidateProvisionedResource(
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource resource)
    {
        if (resource.Resource.Generation != lease.ResourceGeneration ||
            !string.Equals(resource.Resource.ProviderId, lease.ProvisionIntent.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(
                resource.ProviderBinding.AdapterVersion,
                lease.ProvisionIntent.AdapterVersion,
                StringComparison.Ordinal) ||
            resource.ProviderBinding.OptionsSchemaVersion != lease.ProvisionIntent.OptionsSchemaVersion ||
            !string.Equals(
                resource.ProviderBinding.OptionsRevision,
                lease.ProvisionIntent.OptionsRevision,
                StringComparison.Ordinal) ||
            !JsonNode.DeepEquals(
                JsonNode.Parse(resource.ProviderBinding.OptionsSnapshot.GetRawText()),
                JsonNode.Parse(lease.ProvisionIntent.OptionsSnapshot.GetRawText())))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_binding_mismatch",
                "The Sandbox resource does not match the immutable selected provider and options revision.");
    }

    private static bool SameProvisionedResource(
        SandboxProvisionedResource left,
        SandboxProvisionedResource right) =>
        left.Resource == right.Resource &&
        left.Endpoint == right.Endpoint &&
        left.Placement == right.Placement &&
        left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities) &&
        left.StartupPhases.SequenceEqual(right.StartupPhases) &&
        SameProviderBinding(left.ProviderBinding, right.ProviderBinding);

    private static bool SameProviderBinding(
        SandboxProviderBindingSnapshot left,
        SandboxProviderBindingSnapshot right) =>
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        JsonNode.DeepEquals(
            JsonNode.Parse(left.OptionsSnapshot.GetRawText()),
            JsonNode.Parse(right.OptionsSnapshot.GetRawText())) &&
        JsonNode.DeepEquals(
            JsonNode.Parse(left.ReleaseDescriptor.GetRawText()),
            JsonNode.Parse(right.ReleaseDescriptor.GetRawText()));

    private static string LateResourceFingerprint(
        SandboxLeaseSnapshot lease,
        ProviderResourceRef resource) =>
        Fingerprint(string.Join('\0',
            "sandbox-late-resource",
            lease.Fence.Owner.TenantId,
            lease.Fence.Owner.ProjectId,
            lease.Fence.Owner.RunId,
            lease.Fence.Owner.EnvironmentId,
            lease.Fence.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lease.OperationId.ToString("N"),
            lease.ResourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            resource.Seam.ToString(),
            resource.ProviderId,
            resource.ResourceId,
            resource.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static string LateResourceIdempotencyKey(string fingerprint) => "late-" + fingerprint;

    private static string ProvisionFingerprint(
        EnvironmentGenerationFence fence,
        SandboxLeaseProvisionIntent intent) =>
        Fingerprint(string.Join('\0',
            "sandbox-provision",
            fence.Owner.TenantId,
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            fence.Owner.EnvironmentId,
            fence.LifecycleGeneration,
            intent.ProviderId,
            intent.AdapterVersion,
            intent.OptionsSchemaVersion,
            intent.OptionsRevision,
            intent.OptionsSnapshot.GetRawText(),
            intent.SelectionSnapshot.GetRawText(),
            intent.ProviderRequest.GetRawText()));

    private static string RetirementFingerprint(
        SandboxRetirementReason reason,
        SandboxRetirementAuthorization? authorization,
        SandboxTerminalEvidence? terminalEvidence,
        long resourceGeneration,
        long providerFencingGeneration) =>
        Fingerprint(string.Join('\0',
            "sandbox-retirement",
            reason,
            resourceGeneration,
            providerFencingGeneration,
            authorization?.Issuer ?? string.Empty,
            authorization?.ActorId ?? string.Empty,
            authorization?.MembershipRevision ?? 0,
            terminalEvidence is null ? string.Empty : JsonSerializer.Serialize(terminalEvidence, JsonOptions)));

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AddOwnerParameters(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, owner.EnvironmentId);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
