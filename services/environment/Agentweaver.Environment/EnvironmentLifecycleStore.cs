using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed class EnvironmentLifecycleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class EnvironmentLifecycleStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : IEnvironmentLifecycleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Schema = "\"environment\"";
    private const string Owners = $"{Schema}.\"owners\"";
    private const string LifecycleOperations = $"{Schema}.\"lifecycle_operations\"";
    private const string OwnerEffects = $"{Schema}.\"owner_effects\"";
    private const string WorkspaceVolumeCleanup = $"{Schema}.\"workspace_volume_cleanup\"";

    public async Task<EnvironmentLifecycleSnapshot?> GetAsync(
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOwnerAsync(connection, null, owner, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentLifecycleSnapshot> RequireActiveAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        var current = await GetAsync(fence.Owner, cancellationToken).ConfigureAwait(false);
        if (current is null)
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        if (current.State == EnvironmentLifecycleState.Released)
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot accept provider operations.");
        if (current.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Environment lifecycle fence is stale.");
        return current;
    }

    public async Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentLifecycleTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = LifecycleFingerprint(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, request.Owner, cancellationToken).ConfigureAwait(false);

        var previous = await ReadLifecycleOperationAsync(
            connection,
            transaction,
            request.Owner,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (!string.Equals(previous.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "environment_idempotency_conflict",
                    "The Environment lifecycle idempotency key was already used for a different transition.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                new EnvironmentLifecycleSnapshot(
                    new EnvironmentGenerationFence(request.Owner, previous.Value.Generation),
                    previous.Value.State),
                Replayed: true);
        }

        if (request.TargetState == EnvironmentLifecycleState.Released)
            await EnsureOwnerResourcesReleasedAsync(
                connection,
                transaction,
                request.Owner,
                cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        EnvironmentLifecycleSnapshot snapshot;
        if (request.ExpectedLifecycleGeneration == 0)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {Owners}
                    (tenant_id, project_id, run_id, environment_id, lifecycle_generation, state, updated_at)
                VALUES
                    (@tenant_id, @project_id, @run_id, @environment_id, 1, 'Active', @updated_at)
                ON CONFLICT (tenant_id, project_id, run_id, environment_id) DO NOTHING
                RETURNING lifecycle_generation, state
                """, connection, transaction);
            AddOwnerParameters(insert, request.Owner);
            insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await ThrowOwnerConflictAsync(
                    connection, transaction, request.Owner, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable Environment owner conflict.");
            }
            snapshot = Snapshot(request.Owner, reader.GetInt64(0), reader.GetString(1));
        }
        else
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {Owners}
                SET lifecycle_generation = lifecycle_generation + 1,
                    state = @state,
                    updated_at = @updated_at
                WHERE tenant_id = @tenant_id
                  AND project_id = @project_id
                  AND run_id = @run_id
                  AND environment_id = @environment_id
                  AND lifecycle_generation = @expected_generation
                  AND state = 'Active'
                RETURNING lifecycle_generation, state
                """, connection, transaction);
            AddOwnerParameters(update, request.Owner);
            update.Parameters.AddWithValue("state", NpgsqlDbType.Text, request.TargetState.ToString());
            update.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            update.Parameters.AddWithValue(
                "expected_generation",
                NpgsqlDbType.Bigint,
                request.ExpectedLifecycleGeneration);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await ThrowOwnerConflictAsync(
                    connection, transaction, request.Owner, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable Environment lifecycle conflict.");
            }
            snapshot = Snapshot(request.Owner, reader.GetInt64(0), reader.GetString(1));
        }

        await using (var operation = new NpgsqlCommand($"""
            INSERT INTO {LifecycleOperations}
                (operation_id, tenant_id, project_id, run_id, environment_id, idempotency_key,
                 request_fingerprint, expected_lifecycle_generation, result_lifecycle_generation,
                 result_state, created_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @idempotency_key,
                 @request_fingerprint, @expected_generation, @result_generation, @result_state, @created_at)
            """, connection, transaction))
        {
            operation.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, Guid.NewGuid());
            AddOwnerParameters(operation, request.Owner);
            operation.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, request.IdempotencyKey);
            operation.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
            operation.Parameters.AddWithValue(
                "expected_generation",
                NpgsqlDbType.Bigint,
                request.ExpectedLifecycleGeneration);
            operation.Parameters.AddWithValue(
                "result_generation",
                NpgsqlDbType.Bigint,
                snapshot.Fence.LifecycleGeneration);
            operation.Parameters.AddWithValue("result_state", NpgsqlDbType.Text, snapshot.State.ToString());
            operation.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
            await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(snapshot, Replayed: false);
    }

    public async Task<EnvironmentNetworkEffectReservation> ReserveNetworkEffectAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ValidatePolicyEffect(
            resourceId,
            policyGeneration,
            expectedPreviousPolicyGeneration,
            kind,
            idempotencyKey);
        var fingerprint = NetworkEffectFingerprint(
            fence,
            resourceId,
            policyGeneration,
            expectedPreviousPolicyGeneration,
            kind);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);

        var existing = await ReadNetworkEffectByIdempotencyAsync(
            connection, transaction, fence.Owner, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "environment_effect_idempotency_conflict",
                    "The Environment effect idempotency key was already used for a different policy operation.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing.Value.Reservation with { Replayed = true };
        }

        await using (var policyState = new NpgsqlCommand($"""
            SELECT COALESCE((
                SELECT policy_generation
                FROM {OwnerEffects}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND effect_kind = 'NetworkPolicy'
                  AND resource_id = @resource_id
                  AND effect_state IN ('Completed', 'Reconciled')
                ORDER BY policy_generation DESC
                LIMIT 1
            ), 0),
            EXISTS (
                SELECT 1 FROM {OwnerEffects}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND effect_kind = 'NetworkPolicy'
                  AND resource_id = @resource_id
                  AND effect_state IN ('Reserved', 'ReconciliationRequired')
            )
            """, connection, transaction))
        {
            AddOwnerParameters(policyState, fence.Owner);
            policyState.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, resourceId);
            await using var reader = await policyState.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var currentPolicyGeneration = reader.GetInt64(0);
            var hasUnresolvedEffect = reader.GetBoolean(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (hasUnresolvedEffect)
                throw new EnvironmentLifecycleException(
                    "environment_effect_reconciliation_required",
                    "A pending or unresolved provider effect blocks this network-policy resource.");
            if (expectedPreviousPolicyGeneration != currentPolicyGeneration)
                throw new EnvironmentLifecycleException(
                    "stale_policy_generation",
                    "The expected previous Cilium policy generation does not match the Environment owner's last verified applied generation.");
        }

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {OwnerEffects}
                (operation_id, tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 effect_kind, resource_id, policy_generation, expected_previous_policy_generation,
                 operation, idempotency_key, request_fingerprint, effect_state, created_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 'NetworkPolicy', @resource_id, @policy_generation, @expected_previous_policy_generation,
                 @operation, @idempotency_key, @request_fingerprint, 'Reserved', @created_at)
            """, connection, transaction))
        {
            var operationId = Guid.NewGuid();
            insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            AddOwnerParameters(insert, fence.Owner);
            insert.Parameters.AddWithValue(
                "lifecycle_generation",
                NpgsqlDbType.Bigint,
                fence.LifecycleGeneration);
            insert.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, resourceId);
            insert.Parameters.AddWithValue("policy_generation", NpgsqlDbType.Bigint, policyGeneration);
            insert.Parameters.AddWithValue(
                "expected_previous_policy_generation",
                NpgsqlDbType.Bigint,
                expectedPreviousPolicyGeneration);
            insert.Parameters.AddWithValue("operation", NpgsqlDbType.Text, kind.ToString());
            insert.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
            insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                operationId,
                fence,
                resourceId,
                policyGeneration,
                expectedPreviousPolicyGeneration,
                kind,
                EnvironmentNetworkEffectState.Reserved,
                Replayed: false);
        }
    }

    public async Task<EnvironmentNetworkEffectReservation> GetNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentFence);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, currentFence.Owner, cancellationToken)
            .ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, currentFence, cancellationToken)
            .ConfigureAwait(false);
        var reservation = await ReadNetworkEffectByIdAsync(
            connection,
            transaction,
            operationId,
            currentFence.Owner,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_effect_unknown",
                "The exact Environment provider operation was not reserved by this owner.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation;
    }

    public async Task RequireVerifiedNetworkPolicyGenerationAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (string.IsNullOrWhiteSpace(resourceId) || policyGeneration < 1)
            throw new ArgumentException("A policy resource and positive generation are required.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE((
                SELECT policy_generation
                FROM {OwnerEffects}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND effect_kind = 'NetworkPolicy' AND resource_id = @resource_id
                  AND effect_state IN ('Completed', 'Reconciled')
                ORDER BY policy_generation DESC
                LIMIT 1
            ), 0),
            EXISTS (
                SELECT 1 FROM {OwnerEffects}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND effect_kind = 'NetworkPolicy' AND resource_id = @resource_id
                  AND effect_state IN ('Reserved', 'ReconciliationRequired')
            )
            """, connection, transaction);
        AddOwnerParameters(command, fence.Owner);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var currentGeneration = reader.GetInt64(0);
        var hasUnresolvedEffect = reader.GetBoolean(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (hasUnresolvedEffect)
            throw new EnvironmentLifecycleException(
                "environment_effect_reconciliation_required",
                "A pending or unresolved provider effect blocks network-policy verification.");
        if (currentGeneration != policyGeneration)
            throw new EnvironmentLifecycleException(
                "environment_policy_generation_untracked",
                "The requested policy generation is not the Environment owner's latest verified generation.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentNetworkEffectReservation> CompleteNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        bool effectMayHaveApplied,
        bool exactGenerationVerified,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (exactGenerationVerified && !effectMayHaveApplied)
            throw new ArgumentException(
                "A generation cannot be verified when the provider effect did not occur.",
                nameof(exactGenerationVerified));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var reservation = await ReadNetworkEffectByIdAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_effect_unknown",
                "The exact Environment provider operation was not reserved by the owner.");
        if (reservation.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_effect_fence_mismatch",
                "The provider operation belongs to a different Environment lifecycle generation.");
        if (reservation.State != EnvironmentNetworkEffectState.Reserved)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reservation with { Replayed = true };
        }

        var current = await ReadOwnerAsync(connection, transaction, fence.Owner, cancellationToken)
            .ConfigureAwait(false);
        var ownerCurrent = current is { State: EnvironmentLifecycleState.Active } && current.Fence == fence;
        var state = ownerCurrent
            ? exactGenerationVerified
                ? EnvironmentNetworkEffectState.Completed
                : effectMayHaveApplied
                    ? EnvironmentNetworkEffectState.ReconciliationRequired
                    : EnvironmentNetworkEffectState.Failed
            : effectMayHaveApplied
                ? EnvironmentNetworkEffectState.ReconciliationRequired
                : EnvironmentNetworkEffectState.Stale;
        await using (var update = new NpgsqlCommand($"""
            UPDATE {OwnerEffects}
            SET effect_state = @state, completed_at = @completed_at
            WHERE operation_id = @operation_id
              AND effect_kind = 'NetworkPolicy' AND effect_state = 'Reserved'
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("state", NpgsqlDbType.Text, state.ToString());
            update.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "environment_effect_completion_conflict",
                    "The provider operation was concurrently finalized by the Environment owner.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation with
        {
            State = state,
            Replayed = false
        };
    }

    public async Task<EnvironmentNetworkEffectReservation> MarkNetworkEffectReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence currentFence,
        EnvironmentNetworkEffectObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentFence);
        ArgumentNullException.ThrowIfNull(observation);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, currentFence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, currentFence, cancellationToken)
            .ConfigureAwait(false);
        var reservation = await ReadNetworkEffectByIdAsync(
            connection, transaction, operationId, currentFence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_effect_unknown",
                "The exact Environment provider operation was not reserved by the owner.");
        if (!observation.ObjectVerified ||
            observation.AppliedIntentGeneration != reservation.PolicyGeneration ||
            observation.Revoked != (reservation.Kind == EnvironmentNetworkEffectKind.Revoke) ||
            !IsSha256Hash(observation.IntentHash))
            throw new EnvironmentLifecycleException(
                "environment_effect_observation_mismatch",
                "Reconciliation requires an exact provider observation of this operation's policy generation and effect.");
        if (reservation.State == EnvironmentNetworkEffectState.Reconciled)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reservation with { Replayed = true };
        }
        if (reservation.State != EnvironmentNetworkEffectState.ReconciliationRequired)
            throw new EnvironmentLifecycleException(
                "environment_effect_not_reconcilable",
                "Only an owner-recorded stale or unverified provider effect can be reconciled.");

        await using (var update = new NpgsqlCommand($"""
            UPDATE {OwnerEffects}
            SET effect_state = 'Reconciled', completed_at = @completed_at
            WHERE operation_id = @operation_id
              AND effect_kind = 'NetworkPolicy' AND effect_state = 'ReconciliationRequired'
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new EnvironmentLifecycleException(
                    "environment_effect_reconciliation_conflict",
                    "The provider effect changed while its reconciliation was being recorded.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation with { State = EnvironmentNetworkEffectState.Reconciled, Replayed = false };
    }

    private static bool IsSha256Hash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ValidateVolumeId(volumeId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, environmentFence.Owner, cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerAsync(
            connection,
            transaction,
            environmentFence.Owner,
            cancellationToken).ConfigureAwait(false);
        if (owner is null)
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        if (owner.Fence != environmentFence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Environment lifecycle fence is stale.");
        await using var command = new NpgsqlCommand($"""
            SELECT o.lifecycle_generation, latest.target_transition_revision,
                   latest.target_resource_generation, latest.target_data_generation,
                   latest.target_volume_state, latest.operation, latest.target_provider_seam,
                   latest.target_provider_id, latest.target_provider_resource_id,
                   latest.target_provider_binding_json, spec.specification_json
            FROM {Owners} AS o
            JOIN LATERAL (
                SELECT target_transition_revision, target_resource_generation,
                       target_data_generation, target_volume_state, operation, target_provider_seam,
                       target_provider_id, target_provider_resource_id, target_provider_binding_json
                FROM {OwnerEffects}
                WHERE tenant_id = o.tenant_id AND project_id = o.project_id
                  AND run_id = o.run_id AND environment_id = o.environment_id
                  AND effect_kind = 'WorkspaceVolume'
                  AND resource_id = @volume_id
                  AND effect_state = 'Completed'
                ORDER BY target_transition_revision DESC
                LIMIT 1
            ) AS latest ON TRUE
            JOIN LATERAL (
                SELECT specification_json
                FROM {OwnerEffects}
                WHERE tenant_id = o.tenant_id AND project_id = o.project_id
                  AND run_id = o.run_id AND environment_id = o.environment_id
                  AND effect_kind = 'WorkspaceVolume'
                  AND resource_id = @volume_id
                  AND operation = 'Create'
                  AND effect_state = 'Completed'
                ORDER BY target_transition_revision
                LIMIT 1
            ) AS spec ON TRUE
            WHERE o.tenant_id = @tenant_id AND o.project_id = @project_id
              AND o.run_id = @run_id AND o.environment_id = @environment_id
              AND o.lifecycle_generation = @lifecycle_generation
            """, connection, transaction);
        AddOwnerParameters(command, environmentFence.Owner);
        command.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            environmentFence.LifecycleGeneration);
        command.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, volumeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new EnvironmentWorkspaceVolumeSnapshot(
                environmentFence,
                volumeId,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                Enum.Parse<EnvironmentWorkspaceVolumeState>(reader.GetString(4), ignoreCase: false),
                Enum.Parse<EnvironmentWorkspaceVolumeOperation>(reader.GetString(5), ignoreCase: false),
                ParseSpecification(reader.GetString(10)),
                ReadProviderResource(reader, 6, 7, 8, reader.GetInt64(2), "snapshot target"),
                ReadProviderBinding(reader, 9, ReadProviderResource(reader, 6, 7, 8, reader.GetInt64(2), "snapshot target")))
            : null;
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<EnvironmentWorkspaceVolumeSnapshot> CreateWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        JsonElement specification,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (specification.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "Create requires the immutable workspace-volume specification as a JSON object.",
                nameof(specification));
        var reservation = await ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision: 0,
            expectedResourceGeneration: 0,
            expectedDataGeneration: 0,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Create,
            idempotencyKey,
            specification.GetRawText(),
            cancellationToken).ConfigureAwait(false);
        return new(
            environmentFence,
            volumeId,
            reservation.TargetTransitionRevision,
            reservation.TargetResourceGeneration,
            reservation.TargetDataGeneration,
            reservation.TargetPhase,
            EnvironmentWorkspaceVolumeOperation.Create,
            specification.Clone(),
            reservation.CurrentResource,
            reservation.CurrentProviderBinding);
    }

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Provision,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Provision,
            effectMayHaveApplied,
            providerResource,
            providerBinding,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeProvisionReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Provision,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Replace,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReplaceAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Replace,
            effectMayHaveApplied,
            providerResource,
            providerBinding,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReplaceReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Replace,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeBindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Bind,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeBindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Bind,
            effectMayHaveApplied,
            providerResource,
            providerBinding: null,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeBindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Bind,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeUnbindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Unbind,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeUnbindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Unbind,
            effectMayHaveApplied,
            providerResource,
            providerBinding: null,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeUnbindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Unbind,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Attach,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Attach,
            effectMayHaveApplied,
            providerResource,
            providerBinding: null,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeAttachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Attach,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Detach,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Detach,
            effectMayHaveApplied,
            providerResource,
            providerBinding: null,
            effectVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeDetachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Detach,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeFlushAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        long nextDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration,
            EnvironmentWorkspaceVolumeOperation.Flush,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeFlushAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        bool durableFlushVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Flush,
            effectMayHaveApplied,
            providerResource,
            providerBinding: null,
            effectVerified,
            durableFlushVerified,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeFlushReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Flush,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReleaseAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeOperationAsync(
            environmentFence,
            volumeId,
            expectedTransitionRevision,
            expectedResourceGeneration,
            expectedDataGeneration,
            nextDataGeneration: null,
            EnvironmentWorkspaceVolumeOperation.Release,
            idempotencyKey,
            specificationJson: null,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        bool releaseVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Release,
            effectMayHaveApplied,
            providerResource: null,
            providerBinding: null,
            releaseVerified,
            durableFlushVerified: false,
            cancellationToken);

    public Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReleaseReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken) =>
        MarkWorkspaceVolumeTransitionReconciledCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Release,
            cancellationToken);

    public async Task<EnvironmentWorkspaceVolumeCleanupLease?> ClaimWorkspaceVolumeCleanupAsync(
        EnvironmentGenerationFence environmentFence,
        Guid? sourceReplaceOperationId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        if (sourceReplaceOperationId == Guid.Empty)
            throw new ArgumentException("A source replace operation ID cannot be empty.", nameof(sourceReplaceOperationId));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, environmentFence.Owner, cancellationToken)
            .ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, environmentFence, cancellationToken)
            .ConfigureAwait(false);

        var leaseId = Guid.NewGuid();
        await using var command = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT work.work_id
                FROM {WorkspaceVolumeCleanup} AS work
                JOIN {Owners} AS owner
                  ON owner.tenant_id = work.tenant_id
                 AND owner.project_id = work.project_id
                 AND owner.run_id = work.run_id
                 AND owner.environment_id = work.environment_id
                WHERE work.tenant_id = @tenant_id AND work.project_id = @project_id
                  AND work.run_id = @run_id AND work.environment_id = @environment_id
                  AND owner.lifecycle_generation = @lifecycle_generation
                  AND owner.state = 'Active'
                  AND (@source_replace_operation_id IS NULL
                       OR work.source_replace_operation_id = @source_replace_operation_id)
                  AND (work.state = 'Pending'
                       OR (work.state = 'Leased' AND work.lease_expires_at <= clock_timestamp()))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {OwnerEffects} AS latest
                      WHERE latest.tenant_id = work.tenant_id
                        AND latest.project_id = work.project_id
                        AND latest.run_id = work.run_id
                        AND latest.environment_id = work.environment_id
                        AND latest.effect_kind = 'WorkspaceVolume'
                        AND latest.resource_id = work.volume_id
                        AND latest.effect_state = 'Completed'
                        AND latest.target_transition_revision = (
                            SELECT max(current_effect.target_transition_revision)
                            FROM {OwnerEffects} AS current_effect
                            WHERE current_effect.tenant_id = work.tenant_id
                              AND current_effect.project_id = work.project_id
                              AND current_effect.run_id = work.run_id
                              AND current_effect.environment_id = work.environment_id
                              AND current_effect.effect_kind = 'WorkspaceVolume'
                              AND current_effect.resource_id = work.volume_id
                              AND current_effect.effect_state = 'Completed')
                        AND latest.target_resource_generation = work.resource_generation
                        AND latest.target_volume_state <> 'Released')
                ORDER BY work.created_at, work.work_id
                LIMIT 1
                FOR UPDATE OF work SKIP LOCKED
            )
            UPDATE {WorkspaceVolumeCleanup} AS work
            SET state = 'Leased',
                lease_revision = work.lease_revision + 1,
                lease_id = @lease_id,
                lease_expires_at = clock_timestamp() + @lease_duration,
                updated_at = clock_timestamp()
            FROM candidate
            WHERE work.work_id = candidate.work_id
            RETURNING work.work_id, work.lifecycle_generation,
                      work.source_replace_operation_id, work.volume_id,
                      work.resource_generation, work.release_request_json,
                      work.lease_revision, work.lease_id, work.lease_expires_at
            """, connection, transaction);
        AddOwnerParameters(command, environmentFence.Owner);
        command.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            environmentFence.LifecycleGeneration);
        command.Parameters.AddWithValue(
            "source_replace_operation_id",
            NpgsqlDbType.Uuid,
            (object?)sourceReplaceOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("lease_id", NpgsqlDbType.Uuid, leaseId);
        command.Parameters.AddWithValue("lease_duration", NpgsqlDbType.Interval, leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var releaseRequest = JsonSerializer.Deserialize<WorkspaceVolumeReleaseRequest>(
            reader.GetString(5),
            JsonOptions)
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_corrupt",
                "The persisted workspace-volume cleanup release request is empty.");
        var lease = new EnvironmentWorkspaceVolumeCleanupLease(
            reader.GetGuid(0),
            environmentFence,
            reader.GetGuid(2),
            reader.GetInt64(1),
            reader.GetString(3),
            reader.GetInt64(4),
            releaseRequest,
            reader.GetInt64(6),
            reader.GetGuid(7),
            reader.GetFieldValue<DateTimeOffset>(8)).Validate();
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    public async Task<EnvironmentWorkspaceVolumeCleanupStatus> CompleteWorkspaceVolumeCleanupAsync(
        EnvironmentWorkspaceVolumeCleanupLease lease,
        WorkspaceVolumeReleaseReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(receipt);
        lease.Validate();
        _ = receipt.ValidateFor(lease.ReleaseRequest);
        var cleanupState = receipt.Disposition switch
        {
            WorkspaceVolumeReleaseDisposition.Released => EnvironmentWorkspaceVolumeCleanupState.Completed,
            WorkspaceVolumeReleaseDisposition.Retained => EnvironmentWorkspaceVolumeCleanupState.Blocked,
            _ => throw new ArgumentOutOfRangeException(nameof(receipt), "Cleanup requires a verified release or retain receipt.")
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, lease.Fence.Owner, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {WorkspaceVolumeCleanup} AS work
            SET state = @state,
                lease_id = NULL,
                lease_expires_at = NULL,
                completed_at = CASE WHEN @state = 'Completed' THEN clock_timestamp() ELSE NULL END,
                updated_at = clock_timestamp()
            FROM {Owners} AS owner
            WHERE work.work_id = @work_id
              AND work.tenant_id = @tenant_id AND work.project_id = @project_id
              AND work.run_id = @run_id AND work.environment_id = @environment_id
              AND work.source_replace_operation_id = @source_replace_operation_id
              AND work.lifecycle_generation = @source_lifecycle_generation
              AND work.resource_generation = @resource_generation
              AND work.state = 'Leased'
              AND work.lease_revision = @lease_revision
              AND work.lease_id = @lease_id
              AND work.lease_expires_at > clock_timestamp()
              AND work.release_request_json = @release_request_json
              AND owner.tenant_id = work.tenant_id AND owner.project_id = work.project_id
              AND owner.run_id = work.run_id AND owner.environment_id = work.environment_id
              AND owner.lifecycle_generation = @lifecycle_generation
              AND owner.state = 'Active'
            RETURNING work.work_id, work.source_replace_operation_id, work.volume_id,
                      work.resource_generation, work.state, work.lease_revision,
                      work.lease_expires_at
            """, connection, transaction);
        AddCleanupLeaseParameters(command, lease);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, cleanupState.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_lease_stale",
                "The workspace-volume cleanup lease is stale, expired, or no longer owner-authorized.");
        var status = ReadCleanupStatus(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return status;
    }

    public async Task ReleaseWorkspaceVolumeCleanupLeaseAsync(
        EnvironmentWorkspaceVolumeCleanupLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.Validate();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, lease.Fence.Owner, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {WorkspaceVolumeCleanup} AS work
            SET state = 'Pending',
                lease_id = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            FROM {Owners} AS owner
            WHERE work.work_id = @work_id
              AND work.tenant_id = @tenant_id AND work.project_id = @project_id
              AND work.run_id = @run_id AND work.environment_id = @environment_id
              AND work.source_replace_operation_id = @source_replace_operation_id
              AND work.lifecycle_generation = @source_lifecycle_generation
              AND work.resource_generation = @resource_generation
              AND work.state = 'Leased'
              AND work.lease_revision = @lease_revision
              AND work.lease_id = @lease_id
              AND work.lease_expires_at > clock_timestamp()
              AND owner.tenant_id = work.tenant_id AND owner.project_id = work.project_id
              AND owner.run_id = work.run_id AND owner.environment_id = work.environment_id
              AND owner.lifecycle_generation = @lifecycle_generation
              AND owner.state = 'Active'
            """, connection, transaction);
        AddCleanupLeaseParameters(command, lease);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_lease_stale",
                "The workspace-volume cleanup lease is stale, expired, or no longer owner-authorized.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentWorkspaceVolumeCleanupStatus?> GetWorkspaceVolumeCleanupStatusAsync(
        EnvironmentGenerationFence environmentFence,
        Guid sourceReplaceOperationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        if (sourceReplaceOperationId == Guid.Empty)
            throw new ArgumentException(
                "A source replace operation ID cannot be empty.",
                nameof(sourceReplaceOperationId));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT work_id, source_replace_operation_id, volume_id, resource_generation,
                   work.state, lease_revision, lease_expires_at
            FROM {WorkspaceVolumeCleanup} AS work
            JOIN {Owners} AS owner
              ON owner.tenant_id = work.tenant_id AND owner.project_id = work.project_id
             AND owner.run_id = work.run_id AND owner.environment_id = work.environment_id
            WHERE work.tenant_id = @tenant_id AND work.project_id = @project_id
              AND work.run_id = @run_id AND work.environment_id = @environment_id
              AND owner.lifecycle_generation = @lifecycle_generation AND owner.state = 'Active'
              AND work.lifecycle_generation <= @lifecycle_generation
              AND work.source_replace_operation_id = @source_replace_operation_id
            """, connection);
        AddOwnerParameters(command, environmentFence.Owner);
        command.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            environmentFence.LifecycleGeneration);
        command.Parameters.AddWithValue(
            "source_replace_operation_id",
            NpgsqlDbType.Uuid,
            sourceReplaceOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCleanupStatus(reader)
            : null;
    }

    private Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeOperationAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        long? nextDataGeneration,
        EnvironmentWorkspaceVolumeOperation operation,
        string idempotencyKey,
        string? specificationJson,
        CancellationToken cancellationToken) =>
        ReserveWorkspaceVolumeTransitionCoreAsync(
            new WorkspaceVolumeTransitionCommand(
                environmentFence,
                volumeId,
                expectedTransitionRevision,
                expectedResourceGeneration,
                expectedDataGeneration,
                nextDataGeneration,
                operation,
                idempotencyKey,
                specificationJson),
            cancellationToken);

    private async Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeTransitionCoreAsync(
        WorkspaceVolumeTransitionCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWorkspaceVolumeCommand(request);
        var fingerprint = WorkspaceVolumeFingerprint(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, request.EnvironmentFence.Owner, cancellationToken)
            .ConfigureAwait(false);
        await RequireActiveInTransactionAsync(connection, transaction, request.EnvironmentFence, cancellationToken)
            .ConfigureAwait(false);

        var previous = await ReadWorkspaceVolumeOperationByIdempotencyAsync(
            connection,
            transaction,
            request.EnvironmentFence.Owner,
            request.VolumeId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (!string.Equals(previous.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "environment_volume_idempotency_conflict",
                    "The workspace-volume idempotency key was already used for a different owner transition.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToVolumeReservation(previous.Value.Operation, replayed: true);
        }

        var current = await ReadWorkspaceVolumeStateAsync(
            connection,
            transaction,
            request.EnvironmentFence.Owner,
            request.VolumeId,
            cancellationToken).ConfigureAwait(false);
        var transitionTarget = ValidateWorkspaceVolumeTransition(request, current);
        await using (var pending = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {OwnerEffects}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND effect_kind = 'WorkspaceVolume'
                  AND resource_id = @volume_id
                  AND effect_state IN ('Reserved', 'ReconciliationRequired'))
            """, connection, transaction))
        {
            AddOwnerParameters(pending, request.EnvironmentFence.Owner);
            pending.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, request.VolumeId);
            if (await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                throw new EnvironmentLifecycleException(
                    "environment_volume_transition_busy",
                    "Another owner-fenced workspace-volume transition is in progress or requires reconciliation.");
        }

        var now = timeProvider.GetUtcNow();
        var ownerOnlyTransition = request.Operation == EnvironmentWorkspaceVolumeOperation.Create ||
            (request.Operation == EnvironmentWorkspaceVolumeOperation.Release &&
             request.ExpectedResourceGeneration == 0);
        var expectedResource = current?.Resource;
        var targetResource = request.Operation is EnvironmentWorkspaceVolumeOperation.Create or
            EnvironmentWorkspaceVolumeOperation.Provision or
            EnvironmentWorkspaceVolumeOperation.Replace or
            EnvironmentWorkspaceVolumeOperation.Release
                ? null
                : current?.Resource;
        var expectedProviderBinding = current?.ProviderBinding;
        var targetProviderBinding = request.Operation is EnvironmentWorkspaceVolumeOperation.Create or
            EnvironmentWorkspaceVolumeOperation.Provision or
            EnvironmentWorkspaceVolumeOperation.Replace or
            EnvironmentWorkspaceVolumeOperation.Release
                ? null
                : current?.ProviderBinding;
        var operation = new WorkspaceVolumeOperation(
            Guid.NewGuid(),
            request.EnvironmentFence.Owner,
            request.EnvironmentFence.LifecycleGeneration,
            request.VolumeId,
            request.ExpectedTransitionRevision,
            request.ExpectedResourceGeneration,
            request.ExpectedDataGeneration,
            checked(request.ExpectedTransitionRevision + 1),
            transitionTarget.ResourceGeneration,
            transitionTarget.DataGeneration,
            transitionTarget.State,
            request.Operation,
            request.IdempotencyKey,
            fingerprint,
            ownerOnlyTransition
                ? EnvironmentWorkspaceVolumeTransitionState.Completed
                : EnvironmentWorkspaceVolumeTransitionState.Reserved,
            now,
            ownerOnlyTransition ? now : null,
            request.SpecificationJson,
            expectedResource,
            targetResource,
            expectedProviderBinding,
            targetProviderBinding);
        await InsertWorkspaceVolumeOwnerEffectAsync(
            connection, transaction, operation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToVolumeReservation(operation, replayed: false);
    }

    private async Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeTransitionCoreAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        EnvironmentWorkspaceVolumeOperation expectedOperation,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        bool durableFlushVerified,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (effectVerified && !effectMayHaveApplied)
            throw new ArgumentException(
                "A provider effect cannot be verified when it did not occur.",
                nameof(effectVerified));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var operation = await ReadWorkspaceVolumeOperationByIdAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_operation_unknown",
                "The exact workspace-volume transition was not reserved by the Environment owner.");
        if (operation.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_volume_fence_mismatch",
                "The workspace-volume transition belongs to a different Environment lifecycle generation.");
        if (operation.Operation != expectedOperation)
            throw new EnvironmentLifecycleException(
                "environment_volume_operation_mismatch",
                "The reserved workspace-volume operation does not match the typed completion method.");
        if (operation.State != EnvironmentWorkspaceVolumeTransitionState.Reserved)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToVolumeResult(operation, replayed: true);
        }
        if (durableFlushVerified &&
            (operation.Operation != EnvironmentWorkspaceVolumeOperation.Flush ||
             !effectVerified ||
             !effectMayHaveApplied))
            throw new ArgumentException(
                "Durability verification requires a verified provider Flush effect.",
                nameof(durableFlushVerified));

        var currentOwner = await ReadOwnerAsync(connection, transaction, fence.Owner, cancellationToken)
            .ConfigureAwait(false);
        var ownerCurrent = currentOwner is { State: EnvironmentLifecycleState.Active } &&
            currentOwner.Fence == fence;
        var resourceMatchesTarget = operation.Operation switch
        {
            EnvironmentWorkspaceVolumeOperation.Provision or
                EnvironmentWorkspaceVolumeOperation.Replace =>
                IsValidStorageResource(providerResource, operation.TargetResourceGeneration),
            EnvironmentWorkspaceVolumeOperation.Release => providerResource is null,
            _ => providerResource == operation.ExpectedResource
        };
        var bindingMatchesTarget = operation.Operation switch
        {
            EnvironmentWorkspaceVolumeOperation.Provision or
                EnvironmentWorkspaceVolumeOperation.Replace =>
                IsValidProviderBinding(providerBinding, providerResource),
            _ => providerBinding is null
        };
        var operationVerified = effectVerified &&
            resourceMatchesTarget &&
            bindingMatchesTarget &&
            (operation.Operation != EnvironmentWorkspaceVolumeOperation.Flush || durableFlushVerified);
        var complete = ownerCurrent && effectMayHaveApplied && operationVerified;
        var operationState = complete
            ? EnvironmentWorkspaceVolumeTransitionState.Completed
            : effectMayHaveApplied
                ? EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired
                : ownerCurrent
                    ? EnvironmentWorkspaceVolumeTransitionState.Failed
                    : EnvironmentWorkspaceVolumeTransitionState.Stale;

        if (!complete)
        {
            await UpdateWorkspaceVolumeEffectStateAsync(
                connection,
                transaction,
                operationId,
                EnvironmentWorkspaceVolumeTransitionState.Reserved,
                operationState,
                operation.TargetResource,
                operation.TargetProviderBinding,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        if (complete)
        {
            await UpdateWorkspaceVolumeEffectStateAsync(
                connection,
                transaction,
                operationId,
                EnvironmentWorkspaceVolumeTransitionState.Reserved,
                EnvironmentWorkspaceVolumeTransitionState.Completed,
                providerResource,
                operation.Operation is EnvironmentWorkspaceVolumeOperation.Provision or
                    EnvironmentWorkspaceVolumeOperation.Replace
                    ? providerBinding
                    : operation.TargetProviderBinding,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (operation.Operation == EnvironmentWorkspaceVolumeOperation.Replace)
                await InsertWorkspaceVolumeCleanupAsync(
                    connection,
                    transaction,
                    operation,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToVolumeResult(
            operation with
            {
                State = operationState,
                CompletedAt = timeProvider.GetUtcNow(),
                TargetResource = complete ? providerResource : operation.TargetResource
            },
            replayed: false);
    }

    private async Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeTransitionReconciledCoreAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        EnvironmentWorkspaceVolumeOperation expectedOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireOwnerLockAsync(connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        var operation = await ReadWorkspaceVolumeOperationByIdAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_operation_unknown",
                "The exact workspace-volume transition was not reserved by the Environment owner.");
        if (operation.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_volume_fence_mismatch",
                "The workspace-volume transition belongs to a different Environment lifecycle generation.");
        if (operation.Operation != expectedOperation)
            throw new EnvironmentLifecycleException(
                "environment_volume_operation_mismatch",
                "The reserved workspace-volume operation does not match the typed reconciliation method.");
        if (operation.State == EnvironmentWorkspaceVolumeTransitionState.Reconciled)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToVolumeResult(operation, replayed: true);
        }
        if (operation.State != EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired)
            throw new EnvironmentLifecycleException(
                "environment_volume_not_reconcilable",
                "Only an owner-recorded stale or unverified provider effect can be reconciled.");
        await UpdateWorkspaceVolumeEffectStateAsync(
            connection,
            transaction,
            operationId,
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            EnvironmentWorkspaceVolumeTransitionState.Reconciled,
            operation.TargetResource,
            operation.TargetProviderBinding,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToVolumeResult(
            operation with
            {
                State = EnvironmentWorkspaceVolumeTransitionState.Reconciled,
                CompletedAt = timeProvider.GetUtcNow()
            },
            replayed: false);
    }

    private static async Task InsertWorkspaceVolumeCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkspaceVolumeOperation operation,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var oldResource = operation.ExpectedResource
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_source_missing",
                "A replacement has no exact previous provider resource to clean up.");
        var oldBinding = operation.ExpectedProviderBinding
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_binding_missing",
                "A replacement has no exact previous provider binding to clean up.");
        if (oldResource.Generation != operation.ExpectedResourceGeneration)
            throw new EnvironmentLifecycleException(
                "environment_volume_cleanup_source_mismatch",
                "The previous provider resource does not match its owner-recorded generation.");

        await using var specificationCommand = new NpgsqlCommand($"""
            SELECT specification_json
            FROM {OwnerEffects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'WorkspaceVolume'
              AND resource_id = @volume_id
              AND operation = 'Create'
              AND effect_state = 'Completed'
            ORDER BY target_transition_revision
            LIMIT 1
            """, connection, transaction);
        AddOwnerParameters(specificationCommand, operation.Owner);
        specificationCommand.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, operation.ResourceId);
        var specificationJson = await specificationCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) as string;
        if (specificationJson is null)
            throw new EnvironmentLifecycleException(
                "environment_volume_specification_missing",
                "The immutable Create specification is unavailable for replacement cleanup.");

        var specification = JsonSerializer.Deserialize<WorkspaceVolumeSpec>(specificationJson, JsonOptions)
            ?? throw new EnvironmentLifecycleException(
                "environment_volume_specification_corrupt",
                "The immutable Create specification is invalid.");
        if (!string.Equals(specification.VolumeId, operation.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(specification.ProjectId, operation.Owner.ProjectId, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "environment_volume_specification_mismatch",
                "The immutable Create specification does not match the replacement owner.");

        var releaseRequest = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference(
                specification.ProjectId,
                specification.VolumeId,
                operation.ExpectedResourceGeneration),
            oldResource,
            specification.BindingMode,
            specification.GetEffectiveReleasePolicy(),
            specification.OwnerDeletionPolicy,
            oldBinding.ValidateFor(oldResource),
            $"replace-cleanup:{operation.OperationId:N}").Validate();
        var sharedDelete = specification.BindingMode == WorkspaceVolumeBindingMode.Shared &&
            specification.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete;
        var workId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {WorkspaceVolumeCleanup}
                (work_id, tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 source_replace_operation_id, volume_id, resource_generation, release_request_json,
                 state, block_reason, lease_revision, lease_id, lease_expires_at,
                 created_at, updated_at, completed_at)
            VALUES
                (@work_id, @tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 @source_replace_operation_id, @volume_id, @resource_generation, @release_request_json,
                 @state, @block_reason, 0, NULL, NULL, @created_at, @created_at, NULL)
            """, connection, transaction);
        insert.Parameters.AddWithValue("work_id", NpgsqlDbType.Uuid, workId);
        AddOwnerParameters(insert, operation.Owner);
        insert.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            operation.LifecycleGeneration);
        insert.Parameters.AddWithValue(
            "source_replace_operation_id",
            NpgsqlDbType.Uuid,
            operation.OperationId);
        insert.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, operation.ResourceId);
        insert.Parameters.AddWithValue(
            "resource_generation",
            NpgsqlDbType.Bigint,
            operation.ExpectedResourceGeneration);
        insert.Parameters.AddWithValue(
            "release_request_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(releaseRequest, JsonOptions));
        insert.Parameters.AddWithValue(
            "state",
            NpgsqlDbType.Text,
            sharedDelete ? "Blocked" : "Pending");
        insert.Parameters.AddWithValue(
            "block_reason",
            NpgsqlDbType.Text,
            sharedDelete ? "SharedDeleteRequiresReferenceRegistry" : DBNull.Value);
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkspaceVolumeState?> ReadWorkspaceVolumeStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        string volumeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT target_transition_revision, target_resource_generation,
                   target_data_generation, target_volume_state, operation, target_provider_seam,
                   target_provider_id, target_provider_resource_id, target_provider_binding_json
            FROM {OwnerEffects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'WorkspaceVolume'
              AND resource_id = @volume_id
              AND effect_state = 'Completed'
            ORDER BY target_transition_revision DESC
            LIMIT 1
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, volumeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WorkspaceVolumeState(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                Enum.Parse<EnvironmentWorkspaceVolumeState>(reader.GetString(3), ignoreCase: false),
                Enum.Parse<EnvironmentWorkspaceVolumeOperation>(reader.GetString(4), ignoreCase: false),
                ReadProviderResource(reader, 5, 6, 7, reader.GetInt64(1), "current target"),
                ReadProviderBinding(reader, 8, ReadProviderResource(reader, 5, 6, 7, reader.GetInt64(1), "current target")))
            : null;
    }

    private static async Task<(string Fingerprint, WorkspaceVolumeOperation Operation)?>
        ReadWorkspaceVolumeOperationByIdempotencyAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EnvironmentOwnerIdentity owner,
            string volumeId,
            string idempotencyKey,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT effect_kind, operation_id, lifecycle_generation, resource_id,
                   expected_transition_revision, expected_resource_generation, expected_data_generation,
                   target_transition_revision, target_resource_generation, target_data_generation,
                   target_volume_state, operation, idempotency_key, request_fingerprint,
                   effect_state, created_at, completed_at, specification_json,
                   expected_provider_seam, expected_provider_id, expected_provider_resource_id,
                   target_provider_seam, target_provider_id, target_provider_resource_id,
                   expected_provider_binding_json, target_provider_binding_json
            FROM {OwnerEffects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (!string.Equals(reader.GetString(0), "WorkspaceVolume", StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "environment_effect_idempotency_conflict",
                "The Environment owner idempotency key is already used by another effect kind.");
        var operation = ReadWorkspaceVolumeOperation(reader, owner);
        if (!string.Equals(operation.ResourceId, volumeId, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "environment_volume_idempotency_conflict",
                "The workspace-volume idempotency key was already used for another resource.");
        return (operation.RequestFingerprint, operation);
    }

    private static async Task<WorkspaceVolumeOperation?> ReadWorkspaceVolumeOperationByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT effect_kind, operation_id, lifecycle_generation, resource_id,
                   expected_transition_revision, expected_resource_generation, expected_data_generation,
                   target_transition_revision, target_resource_generation, target_data_generation,
                   target_volume_state, operation, idempotency_key, request_fingerprint,
                   effect_state, created_at, completed_at, specification_json,
                   expected_provider_seam, expected_provider_id, expected_provider_resource_id,
                   target_provider_seam, target_provider_id, target_provider_resource_id,
                   expected_provider_binding_json, target_provider_binding_json
            FROM {OwnerEffects}
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (!string.Equals(reader.GetString(0), "WorkspaceVolume", StringComparison.Ordinal))
            return null;
        return ReadWorkspaceVolumeOperation(reader, owner);
    }

    private static WorkspaceVolumeOperation ReadWorkspaceVolumeOperation(
        NpgsqlDataReader reader,
        EnvironmentOwnerIdentity owner) =>
        new(
            reader.GetGuid(1),
            owner,
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            Enum.Parse<EnvironmentWorkspaceVolumeState>(reader.GetString(10), ignoreCase: false),
            Enum.Parse<EnvironmentWorkspaceVolumeOperation>(reader.GetString(11), ignoreCase: false),
            reader.GetString(12),
            reader.GetString(13),
            Enum.Parse<EnvironmentWorkspaceVolumeTransitionState>(reader.GetString(14), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            ReadProviderResource(reader, 18, 19, 20, reader.GetInt64(5), "expected"),
            ReadProviderResource(reader, 21, 22, 23, reader.GetInt64(8), "target"),
            ReadProviderBinding(
                reader,
                24,
                ReadProviderResource(reader, 18, 19, 20, reader.GetInt64(5), "expected")),
            ReadProviderBinding(
                reader,
                25,
                ReadProviderResource(reader, 21, 22, 23, reader.GetInt64(8), "target")));

    private static ProviderResourceRef? ReadProviderResource(
        NpgsqlDataReader reader,
        int seamOrdinal,
        int providerIdOrdinal,
        int resourceIdOrdinal,
        long generation,
        string fieldName)
    {
        var seamIsNull = reader.IsDBNull(seamOrdinal);
        var providerIsNull = reader.IsDBNull(providerIdOrdinal);
        var resourceIsNull = reader.IsDBNull(resourceIdOrdinal);
        if (seamIsNull && providerIsNull && resourceIsNull)
            return null;
        if (seamIsNull || providerIsNull || resourceIsNull)
            throw new EnvironmentLifecycleException(
                "environment_volume_resource_corrupt",
                $"The persisted workspace-volume {fieldName} provider reference is incomplete.");

        var seamValue = reader.GetString(seamOrdinal);
        if (!string.Equals(seamValue, nameof(ProviderSeam.Storage), StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "environment_volume_resource_corrupt",
                $"The persisted workspace-volume {fieldName} provider reference has an invalid seam.");
        var resource = new ProviderResourceRef(
            ProviderSeam.Storage,
            reader.GetString(providerIdOrdinal),
            reader.GetString(resourceIdOrdinal),
            generation);
        if (!IsValidStorageResource(resource, generation))
            throw new EnvironmentLifecycleException(
                "environment_volume_resource_corrupt",
                $"The persisted workspace-volume {fieldName} provider reference is invalid.");
        return resource;
    }

    private static WorkspaceVolumeProviderBindingSnapshot? ReadProviderBinding(
        NpgsqlDataReader reader,
        int ordinal,
        ProviderResourceRef? resource)
    {
        if (reader.IsDBNull(ordinal))
        {
            if (resource is not null)
                throw new EnvironmentLifecycleException(
                    "environment_volume_binding_missing",
                    "The persisted workspace-volume generation has no provider binding snapshot.");
            return null;
        }

        try
        {
            var binding = JsonSerializer.Deserialize<WorkspaceVolumeProviderBindingSnapshot>(
                reader.GetString(ordinal),
                JsonOptions);
            return binding is null || resource is null
                ? throw new InvalidOperationException("The persisted binding does not match a provider resource.")
                : binding.ValidateFor(resource);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new EnvironmentLifecycleException(
                "environment_volume_binding_corrupt",
                "The persisted workspace-volume provider binding is invalid.");
        }
    }

    private static void AddProviderResourceParameters(
        NpgsqlCommand command,
        string prefix,
        ProviderResourceRef? resource)
    {
        command.Parameters.AddWithValue(
            $"{prefix}_provider_seam",
            NpgsqlDbType.Text,
            (object?)resource?.Seam.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            $"{prefix}_provider_id",
            NpgsqlDbType.Text,
            (object?)resource?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            $"{prefix}_provider_resource_id",
            NpgsqlDbType.Text,
            (object?)resource?.ResourceId ?? DBNull.Value);
    }

    private static async Task InsertWorkspaceVolumeOwnerEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkspaceVolumeOperation operation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {OwnerEffects}
                (operation_id, tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 effect_kind, resource_id, expected_provider_seam, expected_provider_id,
                 expected_provider_resource_id, target_provider_seam, target_provider_id,
                 target_provider_resource_id, expected_transition_revision, expected_resource_generation,
                 expected_data_generation, target_transition_revision, target_resource_generation,
                 target_data_generation, target_volume_state, operation, idempotency_key,
                 request_fingerprint, effect_state, created_at, completed_at, specification_json,
                 expected_provider_binding_json, target_provider_binding_json)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 'WorkspaceVolume', @resource_id, @expected_provider_seam, @expected_provider_id,
                 @expected_provider_resource_id, @target_provider_seam, @target_provider_id,
                 @target_provider_resource_id, @expected_transition_revision, @expected_resource_generation,
                 @expected_data_generation, @target_transition_revision, @target_resource_generation,
                 @target_data_generation, @target_volume_state, @operation, @idempotency_key,
                 @request_fingerprint, @effect_state, @created_at, @completed_at, @specification_json,
                 @expected_provider_binding_json, @target_provider_binding_json)
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operation.OperationId);
        AddOwnerParameters(command, operation.Owner);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, operation.LifecycleGeneration);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, operation.ResourceId);
        AddProviderResourceParameters(command, "expected", operation.ExpectedResource);
        AddProviderResourceParameters(command, "target", operation.TargetResource);
        command.Parameters.AddWithValue(
            "expected_transition_revision",
            NpgsqlDbType.Bigint,
            operation.ExpectedTransitionRevision);
        command.Parameters.AddWithValue(
            "expected_resource_generation",
            NpgsqlDbType.Bigint,
            operation.ExpectedResourceGeneration);
        command.Parameters.AddWithValue(
            "expected_data_generation",
            NpgsqlDbType.Bigint,
            operation.ExpectedDataGeneration);
        command.Parameters.AddWithValue(
            "target_transition_revision",
            NpgsqlDbType.Bigint,
            operation.TargetTransitionRevision);
        command.Parameters.AddWithValue(
            "target_resource_generation",
            NpgsqlDbType.Bigint,
            operation.TargetResourceGeneration);
        command.Parameters.AddWithValue(
            "target_data_generation",
            NpgsqlDbType.Bigint,
            operation.TargetDataGeneration);
        command.Parameters.AddWithValue("target_volume_state", NpgsqlDbType.Text, operation.TargetState.ToString());
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, operation.Operation.ToString());
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, operation.IdempotencyKey);
        command.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, operation.RequestFingerprint);
        command.Parameters.AddWithValue("effect_state", NpgsqlDbType.Text, operation.State.ToString());
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, operation.CreatedAt);
        command.Parameters.AddWithValue(
            "completed_at",
            NpgsqlDbType.TimestampTz,
            (object?)operation.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "specification_json",
            NpgsqlDbType.Jsonb,
            (object?)operation.SpecificationJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "expected_provider_binding_json",
            NpgsqlDbType.Jsonb,
            operation.ExpectedProviderBinding is null
                ? DBNull.Value
                : JsonSerializer.Serialize(operation.ExpectedProviderBinding, JsonOptions));
        command.Parameters.AddWithValue(
            "target_provider_binding_json",
            NpgsqlDbType.Jsonb,
            operation.TargetProviderBinding is null
                ? DBNull.Value
                : JsonSerializer.Serialize(operation.TargetProviderBinding, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateWorkspaceVolumeEffectStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        EnvironmentWorkspaceVolumeTransitionState expectedState,
        EnvironmentWorkspaceVolumeTransitionState state,
        ProviderResourceRef? targetResource,
        WorkspaceVolumeProviderBindingSnapshot? targetProviderBinding,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {OwnerEffects}
            SET effect_state = @state, completed_at = @completed_at,
                target_provider_seam = @target_provider_seam,
                target_provider_id = @target_provider_id,
                target_provider_resource_id = @target_provider_resource_id,
                target_provider_binding_json = @target_provider_binding_json
            WHERE operation_id = @operation_id
              AND effect_kind = 'WorkspaceVolume'
              AND effect_state = @expected_state
            """, connection, transaction);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, state.ToString());
        command.Parameters.AddWithValue("expected_state", NpgsqlDbType.Text, expectedState.ToString());
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, completedAt);
        AddProviderResourceParameters(command, "target", targetResource);
        command.Parameters.AddWithValue(
            "target_provider_binding_json",
            NpgsqlDbType.Jsonb,
            targetProviderBinding is null
                ? DBNull.Value
                : JsonSerializer.Serialize(targetProviderBinding, JsonOptions));
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new EnvironmentLifecycleException(
                "environment_volume_transition_conflict",
                "The owner-fenced workspace-volume operation changed before its result was recorded.");
    }

    private static WorkspaceVolumeTarget ValidateWorkspaceVolumeTransition(
        WorkspaceVolumeTransitionCommand request,
        WorkspaceVolumeState? current)
    {
        if (current is null)
        {
            if (request.Operation != EnvironmentWorkspaceVolumeOperation.Create ||
                request.ExpectedTransitionRevision != 0 ||
                request.ExpectedResourceGeneration != 0 ||
                request.ExpectedDataGeneration != 0)
                throw new EnvironmentLifecycleException(
                    "environment_volume_unknown",
                    "Only an explicit Create transition can register an unknown workspace volume.");
            return new(
                1,
                0,
                request.TargetDataGeneration,
                EnvironmentWorkspaceVolumeState.Requested);
        }

        if (current.TransitionRevision != request.ExpectedTransitionRevision ||
            current.ResourceGeneration != request.ExpectedResourceGeneration ||
            current.DataGeneration != request.ExpectedDataGeneration)
            throw new EnvironmentLifecycleException(
                "environment_volume_generation_conflict",
                "The expected workspace-volume transition, resource, or data generation is stale.");
        if (current.State == EnvironmentWorkspaceVolumeState.Released)
            throw new EnvironmentLifecycleException(
                "environment_volume_released",
                "A released workspace volume cannot be revived.");

        var targetState = request.Operation switch
        {
            EnvironmentWorkspaceVolumeOperation.Provision
                when current.State == EnvironmentWorkspaceVolumeState.Requested &&
                     current.ResourceGeneration == 0 =>
                EnvironmentWorkspaceVolumeState.Ready,
            EnvironmentWorkspaceVolumeOperation.Replace
                when current.State == EnvironmentWorkspaceVolumeState.Ready &&
                     current.ResourceGeneration > 0 =>
                EnvironmentWorkspaceVolumeState.Ready,
            EnvironmentWorkspaceVolumeOperation.Bind
                when current.State == EnvironmentWorkspaceVolumeState.Ready =>
                EnvironmentWorkspaceVolumeState.Bound,
            EnvironmentWorkspaceVolumeOperation.Unbind
                when current.State == EnvironmentWorkspaceVolumeState.Bound =>
                EnvironmentWorkspaceVolumeState.Ready,
            EnvironmentWorkspaceVolumeOperation.Attach
                when current.State == EnvironmentWorkspaceVolumeState.Bound =>
                EnvironmentWorkspaceVolumeState.Attached,
            EnvironmentWorkspaceVolumeOperation.Detach
                when current.State == EnvironmentWorkspaceVolumeState.Attached =>
                EnvironmentWorkspaceVolumeState.Bound,
            EnvironmentWorkspaceVolumeOperation.Flush
                when current.State is EnvironmentWorkspaceVolumeState.Ready or
                    EnvironmentWorkspaceVolumeState.Bound or
                    EnvironmentWorkspaceVolumeState.Attached =>
                current.State,
            EnvironmentWorkspaceVolumeOperation.Release => EnvironmentWorkspaceVolumeState.Released,
            _ => throw new EnvironmentLifecycleException(
                "environment_volume_transition_invalid",
                "The workspace-volume operation is not valid for its current owner-recorded state.")
        };
        var targetResourceGeneration = request.Operation switch
        {
            EnvironmentWorkspaceVolumeOperation.Provision or EnvironmentWorkspaceVolumeOperation.Replace =>
                checked(current.ResourceGeneration + 1),
            _ => current.ResourceGeneration
        };
        return new(
            checked(current.TransitionRevision + 1),
            targetResourceGeneration,
            request.TargetDataGeneration,
            targetState);
    }

    private static void ValidateWorkspaceVolumeCommand(WorkspaceVolumeTransitionCommand request)
    {
        ArgumentNullException.ThrowIfNull(request.EnvironmentFence);
        ValidateVolumeId(request.VolumeId);
        if (request.ExpectedTransitionRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedTransitionRevision));
        if (request.ExpectedResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedResourceGeneration));
        if (request.ExpectedDataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedDataGeneration));
        if (!Enum.IsDefined(request.Operation))
            throw new ArgumentOutOfRangeException(nameof(request.Operation));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        if (request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException(
                "Idempotency keys must be bounded and contain no control characters.",
                nameof(request.IdempotencyKey));

        if (request.Operation == EnvironmentWorkspaceVolumeOperation.Create)
        {
            if (request.ExpectedTransitionRevision != 0 ||
                request.ExpectedResourceGeneration != 0 ||
                request.ExpectedDataGeneration != 0 ||
                request.NextDataGeneration is not null)
                throw new ArgumentException(
                    "Create requires zero expected counters and no next data generation.",
                    nameof(request));
            if (request.SpecificationJson is null)
                throw new ArgumentException(
                    "Create requires the immutable workspace-volume specification.",
                    nameof(request));
            using var specification = JsonDocument.Parse(request.SpecificationJson);
            if (specification.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(
                    "Create requires the immutable workspace-volume specification as a JSON object.",
                    nameof(request));
        }
        else
        {
            if (request.ExpectedTransitionRevision == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(request.ExpectedTransitionRevision),
                    "Only explicit volume creation may start at transition revision zero.");
            if (request.SpecificationJson is not null)
                throw new ArgumentException(
                    "Only Create may include a workspace-volume specification.",
                    nameof(request));
        }

        if (request.Operation == EnvironmentWorkspaceVolumeOperation.Provision &&
            request.ExpectedResourceGeneration != 0)
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedResourceGeneration),
                "Initial provisioning requires no current provider resource generation.");
        if (request.Operation == EnvironmentWorkspaceVolumeOperation.Replace &&
            request.ExpectedResourceGeneration == 0)
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedResourceGeneration),
                "Resource replacement requires an existing provider resource generation.");
        if (request.Operation is not (EnvironmentWorkspaceVolumeOperation.Create or
            EnvironmentWorkspaceVolumeOperation.Provision or
            EnvironmentWorkspaceVolumeOperation.Release) &&
            request.ExpectedResourceGeneration == 0)
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedResourceGeneration),
                "This operation requires an existing provider resource generation.");

        if (request.Operation == EnvironmentWorkspaceVolumeOperation.Flush)
        {
            if (request.ExpectedDataGeneration == long.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(request.ExpectedDataGeneration),
                    "The requested flush would overflow the data generation.");
            if (request.NextDataGeneration != request.ExpectedDataGeneration + 1)
                throw new ArgumentException(
                    "Flush must request exactly the next data generation.",
                    nameof(request.NextDataGeneration));
        }
        else if (request.NextDataGeneration is not null)
        {
            throw new ArgumentException(
                "Only Flush may request a next data generation.",
                nameof(request.NextDataGeneration));
        }

        if (((request.Operation is EnvironmentWorkspaceVolumeOperation.Provision or
                  EnvironmentWorkspaceVolumeOperation.Replace) &&
              request.ExpectedResourceGeneration == long.MaxValue) ||
            request.ExpectedTransitionRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedTransitionRevision),
                "The requested transition would overflow a generation counter.");
    }

    private static EnvironmentWorkspaceVolumeTransitionReservation ToVolumeReservation(
        WorkspaceVolumeOperation operation,
        bool replayed) =>
        new(
            operation.OperationId,
            operation.Fence,
            operation.ResourceId,
            operation.ExpectedTransitionRevision,
            operation.ExpectedResourceGeneration,
            operation.ExpectedDataGeneration,
            operation.ExpectedResource,
            operation.ExpectedProviderBinding,
            operation.TargetTransitionRevision,
            operation.TargetResourceGeneration,
            operation.TargetDataGeneration,
            operation.TargetState,
            operation.Operation,
            operation.State,
            replayed);

    private static EnvironmentWorkspaceVolumeTransitionResult ToVolumeResult(
        WorkspaceVolumeOperation operation,
        bool replayed) =>
        new(
            operation.OperationId,
            operation.Fence,
            operation.ResourceId,
            operation.ExpectedTransitionRevision,
            operation.ExpectedResourceGeneration,
            operation.ExpectedDataGeneration,
            operation.ExpectedResource,
            operation.TargetTransitionRevision,
            operation.TargetResourceGeneration,
            operation.TargetDataGeneration,
            operation.TargetResource,
            operation.TargetState,
            operation.Operation,
            operation.State,
            replayed);

    private static async Task EnsureOwnerResourcesReleasedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                EXISTS (
                    SELECT 1 FROM {OwnerEffects}
                    WHERE tenant_id = @tenant_id AND project_id = @project_id
                      AND run_id = @run_id AND environment_id = @environment_id
                      AND effect_state IN ('Reserved', 'ReconciliationRequired')
                )
                OR EXISTS (
                    SELECT 1 FROM (
                        SELECT DISTINCT ON (resource_id) resource_id, target_volume_state
                        FROM {OwnerEffects}
                        WHERE tenant_id = @tenant_id AND project_id = @project_id
                          AND run_id = @run_id AND environment_id = @environment_id
                          AND effect_kind = 'WorkspaceVolume' AND effect_state = 'Completed'
                        ORDER BY resource_id, target_transition_revision DESC
                    ) AS volumes
                    WHERE volumes.target_volume_state <> 'Released'
                )
                OR EXISTS (
                    SELECT 1 FROM (
                        SELECT DISTINCT ON (resource_id) resource_id, operation, effect_state
                        FROM {OwnerEffects}
                        WHERE tenant_id = @tenant_id AND project_id = @project_id
                          AND run_id = @run_id AND environment_id = @environment_id
                          AND effect_kind = 'NetworkPolicy'
                          AND effect_state IN ('Completed', 'Reconciled')
                        ORDER BY resource_id, policy_generation DESC
                    ) AS policies
                    WHERE policies.operation <> 'Revoke'
                       OR policies.effect_state NOT IN ('Completed', 'Reconciled')
                )
                OR EXISTS (
                    SELECT 1 FROM {WorkspaceVolumeCleanup}
                    WHERE tenant_id = @tenant_id AND project_id = @project_id
                      AND run_id = @run_id AND environment_id = @environment_id
                      AND state <> 'Completed'
                )
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            throw new EnvironmentLifecycleException(
                "environment_resources_not_released",
                "Environment release requires every owner-recorded provider effect to be released or reconciled first.");
    }

    private static async Task<EnvironmentLifecycleSnapshot?> ReadOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, state
            FROM {Owners}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Snapshot(owner, reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task RequireActiveInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        var current = await ReadOwnerAsync(connection, transaction, fence.Owner, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        if (current.State == EnvironmentLifecycleState.Released)
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot accept provider operations.");
        if (current.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Environment lifecycle fence is stale.");
    }

    private static async Task ThrowOwnerConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        var current = await ReadOwnerAsync(connection, transaction, owner, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The Environment owner tuple is not registered at the expected lifecycle generation.");
        if (current.State == EnvironmentLifecycleState.Released)
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot be revived.");
        throw new EnvironmentLifecycleException(
            "environment_generation_conflict",
            "The expected Environment lifecycle generation is stale.");
    }

    private static async Task AcquireOwnerLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@owner_lock, 0))",
            connection,
            transaction);
        var ownerKey = string.Concat(
            owner.TenantId.Length, ":", owner.TenantId,
            owner.ProjectId.Length, ":", owner.ProjectId,
            owner.RunId.Length, ":", owner.RunId,
            owner.EnvironmentId.Length, ":", owner.EnvironmentId);
        command.Parameters.AddWithValue("owner_lock", NpgsqlDbType.Text, ownerKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Fingerprint, long Generation, EnvironmentLifecycleState State)?>
        ReadLifecycleOperationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EnvironmentOwnerIdentity owner,
            string idempotencyKey,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_fingerprint, result_lifecycle_generation, result_state
            FROM {LifecycleOperations}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1), ParseLifecycleState(reader.GetString(2)))
            : null;
    }

    private static async Task<(string Fingerprint, EnvironmentNetworkEffectReservation Reservation)?>
        ReadNetworkEffectByIdempotencyAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EnvironmentOwnerIdentity owner,
            string idempotencyKey,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT operation_id, lifecycle_generation, resource_id, policy_generation,
                   expected_previous_policy_generation, operation, effect_state, request_fingerprint
            FROM {OwnerEffects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'NetworkPolicy'
              AND idempotency_key = @idempotency_key
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(7), ReadNetworkEffectReservation(reader, owner, replayed: false))
            : null;
    }

    private static async Task<EnvironmentNetworkEffectReservation?> ReadNetworkEffectByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT operation_id, lifecycle_generation, resource_id, policy_generation,
                   expected_previous_policy_generation, operation, effect_state, request_fingerprint
            FROM {OwnerEffects}
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'NetworkPolicy'
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadNetworkEffectReservation(reader, owner, replayed: false)
            : null;
    }

    private static EnvironmentNetworkEffectReservation ReadNetworkEffectReservation(
        NpgsqlDataReader reader,
        EnvironmentOwnerIdentity owner,
        bool replayed) =>
        new(
            reader.GetGuid(0),
            new EnvironmentGenerationFence(owner, reader.GetInt64(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            Enum.Parse<EnvironmentNetworkEffectKind>(reader.GetString(5), ignoreCase: false),
            Enum.Parse<EnvironmentNetworkEffectState>(reader.GetString(6), ignoreCase: false),
            replayed);

    private static void AddOwnerParameters(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, owner.EnvironmentId);
    }

    private static void AddCleanupLeaseParameters(
        NpgsqlCommand command,
        EnvironmentWorkspaceVolumeCleanupLease lease)
    {
        command.Parameters.AddWithValue("work_id", NpgsqlDbType.Uuid, lease.WorkId);
        AddOwnerParameters(command, lease.Fence.Owner);
        command.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            lease.Fence.LifecycleGeneration);
        command.Parameters.AddWithValue(
            "source_replace_operation_id",
            NpgsqlDbType.Uuid,
            lease.SourceReplaceOperationId);
        command.Parameters.AddWithValue(
            "source_lifecycle_generation",
            NpgsqlDbType.Bigint,
            lease.SourceLifecycleGeneration);
        command.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.ResourceGeneration);
        command.Parameters.AddWithValue("lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
        command.Parameters.AddWithValue("lease_id", NpgsqlDbType.Uuid, lease.LeaseId);
        command.Parameters.AddWithValue(
            "release_request_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(lease.ReleaseRequest, JsonOptions));
    }

    private static EnvironmentWorkspaceVolumeCleanupStatus ReadCleanupStatus(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt64(3),
            Enum.Parse<EnvironmentWorkspaceVolumeCleanupState>(reader.GetString(4), ignoreCase: false),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));

    private static EnvironmentLifecycleSnapshot Snapshot(
        EnvironmentOwnerIdentity owner,
        long generation,
        string state) =>
        new(
            new EnvironmentGenerationFence(owner, generation),
            ParseLifecycleState(state));

    private static EnvironmentLifecycleState ParseLifecycleState(string state) =>
        Enum.Parse<EnvironmentLifecycleState>(state, ignoreCase: false);

    private static string LifecycleFingerprint(EnvironmentLifecycleTransitionRequest request) =>
        Fingerprint(string.Join('\0',
            "lifecycle",
            request.Owner.TenantId,
            request.Owner.ProjectId,
            request.Owner.RunId,
            request.Owner.EnvironmentId,
            request.ExpectedLifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.TargetState.ToString()));

    private static string NetworkEffectFingerprint(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind) =>
        Fingerprint(string.Join('\0',
            "network",
            fence.Owner.TenantId,
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            fence.Owner.EnvironmentId,
            fence.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            resourceId,
            policyGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedPreviousPolicyGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            kind.ToString()));

    private static string WorkspaceVolumeFingerprint(
        WorkspaceVolumeTransitionCommand request) =>
        Fingerprint(string.Join('\0',
            "workspace-volume",
            request.EnvironmentFence.Owner.TenantId,
            request.EnvironmentFence.Owner.ProjectId,
            request.EnvironmentFence.Owner.RunId,
            request.EnvironmentFence.Owner.EnvironmentId,
            request.EnvironmentFence.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.VolumeId,
            request.ExpectedTransitionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ExpectedResourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ExpectedDataGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.TargetDataGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.SpecificationJson ?? string.Empty,
            request.Operation.ToString()));

    private static JsonElement ParseSpecification(string specificationJson)
    {
        using var document = JsonDocument.Parse(specificationJson);
        return document.RootElement.Clone();
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidatePolicyEffect(
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind,
        string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId, nameof(resourceId));
        if (resourceId.Length > 512 || resourceId.Any(char.IsControl))
            throw new ArgumentException(
                "Network policy resource IDs must be bounded and contain no control characters.",
                nameof(resourceId));
        if (policyGeneration <= 0 || expectedPreviousPolicyGeneration < 0 ||
            policyGeneration <= expectedPreviousPolicyGeneration)
            throw new ArgumentOutOfRangeException(
                nameof(policyGeneration),
                "Policy generation must be positive and greater than its explicit expected previous generation.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey));
        if (idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Idempotency keys must be bounded and contain no control characters.", nameof(idempotencyKey));
    }

    private static void ValidateVolumeId(string volumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        if (volumeId.Length > 256 || volumeId.Any(char.IsControl))
            throw new ArgumentException(
                "Workspace volume IDs must be bounded and contain no control characters.",
                nameof(volumeId));
    }

    private static bool IsValidStorageResource(ProviderResourceRef? resource, long generation) =>
        resource is { Seam: ProviderSeam.Storage } &&
        resource.Generation == generation &&
        !string.IsNullOrWhiteSpace(resource.ProviderId) &&
        resource.ProviderId.Length <= 256 &&
        !resource.ProviderId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(resource.ResourceId) &&
        resource.ResourceId.Length <= 512 &&
        !resource.ResourceId.Any(char.IsControl);

    private static bool IsValidProviderBinding(
        WorkspaceVolumeProviderBindingSnapshot? binding,
        ProviderResourceRef? resource)
    {
        if (binding is null || resource is null)
            return false;
        try
        {
            _ = binding.ValidateFor(resource);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record WorkspaceVolumeTransitionCommand(
        EnvironmentGenerationFence EnvironmentFence,
        string VolumeId,
        long ExpectedTransitionRevision,
        long ExpectedResourceGeneration,
        long ExpectedDataGeneration,
        long? NextDataGeneration,
        EnvironmentWorkspaceVolumeOperation Operation,
        string IdempotencyKey,
        string? SpecificationJson)
    {
        public long TargetDataGeneration => NextDataGeneration ?? ExpectedDataGeneration;
    }

    private sealed record WorkspaceVolumeState(
        long TransitionRevision,
        long ResourceGeneration,
        long DataGeneration,
        EnvironmentWorkspaceVolumeState State,
        EnvironmentWorkspaceVolumeOperation LastOperation,
        ProviderResourceRef? Resource,
        WorkspaceVolumeProviderBindingSnapshot? ProviderBinding);

    private sealed record WorkspaceVolumeTarget(
        long TransitionRevision,
        long ResourceGeneration,
        long DataGeneration,
        EnvironmentWorkspaceVolumeState State);

    private sealed record WorkspaceVolumeOperation(
        Guid OperationId,
        EnvironmentOwnerIdentity Owner,
        long LifecycleGeneration,
        string ResourceId,
        long ExpectedTransitionRevision,
        long ExpectedResourceGeneration,
        long ExpectedDataGeneration,
        long TargetTransitionRevision,
        long TargetResourceGeneration,
        long TargetDataGeneration,
        EnvironmentWorkspaceVolumeState TargetState,
        EnvironmentWorkspaceVolumeOperation Operation,
        string IdempotencyKey,
        string RequestFingerprint,
        EnvironmentWorkspaceVolumeTransitionState State,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        string? SpecificationJson,
        ProviderResourceRef? ExpectedResource,
        ProviderResourceRef? TargetResource,
        WorkspaceVolumeProviderBindingSnapshot? ExpectedProviderBinding,
        WorkspaceVolumeProviderBindingSnapshot? TargetProviderBinding)
    {
        public EnvironmentGenerationFence Fence => new(Owner, LifecycleGeneration);
    }
}
