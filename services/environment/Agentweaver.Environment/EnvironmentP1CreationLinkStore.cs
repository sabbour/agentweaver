using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

internal static class EnvironmentP1CreationLinkStore
{
    private const string Table = "\"environment\".\"p1_creation_links\"";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task InsertEnvironmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        long lifecycleGeneration,
        P1InvocationBinding binding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateBinding(owner, binding);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Table}
                (operation_id, tenant_id, project_id, run_id, environment_id, resource_kind,
                 resource_id, lifecycle_generation, contract_version, core_invocation_id, setup_invocation_id,
                 setup_receipt_hash, accepted_selection_hash, root_session_id, actor_issuer, actor_subject,
                 membership_id, membership_revision, execution_fence, root_state_version, accepted_at,
                 link_state, result_resource_generation, result_provider_fencing_generation,
                 result_transition_revision, result_lease_revision, result_provider_seam,
                 result_provider_id, result_provider_resource_id, result_provider_binding_json,
                 created_at, completed_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, 'Environment',
                 @resource_id, @lifecycle_generation, @contract_version, @core_invocation_id, @setup_invocation_id,
                 @receipt_hash, @selection_hash, @root_session_id, @actor_issuer, @actor_subject,
                 @membership_id, @membership_revision, @execution_fence, @root_state_version, @accepted_at,
                 'Completed', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, @created_at, @completed_at)
            """, connection, transaction);
        AddOwner(command, owner);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Varchar, owner.EnvironmentId);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, lifecycleGeneration);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertCompletedWorkspaceVolumeMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        string volumeId,
        long lifecycleGeneration,
        long transitionRevision,
        P1InvocationBinding binding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateBinding(owner, binding);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Table}
                (operation_id, tenant_id, project_id, run_id, environment_id, resource_kind,
                 resource_id, lifecycle_generation, contract_version, core_invocation_id, setup_invocation_id,
                 setup_receipt_hash, accepted_selection_hash, root_session_id, actor_issuer, actor_subject,
                 membership_id, membership_revision, execution_fence, root_state_version, accepted_at,
                 link_state, result_resource_generation, result_transition_revision, created_at, completed_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, 'WorkspaceVolumeMetadata',
                 @resource_id, @lifecycle_generation, @contract_version, @core_invocation_id, @setup_invocation_id,
                 @receipt_hash, @selection_hash, @root_session_id, @actor_issuer, @actor_subject,
                 @membership_id, @membership_revision, @execution_fence, @root_state_version, @accepted_at,
                 'Completed', 0, @transition_revision, @created_at, @completed_at)
            """, connection, transaction);
        AddOwner(command, owner);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Varchar, volumeId);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, lifecycleGeneration);
        command.Parameters.AddWithValue("transition_revision", NpgsqlDbType.Bigint, transitionRevision);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task InsertPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        string resourceKind,
        string resourceId,
        long lifecycleGeneration,
        long? resourceGeneration,
        long? providerFencingGeneration,
        long? transitionRevision,
        P1InvocationBinding binding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateBinding(owner, binding);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Table}
                (operation_id, tenant_id, project_id, run_id, environment_id, resource_kind,
                 resource_id, lifecycle_generation, contract_version, core_invocation_id, setup_invocation_id,
                 setup_receipt_hash, accepted_selection_hash, root_session_id, actor_issuer, actor_subject,
                 membership_id, membership_revision, execution_fence, root_state_version, accepted_at,
                 link_state, result_resource_generation, result_provider_fencing_generation,
                 result_transition_revision, created_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @resource_kind,
                 @resource_id, @lifecycle_generation, @contract_version, @core_invocation_id, @setup_invocation_id,
                 @receipt_hash, @selection_hash, @root_session_id, @actor_issuer, @actor_subject,
                 @membership_id, @membership_revision, @execution_fence, @root_state_version, @accepted_at,
                 'Pending', @resource_generation, @provider_fencing_generation,
                 @transition_revision, @created_at)
            """, connection, transaction);
        AddOwner(command, owner);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue("resource_kind", NpgsqlDbType.Varchar, resourceKind);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Varchar, resourceId);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, lifecycleGeneration);
        command.Parameters.AddWithValue(
            "resource_generation", NpgsqlDbType.Bigint, (object?)resourceGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "provider_fencing_generation",
            NpgsqlDbType.Bigint,
            (object?)providerFencingGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "transition_revision", NpgsqlDbType.Bigint, (object?)transitionRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        string resourceKind,
        string resourceId,
        long lifecycleGeneration,
        P1InvocationBinding binding,
        string state,
        long? resourceGeneration,
        long? providerFencingGeneration,
        long? transitionRevision,
        long? leaseRevision,
        ProviderResourceRef? providerResource,
        object? providerBinding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await RequireSameBindingAsync(
            connection,
            transaction,
            owner,
            operationId,
            resourceKind,
            resourceId,
            lifecycleGeneration,
            binding,
            cancellationToken).ConfigureAwait(false);
        await using (var read = new NpgsqlCommand($"""
            SELECT link_state, result_resource_generation, result_provider_fencing_generation,
                   result_transition_revision, result_lease_revision, result_provider_seam,
                   result_provider_id, result_provider_resource_id,
                   result_provider_binding_json::text
            FROM {Table}
            WHERE operation_id = @operation_id AND tenant_id = @tenant_id
              AND project_id = @project_id AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction))
        {
            AddOwner(read, owner);
            read.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new EnvironmentLifecycleException(
                    "p1_creation_link_not_retained",
                    "The Environment operation was not reserved with this exact current P1 binding.");
            var linkState = reader.GetString(0);
            if (linkState is "Failed" or "Uncertain")
                return;
            if (linkState == "Completed")
            {
                var existingBindingJson = reader.IsDBNull(8) ? null : reader.GetString(8);
                var expectedBindingJson = providerBinding is null
                    ? null
                    : JsonSerializer.Serialize(providerBinding, JsonOptions);
                var sameResult =
                    NullableInt64(reader, 1) == resourceGeneration &&
                    NullableInt64(reader, 2) == providerFencingGeneration &&
                    NullableInt64(reader, 3) == transitionRevision &&
                    NullableInt64(reader, 4) == leaseRevision &&
                    NullableString(reader, 5) == providerResource?.Seam.ToString() &&
                    NullableString(reader, 6) == providerResource?.ProviderId &&
                    NullableString(reader, 7) == providerResource?.ResourceId &&
                    SameJson(existingBindingJson, expectedBindingJson);
                if (!sameResult)
                    throw new EnvironmentLifecycleException(
                        "p1_creation_link_result_conflict",
                        "A completed P1 creation link cannot be changed to a different owner result.");
                return;
            }
            if (linkState != "Pending")
                throw new EnvironmentLifecycleException(
                    "p1_creation_link_not_pending",
                    "The P1 link is missing, already finalized, or belongs to a different owner.");
        }
        await using var command = new NpgsqlCommand($"""
            UPDATE {Table}
            SET link_state = @link_state,
                result_resource_generation = @resource_generation,
                result_provider_fencing_generation = @provider_fencing_generation,
                result_transition_revision = @transition_revision,
                result_lease_revision = @lease_revision,
                result_provider_seam = @provider_seam,
                result_provider_id = @provider_id,
                result_provider_resource_id = @provider_resource_id,
                result_provider_binding_json = @provider_binding_json,
                completed_at = @completed_at
            WHERE operation_id = @operation_id
              AND tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND link_state = 'Pending'
            """, connection, transaction);
        AddOwner(command, owner);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue("link_state", NpgsqlDbType.Varchar, state);
        command.Parameters.AddWithValue(
            "resource_generation", NpgsqlDbType.Bigint, (object?)resourceGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "provider_fencing_generation",
            NpgsqlDbType.Bigint,
            (object?)providerFencingGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "transition_revision", NpgsqlDbType.Bigint, (object?)transitionRevision ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "lease_revision", NpgsqlDbType.Bigint, (object?)leaseRevision ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "provider_seam", NpgsqlDbType.Varchar, providerResource is null
                ? DBNull.Value
                : providerResource.Seam.ToString());
        command.Parameters.AddWithValue(
            "provider_id", NpgsqlDbType.Varchar, providerResource is null
                ? DBNull.Value
                : providerResource.ProviderId);
        command.Parameters.AddWithValue(
            "provider_resource_id", NpgsqlDbType.Varchar, providerResource is null
                ? DBNull.Value
                : providerResource.ResourceId);
        command.Parameters.AddWithValue(
            "provider_binding_json", NpgsqlDbType.Jsonb, providerBinding is null
                ? DBNull.Value
                : JsonSerializer.Serialize(providerBinding, JsonOptions));
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new EnvironmentLifecycleException(
                "p1_creation_link_not_pending",
                "The P1 link is missing, already finalized, or belongs to a different owner.");
    }

    public static async Task RequireRetainedResourceLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        string resourceKind,
        string resourceId,
        long lifecycleGeneration,
        P1InvocationBinding binding,
        CancellationToken cancellationToken)
    {
        ValidateBinding(owner, binding);
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1
                FROM {Table}
                WHERE tenant_id = @tenant_id AND project_id = @project_id
                  AND run_id = @run_id AND environment_id = @environment_id
                  AND resource_kind = @resource_kind AND resource_id = @resource_id
                  AND lifecycle_generation = @lifecycle_generation
                  AND contract_version = @contract_version
                  AND core_invocation_id = @core_invocation_id
                  AND setup_invocation_id = @setup_invocation_id
                  AND setup_receipt_hash = @receipt_hash
                  AND accepted_selection_hash = @selection_hash
                  AND root_session_id = @root_session_id
                  AND actor_issuer = @actor_issuer AND actor_subject = @actor_subject
                  AND membership_id = @membership_id AND membership_revision = @membership_revision
                  AND execution_fence = @execution_fence AND root_state_version = @root_state_version
                  AND accepted_at = @accepted_at
                  AND link_state = 'Completed')
            """, connection, transaction);
        AddOwner(command, owner);
        AddBinding(command, binding);
        command.Parameters.AddWithValue("resource_kind", NpgsqlDbType.Varchar, resourceKind);
        command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Varchar, resourceId);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, lifecycleGeneration);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new EnvironmentLifecycleException(
                "p1_creation_link_not_retained",
                $"The exact Environment and {resourceKind} P1 metadata must already be retained before this write.");
    }

    public static async Task RequireSameBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid operationId,
        string expectedResourceKind,
        string expectedResourceId,
        long expectedLifecycleGeneration,
        P1InvocationBinding binding,
        CancellationToken cancellationToken)
    {
        ValidateBinding(owner, binding);
        await using var command = new NpgsqlCommand($"""
            SELECT resource_kind, resource_id, lifecycle_generation, contract_version, core_invocation_id, setup_invocation_id,
                   setup_receipt_hash, accepted_selection_hash, root_session_id, actor_issuer,
                   actor_subject, membership_id, membership_revision, execution_fence,
                   root_state_version, accepted_at, tenant_id, project_id, run_id
            FROM {Table}
            WHERE operation_id = @operation_id AND tenant_id = @tenant_id
              AND project_id = @project_id AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwner(command, owner);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != expectedResourceKind ||
            reader.GetString(1) != expectedResourceId ||
            reader.GetInt64(2) != expectedLifecycleGeneration ||
            reader.GetInt32(3) != binding.ContractVersion ||
            reader.GetGuid(4) != binding.CoreInvocationId ||
            reader.GetGuid(5) != binding.SetupInvocationId ||
            reader.GetString(6).TrimEnd() != binding.SetupReceiptHash ||
            reader.GetString(7).TrimEnd() != binding.AcceptedSelectionHash ||
            reader.GetString(8) != binding.RootSessionId ||
            reader.GetString(9) != binding.ActorIssuer ||
            reader.GetString(10) != binding.ActorSubject ||
            reader.GetGuid(11) != binding.MembershipId ||
            reader.GetInt64(12) != binding.MembershipRevision ||
            reader.GetInt64(13) != binding.ExecutionFence ||
            reader.GetInt64(14) != binding.RootStateVersion ||
            PostgresTimestamp(reader.GetFieldValue<DateTimeOffset>(15)) != PostgresTimestamp(binding.AcceptedAt) ||
            reader.GetString(16) != owner.TenantId ||
            reader.GetString(17) != owner.ProjectId ||
            reader.GetString(18) != owner.RunId)
            throw new EnvironmentLifecycleException(
                "p1_creation_link_not_retained",
                "The Environment operation was not reserved with this exact current P1 binding.");
    }

    private static void AddOwner(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Varchar, owner.EnvironmentId);
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool SameJson(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));

    private static void AddBinding(NpgsqlCommand command, P1InvocationBinding binding)
    {
        command.Parameters.AddWithValue("core_invocation_id", NpgsqlDbType.Uuid, binding.CoreInvocationId);
        command.Parameters.AddWithValue("setup_invocation_id", NpgsqlDbType.Uuid, binding.SetupInvocationId);
        command.Parameters.AddWithValue("contract_version", NpgsqlDbType.Integer, binding.ContractVersion);
        command.Parameters.AddWithValue("accepted_at", NpgsqlDbType.TimestampTz, PostgresTimestamp(binding.AcceptedAt));
        command.Parameters.AddWithValue("receipt_hash", NpgsqlDbType.Char, binding.SetupReceiptHash);
        command.Parameters.AddWithValue("selection_hash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        command.Parameters.AddWithValue("root_session_id", NpgsqlDbType.Varchar, binding.RootSessionId);
        command.Parameters.AddWithValue("actor_issuer", NpgsqlDbType.Varchar, binding.ActorIssuer);
        command.Parameters.AddWithValue("actor_subject", NpgsqlDbType.Varchar, binding.ActorSubject);
        command.Parameters.AddWithValue("membership_id", NpgsqlDbType.Uuid, binding.MembershipId);
        command.Parameters.AddWithValue("membership_revision", NpgsqlDbType.Bigint, binding.MembershipRevision);
        command.Parameters.AddWithValue("execution_fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue("root_state_version", NpgsqlDbType.Bigint, binding.RootStateVersion);
    }

    private static void ValidateBinding(EnvironmentOwnerIdentity owner, P1InvocationBinding binding)
    {
        if (binding.ContractVersion != 1 ||
            binding.CoreInvocationId == Guid.Empty ||
            binding.SetupInvocationId == Guid.Empty ||
            binding.TenantId != owner.TenantId ||
            binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId ||
            !IsLowercaseHash(binding.SetupReceiptHash) ||
            !IsLowercaseHash(binding.AcceptedSelectionHash) ||
            string.IsNullOrWhiteSpace(binding.RootSessionId) ||
            string.IsNullOrWhiteSpace(binding.ActorIssuer) ||
            string.IsNullOrWhiteSpace(binding.ActorSubject) ||
            binding.MembershipId == Guid.Empty ||
            binding.MembershipRevision < 1 ||
            binding.ExecutionFence < 1 ||
            binding.RootStateVersion < 1 ||
            binding.AcceptedAt == default)
            throw new EnvironmentLifecycleException(
                "p1_invocation_binding_mismatch",
                "The P1 binding does not contain valid provenance for this Environment owner.");
    }

    private static bool IsLowercaseHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static DateTimeOffset PostgresTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }
}
