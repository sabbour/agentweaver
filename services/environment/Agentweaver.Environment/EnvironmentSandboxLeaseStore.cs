using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed class EnvironmentSandboxLeaseStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : ISandboxLeaseStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LateCleanupClaimDuration = TimeSpan.FromMinutes(2);
    private const string Leases = "\"environment\".\"sandbox_leases\"";
    private const string LateResourceCleanups = "\"environment\".\"sandbox_late_resource_cleanups\"";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

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

    public async Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource? provisionedResource,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken)
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
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox provision operation is not recorded.");
        if (lease.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Sandbox operation belongs to a stale Environment lifecycle fence.");
        if (provisionedResource is not null)
            ValidateProvisionedResource(lease, provisionedResource);
        var samePersistedResource =
            lease.ProvisionedResource is not null &&
            provisionedResource is not null &&
            SameProvisionedResource(lease.ProvisionedResource, provisionedResource);
        if (samePersistedResource &&
            (lease.State is SandboxLeaseState.Active or SandboxLeaseState.Released or SandboxLeaseState.Failed))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        if ((lease.State is SandboxLeaseState.Released or SandboxLeaseState.Failed) &&
            provisionedResource is null &&
            lease.ProvisionedResource is null &&
            !effectMayHaveApplied)
        {
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

        var result = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox provision result was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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

    public async Task<SandboxLeaseSnapshot> CompleteReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        long providerFencingGeneration,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(receipt);
        if (operationId == Guid.Empty || providerFencingGeneration < 1)
            throw new ArgumentException("The Sandbox release identity is invalid.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false);
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox release operation is not recorded.");
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
        var result = await ReadByOperationAsync(
            connection, transaction, operationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Sandbox release completion was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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
        await RequireActiveOwnerAsync(connection, transaction, fence, cancellationToken).ConfigureAwait(false);
        var lease = await ReadByOperationAsync(
            connection, transaction, receipt.OperationId, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_operation_unknown",
                "The exact Sandbox partial-release operation is not recorded.");
        if (lease.Fence != fence)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Sandbox partial-release operation belongs to a stale Environment lifecycle fence.");
        if (lease.State == SandboxLeaseState.Released &&
            lease.PartialReleaseReceipt == receipt)
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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
        var hasCandidate = false;
        await using (var select = new NpgsqlCommand($"""
            SELECT resource_generation, provider_resource_fingerprint, resource_json,
                   release_idempotency_key
            FROM {LateResourceCleanups}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND release_receipt_json IS NULL
              AND (claim_expires_at IS NULL OR claim_expires_at <= @now)
            ORDER BY created_at, resource_generation, provider_resource_fingerprint
            LIMIT 1
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwnerParameters(select, fence.Owner);
            select.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resourceGeneration = reader.GetInt64(0);
                resourceFingerprint = reader.GetString(1);
                resourceJson = reader.GetString(2);
                releaseIdempotencyKey = reader.GetString(3);
                hasCandidate = true;
            }
        }
        if (!hasCandidate)
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
            claimExpiresAt).Validate();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claim;
    }

    public async Task CompleteLateResourceCleanupAsync(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim claim,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken)
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
        await RequireActiveOwnerAsync(connection, transaction, currentFence, cancellationToken).ConfigureAwait(false);
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
        await using (var select = new NpgsqlCommand($"""
            SELECT claim_token, claim_expires_at, release_receipt_json
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
        }

        if (persistedReceiptJson is not null)
        {
            var persistedReceipt = JsonSerializer.Deserialize<SandboxReleaseReceipt>(
                persistedReceiptJson,
                JsonOptions) ?? throw new InvalidOperationException("The stored late Sandbox receipt is empty.");
            if (persistedReceipt != receipt)
                throw new EnvironmentLifecycleException(
                    "sandbox_late_cleanup_receipt_conflict",
                    "The late Sandbox cleanup already has a different durable receipt.");
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
        if (persistedClaimExpiresAt is not { } claimExpiresAt || claimExpiresAt <= updatedAt)
            throw new EnvironmentLifecycleException(
                "sandbox_late_cleanup_claim_expired",
                "The late Sandbox cleanup claim expired before its receipt could be committed.");
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
              AND claim_expires_at > @updated_at
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordLateResourceCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource provisionedResource,
        CancellationToken cancellationToken)
    {
        var fingerprint = LateResourceFingerprint(lease, provisionedResource.Resource);
        var now = timeProvider.GetUtcNow();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {LateResourceCleanups}
                (tenant_id, project_id, run_id, environment_id, resource_generation,
                 provider_resource_fingerprint, resource_json, release_idempotency_key,
                 created_at, updated_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, @resource_generation,
                 @provider_resource_fingerprint, @resource_json, @release_idempotency_key,
                 @created_at, @updated_at)
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
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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
                   partial_release_receipt_json
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
                   partial_release_receipt_json
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
                   partial_release_receipt_json
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
                   partial_release_receipt_json
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
            PartialReleaseReceipt = partialReleaseReceipt
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
