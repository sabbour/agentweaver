using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Npgsql;
using static Agentweaver.Environment.Tests.SandboxBuildTestCommandContractTests;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxBuildTestCommandPostgresTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ReservationTimestampMatchesPostgresPrecisionAndCanBeReadBack()
    {
        var fixture = await CreateDurableFixtureAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1234567890).AddTicks(7));
        var store = Store(clock);
        var reserved = await store.ReserveAsync(
            fixture.Request, fixture.Command, Initial(fixture), CancellationToken.None);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT created_at, target_provider_binding_json #>> '{operation,createdAt}'
            FROM environment.owner_effects
            WHERE operation_id = @operation_id
            """, connection);
        command.Parameters.AddWithValue("operation_id", fixture.Command.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var persistedTimestamp = reader.GetFieldValue<DateTimeOffset>(0);
        var jsonTimestamp = DateTimeOffset.Parse(
            reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(persistedTimestamp, jsonTimestamp);
        Assert.Equal(persistedTimestamp, reserved.State.Operation.CreatedAt);
        var restored = await store.GetAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(persistedTimestamp, restored.Operation.CreatedAt);
    }

    [Fact]
    public async Task ConcurrentReservationAndRestartReplayKeepOneOwnerOperation()
    {
        var fixture = await CreateDurableFixtureAsync();
        var initial = Initial(fixture);
        var reservations = await Task.WhenAll(
            Store().ReserveAsync(fixture.Request, fixture.Command, initial, CancellationToken.None),
            Store().ReserveAsync(fixture.Request, fixture.Command, initial, CancellationToken.None));

        var reserved = Assert.Single(reservations, reservation => !reservation.Replayed).State;
        Assert.Single(reservations, reservation => reservation.Replayed);
        await using var restartedDataSource = database.CreateDataSource("buildtest-restarted");
        var restarted = new EnvironmentSandboxBuildTestCommandStore(restartedDataSource, TimeProvider.System);
        var replay = await restarted.ReserveAsync(
            fixture.Request, fixture.Command, initial, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(reserved.Operation.CreatedAt, replay.State.Operation.CreatedAt);
        Assert.Equal(reserved.Operation.UpdatedAt, replay.State.Operation.UpdatedAt);
        Assert.Empty(replay.State.AttemptedEffects);
        Assert.Equal(SandboxBuildTestOperationStatus.Reserved, replay.State.Operation.Status);
        var persisted = await restarted.GetAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(fixture.Command.ImmutableHash, persisted.AcceptedCommand.ImmutableHash);
        Assert.Equal(fixture.Request.ExpectedBinding.SandboxResource, persisted.Operation.ExpectedBinding.SandboxResource);
        var owner = fixture.Request.ExpectedBinding.Fence.Owner;
        Assert.Null(await restarted.GetAsync(
            new(owner.TenantId, owner.ProjectId, "another-run", owner.EnvironmentId),
            fixture.Command.OperationId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReservationRejectsChangedImmutableCommandAndDifferentOwner()
    {
        var fixture = await CreateDurableFixtureAsync();
        await ReserveAsync(fixture);
        var changedCommand = fixture.Command with { Arguments = ["different-test"] };
        changedCommand = changedCommand with { ImmutableHash = changedCommand.ComputeImmutableHash() };
        var changed = fixture with { Command = changedCommand };
        var conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().ReserveAsync(changed.Request, changed.Command, Initial(changed), CancellationToken.None));
        Assert.Equal("buildtest_request_conflict", conflict.Code);

        var otherOwner = await CreateDurableFixtureAsync(fixture.Command.OperationId);
        conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().ReserveAsync(otherOwner.Request, otherOwner.Command, Initial(otherOwner), CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", conflict.Code);
        var persisted = await Store().GetAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(fixture.Command.ImmutableHash, persisted.AcceptedCommand.ImmutableHash);
        Assert.Empty(persisted.AttemptedEffects);
    }

    [Theory]
    [InlineData("lifecycle", "environment_fence_stale")]
    [InlineData("sandbox-generation", "sandbox_lease_unknown")]
    [InlineData("sandbox-operation", "sandbox_binding_stale")]
    [InlineData("sandbox-fence", "sandbox_binding_stale")]
    [InlineData("sandbox-provider", "sandbox_binding_stale")]
    [InlineData("workspace-generation", "workspace_generation_mismatch")]
    [InlineData("workspace-data", "workspace_generation_mismatch")]
    public async Task StaleCurrentBindingCannotReserveAnOwnerEffect(string field, string code)
    {
        var fixture = await CreateDurableFixtureAsync();
        var binding = fixture.Request.ExpectedBinding;
        var stale = field switch
        {
            "lifecycle" => binding with
            {
                Fence = new(binding.Fence.Owner, binding.Fence.LifecycleGeneration + 1)
            },
            "sandbox-generation" => binding with
            {
                SandboxResource = binding.SandboxResource with { Generation = binding.SandboxResource.Generation + 1 }
            },
            "sandbox-operation" => binding with { SandboxLeaseOperationId = Guid.NewGuid() },
            "sandbox-fence" => binding with { ProviderFencingGeneration = binding.ProviderFencingGeneration + 1 },
            "sandbox-provider" => binding with
            {
                SandboxProviderBinding = binding.SandboxProviderBinding! with { OptionsRevision = "changed-options" }
            },
            "workspace-generation" => binding with
            {
                WorkspaceVolume = binding.WorkspaceVolume with
                {
                    ResourceGeneration = binding.WorkspaceVolume.ResourceGeneration + 1
                }
            },
            "workspace-data" => binding with { DataGeneration = binding.DataGeneration + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        fixture = fixture with { Request = fixture.Request with { ExpectedBinding = stale } };

        var exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().ReserveAsync(fixture.Request, fixture.Command, Initial(fixture), CancellationToken.None));
        Assert.Equal(code, exception.Code);
        Assert.Null(await Store().GetAsync(
            binding.Fence.Owner, fixture.Command.OperationId, CancellationToken.None));
    }

    [Fact]
    public async Task RetirementDeniesNewReservationButKeepsExistingReplay()
    {
        var fixture = await CreateDurableFixtureAsync();
        var reserved = await ReserveAsync(fixture);
        var binding = fixture.Request.ExpectedBinding;
        var leaseStore = new EnvironmentSandboxLeaseStore(database.DataSource, TimeProvider.System);
        await leaseStore.BeginRetirementAsync(
            binding.Fence,
            binding.SandboxResource.Generation,
            binding.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon",
            new SandboxRetirementAuthorization("https://projects.example", "actor", 1),
            terminalEvidence: null,
            CancellationToken.None);

        var replay = await Store().ReserveAsync(
            fixture.Request, fixture.Command, Initial(fixture), CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(reserved.Operation.CreatedAt, replay.State.Operation.CreatedAt);
        var next = CreateFixture(binding, Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().ReserveAsync(next.Request, next.Command, Initial(next), CancellationToken.None));
        Assert.Equal("sandbox_binding_stale", exception.Code);
        Assert.Null(await Store().GetAsync(binding.Fence.Owner, next.Command.OperationId, CancellationToken.None));
    }

    [Fact]
    public async Task PolicyUidRequiresDurableAttemptAndRestartCannotForgetOrReplaceIt()
    {
        var fixture = await CreateDurableFixtureAsync();
        var reserved = await ReserveAsync(fixture);
        var owner = fixture.Request.ExpectedBinding.Fence.Owner;
        var bound = reserved with
        {
            Operation = reserved.Operation with
            {
                ExpectedCommandPolicy = reserved.Operation.ExpectedCommandPolicy with { PolicyUid = "verified-command-policy" }
            }
        };
        var exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().SaveAsync(owner, bound, CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        var saved = await Store().SaveAsync(
            owner, bound with { AttemptedEffects = ["command-policy:create"] }, CancellationToken.None);
        await using var restartedDataSource = database.CreateDataSource("buildtest-policy-restarted");
        var restarted = new EnvironmentSandboxBuildTestCommandStore(restartedDataSource, TimeProvider.System);
        var persisted = await restarted.GetAsync(owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("verified-command-policy", persisted.Operation.ExpectedCommandPolicy.PolicyUid);
        Assert.Equal<string>(["command-policy:create"], persisted.AttemptedEffects);

        exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            restarted.SaveAsync(owner, saved with { AttemptedEffects = [] }, CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            restarted.SaveAsync(owner, saved with
            {
                Operation = saved.Operation with
                {
                    ExpectedCommandPolicy = saved.Operation.ExpectedCommandPolicy with { PolicyUid = "different-policy" }
                }
            }, CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            restarted.SaveAsync(owner, reserved, CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
    }

    [Fact]
    public async Task TerminalFailureSurvivesRestartAndCannotReturnToRunning()
    {
        var fixture = await CreateDurableFixtureAsync();
        var reserved = await ReserveAsync(fixture);
        var running = CreateSnapshot(fixture) with
        {
            CreatedAt = reserved.Operation.CreatedAt,
            UpdatedAt = reserved.Operation.UpdatedAt
        };
        var failed = reserved with
        {
            Operation = running with
            {
                Status = SandboxBuildTestOperationStatus.Failed,
                Terminal = new SandboxBuildTestTerminalEvidence(
                    running.Pod!.Uid,
                    "buildtest-command",
                    SandboxBuildTestTerminationKind.Exited,
                    1,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    null)
            },
            AttemptedEffects = ["command-policy:create", "collector-policy:create", "command-pod:create"]
        };
        var saved = await Store().SaveAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, failed, CancellationToken.None);
        var replay = await Store().ReserveAsync(
            fixture.Request, fixture.Command, Initial(fixture), CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(SandboxBuildTestOperationStatus.Failed, replay.State.Operation.Status);
        Assert.Equal(saved.Operation.Terminal, replay.State.Operation.Terminal);
        Assert.Equal<string>(saved.AttemptedEffects, replay.State.AttemptedEffects);
        var exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().SaveAsync(
                fixture.Request.ExpectedBinding.Fence.Owner,
                saved with { Operation = saved.Operation with { Status = SandboxBuildTestOperationStatus.Running, Terminal = null } },
                CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().SaveAsync(
                fixture.Request.ExpectedBinding.Fence.Owner,
                saved with
                {
                    Operation = saved.Operation with { Terminal = saved.Operation.Terminal! with { ExitCode = 2 } }
                },
                CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        var unchanged = await Store().GetAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal(saved.Operation.Terminal, unchanged.Operation.Terminal);
        var exactReplay = await Store().SaveAsync(
            fixture.Request.ExpectedBinding.Fence.Owner, saved, CancellationToken.None);
        Assert.Equal(saved.Operation.Terminal, exactReplay.Operation.Terminal);
        Assert.Equal<string>(saved.AttemptedEffects, exactReplay.AttemptedEffects);
    }

    [Theory]
    [InlineData(SandboxBuildTestOperationStatus.Completed)]
    [InlineData(SandboxBuildTestOperationStatus.Failed)]
    [InlineData(SandboxBuildTestOperationStatus.Interrupted)]
    [InlineData(SandboxBuildTestOperationStatus.Stale)]
    public async Task EveryTerminalStatusPreservesSnapshotWhileAcceptingIdenticalAttemptUpdates(
        SandboxBuildTestOperationStatus status)
    {
        var fixture = await CreateDurableFixtureAsync();
        var accepted = fixture.Command with { Outputs = [] };
        accepted = accepted with { ImmutableHash = accepted.ComputeImmutableHash() };
        fixture = fixture with { Command = accepted };
        var reserved = await ReserveAsync(fixture);
        var snapshot = CreateSnapshot(fixture) with
        {
            Status = status,
            CreatedAt = reserved.Operation.CreatedAt,
            UpdatedAt = reserved.Operation.UpdatedAt
        };
        if (status != SandboxBuildTestOperationStatus.Stale)
        {
            snapshot = snapshot with
            {
                Terminal = new SandboxBuildTestTerminalEvidence(
                    snapshot.Pod!.Uid,
                    "buildtest-command",
                    status == SandboxBuildTestOperationStatus.Interrupted
                        ? SandboxBuildTestTerminationKind.TimedOut
                        : SandboxBuildTestTerminationKind.Exited,
                    status == SandboxBuildTestOperationStatus.Completed ? 0 : 1,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    null)
            };
        }
        var owner = fixture.Request.ExpectedBinding.Fence.Owner;
        var saved = await Store().SaveAsync(
            owner,
            reserved with { Operation = snapshot, AttemptedEffects = ["command-policy:create", "command-pod:create"] },
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            Store().SaveAsync(owner, saved with
            {
                Operation = saved.Operation with { FailureCode = "replaced-terminal-result" }
            }, CancellationToken.None));
        Assert.Equal("buildtest_operation_conflict", exception.Code);
        var replay = await Store().SaveAsync(
            owner, saved with { AttemptedEffects = saved.AttemptedEffects.Add("command-policy:observed") },
            CancellationToken.None);
        Assert.Equal(status, replay.Operation.Status);
        Assert.Equal(saved.Operation.Terminal, replay.Operation.Terminal);
        Assert.Null(replay.Operation.FailureCode);
        Assert.Contains("command-policy:observed", replay.AttemptedEffects);
    }

    private EnvironmentSandboxBuildTestCommandStore Store(TimeProvider? time = null) =>
        new(database.DataSource, time ?? TimeProvider.System);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> ReserveAsync(Fixture fixture) =>
        (await Store().ReserveAsync(
            fixture.Request, fixture.Command, Initial(fixture), CancellationToken.None)).State;

    private static SandboxBuildTestOperationSnapshot Initial(Fixture fixture)
    {
        var snapshot = CreateSnapshot(fixture);
        return snapshot with
        {
            Status = SandboxBuildTestOperationStatus.Reserved,
            ExpectedCommandPolicy = snapshot.ExpectedCommandPolicy with { PolicyUid = "pending" },
            CollectorPolicy = snapshot.CollectorPolicy is { } collector ? collector with { PolicyUid = "pending" } : null,
            CurrentBinding = null,
            Pod = null,
            Output = null
        };
    }

    private async Task<Fixture> CreateDurableFixtureAsync(Guid? operationId = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new EnvironmentOwnerIdentity("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
        var lifecycle = database.CreateStore();
        var fence = (await lifecycle.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "register"), CancellationToken.None)).Snapshot.Fence;
        const string volumeId = "workspace";
        var volumeSpec = JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            volumeId, owner.ProjectId, new(WorkspaceVolumeOwnerKind.Run, owner.RunId), owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany, 8, "azure-files",
            WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete, WorkspaceVolumeOwnerDeletionPolicy.Retain, []));
        await lifecycle.CreateWorkspaceVolumeAsync(fence, volumeId, volumeSpec, "create", CancellationToken.None);
        var provision = await lifecycle.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "workspace-provision", CancellationToken.None);
        var volume = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "pvc-" + suffix, provision.TargetResourceGeneration);
        var volumeBinding = new WorkspaceVolumeProviderBindingSnapshot(
            "azure-files", "1.0.0", 1, "options", JsonSerializer.SerializeToElement(new { endpoint = "test" }),
            JsonSerializer.SerializeToElement(new { resourceId = volume.ResourceId }));
        var provisionedVolume = await lifecycle.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId, fence, true, volume, volumeBinding, true, CancellationToken.None);
        var bind = await lifecycle.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, provisionedVolume.TargetTransitionRevision, volume.Generation, 0, "bind", CancellationToken.None);
        var bound = await lifecycle.CompleteWorkspaceVolumeBindAsync(
            bind.OperationId, fence, true, volume, true, CancellationToken.None);
        var attach = await lifecycle.ReserveWorkspaceVolumeAttachAsync(
            fence, volumeId, bound.TargetTransitionRevision, volume.Generation, 0, "attach", CancellationToken.None);
        await lifecycle.CompleteWorkspaceVolumeAttachAsync(
            attach.OperationId, fence, true, volume, true, CancellationToken.None);

        var template = CreateFixture().Request.ExpectedBinding;
        var providerBinding = template.SandboxProviderBinding!;
        var leaseStore = new EnvironmentSandboxLeaseStore(database.DataSource, TimeProvider.System);
        var lease = await leaseStore.ReserveProvisionAsync(
            fence, "sandbox-provision",
            new SandboxLeaseProvisionIntent(
                providerBinding.ProviderId, providerBinding.AdapterVersion, providerBinding.OptionsSchemaVersion,
                providerBinding.OptionsRevision, providerBinding.OptionsSnapshot,
                JsonSerializer.SerializeToElement(new { projectRevision = 1, runRevision = 1 }),
                JsonSerializer.SerializeToElement(new { workspaceVolumeId = volumeId })),
            CancellationToken.None);
        var sandbox = new SandboxProvisionedResource(
            template.SandboxResource with { ResourceId = "claim-" + suffix, Generation = lease.Lease.ResourceGeneration },
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("test-cluster"),
            ImmutableHashSet.Create(StringComparer.Ordinal, SandboxCapabilities.WorkspacePersistentVolumeClaim),
            [new(SandboxStartupPhase.Started, 1, DateTimeOffset.UtcNow)],
            providerBinding);
        var active = await leaseStore.CompleteProvisionAsync(
            lease.Lease.OperationId, fence, sandbox, true, CancellationToken.None);
        var expected = template with
        {
            Fence = fence,
            SandboxLeaseOperationId = active.OperationId,
            SandboxResource = sandbox.Resource,
            ProviderFencingGeneration = active.ProviderFencingGeneration,
            WorkspaceVolume = new(owner.ProjectId, volumeId, volume.Generation),
            DataGeneration = 0
        };
        return CreateFixture(expected, operationId ?? Guid.NewGuid());
    }
}
