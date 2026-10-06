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
    private const string Schema = "\"environment\"";
    private const string Owners = $"{Schema}.\"owners\"";
    private const string LifecycleOperations = $"{Schema}.\"lifecycle_operations\"";
    private const string OwnerEffects = $"{Schema}.\"owner_effects\"";

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
                   latest.target_provider_id, latest.target_provider_resource_id, spec.specification_json
            FROM {Owners} AS o
            JOIN LATERAL (
                SELECT target_transition_revision, target_resource_generation,
                       target_data_generation, target_volume_state, operation, target_provider_seam,
                       target_provider_id, target_provider_resource_id
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
                ParseSpecification(reader.GetString(9)),
                ReadProviderResource(reader, 6, 7, 8, reader.GetInt64(2), "snapshot target"))
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
            reservation.CurrentResource);
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
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Provision,
            effectMayHaveApplied,
            providerResource,
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
        bool effectVerified,
        CancellationToken cancellationToken) =>
        CompleteWorkspaceVolumeTransitionCoreAsync(
            operationId,
            environmentFence,
            EnvironmentWorkspaceVolumeOperation.Replace,
            effectMayHaveApplied,
            providerResource,
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
            targetResource);
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
        var operationVerified = effectVerified &&
            resourceMatchesTarget &&
            (operation.Operation != EnvironmentWorkspaceVolumeOperation.Flush || durableFlushVerified);
        var complete = ownerCurrent && effectMayHaveApplied && operationVerified;
        var operationState = complete
            ? EnvironmentWorkspaceVolumeTransitionState.Completed
            : effectMayHaveApplied
                ? EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired
                : ownerCurrent
                    ? EnvironmentWorkspaceVolumeTransitionState.Failed
                    : EnvironmentWorkspaceVolumeTransitionState.Stale;

        if (complete)
        {
            await UpdateWorkspaceVolumeEffectStateAsync(
                connection,
                transaction,
                operationId,
                EnvironmentWorkspaceVolumeTransitionState.Reserved,
                EnvironmentWorkspaceVolumeTransitionState.Completed,
                providerResource,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UpdateWorkspaceVolumeEffectStateAsync(
                connection,
                transaction,
                operationId,
                EnvironmentWorkspaceVolumeTransitionState.Reserved,
                operationState,
                operation.TargetResource,
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
                   target_provider_id, target_provider_resource_id
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
                ReadProviderResource(reader, 5, 6, 7, reader.GetInt64(1), "current target"))
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
                   target_provider_seam, target_provider_id, target_provider_resource_id
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
                   target_provider_seam, target_provider_id, target_provider_resource_id
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
            ReadProviderResource(reader, 21, 22, 23, reader.GetInt64(8), "target"));

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
                 request_fingerprint, effect_state, created_at, completed_at, specification_json)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 'WorkspaceVolume', @resource_id, @expected_provider_seam, @expected_provider_id,
                 @expected_provider_resource_id, @target_provider_seam, @target_provider_id,
                 @target_provider_resource_id, @expected_transition_revision, @expected_resource_generation,
                 @expected_data_generation, @target_transition_revision, @target_resource_generation,
                 @target_data_generation, @target_volume_state, @operation, @idempotency_key,
                 @request_fingerprint, @effect_state, @created_at, @completed_at, @specification_json)
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateWorkspaceVolumeEffectStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        EnvironmentWorkspaceVolumeTransitionState expectedState,
        EnvironmentWorkspaceVolumeTransitionState state,
        ProviderResourceRef? targetResource,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {OwnerEffects}
            SET effect_state = @state, completed_at = @completed_at,
                target_provider_seam = @target_provider_seam,
                target_provider_id = @target_provider_id,
                target_provider_resource_id = @target_provider_resource_id
            WHERE operation_id = @operation_id
              AND effect_kind = 'WorkspaceVolume'
              AND effect_state = @expected_state
            """, connection, transaction);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, state.ToString());
        command.Parameters.AddWithValue("expected_state", NpgsqlDbType.Text, expectedState.ToString());
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, completedAt);
        AddProviderResourceParameters(command, "target", targetResource);
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
        ProviderResourceRef? Resource);

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
        ProviderResourceRef? TargetResource)
    {
        public EnvironmentGenerationFence Fence => new(Owner, LifecycleGeneration);
    }
}
