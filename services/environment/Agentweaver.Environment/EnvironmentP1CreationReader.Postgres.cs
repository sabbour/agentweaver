using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public sealed partial class EnvironmentP1CreationReader(
    EnvironmentRuntimePlacementReader placements,
    IEnvironmentP1InvocationBindingReader core,
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads existing creation records under the placement's owner lock and current run-bound
    /// authority. The fixed Core reads do not recurse into Environment or grant effects.
    /// </summary>
    public Task<EnvironmentP1CreationBinding> ReadAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId,
        string environmentId, string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? replacements = null) =>
        placements.GetPodObservationAsync(
            actor, projectId, runId, sessionId, environmentId, profileId, profiles,
            async (context, pod, token) =>
            {
                var binding = await core.ReadCurrentAsync(actor, projectId, runId, token).ConfigureAwait(false)
                    ?? throw Denied();
                var owner = context.Placement.RuntimeOwnerContext ?? throw Denied();
                if (binding.ContractVersion != 1 || binding.TenantId != pod.EnvironmentFence.Owner.TenantId ||
                    binding.ProjectId != projectId || binding.RunId != runId ||
                    binding.RootSessionId != sessionId || binding.ActorIssuer != owner.ActorIssuer ||
                    binding.ActorSubject != owner.ActorId ||
                    binding.ExecutionFence != owner.ExecutionFence ||
                    binding.AcceptedSelectionHash != owner.AcceptedSelectionHash)
                    throw Denied();
                var result = await ReadUnderOwnerLockAsync(
                    dataSource, timeProvider, binding, context, pod, token).ConfigureAwait(false);
                var after = await core.ReadCurrentAsync(actor, projectId, runId, token).ConfigureAwait(false);
                if (after != binding || !actor.Bearer.IsUsable())
                    throw new RuntimeAuthorizationException("p1_invocation_binding_changed");
                return result;
            }, cancellationToken, replacements);

    internal static async Task<EnvironmentP1CreationBinding> ReadUnderOwnerLockAsync(
        NpgsqlDataSource dataSource, TimeProvider timeProvider,
        P1InvocationBinding binding, EnvironmentRuntimeReadinessContext context,
        SandboxPodObservation pod, CancellationToken cancellationToken)
    {
        // The enclosing placement callback holds the owner lock on another connection.
        // This transaction must never reacquire it or write owner state.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var fence = pod.EnvironmentFence;
        var placement = context.Placement;
        var lease = await EnvironmentSandboxLeaseStore.ReadCurrentAsync(
            connection, transaction, fence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw Denied();
        if (lease.Fence != fence || lease.OperationId != pod.ProvisionOperationId ||
            lease.ResourceGeneration != pod.Resource.Generation ||
            lease.ProviderFencingGeneration != pod.FencingGeneration ||
            lease.CurrentFencingGeneration != pod.FencingGeneration ||
            lease.LeaseRevision != placement.LeaseRevision || lease.State != SandboxLeaseState.Active ||
            !lease.IsCurrent || lease.LeaseExpiresAt is null || lease.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw Denied();
        var resource = lease.ProvisionedResource ?? throw Denied();
        var recovery = EnvironmentSandboxManager.ReadProviderRecoveryIntent(lease);
        var volumeId = recovery.Request.VolumeId;
        var workspace = await EnvironmentLifecycleStore.ReadWorkspaceVolumeInTransactionAsync(
            connection, transaction, fence, volumeId, cancellationToken).ConfigureAwait(false)
            ?? throw Denied();
        _ = EnvironmentSandboxManager.ProjectRuntimeWorkspace(fence, lease, workspace);
        var attachment = recovery.Workspace.Negotiation;
        if (resource.Resource != pod.Resource ||
            attachment.EnvironmentFence != fence || workspace.EnvironmentFence != fence ||
            workspace.Phase != EnvironmentWorkspaceVolumeState.Attached ||
            workspace.Resource != attachment.StorageResource || workspace.ProviderBinding is null ||
            workspace.ResourceGeneration != attachment.Volume.ResourceGeneration ||
            workspace.DataGeneration != attachment.DataGeneration ||
            workspace.TransitionRevision != recovery.WorkspaceAttachmentTransitionRevision ||
            attachment.MountPath != pod.WorkspaceAttachment?.MountPath ||
            attachment.ReadOnly != pod.WorkspaceAttachment.ReadOnly)
            throw Denied();
        _ = workspace.ProviderBinding.ValidateFor(workspace.Resource!);
        var descriptor = workspace.ProviderBinding.ReleaseDescriptor;
        if (!SameString(descriptor, "namespace", pod.WorkspaceAttachment.Namespace) ||
            !SameString(descriptor, "claimName", pod.WorkspaceAttachment.ClaimName) ||
            !SameString(descriptor, "claimUid", pod.WorkspaceAttachment.ClaimUid))
            throw Denied();
        var records = await ReadRecordsAsync(
            connection, transaction, fence, volumeId, workspace.ResourceGeneration, pod, cancellationToken)
            .ConfigureAwait(false);
        var result = Join(binding, fence, volumeId, workspace.Resource!, workspace.TransitionRevision,
            resource.Resource, pod.FencingGeneration, placement.LeaseRevision, pod,
            records.Select(record => record.Record), timeProvider.GetUtcNow());
        RequireProviderBinding(result.WorkspaceProvision.OperationId, workspace.ProviderBinding);
        RequireProviderBinding(result.SandboxProvision.OperationId, resource.ProviderBinding);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;

        void RequireProviderBinding(Guid operationId, object expected)
        {
            var actual = records.Single(record => record.Record.Receipt.OperationId == operationId).ProviderBinding;
            if (!JsonNode.DeepEquals(actual, JsonSerializer.SerializeToNode(expected, JsonOptions)))
                throw Denied();
        }
    }

    private static bool SameString(JsonElement descriptor, string key, string expected) =>
        descriptor.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() == expected;

    internal static async Task<List<(EnvironmentP1CreationRecord Record, JsonNode? ProviderBinding)>> ReadRecordsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EnvironmentGenerationFence fence,
        string volumeId, long volumeGeneration, SandboxPodObservation pod, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT operation_id, resource_kind, resource_id, lifecycle_generation,
                   contract_version, core_invocation_id, setup_invocation_id, setup_receipt_hash,
                   accepted_selection_hash, root_session_id, actor_issuer, actor_subject,
                   membership_id, membership_revision, execution_fence, root_state_version, accepted_at,
                   link_state, result_resource_generation, result_provider_fencing_generation,
                   result_transition_revision, result_lease_revision, result_provider_seam,
                   result_provider_id, result_provider_resource_id, created_at, completed_at,
                   result_provider_binding_json::text
            FROM environment.p1_creation_links
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND environment_id = @environment
              AND lifecycle_generation = @lifecycle
              AND ((resource_kind = 'Environment' AND resource_id = @environment)
                OR (resource_kind = 'WorkspaceVolumeMetadata' AND resource_id = @volume)
                OR (resource_kind = 'WorkspaceVolume' AND resource_id = @volume AND result_resource_generation = @volume_generation)
                OR (resource_kind = 'Sandbox' AND operation_id = @sandbox_operation))
            LIMIT 5
            """, connection, transaction);
        AddOwner(command, fence.Owner);
        command.Parameters.AddWithValue("lifecycle", fence.LifecycleGeneration);
        command.Parameters.AddWithValue("volume", volumeId);
        command.Parameters.AddWithValue("volume_generation", volumeGeneration);
        command.Parameters.AddWithValue("sandbox_operation", pod.ProvisionOperationId);
        var records = new List<(EnvironmentP1CreationRecord, JsonNode?)>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var binding = new P1InvocationBinding(
                reader.GetInt32(4), reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7).TrimEnd(),
                fence.Owner.TenantId, fence.Owner.ProjectId, fence.Owner.RunId, reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetGuid(12), reader.GetInt64(13),
                reader.GetString(8).TrimEnd(), reader.GetInt64(14), reader.GetInt64(15),
                reader.GetFieldValue<DateTimeOffset>(16));
            var receipt = new EnvironmentP1CreationReceipt(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                Number(18), Number(19), Number(20), Number(21),
                reader.IsDBNull(22) ? null : new(
                    Enum.Parse<ProviderSeam>(reader.GetString(22)), reader.GetString(23),
                    reader.GetString(24), Number(18) ?? throw Denied()),
                reader.GetFieldValue<DateTimeOffset>(25),
                reader.IsDBNull(26) ? default : reader.GetFieldValue<DateTimeOffset>(26));
            records.Add((new(new(fence.Owner, reader.GetInt64(3)), binding, reader.GetString(17), receipt),
                reader.IsDBNull(27) ? null : JsonNode.Parse(reader.GetString(27))));
        }
        if (records.Count != 4)
            throw Denied();
        return records;

        long? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static void AddOwner(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, owner.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, owner.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, owner.RunId);
        command.Parameters.AddWithValue("environment", NpgsqlDbType.Varchar, owner.EnvironmentId);
    }
}
