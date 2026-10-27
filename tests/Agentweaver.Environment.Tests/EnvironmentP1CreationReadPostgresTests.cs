using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentP1CreationReadPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    private const string MountPath = "/workspace/agentweaver/project";

    [Fact]
    public async Task ReadsActualOwnerCommittedRecordsWithoutWritesAndRejectsForeignOrStaleJoins()
    {
        var id = Guid.NewGuid().ToString("N");
        var owner = new EnvironmentOwnerIdentity("tenant", "project-" + id, "run-" + id, "environment-" + id);
        var binding = new P1InvocationBinding(1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            owner.TenantId, owner.ProjectId, owner.RunId, "root", "https://issuer.test", "actor",
            Guid.NewGuid(), 1, new string('b', 64), 1, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
        var store = fixture.CreateStore();
        var lifecycle = await new EnvironmentLifecycleProducer(store).RegisterP1Async(
            owner, "register", binding, default, _ => Task.CompletedTask);
        var fence = lifecycle.Snapshot.Fence;
        const string volumeId = "volume";
        var spec = new WorkspaceVolumeSpec(volumeId, owner.ProjectId,
            new(WorkspaceVolumeOwnerKind.Run, owner.RunId), owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany, 8,
            "azure-files", WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
        await store.CreateP1WorkspaceVolumeAsync(
            fence, volumeId, JsonSerializer.SerializeToElement(spec), "metadata", binding, default,
            _ => Task.CompletedTask);
        var volume = await store.ReserveP1WorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "volume-provision", binding, default, _ => Task.CompletedTask);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "actual-provider-share", volume.TargetResourceGeneration);
        var storageBinding = new WorkspaceVolumeProviderBindingSnapshot(
            "azure-files", "1.0.0", 1, "options", JsonSerializer.SerializeToElement(new { account = "private" }),
            JsonSerializer.SerializeToElement(new
            {
                resourceId = storage.ResourceId, @namespace = "namespace", claimName = "pvc", claimUid = storage.ResourceId
            }));
        await store.CompleteP1WorkspaceVolumeProvisionAsync(
            volume.OperationId, fence, true, storage, storageBinding, true, binding, default,
            _ => Task.CompletedTask);
        var ready = (await store.GetWorkspaceVolumeAsync(fence, volumeId, default))!;
        var bind = await store.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, ready.TransitionRevision, ready.ResourceGeneration, ready.DataGeneration, "bind", default);
        await store.CompleteWorkspaceVolumeBindAsync(bind.OperationId, fence, true, storage, true, default);
        var leases = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var intent = new SandboxLeaseProvisionIntent("agent-sandbox", "1.0.0", 1, "options",
            JsonSerializer.SerializeToElement(new { image = "controlled-host" }),
            JsonSerializer.SerializeToElement(new { selection = "controlled-selection" }),
            JsonSerializer.SerializeToElement(new { workspaceVolumeId = volumeId }));
        var reservation = await leases.ReserveP1ProvisionAsync(
            fence, "sandbox", volumeId, intent, binding, default, _ => Task.CompletedTask);
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            intent.ProviderId, intent.OptionsRevision, fence, reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration, reservation.Lease.OperationId);
        var bound = (await store.GetWorkspaceVolumeAsync(fence, volumeId, default))!;
        var attach = await store.ReserveWorkspaceVolumeAttachAsync(
            fence, volumeId, bound.TransitionRevision, bound.ResourceGeneration, bound.DataGeneration, "attach", default);
        var request = new SandboxProvisionApiRequest(volumeId, bound.ResourceGeneration, bound.DataGeneration,
            MountPath, false, 1, "sandbox");
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(
            owner.ProjectId, owner.EnvironmentId, owner.RunId, new(owner.ProjectId, volumeId, bound.ResourceGeneration),
            planned, storage, fence, bound.DataGeneration, request.MountPath, false,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim).Validate();
        var recovery = new EnvironmentSandboxManager.SandboxProviderRecoveryIntent(
            1, request, new(negotiation, JsonSerializer.SerializeToElement(
                new AgentSandboxPersistentVolumeClaimAttachment(1, storage.ProviderId, "namespace", "pvc", storage.ResourceId))),
            ImmutableDictionary<string, string>.Empty, attach.TargetTransitionRevision);
        var lease = await leases.SaveProviderRequestAsync(
            reservation.Lease.OperationId, fence,
            JsonSerializer.SerializeToElement(recovery, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);
        await store.CompleteWorkspaceVolumeAttachAsync(attach.OperationId, fence, true, storage, true, default);
        var attempt = await leases.BeginProviderAttemptAsync(SandboxProviderAttemptRequest.ForProvision(lease), default);
        var sandbox = planned with { ResourceId = Guid.NewGuid().ToString("D") };
        var resource = new SandboxProvisionedResource(sandbox, new(Guid.NewGuid()), new("controlled-placement"),
            ImmutableHashSet.Create(SandboxCapabilities.VmIsolation), [],
            new(intent.ProviderId, intent.AdapterVersion, intent.OptionsSchemaVersion, intent.OptionsRevision,
                intent.OptionsSnapshot, JsonSerializer.SerializeToElement(new { providerClaim = sandbox.ResourceId })));
        var active = await leases.CompleteP1ProvisionAsync(
            lease.OperationId, fence, resource, true, binding, default, attempt.Attempt.AttemptId,
            beforeCommitCheck: _ => Task.CompletedTask);
        var pod = new SandboxPodObservation(1, DateTimeOffset.UtcNow, fence, sandbox,
            active.ProviderFencingGeneration, active.OperationId, new string('c', 64),
            "namespace", sandbox.ResourceId, "sandbox-uid", "pod", "pod-uid", "42", 1, "Running",
            new("namespace", "pvc", storage.ResourceId, MountPath, false), true);

        var before = await SnapshotAsync(owner);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync();
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(connection, transaction, owner, default);
        var rows = await EnvironmentP1CreationReader.ReadRecordsAsync(
            connection, transaction, fence, volumeId, storage.Generation, pod, default);
        var read = EnvironmentP1CreationReader.Join(
            binding, fence, volumeId, storage, volume.TargetTransitionRevision, sandbox,
            active.ProviderFencingGeneration, active.LeaseRevision, pod, rows.Select(row => row.Record),
            DateTimeOffset.UtcNow);
        Assert.Equal(lifecycle.OperationId, read.Environment.OperationId);
        Assert.Equal(volume.OperationId, read.WorkspaceProvision.OperationId);
        Assert.Equal(active.OperationId, read.SandboxProvision.OperationId);
        Assert.Equal(storage, read.WorkspaceProvision.ProviderResource);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(read));
        Assert.Throws<RuntimeAuthorizationException>(() => EnvironmentP1CreationReader.Join(
            binding with { CoreInvocationId = Guid.NewGuid() }, fence, volumeId, storage, volume.TargetTransitionRevision,
            sandbox, active.ProviderFencingGeneration, active.LeaseRevision, pod,
            rows.Select(row => row.Record), DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => EnvironmentP1CreationReader.ReadRecordsAsync(
            connection, transaction, new(fence.Owner, fence.LifecycleGeneration + 1), volumeId, storage.Generation, pod, default));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => EnvironmentP1CreationReader.ReadRecordsAsync(
            connection, transaction, new(new(owner.TenantId, owner.ProjectId, "foreign", owner.EnvironmentId),
                fence.LifecycleGeneration), volumeId, storage.Generation, pod, default));
        var placement = new EnvironmentRuntimeBootstrapContext(
            1, owner.TenantId, owner.ProjectId, owner.RunId, owner.EnvironmentId, fence.LifecycleGeneration,
            active.CurrentFencingGeneration, active.ProviderFencingGeneration, active.LeaseRevision, active.LeaseExpiresAt!.Value,
            sandbox, resource.Endpoint, resource.Placement, "profile",
            new("https://runtime.test/runtime/v1/configure"), new("https://orchestrator.test/"));
        var context = new EnvironmentRuntimeReadinessContext(
            1, placement, active.CreatedAt!.Value,
            new(sandbox, SandboxObservedState.Ready, active.ProviderFencingGeneration, true, true, 1, [],
                ProvisionOperationId: active.OperationId) { PodObservation = pod },
            new(30, 30, 30, 30, 30, 120), request.MountPath);
        var current = await EnvironmentP1CreationReader.ReadUnderOwnerLockAsync(
            fixture.DataSource, TimeProvider.System, binding, context, pod, default);
        Assert.NotEqual(planned.ResourceId, current.SandboxProvision.ProviderResource!.ResourceId);
        Assert.Equal(sandbox, current.SandboxProvision.ProviderResource);
        Assert.Equal(active.OperationId, current.SandboxProvision.OperationId);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => EnvironmentP1CreationReader.ReadUnderOwnerLockAsync(
            fixture.DataSource, TimeProvider.System, binding, context,
            pod with { Resource = pod.Resource with { ResourceId = "replacement" } }, default));
        await transaction.CommitAsync();
        Assert.Equal(before, await SnapshotAsync(owner));
    }

    private async Task<string> SnapshotAsync(EnvironmentOwnerIdentity owner)
    {
        await using var command = fixture.DataSource.CreateCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(l) ORDER BY operation_id)::text, '[]')
            FROM environment.p1_creation_links l WHERE project_id = @project
            """);
        command.Parameters.AddWithValue("project", owner.ProjectId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
