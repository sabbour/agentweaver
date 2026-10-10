using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Npgsql;
using static Agentweaver.Environment.Tests.SandboxBuildTestCommandContractTests;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxBuildTestCommandPostgresTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task PublicManagerExecutionAndRestartReconciliationPersistProgressWithoutRepeatingEffects()
    {
        var fixture = await CreateManagerFixtureAsync();
        var result = await fixture.ExecuteAsync();

        Assert.False(result.Replayed);
        Assert.Equal(SandboxBuildTestOperationStatus.Running, result.Operation.Status);
        Assert.NotNull(result.Operation.Pod);
        Assert.NotEqual("pending", result.Operation.ExpectedCommandPolicy.PolicyUid);
        Assert.Equal(1, fixture.Provider.Creates);
        Assert.Equal(1, fixture.Provider.GateReleases);
        Assert.True(fixture.Projects.AuthorizationReads > 1);
        Assert.True(fixture.Projects.SelectionReads > 1);
        var reserved = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(reserved);
        Assert.Contains("command-policy:create", reserved.AttemptedEffects);
        Assert.Contains("command-pod:create", reserved.AttemptedEffects);
        Assert.Contains("command-pod:gate-released", reserved.AttemptedEffects);

        var replay = await fixture.ExecuteAsync();
        Assert.True(replay.Replayed);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(result.Operation),
            JsonSerializer.SerializeToElement(replay.Operation)));
        Assert.Equal(1, fixture.Provider.Creates);
        Assert.Equal(1, fixture.Provider.GateReleases);

        fixture.Provider.Complete = true;
        await using var restartedDataSource = database.CreateDataSource("buildtest-manager-restarted");
        var restarted = fixture.CreateManager(new EnvironmentSandboxBuildTestCommandStore(
            restartedDataSource, TimeProvider.System));
        var completed = await restarted.ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);
        Assert.True(completed.Replayed);
        Assert.Equal(SandboxBuildTestOperationStatus.Completed, completed.Operation.Status);
        Assert.Equal(0, completed.Operation.Terminal!.ExitCode);
        Assert.Equal(result.Operation.Pod!.Uid, completed.Operation.Terminal.SourcePodUid);
        Assert.Equal(1, fixture.Provider.Creates);
        Assert.Equal(1, fixture.Provider.GateReleases);
        var observations = fixture.Provider.Observations;
        var terminalReplay = await restarted.ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(completed.Operation),
            JsonSerializer.SerializeToElement(terminalReplay.Operation)));
        Assert.Equal(observations, fixture.Provider.Observations);
        var persisted = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(SandboxBuildTestOperationStatus.Completed, persisted.Operation.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicManagerRejectsDeniedAuthorityAndStaleBindingBeforeAnyCommandEffect(bool staleBinding)
    {
        var fixture = await CreateManagerFixtureAsync();
        if (staleBinding)
        {
            var request = fixture.Request with
            {
                ExpectedBinding = fixture.Request.ExpectedBinding with
                {
                    SandboxResource = fixture.Request.ExpectedBinding.SandboxResource with
                    {
                        Generation = fixture.Request.ExpectedBinding.SandboxResource.Generation + 1
                    }
                }
            };
            var failure = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
                fixture.ExecuteAsync(request));
            Assert.Equal("sandbox_binding_stale", failure.Code);
        }
        else
        {
            fixture.Projects.CanWrite = false;
            var failure = await Assert.ThrowsAsync<ProjectsConfigApiException>(() => fixture.ExecuteAsync());
            Assert.Equal("project_write_not_authorized", failure.Code);
            Assert.Equal(0, fixture.Projects.SelectionReads);
        }

        Assert.Equal(0, fixture.Provider.Creates);
        Assert.Equal(0, fixture.Provider.GateReleases);
        Assert.Equal(1, fixture.Policies.Creates);
        Assert.Null(await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None));
    }

    [Fact]
    public async Task PublicManagerRechecksAuthorityAfterPolicyCreationBeforeCreatingAPod()
    {
        var fixture = await CreateManagerFixtureAsync();
        fixture.Policies.AfterCreate = () => fixture.Projects.CanWrite = false;

        var failure = await Assert.ThrowsAsync<ProjectsConfigApiException>(() => fixture.ExecuteAsync());

        Assert.Equal("project_write_not_authorized", failure.Code);
        Assert.Equal(0, fixture.Provider.Creates);
        Assert.Equal(0, fixture.Provider.GateReleases);
        var persisted = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Contains("command-policy:create", persisted.AttemptedEffects);
        Assert.DoesNotContain("command-pod:create", persisted.AttemptedEffects);
        Assert.NotEqual("pending", persisted.Operation.ExpectedCommandPolicy.PolicyUid);
    }

    [Fact]
    public async Task PublicManagerPersistsCheckpointStalenessBeforeTheNextProviderEffect()
    {
        var fixture = await CreateManagerFixtureAsync();
        fixture.Policies.AfterCreate = () =>
        {
            var changed = fixture.Command with { Arguments = ["changed"] };
            fixture.Verifier.Command = changed with { ImmutableHash = changed.ComputeImmutableHash() };
        };

        var result = await fixture.ExecuteAsync();

        Assert.Equal(SandboxBuildTestOperationStatus.Stale, result.Operation.Status);
        Assert.Equal("buildtest_checkpoint_stale", result.Operation.FailureCode);
        Assert.Equal(0, fixture.Provider.Creates);
        var persisted = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(SandboxBuildTestOperationStatus.Stale, persisted.Operation.Status);
        Assert.DoesNotContain("command-pod:create", persisted.AttemptedEffects);
    }

    [Fact]
    public async Task PublicManagerKeepsAnUncertainPolicyAttemptForReconciliationWithoutCreatingAgain()
    {
        var fixture = await CreateManagerFixtureAsync();
        fixture.Policies.FailNextCreate = true;
        var failed = await fixture.ExecuteAsync();

        Assert.Equal(SandboxBuildTestOperationStatus.ReconciliationRequired, failed.Operation.Status);
        Assert.Equal("buildtest_policy_effect_unknown", failed.Operation.FailureCode);
        Assert.Equal(0, fixture.Provider.Creates);
        var persisted = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Contains("command-policy:create", persisted.AttemptedEffects);
        var creates = fixture.Policies.Creates;

        var unresolved = await fixture.CreateManager(Store()).ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);

        Assert.True(unresolved.Replayed);
        Assert.Equal(SandboxBuildTestOperationStatus.ReconciliationRequired, unresolved.Operation.Status);
        Assert.Equal(creates, fixture.Policies.Creates);
        Assert.Equal(0, fixture.Provider.Creates);
        fixture.Policies.MakeLastAttemptVisible();
        var recovered = await fixture.CreateManager(Store()).ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);
        Assert.Equal(SandboxBuildTestOperationStatus.Running, recovered.Operation.Status);
        Assert.Null(recovered.Operation.FailureCode);
        Assert.Equal(creates, fixture.Policies.Creates);
        Assert.Equal(1, fixture.Provider.Creates);
        Assert.Equal(1, fixture.Provider.GateReleases);
    }

    [Fact]
    public async Task PublicManagerReconciliationRetainsUncertainCommandCreationWithoutCreatingAgain()
    {
        var fixture = await CreateManagerFixtureAsync();
        var running = await fixture.ExecuteAsync();
        Assert.Equal(1, fixture.Provider.Creates);
        var uncertain = fixture with { Provider = new ManagerCommandProvider() };
        var manager = uncertain.CreateManager(Store());

        var result = await manager.ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);

        Assert.True(result.Replayed);
        Assert.Equal(SandboxBuildTestOperationStatus.ReconciliationRequired, result.Operation.Status);
        Assert.Equal("buildtest_command_pod_effect_unknown", result.Operation.FailureCode);
        Assert.Equal(running.Operation.Pod!.Uid, result.Operation.Pod!.Uid);
        Assert.Equal(0, uncertain.Provider.Creates);
        Assert.Equal(0, uncertain.Provider.GateReleases);
        var persisted = await Store().GetAsync(fixture.Owner, fixture.Command.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Contains("command-pod:create", persisted.AttemptedEffects);
        Assert.Equal(running.Operation.Pod.Uid, persisted.Operation.Pod!.Uid);
        var unresolved = await manager.ReconcileAsync(
            fixture.Caller, fixture.Owner.ProjectId, fixture.Owner.RunId, fixture.Owner.EnvironmentId,
            fixture.Command.OperationId, fixture.Request, CancellationToken.None);
        Assert.Equal("buildtest_command_pod_effect_unknown", unresolved.Operation.FailureCode);
        Assert.Equal(0, uncertain.Provider.Creates);
        Assert.Equal(0, uncertain.Provider.GateReleases);
    }

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

    // Projects responses and Kubernetes effects are controlled; owner bindings use real PostgreSQL.
    private async Task<ManagerFixture> CreateManagerFixtureAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new EnvironmentOwnerIdentity(
            "tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
        var lifecycle = database.CreateStore();
        var fence = (await lifecycle.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "manager-register"), CancellationToken.None)).Snapshot.Fence;
        const string volumeId = "workspace";
        var specification = JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            volumeId, owner.ProjectId, new(WorkspaceVolumeOwnerKind.Run, owner.RunId), owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany, 8, "azure-files",
            WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []));
        await lifecycle.CreateWorkspaceVolumeAsync(fence, volumeId, specification, "create", CancellationToken.None);
        var provision = await lifecycle.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "workspace-provision", CancellationToken.None);
        var storage = new ProviderResourceRef(
            ProviderSeam.Storage, "azure-files", "pvc-" + suffix, provision.TargetResourceGeneration);
        var storageBinding = new WorkspaceVolumeProviderBindingSnapshot(
            storage.ProviderId, "1.0.0", 1, "storage-options",
            JsonSerializer.SerializeToElement(new { endpoint = "test" }),
            JsonSerializer.SerializeToElement(new { resourceId = storage.ResourceId }));
        var provisioned = await lifecycle.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId, fence, true, storage, storageBinding, true, CancellationToken.None);
        var bind = await lifecycle.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, provisioned.TargetTransitionRevision, storage.Generation, 0, "bind", CancellationToken.None);
        var bound = await lifecycle.CompleteWorkspaceVolumeBindAsync(
            bind.OperationId, fence, true, storage, true, CancellationToken.None);
        var attach = await lifecycle.ReserveWorkspaceVolumeAttachAsync(
            fence, volumeId, bound.TargetTransitionRevision, storage.Generation, 0, "attach", CancellationToken.None);
        var attached = await lifecycle.CompleteWorkspaceVolumeAttachAsync(
            attach.OperationId, fence, true, storage, true, CancellationToken.None);

        var capabilities = ImmutableArray.Create(
            CiliumEgressCapabilities.L3L4, CiliumEgressCapabilities.Fqdn,
            CiliumEgressCapabilities.Cidr, CiliumEgressCapabilities.Dns);
        var networkOptions = new CiliumEgressProviderOptions(
            "sandbox", new Version(1, 0, 0), 1, "network-options",
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add(
                "kube-system/kube-dns",
                ImmutableDictionary<string, string>.Empty
                    .Add("k8s:io.kubernetes.pod.namespace", "kube-system")
                    .Add("k8s:k8s-app", "kube-dns")));
        var rules = ImmutableArray.Create(
            new NetworkEgressRule(NetworkEgressPurpose.SourceControl, NetworkEgressDestinationKind.Cidr,
                "203.0.113.0/24", 443, EgressProtocol.Tcp),
            new NetworkEgressRule(NetworkEgressPurpose.SourceControl, NetworkEgressDestinationKind.Fqdn,
                "api.github.com", 443, EgressProtocol.Tcp),
            new NetworkEgressRule(NetworkEgressPurpose.DnsResolver, NetworkEgressDestinationKind.KubernetesService,
                "kube-system/kube-dns", 53, EgressProtocol.Tcp),
            new NetworkEgressRule(NetworkEgressPurpose.DnsResolver, NetworkEgressDestinationKind.KubernetesService,
                "kube-system/kube-dns", 53, EgressProtocol.Udp));
        var selection = new EffectiveNetworkPolicySelection(
            owner.ProjectId, owner.RunId, 1, 1, 1, "context-" + suffix,
            [new(ProviderCardinality.Layered, ProviderSeam.NetworkPolicy,
                [new(ProviderSeam.NetworkPolicy, CiliumEgressPolicyAdapter.ProviderId,
                    "1.0.0", 1, networkOptions.OptionsRevision,
                    ProviderHostingPattern.KubernetesController, capabilities, capabilities, NetworkPolicyLayer.L3L4)])],
            rules, null, rules, rules);
        var projects = new ManagerProjectsResponses(owner, selection);
        var policies = new ManagerPolicyStore();
        var adapter = new CiliumEgressPolicyAdapter(policies, networkOptions);
        var egress = new EnvironmentEgressManager(projects, adapter, networkOptions, lifecycle);
        var caller = new CurrentCallerRequest("controlled-projects-protocol-token", owner.TenantId);
        var network = await egress.ApplyAndVerifyAsync(
            caller, new(fence, 1, 0, "manager-network"), CancellationToken.None);
        Assert.True(network.ReadyForDispatch, network.FailureMessage);
        projects.ResetReads();

        var template = CreateFixture();
        var command = template.Command with { OperationId = Guid.NewGuid(), Outputs = [] };
        var options = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion, "sandbox-options", networkOptions.Namespace,
            storage.ProviderId, "runtime-test@sha256:" + new string('a', 64),
            "linux/amd64", 1, "kata-test", "kata-test", "100m", "128Mi", 20, 100,
            new(30, 30, 30, 30, 30, 120))
        {
            AcceptedBuildTestProfile = command.ExecutionOptions
        };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var providerBinding = new SandboxProviderBindingSnapshot(
            "agent-sandbox", "1.0.0", options.OptionsSchemaVersion, options.OptionsRevision,
            JsonSerializer.SerializeToElement(options, json),
            JsonSerializer.SerializeToElement(new { claimName = "claim-" + suffix }));
        var provisionRequest = new SandboxProvisionApiRequest(
            volumeId, storage.Generation, 0, "/workspace/agentweaver/project", false, 1, "manager-sandbox");
        var leases = new EnvironmentSandboxLeaseStore(database.DataSource, TimeProvider.System);
        var reservation = await leases.ReserveProvisionAsync(
            fence, "manager-sandbox",
            new(providerBinding.ProviderId, providerBinding.AdapterVersion, providerBinding.OptionsSchemaVersion,
                providerBinding.OptionsRevision, providerBinding.OptionsSnapshot,
                JsonSerializer.SerializeToElement(selection, json),
                JsonSerializer.SerializeToElement(provisionRequest, json)), CancellationToken.None);
        var sandbox = new SandboxProvisionedResource(
            new(ProviderSeam.Sandbox, providerBinding.ProviderId, "claim-" + suffix, reservation.Lease.ResourceGeneration),
            new(Guid.NewGuid()), new("controlled-cluster"),
            ImmutableHashSet.Create(StringComparer.Ordinal,
                SandboxCapabilities.WorkspacePersistentVolumeClaim, SandboxCapabilities.BuildTestCommandPod),
            [new(SandboxStartupPhase.Started, 1, DateTimeOffset.UtcNow)], providerBinding);
        var workspace = new SandboxWorkspaceAttachment(
            new(owner.ProjectId, owner.EnvironmentId, owner.RunId,
                new(owner.ProjectId, volumeId, storage.Generation), sandbox.Resource, storage, fence, 0,
                provisionRequest.MountPath, false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            JsonSerializer.SerializeToElement(new AgentSandboxPersistentVolumeClaimAttachment(
                1, storage.ProviderId, networkOptions.Namespace, storage.ResourceId, "pvc-uid-" + suffix), json));
        var selector = EnvironmentEgressSelector.Create(
            owner.EnvironmentId, owner.TenantId, owner.ProjectId, owner.RunId, networkOptions.Namespace);
        await leases.SaveProviderRequestAsync(
            reservation.Lease.OperationId, fence,
            JsonSerializer.SerializeToElement(new
            {
                contractVersion = 1,
                request = provisionRequest,
                workspace,
                egressSelectorLabels = selector.MatchLabels,
                workspaceAttachmentTransitionRevision = attached.TargetTransitionRevision
            }, json), CancellationToken.None);
        var active = await leases.CompleteProvisionAsync(
            reservation.Lease.OperationId, fence, sandbox, true, CancellationToken.None);
        var expected = new SandboxBuildTestExpectedBinding(
            fence, active.OperationId, sandbox.Resource, active.ProviderFencingGeneration,
            new(owner.ProjectId, volumeId, storage.Generation), 0, 1)
        {
            SandboxProviderBinding = providerBinding
        };
        command = command with
        {
            Checkpoint = command.Checkpoint with { ProjectId = owner.ProjectId, RunId = owner.RunId }
        };
        command = command with { ImmutableHash = command.ComputeImmutableHash() };
        return new(owner, caller, command, new(command.Checkpoint, expected),
            projects, policies, new ManagerCommandProvider(), new ManagerCommandVerifier(command),
            lifecycle, leases, egress, networkOptions, Store());
    }

    private sealed record ManagerFixture(
        EnvironmentOwnerIdentity Owner,
        CurrentCallerRequest Caller,
        SandboxBuildTestAcceptedCommand Command,
        SandboxBuildTestApiRequest Request,
        ManagerProjectsResponses Projects,
        ManagerPolicyStore Policies,
        ManagerCommandProvider Provider,
        ManagerCommandVerifier Verifier,
        IEnvironmentLifecycleStore Lifecycle,
        ISandboxLeaseStore Leases,
        EnvironmentEgressManager Egress,
        CiliumEgressProviderOptions NetworkOptions,
        IEnvironmentSandboxBuildTestCommandStore Commands)
    {
        public EnvironmentSandboxBuildTestCommandManager CreateManager(
            IEnvironmentSandboxBuildTestCommandStore store) =>
            new(Egress, Lifecycle, Leases, store, Verifier, Provider,
                Policies, TimeProvider.System, NetworkOptions);

        public Task<SandboxBuildTestOperationResult> ExecuteAsync(SandboxBuildTestApiRequest? request = null) =>
            CreateManager(Commands).ExecuteAsync(
                Caller, Owner.ProjectId, Owner.RunId, Owner.EnvironmentId, request ?? Request, CancellationToken.None);
    }

    private sealed class ManagerProjectsResponses(
        EnvironmentOwnerIdentity owner, EffectiveNetworkPolicySelection selection) : IProjectsConfigClient
    {
        public bool CanWrite { get; set; } = true;
        public int AuthorizationReads { get; private set; }
        public int SelectionReads { get; private set; }

        public void ResetReads()
        {
            AuthorizationReads = 0;
            SelectionReads = 0;
        }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller, CancellationToken cancellationToken)
        {
            Assert.Equal(owner.TenantId, caller.TenantSelector);
            AuthorizationReads++;
            var grants = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
            grants.Add(new(ProjectAuthorizationPermission.ReadRunSelection, 1));
            if (CanWrite)
                grants.Add(new(ProjectAuthorizationPermission.WriteProjects, 1));
            return Task.FromResult(new ProjectAuthorizationContextResponse(
                1, "https://controlled-identity.test", "controlled-actor", owner.TenantId, 1,
                owner.ProjectId, owner.RunId,
                [new(ProjectAuthorityResourceType.Project, owner.ProjectId, grants.ToImmutable())]));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller, string projectId, string runId, CancellationToken cancellationToken)
        {
            Assert.Equal(owner.ProjectId, projectId);
            Assert.Equal(owner.RunId, runId);
            SelectionReads++;
            return Task.FromResult(selection);
        }
    }

    private sealed class ManagerCommandVerifier(SandboxBuildTestAcceptedCommand command)
        : ISandboxBuildTestAcceptedCommandVerifier
    {
        public SandboxBuildTestAcceptedCommand Command { get; set; } = command;

        public Task<SandboxBuildTestAcceptedCommand> ResolveAsync(
            SandboxBuildTestCheckpointReference checkpoint, CancellationToken cancellationToken)
        {
            Assert.Equal(Command.Checkpoint, checkpoint);
            return Task.FromResult(Command);
        }
    }

    private sealed class ManagerPolicyStore : ICiliumPolicyResourceStore
    {
        private readonly Dictionary<(string Namespace, string Name), CiliumNetworkPolicyDocument> _policies = [];
        private CiliumNetworkPolicyDocument? _lastAttempt;
        public bool FailNextCreate { get; set; }
        public Action? AfterCreate { get; set; }
        public int Creates { get; private set; }

        public void MakeLastAttemptVisible()
        {
            var policy = _lastAttempt ?? throw new InvalidOperationException("There is no uncertain policy attempt.");
            _policies.Add((policy.Metadata.Namespace, policy.Metadata.Name), policy);
        }

        public Task<CiliumNetworkPolicyDocument?> GetAsync(
            string @namespace, string name, CancellationToken cancellationToken)
        {
            _policies.TryGetValue((@namespace, name), out var policy);
            return Task.FromResult(policy);
        }

        public Task<CiliumNetworkPolicyDocument> CreateAsync(
            CiliumNetworkPolicyDocument policy, CancellationToken cancellationToken)
        {
            Creates++;
            var persisted = policy with
            {
                Metadata = policy.Metadata with
                {
                    Uid = Guid.NewGuid().ToString("N"), ResourceVersion = "1", Generation = 1
                }
            };
            _lastAttempt = persisted;
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new HttpRequestException("Controlled uncertain policy create.");
            }
            _policies.Add((policy.Metadata.Namespace, policy.Metadata.Name), persisted);
            AfterCreate?.Invoke();
            return Task.FromResult(persisted);
        }

        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string expectedResourceVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The manager must not replace a pinned policy.");

        public Task DeleteAsync(
            string @namespace, string name, string expectedResourceVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("These execution paths must not delete a policy.");
    }

    private sealed class ManagerCommandProvider : ISandboxBuildTestCommandProvider
    {
        private SandboxBuildTestPodReference? _pod;
        public bool Complete { get; set; }
        public int Creates { get; private set; }
        public int GateReleases { get; private set; }
        public int Observations { get; private set; }

        public Task<SandboxBuildTestPodReference> CreateGatedAsync(
            SandboxBuildTestProviderRequest request, CancellationToken cancellationToken)
        {
            Creates++;
            Assert.Null(_pod);
            var pod = new SandboxBuildTestPodReference(
                "sandbox", "command-" + request.AcceptedCommand.OperationId.ToString("N"),
                Guid.NewGuid().ToString("N"), "1");
            _pod = pod;
            return Task.FromResult(pod);
        }

        public Task<SandboxBuildTestPodReference?> GetAsync(
            SandboxBuildTestProviderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_pod);

        public Task<SandboxBuildTestPodReference> ReleaseSchedulingGateAsync(
            SandboxBuildTestProviderRequest request, SandboxBuildTestPodReference pod, CancellationToken cancellationToken)
        {
            Assert.Equal(_pod!.Uid, pod.Uid);
            GateReleases++;
            return Task.FromResult(_pod);
        }

        public Task<SandboxBuildTestPodObservation> ObserveAsync(
            SandboxBuildTestProviderRequest request, SandboxBuildTestPodReference pod,
            int maximumOutputBytes, CancellationToken cancellationToken)
        {
            Assert.Equal(_pod!.Uid, pod.Uid);
            Observations++;
            var output = new SandboxBuildTestOutputCapture(
                pod.Uid, "buildtest-command", [], 0,
                Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), false, 0);
            var terminal = Complete
                ? new SandboxBuildTestTerminalEvidence(
                    pod.Uid, "buildtest-command", SandboxBuildTestTerminationKind.Exited, 0,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), null)
                : null;
            return Task.FromResult(new SandboxBuildTestPodObservation(
                pod, Complete ? SandboxBuildTestPodState.Succeeded : SandboxBuildTestPodState.Running,
                terminal, output));
        }

        public Task<SandboxBuildTestPodReference> CreateOutputCollectorGatedAsync(
            SandboxBuildTestProviderRequest request, SandboxBuildTestOutputCollectorRequest collectorRequest,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This command has no collected file outputs.");

        public Task<SandboxBuildTestPodReference?> GetOutputCollectorAsync(
            SandboxBuildTestProviderRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This command has no collected file outputs.");

        public Task StopAsync(
            SandboxBuildTestProviderRequest request, SandboxBuildTestPodReference pod, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The successful command must not be stopped.");

        public Task DeleteAsync(
            SandboxBuildTestProviderRequest request, SandboxBuildTestPodReference pod, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("These paths must retain their Pod evidence.");
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
