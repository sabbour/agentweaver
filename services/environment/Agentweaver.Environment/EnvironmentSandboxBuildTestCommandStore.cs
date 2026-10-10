using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed record EnvironmentSandboxBuildTestCommandState(
    SandboxBuildTestAcceptedCommand AcceptedCommand,
    SandboxBuildTestOperationSnapshot Operation,
    ImmutableArray<string> AttemptedEffects);

public sealed record EnvironmentSandboxBuildTestCommandReservation(
    EnvironmentSandboxBuildTestCommandState State,
    bool Replayed);

public interface IEnvironmentSandboxBuildTestCommandStore
{
    Task<EnvironmentSandboxBuildTestCommandReservation> ReserveAsync(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestOperationSnapshot initialOperation,
        CancellationToken cancellationToken);

    Task<EnvironmentSandboxBuildTestCommandState?> GetAsync(
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<EnvironmentSandboxBuildTestCommandState> SaveAsync(
        EnvironmentOwnerIdentity owner,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentSandboxBuildTestCommandStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : IEnvironmentSandboxBuildTestCommandStore
{
    private const string Owners = "\"environment\".\"owners\"";
    private const string Leases = "\"environment\".\"sandbox_leases\"";
    private const string Effects = "\"environment\".\"owner_effects\"";
    internal const string PendingPolicyUid = "pending";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<EnvironmentSandboxBuildTestCommandReservation> ReserveAsync(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestOperationSnapshot initialOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(acceptedCommand);
        ArgumentNullException.ThrowIfNull(initialOperation);
        _ = request.Validate();
        acceptedCommand = acceptedCommand.Validate();
        var fingerprint = request.ComputeRequestFingerprint(acceptedCommand);
        if (!SameCheckpoint(request.Checkpoint, acceptedCommand.Checkpoint) ||
            initialOperation.OperationId != acceptedCommand.OperationId ||
            initialOperation.RequestFingerprint != fingerprint)
            throw new ArgumentException("The accepted BuildTest command does not match its checkpoint request.");
        _ = initialOperation.Validate(
            acceptedCommand,
            acceptedCommand.ExecutionOptions.MaximumOutputBytes);
        var owner = request.ExpectedBinding.Fence.Owner;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection,
            transaction,
            owner,
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadByOperationIdAsync(
            connection,
            transaction,
            acceptedCommand.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Owner != owner || existing.EffectKind != "BuildTestCommand")
                throw new EnvironmentLifecycleException(
                    "buildtest_operation_conflict",
                    "The accepted BuildTest operation ID is already used by a different owner effect.");
            var existingState = DeserializeState(existing);
            if (existing.Fingerprint != fingerprint ||
                !SameJson(existingState.AcceptedCommand, acceptedCommand) ||
                !SameJson(existingState.Operation.ExpectedBinding, request.ExpectedBinding))
                throw new EnvironmentLifecycleException(
                    "buildtest_request_conflict",
                    "The accepted BuildTest operation ID was reused with a different immutable request.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(existingState, true);
        }

        await RequireActiveOwnerAsync(
            connection,
            transaction,
            request.ExpectedBinding.Fence,
            cancellationToken).ConfigureAwait(false);
        await RequireCurrentSandboxAndWorkspaceAsync(
            connection,
            transaction,
            request.ExpectedBinding,
            cancellationToken).ConfigureAwait(false);
        await EnsureIdempotencyKeyUnusedAsync(
            connection,
            transaction,
            owner,
            acceptedCommand.OperationId,
            cancellationToken).ConfigureAwait(false);

        var now = CurrentTimestamp();
        var initialState = new EnvironmentSandboxBuildTestCommandState(
            acceptedCommand,
            initialOperation with { CreatedAt = now, UpdatedAt = now },
            []);
        var completedAt = CompletionTime(initialState.Operation, now);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {Effects}
                (operation_id, tenant_id, project_id, run_id, environment_id,
                 lifecycle_generation, effect_kind, resource_id,
                 expected_provider_seam, expected_provider_id, expected_provider_resource_id,
                 target_provider_seam, target_provider_id, target_provider_resource_id,
                 policy_generation, expected_previous_policy_generation,
                 expected_transition_revision, expected_resource_generation, expected_data_generation,
                 target_transition_revision, target_resource_generation, target_data_generation,
                 target_volume_state, operation, specification_json,
                 expected_provider_binding_json, target_provider_binding_json,
                 idempotency_key, request_fingerprint, effect_state, created_at, completed_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id,
                 @lifecycle_generation, 'BuildTestCommand', @resource_id,
                 'Sandbox', @provider_id, @provider_resource_id,
                 NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                 'Execute', @specification_json, @expected_provider_binding_json,
                 @target_provider_binding_json, @idempotency_key, @request_fingerprint,
                 @effect_state, @created_at, @completed_at)
            """, connection, transaction);
        AddOwnerParameters(insert, owner);
        insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, acceptedCommand.OperationId);
        insert.Parameters.AddWithValue(
            "lifecycle_generation",
            NpgsqlDbType.Bigint,
            request.ExpectedBinding.Fence.LifecycleGeneration);
        insert.Parameters.AddWithValue(
            "resource_id",
            NpgsqlDbType.Text,
            request.ExpectedBinding.SandboxResource.ResourceId);
        insert.Parameters.AddWithValue(
            "provider_id",
            NpgsqlDbType.Text,
            request.ExpectedBinding.SandboxResource.ProviderId);
        insert.Parameters.AddWithValue(
            "provider_resource_id",
            NpgsqlDbType.Text,
            request.ExpectedBinding.SandboxResource.ResourceId);
        insert.Parameters.AddWithValue(
            "specification_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(acceptedCommand, JsonOptions));
        insert.Parameters.AddWithValue(
            "expected_provider_binding_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(request.ExpectedBinding, JsonOptions));
        insert.Parameters.AddWithValue(
            "target_provider_binding_json",
            NpgsqlDbType.Jsonb,
            SerializeMutableState(initialState));
        insert.Parameters.AddWithValue(
            "idempotency_key",
            NpgsqlDbType.Text,
            IdempotencyKey(acceptedCommand.OperationId));
        insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
        insert.Parameters.AddWithValue(
            "effect_state",
            NpgsqlDbType.Text,
            ToEffectState(initialState.Operation.Status));
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        insert.Parameters.AddWithValue(
            "completed_at",
            NpgsqlDbType.TimestampTz,
            (object?)completedAt ?? DBNull.Value);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new EnvironmentLifecycleException(
                "buildtest_operation_conflict",
                "The accepted BuildTest operation conflicts with an existing owner effect.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(initialState, false);
    }

    public async Task<EnvironmentSandboxBuildTestCommandState?> GetAsync(
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (operationId == Guid.Empty)
            throw new ArgumentException("A non-empty BuildTest operation ID is required.", nameof(operationId));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadByOwnerAndOperationIdAsync(
            connection,
            null,
            owner,
            operationId,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : DeserializeState(row);
    }

    public async Task<EnvironmentSandboxBuildTestCommandState> SaveAsync(
        EnvironmentOwnerIdentity owner,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.AcceptedCommand);
        ArgumentNullException.ThrowIfNull(state.Operation);
        var operation = state.Operation;
        if (operation.ExpectedBinding.Fence.Owner != owner ||
            operation.OperationId != state.AcceptedCommand.OperationId ||
            operation.ImmutableHash != state.AcceptedCommand.ImmutableHash ||
            operation.RequestFingerprint != new SandboxBuildTestApiRequest(
                state.AcceptedCommand.Checkpoint,
                operation.ExpectedBinding).ComputeRequestFingerprint(state.AcceptedCommand))
            throw new ArgumentException("The BuildTest operation state does not match its owner or accepted command.");
        _ = operation.Validate(
            state.AcceptedCommand,
            state.AcceptedCommand.ExecutionOptions.MaximumOutputBytes);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection,
            transaction,
            owner,
            cancellationToken).ConfigureAwait(false);
        var existing = await ReadByOwnerAndOperationIdAsync(
            connection,
            transaction,
            owner,
            operation.OperationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "buildtest_operation_unknown",
                "The exact BuildTest operation is not reserved.");
        var previous = DeserializeState(existing);
        if (existing.EffectKind != "BuildTestCommand" ||
            existing.Fingerprint != operation.RequestFingerprint ||
            !SameJson(previous.AcceptedCommand, state.AcceptedCommand) ||
            !SameJson(previous.Operation.ExpectedBinding, operation.ExpectedBinding) ||
            !CanAdvanceCommandPolicyBinding(
                previous.Operation.ExpectedCommandPolicy,
                operation.ExpectedCommandPolicy,
                state.AttemptedEffects) ||
            !CanAdvanceCollectorPolicyBinding(
                previous.Operation.CollectorPolicy,
                operation.CollectorPolicy,
                state.AttemptedEffects) ||
            operation.CreatedAt != previous.Operation.CreatedAt ||
            operation.UpdatedAt < previous.Operation.UpdatedAt ||
            !IsAllowedOperationTransition(previous.Operation, operation) ||
            state.AttemptedEffects.IsDefault ||
            state.AttemptedEffects.Any(string.IsNullOrWhiteSpace) ||
            state.AttemptedEffects.Distinct(StringComparer.Ordinal).Count() != state.AttemptedEffects.Length ||
            !previous.AttemptedEffects.All(effect => state.AttemptedEffects.Contains(effect, StringComparer.Ordinal)))
            throw new EnvironmentLifecycleException(
                "buildtest_operation_conflict",
                "The BuildTest operation update changed immutable intent or has invalid state.");

        var now = CurrentTimestamp();
        var updated = state with { Operation = operation with { UpdatedAt = now } };
        var completedAt = CompletionTime(updated.Operation, existing.CreatedAt);
        await using var update = new NpgsqlCommand($"""
            UPDATE {Effects}
            SET target_provider_binding_json = @target_provider_binding_json,
                effect_state = @effect_state,
                completed_at = @completed_at
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'BuildTestCommand'
              AND request_fingerprint = @request_fingerprint
            """, connection, transaction);
        AddOwnerParameters(update, owner);
        update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operation.OperationId);
        update.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, operation.RequestFingerprint);
        update.Parameters.AddWithValue(
            "target_provider_binding_json",
            NpgsqlDbType.Jsonb,
            SerializeMutableState(updated));
        update.Parameters.AddWithValue("effect_state", NpgsqlDbType.Text, ToEffectState(operation.Status));
        update.Parameters.AddWithValue(
            "completed_at",
            NpgsqlDbType.TimestampTz,
            (object?)completedAt ?? DBNull.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new EnvironmentLifecycleException(
                "buildtest_operation_conflict",
                "The exact BuildTest operation changed while its state was being saved.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task RequireActiveOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, state
            FROM {Owners}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            FOR UPDATE
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
                "A released Environment cannot reserve a BuildTest command.");
        if (generation != fence.LifecycleGeneration)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The BuildTest request uses a stale Environment lifecycle fence.");
    }

    private static async Task RequireCurrentSandboxAndWorkspaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SandboxBuildTestExpectedBinding expected,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, provider_fencing_generation,
                   current_fencing_generation, operation_id, lease_state,
                   resource_json, is_current, lease_revision
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
            FOR UPDATE
            """, connection, transaction))
        {
            AddOwnerParameters(command, expected.Fence.Owner);
            command.Parameters.AddWithValue(
                "resource_generation",
                NpgsqlDbType.Bigint,
                expected.SandboxResource.Generation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new EnvironmentLifecycleException(
                    "sandbox_lease_unknown",
                    "The exact BuildTest Sandbox lease is not registered.");
            var lifecycleGeneration = reader.GetInt64(0);
            var providerFence = reader.GetInt64(1);
            var currentFence = reader.GetInt64(2);
            var leaseOperation = reader.GetGuid(3);
            var leaseState = reader.GetString(4);
            var resourceJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            var isCurrent = reader.GetBoolean(6);
            var leaseRevision = reader.GetInt64(7);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (lifecycleGeneration != expected.Fence.LifecycleGeneration ||
                providerFence != expected.ProviderFencingGeneration ||
                currentFence != providerFence ||
                leaseOperation != expected.SandboxLeaseOperationId ||
                leaseState != SandboxLeaseState.Active.ToString() ||
                !isCurrent ||
                resourceJson is null ||
                leaseRevision < 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_binding_stale",
                    "The BuildTest request does not match the exact current active Sandbox lease.");

            SandboxProvisionedResource resource;
            try
            {
                resource = (JsonSerializer.Deserialize<SandboxProvisionedResource>(resourceJson, JsonOptions)
                    ?? throw new JsonException("The current Sandbox lease has no provisioned resource."))
                    .Validate();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_binding_invalid",
                    "The current Sandbox lease resource binding is invalid.");
            }

            if (resource.Resource != expected.SandboxResource ||
                expected.SandboxProviderBinding is null ||
                !SameJson(resource.ProviderBinding, expected.SandboxProviderBinding))
                throw new EnvironmentLifecycleException(
                    "sandbox_binding_stale",
                    "The BuildTest request does not match the current Sandbox provider binding.");
        }

        await using var workspace = new NpgsqlCommand($"""
            SELECT target_transition_revision, target_resource_generation, target_data_generation,
                   target_volume_state, target_provider_seam, target_provider_id,
                   target_provider_resource_id, target_provider_binding_json
            FROM {Effects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'WorkspaceVolume'
              AND resource_id = @volume_id
              AND effect_state = 'Completed'
            ORDER BY target_transition_revision DESC
            LIMIT 1
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(workspace, expected.Fence.Owner);
        workspace.Parameters.AddWithValue("volume_id", NpgsqlDbType.Text, expected.WorkspaceVolume.VolumeId);
        await using var workspaceReader = await workspace.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await workspaceReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            workspaceReader.IsDBNull(0) ||
            workspaceReader.GetInt64(1) != expected.WorkspaceVolume.ResourceGeneration ||
            workspaceReader.GetInt64(2) != expected.DataGeneration ||
            workspaceReader.GetString(3) != EnvironmentWorkspaceVolumeState.Attached.ToString() ||
            workspaceReader.IsDBNull(4) ||
            workspaceReader.GetString(4) != ProviderSeam.Storage.ToString() ||
            workspaceReader.IsDBNull(5) ||
            workspaceReader.IsDBNull(6) ||
            workspaceReader.IsDBNull(7))
            throw new EnvironmentLifecycleException(
                "workspace_generation_mismatch",
                "The BuildTest request does not match the exact attached current Workspace volume.");
    }

    private static async Task EnsureIdempotencyKeyUnusedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT operation_id, effect_kind
            FROM {Effects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND idempotency_key = @idempotency_key
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue(
            "idempotency_key",
            NpgsqlDbType.Text,
            IdempotencyKey(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "buildtest_operation_conflict",
                "The BuildTest operation idempotency key is already used by another owner effect.");
    }

    private static async Task<StoredRow?> ReadByOperationIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT tenant_id, project_id, run_id, environment_id,
                   effect_kind, request_fingerprint, specification_json::text,
                   expected_provider_binding_json::text, target_provider_binding_json::text,
                   created_at
            FROM {Effects}
            WHERE operation_id = @operation_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9))
            : null;
    }

    private static async Task<StoredRow?> ReadByOwnerAndOperationIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT effect_kind, request_fingerprint, specification_json::text,
                   expected_provider_binding_json::text, target_provider_binding_json::text,
                   created_at
            FROM {Effects}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND operation_id = @operation_id AND effect_kind = 'BuildTestCommand'
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                owner,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    private static EnvironmentSandboxBuildTestCommandState DeserializeState(StoredRow row)
    {
        try
        {
            var accepted = JsonSerializer.Deserialize<SandboxBuildTestAcceptedCommand>(
                row.SpecificationJson,
                JsonOptions)?.Validate()
                ?? throw new JsonException("The persisted BuildTest command is empty.");
            var expected = JsonSerializer.Deserialize<SandboxBuildTestExpectedBinding>(
                row.ExpectedBindingJson,
                JsonOptions)?.Validate()
                ?? throw new JsonException("The persisted BuildTest expected binding is empty.");
            var mutable = JsonSerializer.Deserialize<MutableState>(row.TargetBindingJson, JsonOptions)
                ?? throw new JsonException("The persisted BuildTest mutable state is empty.");
            var operation = mutable.Operation;
            if (operation.OperationId != accepted.OperationId ||
                operation.ImmutableHash != accepted.ImmutableHash ||
                operation.RequestFingerprint != row.Fingerprint ||
                !SameJson(operation.ExpectedBinding, expected) ||
                operation.CreatedAt != row.CreatedAt ||
                mutable.AttemptedEffects.IsDefault ||
                mutable.AttemptedEffects.Any(string.IsNullOrWhiteSpace) ||
                mutable.AttemptedEffects.Distinct(StringComparer.Ordinal).Count() !=
                    mutable.AttemptedEffects.Length)
                throw new JsonException("The persisted BuildTest fields do not agree.");
            _ = operation.Validate(accepted, accepted.ExecutionOptions.MaximumOutputBytes);
            return new(accepted, operation, mutable.AttemptedEffects);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new EnvironmentLifecycleException(
                "buildtest_record_invalid",
                "The persisted BuildTest command state is invalid.");
        }
    }

    private static string SerializeMutableState(EnvironmentSandboxBuildTestCommandState state) =>
        JsonSerializer.Serialize(
            new MutableState(state.Operation, state.AttemptedEffects),
            JsonOptions);

    private static bool SameJson<T>(T left, T right) =>
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left, JsonOptions),
            JsonSerializer.SerializeToNode(right, JsonOptions));

    private DateTimeOffset CurrentTimestamp()
    {
        var utc = timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static bool CanAdvanceCommandPolicyBinding(
        SandboxBuildTestCommandNetworkPolicyBinding previous,
        SandboxBuildTestCommandNetworkPolicyBinding next,
        ImmutableArray<string> attempts) =>
        CanAdvancePolicyUid(
            previous,
            next,
            attempts,
            static binding => binding.PolicyUid,
            static (binding, uid) => binding with { PolicyUid = uid },
            "command-policy");

    private static bool CanAdvanceCollectorPolicyBinding(
        SandboxBuildTestOutputCollectorNetworkPolicyBinding? previous,
        SandboxBuildTestOutputCollectorNetworkPolicyBinding? next,
        ImmutableArray<string> attempts)
    {
        if (previous is null || next is null)
            return previous is null && next is null;
        return CanAdvancePolicyUid(
            previous,
            next,
            attempts,
            static binding => binding.PolicyUid,
            static (binding, uid) => binding with { PolicyUid = uid },
            "collector-policy");
    }

    private static bool CanAdvancePolicyUid<TBinding>(
        TBinding previous,
        TBinding next,
        ImmutableArray<string> attempts,
        Func<TBinding, string> getUid,
        Func<TBinding, string, TBinding> withUid,
        string role)
    {
        if (SameJson(previous, next))
            return true;
        if (attempts.IsDefault ||
            getUid(previous) != PendingPolicyUid ||
            getUid(next) == PendingPolicyUid ||
            !SameJson(withUid(previous, getUid(next)), next))
            return false;
        return attempts.Contains($"{role}:create", StringComparer.Ordinal) ||
            attempts.Contains($"{role}:observed", StringComparer.Ordinal);
    }

    private static bool IsAllowedOperationTransition(
        SandboxBuildTestOperationSnapshot previous,
        SandboxBuildTestOperationSnapshot next)
    {
        var wasTerminal = previous.Status is SandboxBuildTestOperationStatus.Completed or
            SandboxBuildTestOperationStatus.Failed or
            SandboxBuildTestOperationStatus.Interrupted or
            SandboxBuildTestOperationStatus.Stale;
        return wasTerminal
            ? SameJson(previous with { UpdatedAt = next.UpdatedAt }, next)
            : Enum.IsDefined(next.Status);
    }

    private static bool SameCheckpoint(
        SandboxBuildTestCheckpointReference left,
        SandboxBuildTestCheckpointReference right) =>
        left.ProjectId == right.ProjectId &&
        left.RunId == right.RunId &&
        left.SessionId == right.SessionId &&
        left.CheckpointId == right.CheckpointId &&
        left.WorkPlanId == right.WorkPlanId &&
        left.StepId == right.StepId &&
        left.CheckpointRevision == right.CheckpointRevision &&
        left.DecisionStateVersion == right.DecisionStateVersion &&
        left.ExecutionFence == right.ExecutionFence &&
        string.Equals(left.AcceptedSelectionHash, right.AcceptedSelectionHash, StringComparison.Ordinal);

    private static string IdempotencyKey(Guid operationId) => $"buildtest-command-{operationId:N}";

    private static string ToEffectState(SandboxBuildTestOperationStatus status) => status switch
    {
        SandboxBuildTestOperationStatus.Reserved or SandboxBuildTestOperationStatus.Running => "Reserved",
        SandboxBuildTestOperationStatus.Completed => "Completed",
        SandboxBuildTestOperationStatus.Failed => "Failed",
        SandboxBuildTestOperationStatus.Interrupted or
            SandboxBuildTestOperationStatus.ReconciliationRequired => "ReconciliationRequired",
        SandboxBuildTestOperationStatus.Stale => "Stale",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static DateTimeOffset? CompletionTime(
        SandboxBuildTestOperationSnapshot operation,
        DateTimeOffset fallback) =>
        operation.Status is SandboxBuildTestOperationStatus.Completed or
            SandboxBuildTestOperationStatus.Failed or
            SandboxBuildTestOperationStatus.Interrupted or
            SandboxBuildTestOperationStatus.Stale
            ? operation.UpdatedAt > fallback ? operation.UpdatedAt : fallback
            : null;

    private static void AddOwnerParameters(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, owner.EnvironmentId);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record MutableState(
        SandboxBuildTestOperationSnapshot Operation,
        ImmutableArray<string> AttemptedEffects);

    private sealed record StoredRow(
        EnvironmentOwnerIdentity Owner,
        string EffectKind,
        string Fingerprint,
        string SpecificationJson,
        string ExpectedBindingJson,
        string TargetBindingJson,
        DateTimeOffset CreatedAt);
}
