using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed partial class RuntimeResumeBootstrapTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void BootstrapRequiresThePostFenceOwnerVersionAndExactNewRegistration()
    {
        var request = Create();
        Assert.Equal(request, request.Validate());
        Assert.Equal("runtime_resume_bootstrap_request_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() => (request with
            {
                Resume = request.Resume with { PhaseVersion = 3 }
            }).Validate()).Code);
        Assert.Throws<RuntimeAuthorizationException>(() => (request with
        {
            Registration = request.Resume.SourceRelease.HostReceipt.Registration
        }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() => (request with
        {
            ExchangeOperationId = request.ConsumeOperationId
        }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() => (request with
        {
            ResumeAttemptId = Guid.NewGuid()
        }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() => (request with
        {
            Registration = request.Registration with
            {
                Binding = request.Registration.Binding with { ResumeOperationId = Guid.NewGuid() }
            }
        }).Validate());
    }

    [Fact]
    public void ResumeAttemptIdentityUsesTheExactRevokedPriorRegistrationAndStableDistinctEffectIds()
    {
        var request = Create();
        var initial = RuntimeResumeAttemptIdentity.Initial(request.Resume.OperationId);
        Assert.Equal(initial, request.ResumeAttemptId);
        Assert.Equal(initial, RuntimeResumeAttemptIdentity.Initial(request.Resume.OperationId));
        var revoked = request.Registration with { Revision = 2, State = RuntimeRegistrationState.Revoked };
        var replacement = RuntimeResumeAttemptIdentity.Replacement(request.Resume.OperationId, revoked);
        Assert.NotEqual(initial, replacement);
        Assert.Equal(replacement, RuntimeResumeAttemptIdentity.Replacement(request.Resume.OperationId, revoked));
        Assert.NotEqual(replacement, RuntimeResumeAttemptIdentity.Replacement(
            request.Resume.OperationId, revoked with { RuntimeInstanceId = Guid.NewGuid() }));
        Assert.NotEqual(replacement, RuntimeResumeAttemptIdentity.Replacement(
            request.Resume.OperationId, revoked with { Revision = 3 }));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeAttemptIdentity.Replacement(request.Resume.OperationId, request.Registration));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeAttemptIdentity.Replacement(Guid.NewGuid(), revoked));
        var effects = new[]
        {
            RuntimeResumeAttemptIdentity.Bootstrap(request.Resume.OperationId, replacement),
            RuntimeResumeAttemptIdentity.Consume(request.Resume.OperationId, replacement),
            RuntimeResumeAttemptIdentity.Exchange(request.Resume.OperationId, replacement)
        };
        Assert.Equal(3, effects.Distinct().Count());
        Assert.DoesNotContain(request.BootstrapOperationId, effects);
        Assert.DoesNotContain(request.ConsumeOperationId, effects);
        Assert.DoesNotContain(request.ExchangeOperationId, effects);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeAttemptIdentity.Initial(Guid.Empty));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeResumeAttemptIdentity.Bootstrap(request.Resume.OperationId, Guid.Empty));
    }

    [Fact]
    public void OrdinaryRuntimeBindingsDoNotGainResumeFieldsOrChangeTheirJson()
    {
        var source = Create().Resume.SourceRelease.HostReceipt.Registration;
        var json = JsonSerializer.SerializeToElement(source.Binding, JsonOptions);
        Assert.False(json.TryGetProperty("resumeOperationId", out _));
        Assert.False(json.TryGetProperty("resumeAttemptId", out _));
        Assert.True(JsonElement.DeepEquals(json,
            JsonSerializer.SerializeToElement(source.Binding with
            {
                ResumeOperationId = null, ResumeAttemptId = null
            }, JsonOptions)));
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeContractValidation.Validate(source with
        {
            Binding = source.Binding with { ResumeAttemptId = Guid.NewGuid() }
        }));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("delivery")]
    [InlineData("attempt")]
    [InlineData("registration")]
    [InlineData("present")]
    [InlineData("unspecified")]
    public async Task OnlyAnExactDeliveredAttemptAndFreshMissingHostStateCanRequestReplacement(string fault)
    {
        var request = Create();
        var binding = request.Registration.Binding;
        var hash = request.Resume.SourceRelease.HostReceipt.SourceGrant.ConfigurationHash;
        var delivery = new RuntimeBootstrapDeliveryReceipt(
            request.BootstrapOperationId, Guid.NewGuid(), request.Registration.RuntimeInstanceId,
            request.Registration.Revision, binding.PlacementUid, binding.PlacementGeneration,
            binding.ExecutionFence, hash, DateTimeOffset.UtcNow)
        {
            EnvironmentCurrentFencingGeneration = binding.EnvironmentCurrentFencingGeneration,
            EnvironmentProviderFencingGeneration = binding.EnvironmentProviderFencingGeneration
        };
        var state = new RuntimeResumeHostStateReceipt(1, request.Resume.OperationId,
            request.Resume.SourceManifest.ManifestId, request.Registration,
            request.ResumeAttemptId, RuntimeResumeHostState.Missing);
        var loss = new RuntimeResumeBootstrapStateLossReceipt(
            fault == "delivery" ? delivery with { OperationId = Guid.NewGuid() } : delivery,
            state with
            {
                ResumeAttemptId = fault == "attempt" ? Guid.NewGuid() : state.ResumeAttemptId,
                Registration = fault == "registration"
                    ? state.Registration with { RuntimeInstanceId = Guid.NewGuid() } : state.Registration,
                State = fault switch
                {
                    "present" => RuntimeResumeHostState.BootstrapPresent,
                    "unspecified" => RuntimeResumeHostState.Unspecified,
                    _ => state.State
                }
            });
        var outcome = new RuntimeResumeBootstrapOutcome(null, loss);
        using var handler = new Handler(outcome);
        using var http = new HttpClient(handler);
        var reader = new RuntimeResumeBootstrapHttpClient(http, new("https://environment.test/"));
        if (fault == "exact")
        {
            Assert.Equal(loss, outcome.ValidateFor(request).StateLoss);
            var failure = await Assert.ThrowsAsync<RuntimeResumeBootstrapStateLostException>(() =>
                reader.BootstrapAsync(request, Actor(), CancellationToken.None));
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(loss), JsonSerializer.SerializeToElement(failure.Receipt)));
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                reader.BootstrapAsync(request, Actor(), CancellationToken.None));
        }
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeResumeBootstrapOutcome(null, null).ValidateFor(request));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("kind")]
    [InlineData("registration")]
    [InlineData("position")]
    public void MissingOrChangedSavedConfigurationCannotBecomeAnEmptyConfiguration(string fault)
    {
        var request = Create();
        var host = request.Resume.SourceRelease.HostReceipt;
        var acknowledgment = host.ConfigurationAcknowledgment!;
        var material = acknowledgment.Reference.Material!;
        material = fault switch
        {
            "hash" => material with { Sha256 = new string('f', 64) },
            "kind" => material with { Kind = SessionMaterialKind.SdkCache },
            "registration" => material with { RuntimeInstanceId = Guid.NewGuid() },
            _ => material
        };
        var changed = host with
        {
            ConfigurationAcknowledgment = fault == "missing" ? null : acknowledgment with
            {
                Position = fault == "position" ? host.CacheAcknowledgment.Position : acknowledgment.Position,
                Reference = acknowledgment.Reference with { Material = material }
            }
        };
        request = request with
        {
            Resume = request.Resume with { SourceRelease = request.Resume.SourceRelease with { HostReceipt = changed } }
        };
        Assert.Throws<RuntimeAuthorizationException>(() => request.Validate());
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("bytes")]
    [InlineData("event")]
    [InlineData("no-store")]
    [InlineData("expired")]
    public async Task ProtectedConfigurationReadBindsExactAcknowledgedBytesAndTheCurrentActor(string fault)
    {
        var request = Create();
        var material = new SessionMaterialReadResult(
            request.Resume.SourceRelease.HostReceipt.ConfigurationAcknowledgment!, "{}"u8.ToArray());
        material = fault switch
        {
            "bytes" => material with { Bytes = "{ }"u8.ToArray() },
            "event" => material with { Material = material.Material with { EventId = Guid.NewGuid() } },
            _ => material
        };
        var actor = Actor();
        using var handler = new Handler(material)
        {
            NoStore = fault != "no-store",
            BeforeResponse = fault == "expired" ? actor.Bearer.Invalidate : null
        };
        using var http = new HttpClient(handler);
        var reader = new RuntimeResumeConfigurationHttpClient(http, new("https://events.test/"));
        if (fault == "exact")
        {
            var result = await reader.ReadAsync(request, actor, CancellationToken.None);
            Assert.Equal("{}"u8.ToArray(), result.Bytes);
            Assert.Equal("/internal/runtime/resume/configuration", handler.Path);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                reader.ReadAsync(request, actor, CancellationToken.None));
            Assert.Equal(fault switch
            {
                "no-store" => "runtime_owner_cache_contract_invalid",
                "expired" => "runtime_resume_actor_expired",
                _ => "runtime_resume_configuration_changed"
            }, failure.Code);
        }
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("journal-rebuild")]
    [InlineData("cache")]
    [InlineData("model")]
    [InlineData("session")]
    public void ReadinessAndAFreshFenceAreNotEvidenceThatTheSavedNativeCacheWasRecovered(string fault)
    {
        var request = Create();
        var receipt = Cache(request);
        receipt = fault switch
        {
            "fresh" => receipt with { RecoveryMode = "fresh" },
            "journal-rebuild" => receipt with { RecoveryMode = "journal-rebuild" },
            "cache" => receipt with
            {
                CacheAcknowledgment = receipt.CacheAcknowledgment with { EventId = Guid.NewGuid() }
            },
            "model" => receipt with { Source = receipt.Source with { ModelId = "different-model" } },
            _ => receipt with { Source = receipt.Source with { SdkSessionId = "foreign-native-session" } }
        };
        Assert.Throws<RuntimeAuthorizationException>(() => receipt.ValidateFor(request));
        Assert.Equal(Cache(request), Cache(request).ValidateFor(request));
    }

    [Fact]
    public async Task MissingCompositionOrDeniedPostFenceAuthorityCannotRequestABrokerGrant()
    {
        var request = Create();
        using var handler = new Handler(request) { StatusCode = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler);
        var options = new EnvironmentRuntimeBootstrapDeliveryOptions(
            new("https://core.test/"), new("https://broker.test/"));
        var delivery = new EnvironmentRuntimeBootstrapDelivery(
            http, options, null!, new([]), TimeProvider.System);
        Assert.Equal("runtime_resume_configuration_owner_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                delivery.BootstrapResumeAsync(request, Actor(), CancellationToken.None))).Code);
        Assert.Null(handler.Path);
        delivery = new(http, options with { EventsOwnerAddress = new("https://events.test/") },
            null!, new([]), TimeProvider.System);
        Assert.Equal("runtime_resume_bootstrap_store_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                delivery.BootstrapResumeAsync(request, Actor(), CancellationToken.None))).Code);
        Assert.Null(handler.Path);
        delivery = new(http, options with { EventsOwnerAddress = new("https://events.test/") },
            null!, new([]), TimeProvider.System, new AuthorityOnlyStore());
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            delivery.BootstrapResumeAsync(request, Actor(), CancellationToken.None));
        Assert.Equal("/internal/runtime/resume/require-bootstrap-current", handler.Path);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RestartBootstrapUsesTheSavedSourceProfileAndTheActualNewEndpoint()
    {
        var request = Create();
        var source = request.Resume.SourceRelease.HostReceipt.Registration.Binding;
        var placement = request.Resume.RestoredWorkspace!.Placement;
        var profile = new EnvironmentRuntimeBootstrapProfile(
            source.ProfileId, source.ConfigureEndpoint, source.ObservationEndpoint)
        {
            McpServer = new("accepted-mcp", new("https://mcp.test/"))
        };
        var registry = new EnvironmentRuntimeBootstrapProfileRegistry(
        [
            new(request.Resume.SourceManifest.EnvironmentFence!.Owner, profile,
                new(ProviderSeam.Sandbox, source.PlacementProviderId!, source.PlacementUid, source.PlacementGeneration))
        ]);
        var projection = new EnvironmentSandboxPlacementProjectionV1(
            1, placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId,
            placement.LifecycleGeneration, placement.CurrentFencingGeneration, placement.ProviderFencingGeneration,
            placement.LeaseRevision, placement.LeaseExpiresAt, true, SandboxLeaseState.Active,
            placement.Resource, placement.Endpoint, placement.Placement)
        {
            Image = placement.Image,
            RuntimeResume = request.Resume with { PhaseVersion = 3 },
            RuntimeReadiness = new(DateTimeOffset.UtcNow,
                new(placement.Resource, SandboxObservedState.Ready, placement.ProviderFencingGeneration,
                    true, true, 2, []) { RuntimeEndpoint = placement.RuntimeEndpoint },
                new(30, 30, 30, 30, 30, 120), "/workspace/agentweaver/workspace")
        };
        var restored = EnvironmentRuntimePlacementReader.ProjectBootstrapProfile(projection, source.ProfileId, registry);
        Assert.Equal(placement.ConfigureEndpoint, restored.ConfigureEndpoint);
        Assert.NotEqual(source.ConfigureEndpoint, restored.ConfigureEndpoint);
        Assert.Equal(profile.McpServer, restored.McpServer);
        Assert.Equal(placement.Resource, restored.Resource);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            EnvironmentRuntimePlacementReader.ProjectBootstrapProfile(projection, "foreign-profile", registry));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            EnvironmentRuntimePlacementReader.ProjectBootstrapProfile(
                projection with { RuntimeReadiness = null }, source.ProfileId, registry));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("selection")]
    [InlineData("released")]
    [InlineData("resource")]
    [InlineData("flush")]
    [InlineData("host")]
    [InlineData("volume")]
    public async Task SourceManagerRequiresExactRetainedSelectionFlushAndVolumeBeforeAnyLifecycleEffect(string fault)
    {
        var fixture = RuntimeSuspendPlacementReleaseTests.Create();
        var original = RuntimeWorkspaceResumeTests.Request();
        var request = original with
        {
            SourceRelease = fixture.Request,
            SourceManifest = original.SourceManifest with
            {
                ManifestId = fixture.Request.HostReceipt.ManifestId,
                CacheAcknowledgment = fixture.Request.HostReceipt.CacheAcknowledgment,
                WorkspaceResource = fixture.Volume.Resource
            }
        };
        var owner = fixture.Lease.Fence.Owner;
        var selection = EnvironmentEgressManagerTests.Selection() with
        {
            ProjectId = owner.ProjectId, RunId = owner.RunId
        };
        var savedSelection = selection with
        {
            ContextRevision = fault == "selection" ? "foreign-selection" : selection.ContextRevision
        };
        var lease = fixture.Lease with
        {
            State = fault == "released" ? SandboxLeaseState.Active : SandboxLeaseState.Released,
            IsCurrent = false,
            ProvisionIntent = fixture.Lease.ProvisionIntent with
            {
                SelectionSnapshot = JsonSerializer.SerializeToElement(savedSelection, JsonOptions)
            },
            ProvisionedResource = fixture.Lease.ProvisionedResource! with
            {
                Resource = fixture.Lease.ProvisionedResource.Resource with
                {
                    ResourceId = fault == "resource" ? "foreign-placement" :
                        fixture.Lease.ProvisionedResource.Resource.ResourceId
                }
            }
        };
        var volume = new EnvironmentWorkspaceVolumeSnapshot(
            fixture.Volume.EnvironmentFence, fixture.Volume.VolumeId, 6, 1,
            fault == "volume" ? 9 : 8, EnvironmentWorkspaceVolumeState.Bound,
            EnvironmentWorkspaceVolumeOperation.Detach, fixture.Volume.Specification,
            fixture.Volume.Resource, fixture.Volume.ProviderBinding);
        var host = fixture.Request.HostReceipt;
        var evidence = fixture.Evidence with
        {
            RuntimeHostReceipt = JsonSerializer.SerializeToElement(
                fault == "host" ? host with { OperationId = Guid.NewGuid() } : host,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var lifecycle = new SourceLifecycle(fixture.Lease.Fence, volume,
            fault == "flush" ? null : evidence);
        var authorization = new ProjectAuthorizationContextResponse(
            1, "https://issuer.test/", "actor", owner.TenantId, 1, owner.ProjectId, owner.RunId,
            [new(ProjectAuthorityResourceType.Project, owner.ProjectId,
                [new(ProjectAuthorizationPermission.ReadRunSelection, 1),
                 new(ProjectAuthorizationPermission.WriteProjects, 1)])]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
        var egress = new EnvironmentEgressManager(projects, null!, null!, lifecycle);
        using var handler = new Handler(request);
        using var http = new HttpClient(handler);
        var profiles = new EnvironmentRuntimeBootstrapProfileRegistry(
        [
            new(owner, new(fixture.Request.HostReceipt.Registration.Binding.ProfileId,
                fixture.Request.HostReceipt.Registration.Binding.ConfigureEndpoint,
                fixture.Request.HostReceipt.Registration.Binding.ObservationEndpoint),
                fixture.Lease.ProvisionedResource.Resource)
        ]);
        var delivery = new EnvironmentRuntimeBootstrapDelivery(
            http, new(new("https://core.test/"), new("https://broker.test/")), null!, profiles, TimeProvider.System);
        var manager = new EnvironmentRuntimeWorkspaceResumeManager(
            lifecycle, new SourceLeases(lease), null!, egress, delivery, null!, profiles);
        if (fault == "exact")
        {
            await Assert.ThrowsAsync<BeforeEffectReachedException>(() =>
                manager.PrepareAsync(request, Actor(), CancellationToken.None));
            Assert.True(lifecycle.ReachedEffectBoundary);
            Assert.True(handler.Calls >= 3);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                manager.PrepareAsync(request, Actor(), CancellationToken.None));
            Assert.Equal(fault switch
            {
                "flush" or "host" => "runtime_resume_source_flush_missing",
                "volume" => "runtime_resume_retained_workspace_changed",
                _ => "runtime_resume_source_placement_changed"
            }, failure.Code);
            Assert.False(lifecycle.ReachedEffectBoundary);
        }
    }

    internal static RuntimeResumeBootstrapRequest Create()
    {
        var source = RuntimeWorkspaceResumeTests.Request();
        var host = source.SourceRelease.HostReceipt;
        var hash = RuntimeContractValidation.Hash("{}"u8);
        var cache = host.CacheAcknowledgment.Reference.Material!;
        var reference = new SessionObjectReference(
            new ObjectKey("private/runtime/configuration"), SessionMaterialValidation.Purpose(SessionMaterialKind.RuntimeConfiguration), 2)
        {
            Material = cache with { Kind = SessionMaterialKind.RuntimeConfiguration, Sha256 = hash }
        };
        host = host with
        {
            SourceGrant = host.SourceGrant with { ConfigurationHash = hash },
            ConfigurationAcknowledgment = new(1, host.CacheAcknowledgment.Identity, Guid.NewGuid(), 8, reference)
        };
        source = source with { SourceRelease = source.SourceRelease with { HostReceipt = host } };
        var workspace = RuntimeWorkspaceResumeTests.Workspace(source);
        var image = new SandboxImageIdentity("sha256:" + new string('b', 64), "linux/amd64", 1);
        workspace = workspace with { Placement = workspace.Placement with { Image = image } };
        var placement = workspace.Placement;
        var attempt = RuntimeResumeAttemptIdentity.Initial(source.OperationId);
        var binding = host.Registration.Binding with
        {
            ExecutionFence = 2, EnvironmentLifecycleGeneration = placement.LifecycleGeneration,
            EnvironmentCurrentFencingGeneration = placement.CurrentFencingGeneration,
            EnvironmentProviderFencingGeneration = placement.ProviderFencingGeneration,
            EnvironmentLeaseRevision = placement.LeaseRevision, PlacementUid = placement.Resource.ResourceId,
            PlacementGeneration = placement.Resource.Generation, ConfigureEndpoint = placement.ConfigureEndpoint,
            Image = image, ResumeOperationId = source.OperationId, ResumeAttemptId = attempt
        };
        return new(1, source with { PhaseVersion = 4, RestoredWorkspace = workspace },
            new(Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active, placement.LeaseExpiresAt),
            RuntimeResumeAttemptIdentity.Bootstrap(source.OperationId, attempt),
            RuntimeResumeAttemptIdentity.Consume(source.OperationId, attempt),
            RuntimeResumeAttemptIdentity.Exchange(source.OperationId, attempt))
        {
            ResumeAttemptId = attempt
        };
    }

    private static RuntimeHostResumeCacheReceipt Cache(RuntimeResumeBootstrapRequest request)
    {
        var facts = request.Resume.SourceRelease.HostReceipt.NativeTurn.Admission.Source with
        {
            RuntimeInstanceId = request.Registration.RuntimeInstanceId,
            RegistrationRevision = request.Registration.Revision,
            SdkSessionId = RuntimeContractValidation.NativeSessionId(request.Registration.Binding)
        };
        return new(1, request.Resume.OperationId, request.Resume.SourceManifest.ManifestId,
            request.Registration, request.Resume.SourceManifest.CacheAcknowledgment!, facts, "native-cache");
    }

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant");

    private sealed class BeforeEffectReachedException : Exception;

    private sealed class AuthorityOnlyStore : IEnvironmentRuntimeResumeBootstrapStore
    {
        public async Task<RuntimeResumeBootstrapReceipt> ExecuteAsync(
            RuntimeResumeBootstrapRequest request, Func<CancellationToken, Task> requireCurrent,
            Func<RuntimeResumeBootstrapReceipt?, CancellationToken, Task<RuntimeResumeBootstrapReceipt>> reconcile,
            CancellationToken cancellationToken)
        {
            await requireCurrent(cancellationToken);
            throw new InvalidOperationException("The denied authority must prevent all bootstrap effects.");
        }
    }

    private sealed class SourceLifecycle(
        EnvironmentGenerationFence fence, EnvironmentWorkspaceVolumeSnapshot volume,
        WorkspaceVolumeFlushEvidence? evidence) :
        EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence), IEnvironmentLifecycleStore
    {
        public bool ReachedEffectBoundary { get; private set; }

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, CancellationToken cancellationToken) =>
            Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(volume);

        public Task<WorkspaceVolumeFlushEvidence?> GetRetainedWorkspaceVolumeFlushEvidenceAsync(
            EnvironmentGenerationFence currentFence, EnvironmentGenerationFence sourceFence, string volumeId,
            long targetTransitionRevision, CancellationToken cancellationToken)
        {
            Assert.Equal(fence, currentFence);
            Assert.Equal(fence, sourceFence);
            Assert.Equal(5, targetTransitionRevision);
            return Task.FromResult(evidence);
        }

        public override async Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
            EnvironmentLifecycleTransitionRequest request, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null)
        {
            await beforeCommitCheck!(cancellationToken);
            ReachedEffectBoundary = true;
            throw new BeforeEffectReachedException();
        }
    }

    internal class SourceLeases(SandboxLeaseSnapshot lease) : ISandboxLeaseStore
    {
        public Task<SandboxLeaseSnapshot?> GetAsync(
            EnvironmentGenerationFence fence, long resourceGeneration, CancellationToken cancellationToken)
        {
            Assert.Equal(lease.Fence, fence);
            Assert.Equal(lease.ResourceGeneration, resourceGeneration);
            return Task.FromResult<SandboxLeaseSnapshot?>(lease);
        }

        public virtual Task<SandboxLeaseReservation> ReserveProvisionAsync(EnvironmentGenerationFence fence,
            string idempotencyKey, SandboxLeaseProvisionIntent intent, CancellationToken cancellationToken) =>
            throw new BeforeEffectReachedException();
        public Task<SandboxLeaseSnapshot?> GetCurrentAsync(
            EnvironmentGenerationFence fence, CancellationToken cancellationToken) =>
            Task.FromResult<SandboxLeaseSnapshot?>(lease);
        public Task<T> GetCurrentAsync<T>(EnvironmentGenerationFence fence,
            Func<SandboxLeaseSnapshot?, CancellationToken, Task<T>> callback, CancellationToken cancellationToken) =>
            callback(lease, cancellationToken);
        public Task<T> GetCurrentWithWorkspaceAsync<T>(EnvironmentGenerationFence fence, string volumeId,
            Func<SandboxLeaseSnapshot?, EnvironmentWorkspaceVolumeSnapshot?, CancellationToken, Task<T>> callback,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(Guid operationId, EnvironmentGenerationFence fence,
            JsonElement providerRequest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<SandboxLeaseSnapshot> CompleteProvisionAsync(Guid operationId, EnvironmentGenerationFence fence,
            SandboxProvisionedResource? provisionedResource, bool effectMayHaveApplied,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxLeaseSnapshot> BeginRetirementAsync(EnvironmentGenerationFence fence,
            long resourceGeneration, long providerFencingGeneration, SandboxRetirementReason reason,
            string idempotencyKey, SandboxRetirementAuthorization? authorization,
            SandboxTerminalEvidence? terminalEvidence, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SandboxLeaseSnapshot> CompleteReleaseAsync(Guid operationId, EnvironmentGenerationFence fence,
            long providerFencingGeneration, SandboxReleaseReceipt receipt, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null) => throw new NotSupportedException();
        public Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
            SandboxPartialReleaseCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
            EnvironmentGenerationFence fence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteLateResourceCleanupAsync(EnvironmentGenerationFence currentFence,
            SandboxLateResourceCleanupClaim claim, SandboxReleaseReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Handler(object body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public int Calls { get; private set; }
        public bool NoStore { get; init; } = true;
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public Action? BeforeResponse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            Assert.True(request.Headers.CacheControl?.NoStore);
            BeforeResponse?.Invoke();
            var response = new HttpResponseMessage(StatusCode)
            {
                RequestMessage = request,
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            if (NoStore)
                response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
