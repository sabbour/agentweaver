using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed class RuntimeResumeBootstrapPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task LostResponseRestartAndConcurrentDuplicatesRetainOneReceiptAndRequireFreshAuthority()
    {
        var request = RuntimeResumeBootstrapTests.Create();
        var owner = request.Resume.SourceManifest.EnvironmentFence!.Owner;
        var lifecycle = fixture.CreateStore();
        await lifecycle.TransitionAsync(new(owner, 0, EnvironmentLifecycleState.Active, "register"),
            CancellationToken.None);
        var resumed = await lifecycle.TransitionAsync(new(owner, 1, EnvironmentLifecycleState.Active, "resume"),
            CancellationToken.None);
        var leases = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var intent = new SandboxLeaseProvisionIntent("agent-sandbox", "1.0.0", 1, "options-1",
            JsonSerializer.SerializeToElement(new { image = "agenthost:1", @namespace = "sandbox-system" }),
            JsonSerializer.SerializeToElement(new { projectRevision = 2, runRevision = 3 }),
            JsonSerializer.SerializeToElement(new { workspaceVolumeId = "workspace" }));
        var reservation = await leases.ReserveProvisionAsync(
            resumed.Snapshot.Fence, "placement", intent, CancellationToken.None);
        var context = request.Resume.RestoredWorkspace!;
        var resource = context.Placement.Resource with { Generation = reservation.Lease.ResourceGeneration };
        var provisioned = new SandboxProvisionedResource(resource, context.Placement.Endpoint,
            context.Placement.Placement, ImmutableHashSet.Create(SandboxCapabilities.VmIsolation,
                SandboxCapabilities.WorkspacePersistentVolumeClaim),
            [new(SandboxStartupPhase.Started, 1, DateTimeOffset.UtcNow)],
            new("agent-sandbox", "1.0.0", 1, "options-1", intent.OptionsSnapshot,
                JsonSerializer.SerializeToElement(new { @namespace = "sandbox-system", claimName = "claim" })));
        var lease = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            leases, reservation.Lease.OperationId, resumed.Snapshot.Fence, provisioned, true, CancellationToken.None);
        var placement = context.Placement with
        {
            Resource = resource, LeaseRevision = lease.LeaseRevision, LeaseExpiresAt = lease.LeaseExpiresAt!.Value,
            CurrentFencingGeneration = lease.CurrentFencingGeneration,
            ProviderFencingGeneration = lease.ProviderFencingGeneration
        };
        request = request with
        {
            Resume = request.Resume with
            {
                RestoredWorkspace = context with
                {
                    Placement = placement,
                    Workspace = context.Workspace with { SandboxResource = resource }
                }
            },
            Registration = request.Registration with
            {
                ExpiresAt = lease.LeaseExpiresAt.Value,
                Binding = request.Registration.Binding with
                {
                    PlacementGeneration = resource.Generation, EnvironmentLeaseRevision = lease.LeaseRevision,
                    EnvironmentCurrentFencingGeneration = lease.CurrentFencingGeneration,
                    EnvironmentProviderFencingGeneration = lease.ProviderFencingGeneration
                }
            }
        };
        request.Validate();
        var current = request.Registration;
        var receipt = Receipt(request);
        var checks = 0;
        var effects = 0;
        var store = new EnvironmentRuntimeResumeBootstrapStore(fixture.DataSource, TimeProvider.System);
        await Assert.ThrowsAsync<LostResponseException>(() => store.ExecuteAsync(request, async token =>
        {
            await RequireCurrentAsync(token);
            if (++checks == 3)
                throw new LostResponseException();
        }, async (saved, token) =>
        {
            Assert.Null(saved);
            await RequireCurrentAsync(token);
            effects++;
            return receipt;
        }, CancellationToken.None));
        Assert.Equal(1, effects);

        var restarted = new EnvironmentRuntimeResumeBootstrapStore(fixture.DataSource, TimeProvider.System);
        var recovered = await restarted.ExecuteAsync(request, RequireCurrentAsync, ReconcileAsync, CancellationToken.None);
        AssertSame(receipt, recovered);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            new EnvironmentRuntimeResumeBootstrapStore(fixture.DataSource, TimeProvider.System)
                .ExecuteAsync(request, RequireCurrentAsync, ReconcileAsync, CancellationToken.None)));
        Assert.All(concurrent, result => AssertSame(receipt, result));
        Assert.Equal(1, effects);

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => restarted.ExecuteAsync(
            request with { ConsumeOperationId = Guid.NewGuid() }, RequireCurrentAsync,
            (_, _) => throw new InvalidOperationException("Changed effect IDs cannot reach reconciliation."),
            CancellationToken.None));

        var revoked = current with { Revision = 2, State = RuntimeRegistrationState.Revoked };
        var attempt = RuntimeResumeAttemptIdentity.Replacement(request.Resume.OperationId, revoked);
        var replacement = request with
        {
            ResumeAttemptId = attempt,
            BootstrapOperationId = RuntimeResumeAttemptIdentity.Bootstrap(request.Resume.OperationId, attempt),
            ConsumeOperationId = RuntimeResumeAttemptIdentity.Consume(request.Resume.OperationId, attempt),
            ExchangeOperationId = RuntimeResumeAttemptIdentity.Exchange(request.Resume.OperationId, attempt),
            Registration = current with
            {
                RuntimeInstanceId = Guid.NewGuid(),
                Binding = current.Binding with { ResumeAttemptId = attempt }
            }
        };
        current = replacement.Registration;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => restarted.ExecuteAsync(
            request, RequireCurrentAsync,
            (_, _) => throw new InvalidOperationException("An old registration cannot reconcile a new attempt."),
            CancellationToken.None));
        var replacementReceipt = Receipt(replacement);
        var replaced = await restarted.ExecuteAsync(replacement, token => RequireReplacementAsync(token),
            (saved, _) =>
            {
                Assert.Null(saved);
                effects++;
                return Task.FromResult(replacementReceipt);
            }, CancellationToken.None);
        AssertSame(replacementReceipt, replaced);
        Assert.NotEqual(receipt.Registration.RuntimeInstanceId, replaced.Registration.RuntimeInstanceId);
        Assert.Equal(2, effects);

        async Task RequireCurrentAsync(CancellationToken token)
        {
            if (request.Registration != current)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_registration_changed");
            await lifecycle.RequireActiveAsync(resumed.Snapshot.Fence, token);
            AssertSame(lease, await leases.GetCurrentAsync(resumed.Snapshot.Fence, token));
        }

        async Task RequireReplacementAsync(CancellationToken token)
        {
            Assert.Equal(replacement.Registration, current);
            await lifecycle.RequireActiveAsync(resumed.Snapshot.Fence, token);
            AssertSame(lease, await leases.GetCurrentAsync(resumed.Snapshot.Fence, token));
        }

        async Task<RuntimeResumeBootstrapReceipt> ReconcileAsync(
            RuntimeResumeBootstrapReceipt? saved, CancellationToken token)
        {
            Assert.NotNull(saved);
            await RequireCurrentAsync(token);
            AssertSame(receipt, saved);
            return saved;
        }
    }

    internal static RuntimeResumeBootstrapReceipt Receipt(RuntimeResumeBootstrapRequest request)
    {
        var registration = request.Registration;
        var binding = registration.Binding;
        var source = request.Resume.SourceRelease.HostReceipt;
        var now = DateTimeOffset.UtcNow;
        var delivery = new RuntimeBootstrapDeliveryReceipt(request.BootstrapOperationId, Guid.NewGuid(),
            registration.RuntimeInstanceId, registration.Revision, binding.PlacementUid, binding.PlacementGeneration,
            binding.ExecutionFence, source.SourceGrant.ConfigurationHash, now)
        {
            EnvironmentCurrentFencingGeneration = binding.EnvironmentCurrentFencingGeneration,
            EnvironmentProviderFencingGeneration = binding.EnvironmentProviderFencingGeneration
        };
        var grant = source.SourceGrant with
        {
            GrantId = Guid.NewGuid(), RuntimeInstanceId = registration.RuntimeInstanceId,
            RegistrationRevision = registration.Revision, Purpose = RuntimeCredentialPurpose.Observe,
            Audience = binding.ObservationEndpoint, State = RuntimeCredentialState.Active,
            ExpiresAt = registration.ExpiresAt, RecordedAt = now
        };
        var facts = source.NativeTurn.Admission.Source with
        {
            RuntimeInstanceId = registration.RuntimeInstanceId, RegistrationRevision = registration.Revision,
            SdkSessionId = RuntimeContractValidation.NativeSessionId(binding)
        };
        var readiness = new RuntimeHostReadinessReceipt(1, registration.RuntimeInstanceId, registration.Revision,
            binding.ExecutionFence, binding.Image!,
            [.. Enum.GetValues<SandboxStartupPhase>().Select(phase => new SandboxStartupPhaseObservation(
                phase, 1, now,
                phase == SandboxStartupPhase.ImageReady ? binding.Image!.Digest : null,
                phase == SandboxStartupPhase.ImageReady ? binding.Image!.CompressedPullBytes : null))], grant)
        {
            ResumeCache = new(1, request.Resume.OperationId, request.Resume.SourceManifest.ManifestId,
                registration, request.Resume.SourceManifest.CacheAcknowledgment!, facts, "native-cache")
        };
        return new RuntimeResumeBootstrapReceipt(1, request.Resume.OperationId,
            request.Resume.SourceManifest.ManifestId, registration, delivery, readiness).ValidateFor(request);
    }

    private static void AssertSame<T>(T expected, T actual) =>
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected),
            JsonSerializer.SerializeToElement(actual)));

    private sealed class LostResponseException : Exception;
}
