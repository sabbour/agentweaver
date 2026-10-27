using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentP1CreationLinkPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task P1EnvironmentRegistrationLinksTheCommittedOperationAndRejectsRetrofitting()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var binding = NewBinding(owner);
        var producer = new EnvironmentLifecycleProducer(store);
        var rollbackCallbackCount = 0;
        var registrationRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            producer.RegisterP1Async(
                owner,
                "p1-registration",
                binding,
                CancellationToken.None,
                _ => Interlocked.Increment(ref rollbackCallbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", registrationRollback.Code);
        Assert.Null(await store.GetAsync(owner, CancellationToken.None));
        Assert.Equal(0, await CountLinksAsync(owner));

        var created = await producer.RegisterP1Async(
            owner, "p1-registration", binding, CancellationToken.None, _ => Task.CompletedTask);
        var replay = await producer.RegisterP1Async(
            owner, "p1-registration", binding, CancellationToken.None, _ => Task.CompletedTask);

        Assert.False(created.Replayed);
        Assert.True(replay.Replayed);
        Assert.NotNull(created.OperationId);
        Assert.NotEqual(Guid.Empty, created.OperationId!.Value);
        Assert.Equal(created.OperationId, replay.OperationId);
        var link = await ReadLinkAsync(created.OperationId.Value);
        Assert.Equal("Environment", link.ResourceKind);
        Assert.Equal(owner.EnvironmentId, link.ResourceId);
        Assert.Equal(1, link.LifecycleGeneration);
        Assert.Equal("Completed", link.State);
        Assert.Equal(binding.CoreInvocationId, link.CoreInvocationId);
        Assert.Equal(binding.SetupInvocationId, link.SetupInvocationId);
        Assert.Equal(binding.SetupReceiptHash, link.SetupReceiptHash);
        Assert.Equal(binding.AcceptedSelectionHash, link.AcceptedSelectionHash);
        await AssertAppendOnlyAsync(created.OperationId.Value);

        var changedBinding = binding with { CoreInvocationId = Guid.NewGuid() };
        var failure = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            producer.RegisterP1Async(
                owner, "p1-registration", changedBinding, CancellationToken.None, _ => Task.CompletedTask));
        Assert.Equal("p1_creation_link_not_retained", failure.Code);

        var legacyOwner = NewOwner();
        var legacy = await producer.RegisterAsync(legacyOwner, "shared-idempotency", CancellationToken.None);
        var retrofit = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            producer.RegisterP1Async(
                legacyOwner, "shared-idempotency", NewBinding(legacyOwner), CancellationToken.None,
                _ => Task.CompletedTask));
        Assert.Equal("p1_creation_link_not_retained", retrofit.Code);
        Assert.Equal(0, await CountLinksAsync(legacyOwner));
        Assert.NotEqual(created.OperationId, legacy.OperationId);
    }

    [Fact]
    public async Task P1ConcurrentRegistrationRetriesRetainOneExactBinding()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var binding = NewBinding(owner);
        var producer = new EnvironmentLifecycleProducer(store);
        var callbacks = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            producer.RegisterP1Async(
                owner,
                "concurrent-p1-registration",
                binding,
                CancellationToken.None,
                _ =>
                {
                    Interlocked.Increment(ref callbacks);
                    return Task.CompletedTask;
                })));

        Assert.Single(results, result => !result.Replayed);
        Assert.All(results, result => Assert.Equal(results[0].OperationId, result.OperationId));
        Assert.Equal(7, callbacks);
        Assert.Equal(1, await CountLinksAsync(owner));
    }

    [Fact]
    public async Task P1WorkspaceVolumeCompletionLinksTheRealProviderIdentityAndRetainsUncertainResults()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var binding = NewBinding(owner);
        var lifecycle = await new EnvironmentLifecycleProducer(store).RegisterP1Async(
            owner, "p1-registration", binding, CancellationToken.None, _ => Task.CompletedTask);
        const string volumeId = "p1-volume";
        var createRollbackCount = 0;
        var createRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.CreateP1WorkspaceVolumeAsync(
                lifecycle.Snapshot.Fence,
                volumeId,
                CreateVolumeSpecification(owner, volumeId),
                "volume-create",
                binding,
                CancellationToken.None,
                _ => Interlocked.Increment(ref createRollbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", createRollback.Code);
        Assert.Null(await store.GetWorkspaceVolumeAsync(
            lifecycle.Snapshot.Fence, volumeId, CancellationToken.None));
        Assert.Equal(1, await CountLinksAsync(owner));

        var metadata = await store.CreateP1WorkspaceVolumeAsync(
            lifecycle.Snapshot.Fence,
            volumeId,
            CreateVolumeSpecification(owner, volumeId),
            "volume-create",
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.Equal(1, metadata.TransitionRevision);
        Assert.Equal(0, metadata.ResourceGeneration);
        Assert.Equal(2, await CountLinksAsync(owner));
        var metadataOperationId = await ReadVolumeOperationIdAsync(owner, "volume-create");
        var metadataLink = await ReadLinkAsync(metadataOperationId);
        Assert.Equal("WorkspaceVolumeMetadata", metadataLink.ResourceKind);
        Assert.Equal(volumeId, metadataLink.ResourceId);
        Assert.Equal("Completed", metadataLink.State);
        Assert.Null(metadataLink.ProviderResourceId);
        var reserveRollbackCount = 0;
        var reserveRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.ReserveP1WorkspaceVolumeProvisionAsync(
                lifecycle.Snapshot.Fence,
                volumeId,
                expectedTransitionRevision: 1,
                expectedResourceGeneration: 0,
                expectedDataGeneration: 0,
                "p1-volume-provision",
                binding,
                CancellationToken.None,
                _ => Interlocked.Increment(ref reserveRollbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", reserveRollback.Code);
        Assert.Equal(2, await CountLinksAsync(owner));

        var reservation = await store.ReserveP1WorkspaceVolumeProvisionAsync(
            lifecycle.Snapshot.Fence,
            volumeId,
            expectedTransitionRevision: 1,
            expectedResourceGeneration: 0,
            expectedDataGeneration: 0,
            "p1-volume-provision",
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.False(reservation.Replayed);
        Assert.Equal(3, await CountLinksAsync(owner));
        Assert.Equal("Pending", (await ReadLinkAsync(reservation.OperationId)).State);
        var reservationReplay = await store.ReserveP1WorkspaceVolumeProvisionAsync(
            lifecycle.Snapshot.Fence,
            volumeId,
            expectedTransitionRevision: 1,
            expectedResourceGeneration: 0,
            expectedDataGeneration: 0,
            "p1-volume-provision",
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.True(reservationReplay.Replayed);
        Assert.Equal(reservation.OperationId, reservationReplay.OperationId);

        var resource = new ProviderResourceRef(
            ProviderSeam.Storage, "azure-files", "actual-provider-file-share", reservation.TargetResourceGeneration);
        var providerBinding = CreateVolumeProviderBinding(resource);
        var staleCallback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.CompleteP1WorkspaceVolumeProvisionAsync(
                reservation.OperationId,
                lifecycle.Snapshot.Fence,
                effectMayHaveApplied: true,
                resource,
                providerBinding,
                effectVerified: true,
                binding,
                CancellationToken.None,
                _ => Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))));
        Assert.Equal("p1_invocation_binding_changed", staleCallback.Code);
        Assert.Equal("Pending", (await ReadLinkAsync(reservation.OperationId)).State);
        Assert.Null((await store.GetWorkspaceVolumeAsync(
            lifecycle.Snapshot.Fence, volumeId, CancellationToken.None))!.Resource);
        var completionRollbackCount = 0;
        var completionRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.CompleteP1WorkspaceVolumeProvisionAsync(
                reservation.OperationId,
                lifecycle.Snapshot.Fence,
                effectMayHaveApplied: true,
                resource,
                providerBinding,
                effectVerified: true,
                binding,
                CancellationToken.None,
                _ => Interlocked.Increment(ref completionRollbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", completionRollback.Code);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested,
            (await store.GetWorkspaceVolumeAsync(
                lifecycle.Snapshot.Fence, volumeId, CancellationToken.None))!.Phase);
        Assert.Equal("Pending", (await ReadLinkAsync(reservation.OperationId)).State);

        var uncertain = await store.CompleteP1WorkspaceVolumeProvisionAsync(
            reservation.OperationId,
            lifecycle.Snapshot.Fence,
            effectMayHaveApplied: true,
            resource,
            providerBinding,
            effectVerified: false,
            binding,
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, uncertain.TransitionState);
        var link = await ReadLinkAsync(reservation.OperationId);
        Assert.Equal("Uncertain", link.State);
        Assert.Equal(reservation.TargetResourceGeneration, link.ResourceGeneration);
        Assert.Equal(reservation.TargetTransitionRevision, link.TransitionRevision);
        Assert.Equal("Storage", link.ProviderSeam);
        Assert.Equal(resource.ProviderId, link.ProviderId);
        Assert.Equal(resource.ResourceId, link.ProviderResourceId);
        Assert.Contains(resource.ResourceId, link.ProviderBindingJson);
        var uncertainRetry = await store.CompleteP1WorkspaceVolumeProvisionAsync(
            reservation.OperationId,
            lifecycle.Snapshot.Fence,
            effectMayHaveApplied: true,
            resource,
            providerBinding,
            effectVerified: true,
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            uncertainRetry.TransitionState);
        Assert.Equal("Uncertain", (await ReadLinkAsync(reservation.OperationId)).State);
    }

    [Fact]
    public async Task P1SandboxCompletionLinksTheActualProviderResourceAndLeaseFence()
    {
        var lifecycleStore = fixture.CreateStore();
        var owner = NewOwner();
        var binding = NewBinding(owner);
        var lifecycle = await new EnvironmentLifecycleProducer(lifecycleStore).RegisterP1Async(
            owner, "p1-registration", binding, CancellationToken.None, _ => Task.CompletedTask);
        const string volumeId = "p1-volume";
        _ = await lifecycleStore.CreateP1WorkspaceVolumeAsync(
            lifecycle.Snapshot.Fence,
            volumeId,
            CreateVolumeSpecification(owner, volumeId),
            "p1-volume-create",
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var intent = NewSandboxIntent(volumeId);
        var reserveRollbackCount = 0;
        var reserveRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.ReserveP1ProvisionAsync(
                lifecycle.Snapshot.Fence,
                "p1-sandbox-provision",
                volumeId,
                intent,
                binding,
                CancellationToken.None,
                _ => Interlocked.Increment(ref reserveRollbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", reserveRollback.Code);
        Assert.Null(await store.GetCurrentAsync(lifecycle.Snapshot.Fence, CancellationToken.None));
        Assert.Equal(2, await CountLinksAsync(owner));

        var reservation = await store.ReserveP1ProvisionAsync(
            lifecycle.Snapshot.Fence,
            "p1-sandbox-provision",
            volumeId,
            intent,
            binding,
            CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.False(reservation.Replayed);

        var lease = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId,
            lifecycle.Snapshot.Fence,
            reservation.Lease.ProvisionIntent.ProviderRequest,
            CancellationToken.None);
        var attempt = await store.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForProvision(lease),
            CancellationToken.None);
        var providerResource = CreateSandboxResource(lifecycle.Snapshot.Fence, lease);
        var completionRollbackCount = 0;
        var completionRollback = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.CompleteP1ProvisionAsync(
                lease.OperationId,
                lifecycle.Snapshot.Fence,
                providerResource,
                effectMayHaveApplied: true,
                binding,
                CancellationToken.None,
                attempt.Attempt.AttemptId,
                beforeCommitCheck: _ => Interlocked.Increment(ref completionRollbackCount) == 2
                    ? Task.FromException(new RuntimeAuthorizationException("p1_invocation_binding_changed"))
                    : Task.CompletedTask));
        Assert.Equal("p1_invocation_binding_changed", completionRollback.Code);
        Assert.Equal(SandboxLeaseState.Provisioning,
            (await store.GetCurrentAsync(lifecycle.Snapshot.Fence, CancellationToken.None))!.State);
        Assert.Equal("Pending", (await ReadLinkAsync(lease.OperationId)).State);

        var active = await store.CompleteP1ProvisionAsync(
            lease.OperationId,
            lifecycle.Snapshot.Fence,
            providerResource,
            effectMayHaveApplied: true,
            binding,
            CancellationToken.None,
            attempt.Attempt.AttemptId,
            beforeCommitCheck: _ => Task.CompletedTask);

        Assert.Equal(SandboxLeaseState.Active, active.State);
        var link = await ReadLinkAsync(lease.OperationId);
        Assert.Equal("Sandbox", link.ResourceKind);
        Assert.Equal(lease.OperationId.ToString("D"), link.ResourceId);
        Assert.Equal("Completed", link.State);
        Assert.Equal(active.ResourceGeneration, link.ResourceGeneration);
        Assert.Equal(active.ProviderFencingGeneration, link.ProviderFencingGeneration);
        Assert.Equal(active.LeaseRevision, link.LeaseRevision);
        Assert.Equal("Sandbox", link.ProviderSeam);
        Assert.Equal(providerResource.Resource.ProviderId, link.ProviderId);
        Assert.Equal(providerResource.Resource.ResourceId, link.ProviderResourceId);
        using var persistedBinding = JsonDocument.Parse(link.ProviderBindingJson);
        Assert.Equal(
            providerResource.ProviderBinding.ReleaseDescriptor.GetProperty("providerClaim").GetString(),
            persistedBinding.RootElement.GetProperty("releaseDescriptor").GetProperty("providerClaim").GetString());
        _ = await store.CompleteP1ProvisionAsync(
            lease.OperationId,
            lifecycle.Snapshot.Fence,
            providerResource,
            effectMayHaveApplied: true,
            binding,
            CancellationToken.None,
            attempt.Attempt.AttemptId,
            beforeCommitCheck: _ => Task.CompletedTask);
        Assert.Equal("Completed", (await ReadLinkAsync(lease.OperationId)).State);
    }

    private async Task<CreationLink> ReadLinkAsync(Guid operationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT resource_kind, resource_id, lifecycle_generation, core_invocation_id,
                   setup_invocation_id, setup_receipt_hash, accepted_selection_hash, link_state,
                   result_resource_generation, result_provider_fencing_generation,
                   result_transition_revision, result_lease_revision, result_provider_seam,
                   result_provider_id, result_provider_resource_id,
                   COALESCE(result_provider_binding_json::text, '')
            FROM environment.p1_creation_links
            WHERE operation_id = @operation_id
            """,
            connection);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var link = new CreationLink(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetString(5).TrimEnd(),
            reader.GetString(6).TrimEnd(),
            reader.GetString(7),
            NullableInt64(reader, 8),
            NullableInt64(reader, 9),
            NullableInt64(reader, 10),
            NullableInt64(reader, 11),
            NullableString(reader, 12),
            NullableString(reader, 13),
            NullableString(reader, 14),
            reader.GetString(15));
        Assert.False(await reader.ReadAsync());
        return link;
    }

    private async Task<long> CountLinksAsync(EnvironmentOwnerIdentity owner)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM environment.p1_creation_links
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """,
            connection);
        AddOwner(command, owner);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<Guid> ReadVolumeOperationIdAsync(EnvironmentOwnerIdentity owner, string idempotencyKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT operation_id
            FROM environment.owner_effects
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'WorkspaceVolume' AND idempotency_key = @idempotency_key
            """,
            connection);
        AddOwner(command, owner);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertAppendOnlyAsync(Guid operationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE environment.p1_creation_links
            SET core_invocation_id = @new_invocation_id
            WHERE operation_id = @operation_id
            """,
            connection);
        command.Parameters.AddWithValue("new_invocation_id", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", failure.SqlState);
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddOwner(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Varchar, owner.EnvironmentId);
    }

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
    }

    private static P1InvocationBinding NewBinding(EnvironmentOwnerIdentity owner) =>
        new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            "root-session-" + Guid.NewGuid().ToString("N"),
            "https://identity.example",
            "actor-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(),
            1,
            new string('b', 64),
            3,
            1,
            new DateTimeOffset(2026, 10, 27, 12, 30, 0, TimeSpan.Zero).AddTicks(1_234_567));

    private static JsonElement CreateVolumeSpecification(EnvironmentOwnerIdentity owner, string volumeId) =>
        JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            volumeId,
            owner.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId),
            owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            8,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            []));

    private static WorkspaceVolumeProviderBindingSnapshot CreateVolumeProviderBinding(
        ProviderResourceRef resource) =>
        new WorkspaceVolumeProviderBindingSnapshot(
            resource.ProviderId,
            "1.0.0",
            1,
            "p1-options",
            JsonSerializer.SerializeToElement(new { account = "storage-account" }),
            JsonSerializer.SerializeToElement(new { resourceId = resource.ResourceId }))
            .ValidateFor(resource);

    private static SandboxLeaseProvisionIntent NewSandboxIntent(string volumeId) =>
        new(
            "agent-sandbox",
            "1.0.0",
            1,
            "p1-options",
            JsonSerializer.SerializeToElement(new { image = "agenthost:1", namespaceName = "sandbox" }),
            JsonSerializer.SerializeToElement(new { runSelection = "accepted" }),
            JsonSerializer.SerializeToElement(new { workspaceVolumeId = volumeId }));

    private static SandboxProvisionedResource CreateSandboxResource(
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot lease)
    {
        var resource = SandboxResourceIdentity.CreatePlannedReference(
            lease.ProvisionIntent.ProviderId,
            lease.ProvisionIntent.OptionsRevision,
            fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId);
        var providerBinding = new SandboxProviderBindingSnapshot(
            lease.ProvisionIntent.ProviderId,
            lease.ProvisionIntent.AdapterVersion,
            lease.ProvisionIntent.OptionsSchemaVersion,
            lease.ProvisionIntent.OptionsRevision,
            lease.ProvisionIntent.OptionsSnapshot,
            JsonSerializer.SerializeToElement(new
            {
                namespaceName = "sandbox",
                providerClaim = resource.ResourceId
            }));
        return new SandboxProvisionedResource(
            resource,
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("test-cluster"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                SandboxCapabilities.VmIsolation,
                SandboxCapabilities.WorkspacePersistentVolumeClaim),
            [],
            providerBinding);
    }

    private sealed record CreationLink(
        string ResourceKind,
        string ResourceId,
        long LifecycleGeneration,
        Guid CoreInvocationId,
        Guid SetupInvocationId,
        string SetupReceiptHash,
        string AcceptedSelectionHash,
        string State,
        long? ResourceGeneration,
        long? ProviderFencingGeneration,
        long? TransitionRevision,
        long? LeaseRevision,
        string? ProviderSeam,
        string? ProviderId,
        string? ProviderResourceId,
        string ProviderBindingJson);
}
