using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;

namespace Agentweaver.Environment.Tests;

public sealed partial class RuntimeResumeBootstrapTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ResumeAuthorityLossAfterReservationPreservesOnlyEffectsThatActuallyStarted(int denyAt)
    {
        var resume = Create().Resume with { PhaseVersion = 2, RestoredWorkspace = null };
        var fence = new EnvironmentGenerationFence(resume.SourceManifest.EnvironmentFence!.Owner, 2);
        var options = new AgentSandboxOptions(AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('a', 64),
            "linux/amd64", 1, "kata-vm", "kata-qemu", "500m", "512Mi", 1, 1,
            new(30, 90, 30, 30, 30, 180));
        var storageOptions = new AzureFilesCsiOptions(1, "storage-options", "agentweaver", "azure-files", 8, 30, 30);
        var selection = EnvironmentEgressManagerTests.Selection();
        selection = selection with
        {
            ProjectId = fence.Owner.ProjectId,
            RunId = fence.Owner.RunId,
            Providers = selection.Providers
                .Add(Exclusive(AgentSandboxProviderMetadata.CreateRegistration(options)))
                .Add(Exclusive(AzureFilesCsiProviderMetadata.CreateRegistration(storageOptions)))
        };
        var authorization = new ProjectAuthorizationContextResponse(1, "https://issuer.test/", "actor",
            fence.Owner.TenantId, 1, fence.Owner.ProjectId, fence.Owner.RunId,
            [new(ProjectAuthorityResourceType.Project, fence.Owner.ProjectId,
                [new(ProjectAuthorizationPermission.ReadRunSelection, 1),
                 new(ProjectAuthorizationPermission.WriteProjects, 1)])]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
        using var handler = new Handler(new { });
        using var http = new HttpClient(handler) { BaseAddress = new("https://kubernetes.test/") };
        var kubernetes = new KubernetesAgentSandboxClient(http, KubernetesTestCaCertificate.Primary);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-1", 1);
        var specification = JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            "workspace", fence.Owner.ProjectId, new(WorkspaceVolumeOwnerKind.Run, fence.Owner.RunId),
            fence.Owner.EnvironmentId, WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany, 8, storage.ProviderId, WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete, WorkspaceVolumeOwnerDeletionPolicy.Retain, []), JsonOptions);
        var volume = new EnvironmentWorkspaceVolumeSnapshot(fence, "workspace", 6, 1, 8,
            EnvironmentWorkspaceVolumeState.Bound, EnvironmentWorkspaceVolumeOperation.Detach, specification, storage,
            new(storage.ProviderId, "1.0.0", 1, storageOptions.OptionsRevision,
                JsonSerializer.SerializeToElement(storageOptions, JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    @namespace = "agentweaver", claimName = "claim-1", claimUid = "claim-uid",
                    // Keep setup free of Kubernetes calls so denial tests can assert no provider traffic.
                    clusterIdentity = "6b333f2199e82077ee02ceffd63b1a2e6ff8becf905f3aa56167a853c36aea75"
                }, JsonOptions)));
        var lifecycle = new ProvisionLifecycle(fence, volume);
        var egress = EnvironmentEgressManagerTests.Manager(projects,
            new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(), lifecycle);
        var caller = new CurrentCallerRequest("actor-token", fence.Owner.TenantId);
        await egress.ApplyAndVerifyAsync(caller, new(fence, 1, 0, "network-1"), CancellationToken.None);
        await egress.ApplyAndVerifyAsync(caller, new(fence, 2, 1, "network-2"), CancellationToken.None);
        var template = RuntimeSuspendPlacementReleaseTests.Create().Lease;
        var leases = new ProvisionLeases(template with
        {
            Fence = fence, ResourceGeneration = 2, CurrentFencingGeneration = 3, ProviderFencingGeneration = 3,
            State = SandboxLeaseState.Provisioning, ProvisionedResource = null, RetirementReason = null
        });
        var provider = new ProvisionSandbox(options);
        var manager = new EnvironmentSandboxManager(projects, lifecycle, leases, provider, options,
            kubernetes, EnvironmentEgressManagerTests.Options(), egress);
        var guardCalls = 0;
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => manager.ProvisionResumeAsync(
            caller, new("workspace", 1, 8, "/workspace/agentweaver/workspace", false, 2, "resume-placement"),
            resume, _ =>
            {
                if (++guardCalls == denyAt)
                    throw new RuntimeAuthorizationException("resume_authority_lost");
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.Equal("resume_authority_lost", failure.Code);
        Assert.Equal(denyAt, guardCalls);
        Assert.Equal(1, leases.Reservations);
        Assert.Equal(1, leases.Completions);
        Assert.Equal(denyAt >= 4 ? 1 : 0, leases.SavedRequests);
        Assert.Equal(denyAt == 6 ? 1 : 0, provider.Calls);
        Assert.Equal(denyAt == 5 ? 1 : 0, lifecycle.DetachCalls);
        Assert.Equal(denyAt == 6, leases.EffectMayHaveApplied);
        Assert.Equal(denyAt == 6 ? SandboxLeaseState.Active : SandboxLeaseState.Released, leases.Current.State);
        Assert.Equal(denyAt == 6 ? EnvironmentWorkspaceVolumeState.Attached : EnvironmentWorkspaceVolumeState.Bound,
            lifecycle.Current.Phase);
        Assert.Equal(storage, lifecycle.Current.Resource);
        Assert.Equal(8, lifecycle.Current.DataGeneration);
        Assert.Equal(denyAt == 6 ? provider.Resource : null, leases.Current.ProvisionedResource);
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task P1SandboxProvisionAndObservationUseOnlyRunBoundReadSelection()
    {
        var fixture = await CreateP1SandboxFixture(bindingMatchesExpectation: true);
        var credential = NewP1Credential();

        try
        {
            var result = await fixture.Manager.ProvisionAsync(
                fixture.Caller,
                fixture.Owner.ProjectId,
                fixture.Owner.RunId,
                fixture.Owner.EnvironmentId,
                fixture.Request,
                CancellationToken.None,
                fixture.Actor(credential));

            Assert.Equal(SandboxLeaseState.Active, result.State);
            Assert.NotNull(result.Resource);
            Assert.Equal(1, fixture.Leases.Reservations);
            Assert.Equal(1, fixture.Provider.Calls);
            Assert.Equal(1, fixture.Lifecycle.AttachCalls);
            Assert.True(fixture.Projects.SelectionReads >= 2);
            Assert.Equal(
                ProjectAuthorizationPermission.ReadRunSelection,
                Assert.Single(fixture.Authorization.EffectiveAuthority.Single().Permissions).Permission);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    public async Task P1SandboxMissingOrStaleBindingRejectsBeforeLeaseOrProviderWrite(string state)
    {
        var fixture = await CreateP1SandboxFixture(bindingMatchesExpectation: true);
        if (state == "missing")
            fixture.BindingReader.CurrentBinding = null;
        else
            fixture.BindingReader.CurrentBinding = fixture.Binding with { CoreInvocationId = Guid.NewGuid() };
        var credential = NewP1Credential();

        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Manager.ProvisionAsync(
                    fixture.Caller,
                    fixture.Owner.ProjectId,
                    fixture.Owner.RunId,
                    fixture.Owner.EnvironmentId,
                    fixture.Request,
                    CancellationToken.None,
                    fixture.Actor(credential)));

            Assert.Equal(
                state == "missing"
                    ? "p1_invocation_binding_missing"
                    : "p1_invocation_binding_mismatch",
                failure.Code);
            Assert.Equal(0, fixture.Leases.Reservations);
            Assert.Equal(0, fixture.Provider.Calls);
            Assert.Equal(0, fixture.Lifecycle.AttachCalls);
            Assert.Equal(EnvironmentWorkspaceVolumeState.Bound, fixture.Lifecycle.Current.Phase);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    private static async Task<P1SandboxFixture> CreateP1SandboxFixture(bool bindingMatchesExpectation)
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 2);
        var now = DateTimeOffset.UtcNow;
        var sandboxOptions = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options",
            "agentweaver",
            "azure-files-csi",
            "runtime-test@sha256:" + new string('a', 64),
            "linux/amd64",
            123456,
            "kata-test",
            "kata-test",
            "100m",
            "128Mi",
            20,
            100,
            new(30, 30, 30, 30, 30, 120));
        var storageOptions = new AzureFilesCsiOptions(
            1, "storage-options", "agentweaver", "azure-files", 8, 30, 30);
        var selection = EnvironmentEgressManagerTests.Selection() with
        {
            ProjectId = owner.ProjectId,
            RunId = owner.RunId,
            Providers = EnvironmentEgressManagerTests.Selection().Providers
                .Add(Exclusive(AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions)))
                .Add(Exclusive(AzureFilesCsiProviderMetadata.CreateRegistration(storageOptions)))
        };
        var authorization = new ProjectAuthorizationContextResponse(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://issuer.test/",
            "actor-a",
            owner.TenantId,
            1,
            owner.ProjectId,
            owner.RunId,
            [
                new(
                    ProjectAuthorityResourceType.Project,
                    owner.ProjectId,
                    [new(ProjectAuthorizationPermission.ReadRunSelection, 1)])
            ]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
        var lifecycle = new ProvisionLifecycle(fence, CreateP1Workspace(fence, storageOptions));
        var networkOptions = EnvironmentEgressManagerTests.Options();
        var ciliumStore = new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore();
        var cilium = new CiliumEgressPolicyAdapter(ciliumStore, networkOptions);
        var selector = EnvironmentEgressSelector.Create(
            owner.EnvironmentId,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            networkOptions.Namespace);
        var compilation = EgressIntentCompiler.Compile(selection);
        Assert.True(compilation.IsSuccess, compilation.Failure?.Message);
        var networkOperation = await lifecycle.ReserveNetworkEffectAsync(
            fence,
            $"{selector.Namespace}/{selector.PolicyName}",
            1,
            0,
            EnvironmentNetworkEffectKind.Apply,
            "p1-test-network",
            CancellationToken.None);
        await cilium.ApplyAsync(selector, compilation.Intent!, 1, 0, CancellationToken.None);
        await lifecycle.CompleteNetworkEffectAsync(
            networkOperation.OperationId, fence, true, true, CancellationToken.None);
        var egress = new EnvironmentEgressManager(projects, cilium, networkOptions, lifecycle);

        var template = RuntimeSuspendPlacementReleaseTests.Create().Lease with
        {
            Fence = fence,
            ResourceGeneration = 2,
            CurrentFencingGeneration = 3,
            ProviderFencingGeneration = 3,
            State = SandboxLeaseState.Provisioning,
            ProvisionedResource = null,
            RetirementReason = null,
            IsCurrent = true,
            LeaseRevision = 1,
            LeaseExpiresAt = now.AddMinutes(5),
            CreatedAt = now.AddSeconds(-1)
        };
        var leases = new ProvisionLeases(template);
        var provider = new ProvisionSandbox(sandboxOptions);
        var handler = new Handler(new { });
        var kubernetes = new KubernetesAgentSandboxClient(
            new HttpClient(handler) { BaseAddress = new("https://kubernetes.test/") },
            KubernetesTestCaCertificate.Primary);
        var bindingReader = new P1SandboxBindingReader();
        var invocationId = Guid.NewGuid();
        var setupInvocationId = Guid.NewGuid();
        var expectation = new P1InvocationBindingExpectation(
            invocationId,
            setupInvocationId,
            new string('a', 64),
            "root-session-p1",
            new string('b', 64),
            3);
        var binding = new P1InvocationBinding(
            1,
            invocationId,
            setupInvocationId,
            expectation.SetupReceiptHash,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            expectation.RootSessionId,
            authorization.Issuer,
            authorization.ActorId,
            Guid.NewGuid(),
            authorization.MembershipRevision,
            expectation.AcceptedSelectionHash,
            expectation.ExecutionFence,
            1,
            now);
        bindingReader.CurrentBinding = bindingMatchesExpectation
            ? binding
            : binding with { CoreInvocationId = Guid.NewGuid() };
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycle,
            leases,
            provider,
            sandboxOptions,
            kubernetes,
            networkOptions,
            egress,
            p1BindingReader: bindingReader);
        var request = new SandboxProvisionApiRequest(
            "workspace", 1, 8, "/workspace/agentweaver/project", false, 1, "p1-sandbox-provision")
        {
            P1Binding = expectation
        };
        return new P1SandboxFixture(
            owner,
            new CurrentCallerRequest("actor-token", owner.TenantId),
            manager,
            projects,
            authorization,
            lifecycle,
            leases,
            provider,
            bindingReader,
            binding,
            request);
    }

    private static EnvironmentWorkspaceVolumeSnapshot CreateP1Workspace(
        EnvironmentGenerationFence fence,
        AzureFilesCsiOptions storageOptions)
    {
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-1", 1);
        var specification = JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            "workspace",
            fence.Owner.ProjectId,
            new(WorkspaceVolumeOwnerKind.Run, fence.Owner.RunId),
            fence.Owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            8,
            storage.ProviderId,
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            []), JsonOptions);
        var binding = new WorkspaceVolumeProviderBindingSnapshot(
            storage.ProviderId,
            "1.0.0",
            1,
            storageOptions.OptionsRevision,
            JsonSerializer.SerializeToElement(storageOptions, JsonOptions),
            JsonSerializer.SerializeToElement(new
            {
                @namespace = "agentweaver",
                claimName = "claim-1",
                claimUid = "claim-uid",
                clusterIdentity = "6b333f2199e82077ee02ceffd63b1a2e6ff8becf905f3aa56167a853c36aea75"
            }, JsonOptions));
        return new(
            fence,
            "workspace",
            6,
            1,
            8,
            EnvironmentWorkspaceVolumeState.Bound,
            EnvironmentWorkspaceVolumeOperation.Detach,
            specification,
            storage,
            binding);
    }

    private static SecretCredential NewP1Credential() =>
        new("actor-token", DateTimeOffset.UtcNow.AddMinutes(5), TimeProvider.System);

    private sealed record P1SandboxFixture(
        EnvironmentOwnerIdentity Owner,
        CurrentCallerRequest Caller,
        EnvironmentSandboxManager Manager,
        EnvironmentEgressManagerTests.FakeProjectsConfigClient Projects,
        ProjectAuthorizationContextResponse Authorization,
        ProvisionLifecycle Lifecycle,
        ProvisionLeases Leases,
        ProvisionSandbox Provider,
        P1SandboxBindingReader BindingReader,
        P1InvocationBinding Binding,
        SandboxProvisionApiRequest Request)
    {
        public RuntimeActorAuthorization Actor(SecretCredential credential) =>
            new(credential, Owner.TenantId);
    }

    private sealed class P1SandboxBindingReader : IEnvironmentP1InvocationBindingReader
    {
        public P1InvocationBinding? CurrentBinding { get; set; }

        public Task<P1InvocationBinding?> ReadCurrentAsync(
            RuntimeActorAuthorization actor,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(CurrentBinding);
    }

    private static EffectiveProviderSelection Exclusive(ProviderRegistration registration)
    {
        var descriptor = registration.Descriptor;
        return new(ProviderCardinality.Exclusive, descriptor.Seam,
            [new(descriptor.Seam, descriptor.Id, descriptor.AdapterVersion.ToString(),
                registration.OptionsSchemaVersion, registration.OptionsRevision, descriptor.Hosting,
                descriptor.AdvertisedCapabilities.ToImmutableArray(), [])]);
    }

    private sealed class ProvisionLeases(SandboxLeaseSnapshot template) : SourceLeases(template)
    {
        public SandboxLeaseSnapshot Current { get; private set; } = template;
        public int Reservations { get; private set; }
        public int SavedRequests { get; private set; }
        public int Completions { get; private set; }
        public bool EffectMayHaveApplied { get; private set; }
        private Guid? _providerAttemptId;

        public override Task<SandboxLeaseSnapshot?> GetCurrentAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Current.Fence, fence);
            return Task.FromResult<SandboxLeaseSnapshot?>(Current);
        }

        public override Task<SandboxLeaseReservation> ReserveProvisionAsync(EnvironmentGenerationFence fence,
            string idempotencyKey, SandboxLeaseProvisionIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.Fence, fence);
            Reservations++;
            Current = Current with { ProvisionIntent = intent };
            return Task.FromResult(new SandboxLeaseReservation(Current, false));
        }

        public override async Task<SandboxLeaseReservation> ReserveP1ProvisionAsync(
            EnvironmentGenerationFence fence,
            string idempotencyKey,
            string workspaceVolumeId,
            SandboxLeaseProvisionIntent intent,
            P1InvocationBinding binding,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task> beforeCommitCheck)
        {
            Assert.Equal(Current.Fence, fence);
            Assert.Equal("workspace", workspaceVolumeId);
            await beforeCommitCheck(cancellationToken);
            Reservations++;
            Current = Current with { ProvisionIntent = intent };
            await beforeCommitCheck(cancellationToken);
            return new(Current, Replayed: false);
        }

        public override Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(Guid operationId,
            EnvironmentGenerationFence fence, JsonElement providerRequest, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.OperationId, operationId);
            Assert.Equal(Current.Fence, fence);
            SavedRequests++;
            if (providerRequest.TryGetProperty("runtimeResume", out var runtimeResume) &&
                runtimeResume.ValueKind == JsonValueKind.Object)
                Assert.Equal(2, runtimeResume.GetProperty("phaseVersion").GetInt64());
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
                "sandbox-provider-request",
                operationId.ToString("N"),
                fence.Owner.TenantId,
                fence.Owner.ProjectId,
                fence.Owner.RunId,
                fence.Owner.EnvironmentId,
                fence.LifecycleGeneration,
                providerRequest.GetRawText())))).ToLowerInvariant();
            Current = Current with
            {
                ProvisionIntent = Current.ProvisionIntent with { ProviderRequest = providerRequest },
                ProviderRequestFingerprint = fingerprint
            };
            return Task.FromResult(Current);
        }

        public override Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.OperationId, request.OperationId);
            Assert.Equal(Current.ProviderRequestFingerprint, request.RequestFingerprint);
            var attempt = new SandboxProviderAttemptSnapshot(Guid.NewGuid(), request.Validate(),
                SandboxProviderAttemptState.InProgress, null, null, DateTimeOffset.UtcNow, null, null).Validate();
            _providerAttemptId = attempt.AttemptId;
            return Task.FromResult(new SandboxProviderAttemptReservation(attempt, false));
        }

        public override Task<SandboxLeaseSnapshot> CompleteProvisionAsync(Guid operationId,
            EnvironmentGenerationFence fence, SandboxProvisionedResource? provisionedResource,
            bool effectMayHaveApplied, CancellationToken cancellationToken, Guid? providerAttemptId = null,
            string? providerErrorCode = null)
        {
            Assert.Equal(Current.OperationId, operationId);
            Assert.Equal(Current.Fence, fence);
            if (providerAttemptId is not null)
                Assert.Equal(_providerAttemptId, providerAttemptId);
            Completions++;
            EffectMayHaveApplied = effectMayHaveApplied;
            Complete(provisionedResource);
            return Task.FromResult(Current);
        }

        public override async Task<SandboxLeaseSnapshot> CompleteP1ProvisionAsync(
            Guid operationId,
            EnvironmentGenerationFence fence,
            SandboxProvisionedResource? provisionedResource,
            bool effectMayHaveApplied,
            P1InvocationBinding binding,
            CancellationToken cancellationToken,
            Guid? providerAttemptId = null,
            string? providerErrorCode = null,
            Func<CancellationToken, Task>? beforeCommitCheck = null,
            bool p1BindingCurrent = true)
        {
            Assert.Equal(Current.OperationId, operationId);
            Assert.Equal(Current.Fence, fence);
            Assert.True(p1BindingCurrent);
            if (providerAttemptId is not null)
                Assert.Equal(_providerAttemptId, providerAttemptId);
            if (beforeCommitCheck is not null)
                await beforeCommitCheck(cancellationToken);
            Completions++;
            EffectMayHaveApplied = effectMayHaveApplied;
            Complete(provisionedResource);
            if (beforeCommitCheck is not null)
                await beforeCommitCheck(cancellationToken);
            return Current;
        }

        private void Complete(SandboxProvisionedResource? provisionedResource) =>
            Current = Current with
            {
                ProvisionedResource = provisionedResource,
                State = provisionedResource is null ? SandboxLeaseState.Released : SandboxLeaseState.Active,
                IsCurrent = provisionedResource is not null,
                CurrentFencingGeneration = provisionedResource is null
                    ? Current.CurrentFencingGeneration
                    : Current.ProviderFencingGeneration,
                LeaseRevision = Current.LeaseRevision + 1,
                LeaseExpiresAt = provisionedResource is null
                    ? null
                    : DateTimeOffset.UtcNow.AddMinutes(5)
            };
    }

    private sealed class ProvisionLifecycle(EnvironmentGenerationFence fence, EnvironmentWorkspaceVolumeSnapshot volume) :
        EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence)
    {
        private readonly Dictionary<Guid, EnvironmentWorkspaceVolumeTransitionReservation> _reservations = [];
        public EnvironmentWorkspaceVolumeSnapshot Current { get; private set; } = volume;
        public int DetachCalls { get; private set; }
        public int AttachCalls { get; private set; }

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, CancellationToken cancellationToken) =>
            Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(Current);

        public override Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long expectedTransitionRevision,
            long expectedResourceGeneration, long expectedDataGeneration, string idempotencyKey,
            CancellationToken cancellationToken) =>
            Reserve(environmentFence, volumeId, expectedTransitionRevision, expectedResourceGeneration,
                expectedDataGeneration, EnvironmentWorkspaceVolumeOperation.Attach);

        public override Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long expectedTransitionRevision,
            long expectedResourceGeneration, long expectedDataGeneration, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DetachCalls++;
            return Reserve(environmentFence, volumeId, expectedTransitionRevision, expectedResourceGeneration,
                expectedDataGeneration, EnvironmentWorkspaceVolumeOperation.Detach);
        }

        private Task<EnvironmentWorkspaceVolumeTransitionReservation> Reserve(EnvironmentGenerationFence environmentFence,
            string volumeId, long revision, long resourceGeneration, long dataGeneration,
            EnvironmentWorkspaceVolumeOperation operation)
        {
            Assert.Equal(fence, environmentFence);
            Assert.Equal(Current.VolumeId, volumeId);
            Assert.Equal(Current.ResourceGeneration, resourceGeneration);
            Assert.Equal(Current.DataGeneration, dataGeneration);
            var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(Guid.NewGuid(), fence,
                volumeId, revision, resourceGeneration, dataGeneration, Current.Resource, Current.ProviderBinding,
                revision + 1, resourceGeneration, dataGeneration,
                operation == EnvironmentWorkspaceVolumeOperation.Attach
                    ? EnvironmentWorkspaceVolumeState.Attached : EnvironmentWorkspaceVolumeState.Bound,
                operation, EnvironmentWorkspaceVolumeTransitionState.Reserved, false);
            _reservations.Add(reservation.OperationId, reservation);
            return Task.FromResult(reservation);
        }

        public override Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
            Guid operationId, EnvironmentGenerationFence environmentFence, bool effectMayHaveApplied,
            ProviderResourceRef? providerResource, bool effectVerified, CancellationToken cancellationToken)
        {
            AttachCalls++;
            return Complete(operationId, environmentFence, providerResource);
        }

        public override Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
            Guid operationId, EnvironmentGenerationFence environmentFence, bool effectMayHaveApplied,
            ProviderResourceRef? providerResource, bool effectVerified, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null) =>
            Complete(operationId, environmentFence, providerResource);

        private Task<EnvironmentWorkspaceVolumeTransitionResult> Complete(Guid operationId,
            EnvironmentGenerationFence environmentFence, ProviderResourceRef? resource)
        {
            var reservation = _reservations[operationId];
            Assert.Equal(fence, environmentFence);
            Assert.Equal(Current.Resource, resource);
            Current = new(fence, Current.VolumeId, reservation.TargetTransitionRevision,
                Current.ResourceGeneration, Current.DataGeneration, reservation.TargetPhase, reservation.Operation,
                Current.Specification, Current.Resource, Current.ProviderBinding);
            return Task.FromResult(new EnvironmentWorkspaceVolumeTransitionResult(operationId, fence, Current.VolumeId,
                reservation.ExpectedTransitionRevision, Current.ResourceGeneration, Current.DataGeneration, resource,
                reservation.TargetTransitionRevision, Current.ResourceGeneration, Current.DataGeneration, resource,
                reservation.TargetPhase, reservation.Operation, EnvironmentWorkspaceVolumeTransitionState.Completed, false));
        }
    }

    private sealed class ProvisionSandbox(AgentSandboxOptions options) : ISandboxProvider
    {
        public int Calls { get; private set; }
        public SandboxProvisionedResource? Resource { get; private set; }
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Resource = new(request.Workspace.Negotiation.SandboxResource, new(Guid.NewGuid()), new("cluster"),
                ImmutableHashSet.Create(SandboxCapabilities.VmIsolation, SandboxCapabilities.WorkspacePersistentVolumeClaim),
                [], new("agent-sandbox", "1.0.0", options.OptionsSchemaVersion, options.OptionsRevision,
                    JsonSerializer.SerializeToElement(options, JsonOptions), JsonSerializer.SerializeToElement(new { })));
            return Task.FromResult(Resource);
        }
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request,
            CancellationToken cancellationToken = default)
        {
            var resource = Resource ?? throw new InvalidOperationException("The Sandbox provider was not invoked.");
            return Task.FromResult(new SandboxObservation(
                request.Resource,
                SandboxObservedState.Pending,
                request.FencingGeneration,
                VmIsolationVerified: true,
                WorkspaceAttachmentVerified: true,
                VerifiedNetworkGeneration: null,
                ImmutableArray<SandboxStartupPhaseObservation>.Empty,
                ProvisionedResource: resource));
        }
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
