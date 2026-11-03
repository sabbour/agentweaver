using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;

namespace Agentweaver.Environment.Tests;

public sealed partial class EnvironmentRuntimeBootstrapProfileRegistryTests
{
    [Fact]
    public async Task EmptyStaticRegistryResolvesAndRehydratesThroughBootstrapAndReadiness()
    {
        using var fixture = await DefaultSourceFixture.CreateAsync();
        var first = await fixture.ResolveAsync("agenthost-default");
        Assert.Equal(first, await fixture.ResolveAsync("agenthost-default"));
        Assert.Equal(first.ProfileId, (await fixture.ResolveAsync(first.ProfileId)).ProfileId);
        var saved = Assert.Single(fixture.Replacements.Values);
        Assert.Equal(fixture.Owner, saved.Registration.Owner);
        Assert.Equal(fixture.Lease.ProvisionedResource!.Resource, saved.Registration.Placement);
        Assert.Equal(fixture.OwnerContext.ActorIssuer, saved.ActorIssuer);
        Assert.Equal(fixture.OwnerContext.ActorId, saved.ActorId);
        Assert.Equal(fixture.OwnerContext.AcceptedSelectionHash, saved.AcceptedSelectionHash);
        Assert.Equal(fixture.Lease.Fence.LifecycleGeneration, saved.LifecycleGeneration);
        Assert.Equal(fixture.Lease.CurrentFencingGeneration, saved.CurrentFencingGeneration);
        Assert.Equal(fixture.Lease.ProviderFencingGeneration, saved.ProviderFencingGeneration);
        Assert.Equal(fixture.Lease.LeaseRevision, saved.LeaseRevision);
        Assert.Equal(fixture.Lease.LeaseExpiresAt, saved.LeaseExpiresAt);
        Assert.Equal(fixture.Endpoint, saved.RuntimeEndpoint);
        Assert.Equal(new Uri("https://orchestrator.test/internal/runtime/observations"),
            saved.Registration.Profile.ObservationEndpoint);
        Assert.True(fixture.Profiles.TryResolveSource(fixture.Owner, "agenthost-default",
            saved.Registration.Placement, fixture.Endpoint, out var source));
        Assert.Equal(EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(source!.Profile),
            saved.SourceProfileConfigurationHash);
        var bootstrap = await fixture.ReadAsync(first.ProfileId);
        var readiness = await fixture.Reader.GetReadinessContextAsync(fixture.Actor,
            fixture.Owner.ProjectId, fixture.Owner.RunId, "session", fixture.Owner.EnvironmentId,
            first.ProfileId, fixture.Profiles, default, fixture.Replacements);
        Assert.Equal(bootstrap, readiness!.Placement);
        Assert.Equal(saved.Registration.Placement, bootstrap!.Resource);
        Assert.Equal(fixture.OwnerContext, bootstrap.RuntimeOwnerContext);
        Assert.Equal(fixture.Endpoint, bootstrap.RuntimeEndpoint);
        Assert.DoesNotContain("actor-token", JsonSerializer.Serialize(saved));
        Assert.True(fixture.Transport.HealthReads >= 3);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("selection")]
    [InlineData("observation")]
    [InlineData("receiver")]
    [InlineData("endpoint")]
    [InlineData("hash")]
    [InlineData("expired")]
    public async Task DefaultReplacementFailsClosedWhenItsAuthorityOrSourceChanges(string fault)
    {
        using var fixture = await DefaultSourceFixture.CreateAsync();
        var resolved = await fixture.ResolveAsync("agenthost-default");
        switch (fault)
        {
            case "actor":
                fixture.Transport.Context = fixture.OwnerContext with { ActorId = Guid.NewGuid().ToString("D") };
                break;
            case "selection":
                fixture.Transport.Context = fixture.OwnerContext with { AcceptedSelectionHash = new string('b', 64) };
                break;
            case "observation":
                fixture.Profiles = new([], "agenthost-default", new("https://changed.test/"));
                break;
            case "receiver":
                fixture.Provider.Endpoint = fixture.Endpoint with
                {
                    ReceiverConfigureEndpoint = new("https://changed.test:8443/runtime/v1/configure")
                };
                break;
            case "endpoint":
                fixture.Provider.Endpoint = fixture.Endpoint with { PodUid = "changed-pod", PodAddress = "10.1.1.8" };
                break;
            case "hash":
                var saved = Assert.Single(fixture.Replacements.Values);
                fixture.Replacements.Values[0] = saved with { SourceProfileConfigurationHash = new string('f', 64) };
                break;
            case "expired":
                fixture.Clock.Now = fixture.Lease.LeaseExpiresAt!.Value.AddSeconds(1);
                break;
        }
        if (fault == "expired")
        {
            Assert.Equal("sandbox_lease_expired",
                (await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
                    fixture.ReadAsync(resolved.ProfileId))).Code);
            Assert.Equal("sandbox_lease_expired",
                (await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
                    fixture.ResolveAsync("agenthost-default"))).Code);
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.ReadAsync(resolved.ProfileId));
            if (fault != "endpoint")
                await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.ResolveAsync(resolved.ProfileId));
            else
                Assert.NotEqual(resolved.ProfileId, (await fixture.ResolveAsync(resolved.ProfileId)).ProfileId);
        }
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("ProjectId")]
    [InlineData("RunId")]
    [InlineData("EnvironmentId")]
    [InlineData("lifecycle")]
    [InlineData("fence")]
    [InlineData("revision")]
    [InlineData("expiry")]
    public async Task DefaultReplacementCannotBeReusedAcrossOwnersOrLeaseVersions(string fault)
    {
        using var fixture = await DefaultSourceFixture.CreateAsync();
        var id = (await fixture.ResolveAsync("agenthost-default")).ProfileId;
        var placement = fixture.Projection();
        placement = fault switch
        {
            "TenantId" => placement with { TenantId = "foreign" },
            "ProjectId" => placement with { ProjectId = "foreign" },
            "RunId" => placement with { RunId = "foreign" },
            "EnvironmentId" => placement with { EnvironmentId = "foreign" },
            "lifecycle" => placement with { LifecycleGeneration = placement.LifecycleGeneration + 1 },
            "fence" => placement with
            {
                CurrentFencingGeneration = placement.CurrentFencingGeneration + 1,
                ProviderFencingGeneration = placement.ProviderFencingGeneration + 1
            },
            "revision" => placement with { LeaseRevision = placement.LeaseRevision + 1 },
            _ => placement with { LeaseExpiresAt = placement.LeaseExpiresAt.AddSeconds(1) }
        };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Reader.ProjectBootstrapProfileAsync(placement, id, fixture.Profiles,
                fixture.Replacements, default, ownerContext: fixture.OwnerContext));
    }

    [Fact]
    public async Task SharedDefaultIdProducesOwnerScopedReplacementsAndStillRequiresLiveVerification()
    {
        using var first = await DefaultSourceFixture.CreateAsync();
        using var second = await DefaultSourceFixture.CreateAsync("other-environment");
        var firstId = (await first.ResolveAsync("agenthost-default")).ProfileId;
        var secondId = (await second.ResolveAsync("agenthost-default")).ProfileId;
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("runtime_delivery_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => second.ReadAsync(firstId))).Code);
        first.Transport.HealthAvailable = false;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => first.ResolveAsync("agenthost-default"));
        Assert.Single(first.Replacements.Values);
    }

    [Theory]
    [InlineData(SandboxLeaseState.Released)]
    [InlineData(SandboxLeaseState.Releasing)]
    public async Task InactiveLeaseCannotCreateADefaultReplacement(SandboxLeaseState state)
    {
        using var fixture = await DefaultSourceFixture.CreateAsync(state: state);
        Assert.Equal("sandbox_placement_unavailable",
            (await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
                fixture.ResolveAsync("agenthost-default"))).Code);
        Assert.Empty(fixture.Replacements.Values);
        Assert.Equal(0, fixture.Transport.HealthReads);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("source-fence")]
    [InlineData("source-actor")]
    [InlineData("source-hash")]
    [InlineData("receiver")]
    [InlineData("lifecycle")]
    [InlineData("new-requested-source")]
    [InlineData("new-fence")]
    [InlineData("new-endpoint")]
    public async Task DefaultResumeRehydratesTheSavedSourceWithoutHashingPodIdentity(string fault)
    {
        var resume = RuntimeWorkspaceResumeTests.Request();
        var workspace = RuntimeWorkspaceResumeTests.Workspace(resume);
        var binding = resume.SourceRelease.HostReceipt.Registration.Binding with
        {
            ConfigureEndpoint = workspace.Placement.ConfigureEndpoint
        };
        resume = resume with
        {
            SourceRelease = resume.SourceRelease with
            {
                HostReceipt = resume.SourceRelease.HostReceipt with
                {
                    Registration = resume.SourceRelease.HostReceipt.Registration with { Binding = binding }
                }
            }
        };
        var owner = resume.SourceManifest.EnvironmentFence!.Owner;
        var profiles = new EnvironmentRuntimeBootstrapProfileRegistry(
            [], "agenthost-default", new("https://core.test/"));
        var oldEndpoint = workspace.Placement.RuntimeEndpoint! with { PodUid = "old-pod", PodAddress = "10.1.1.7" };
        var oldResource = new ProviderResourceRef(
            ProviderSeam.Sandbox, binding.PlacementProviderId!, binding.PlacementUid, binding.PlacementGeneration);
        Assert.True(profiles.TryResolveSource(owner, "agenthost-default", oldResource, oldEndpoint, out var source));
        var profile = source!.Profile with { ProfileId = binding.ProfileId };
        var saved = new EnvironmentRuntimeBootstrapProfileReplacement(
            1, "agenthost-default", "agenthost-default", binding.ActorIssuer, binding.ActorId,
            binding.AcceptedSelectionHash, EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(source.Profile),
            EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(profile), new(owner, profile, oldResource),
            binding.EnvironmentLifecycleGeneration, binding.EnvironmentCurrentFencingGeneration,
            binding.EnvironmentProviderFencingGeneration, binding.EnvironmentLeaseRevision,
            DateTimeOffset.UtcNow.AddMinutes(5), oldEndpoint).Validate();
        var replacements = new DefaultReplacementStore();
        replacements.Values.Add(fault switch
        {
            "source-fence" => saved with { CurrentFencingGeneration = saved.CurrentFencingGeneration + 1 },
            "source-actor" => saved with { ActorId = Guid.NewGuid().ToString("D") },
            "source-hash" => saved with { SourceProfileConfigurationHash = new string('f', 64) },
            _ => saved
        });
        var placement = workspace.Placement;
        var endpoint = placement.RuntimeEndpoint!;
        if (fault == "receiver")
            endpoint = endpoint with { ReceiverConfigureEndpoint = new("https://changed.test:8443/runtime/v1/configure") };
        var projection = new EnvironmentSandboxPlacementProjectionV1(
            1, owner.TenantId, owner.ProjectId, owner.RunId, owner.EnvironmentId,
            fault == "lifecycle" ? 3 : placement.LifecycleGeneration,
            placement.CurrentFencingGeneration, placement.ProviderFencingGeneration,
            placement.LeaseRevision, placement.LeaseExpiresAt, true, SandboxLeaseState.Active,
            placement.Resource, placement.Endpoint, placement.Placement)
        {
            RuntimeResume = resume,
            RuntimeReadiness = new(DateTimeOffset.UtcNow,
                new(placement.Resource, SandboxObservedState.Pending, placement.ProviderFencingGeneration,
                    true, true, null, []) { RuntimeEndpoint = endpoint },
                new(30, 30, 30, 30, 30, 120), workspace.Workspace.MountPath)
        };
        var reader = new EnvironmentRuntimePlacementReader(null!, null!, TimeProvider.System);
        var read = () => reader.ProjectBootstrapProfileAsync(
            projection, binding.ProfileId, profiles, replacements, default, sourceBinding: binding);
        if (fault != "exact" && !fault.StartsWith("new-", StringComparison.Ordinal))
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(read);
            return;
        }
        var restored = await read();
        Assert.Equal(placement.Resource, restored.Resource);
        Assert.NotEqual(oldResource, restored.Resource);
        Assert.NotEqual(oldEndpoint.PodUid, restored.RuntimeEndpoint!.PodUid);
        Assert.NotEqual(oldEndpoint.PodAddress, restored.RuntimeEndpoint.PodAddress);
        Assert.Equal(binding.ConfigureEndpoint, restored.ConfigureEndpoint);
        Assert.True(profiles.TryResolveSource(owner, "agenthost-default", placement.Resource, endpoint, out var current));
        Assert.Equal(saved.SourceProfileConfigurationHash,
            EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(current!.Profile));

        var newProfile = profile with { ProfileId = "profile-restored" };
        var replacement = saved with
        {
            RequestedSourceProfileId = fault == "new-requested-source" ? "foreign-profile" : binding.ProfileId,
            Registration = new(owner, newProfile, placement.Resource),
            RegistrationProfileConfigurationHash =
                EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(newProfile),
            LifecycleGeneration = placement.LifecycleGeneration,
            CurrentFencingGeneration = fault == "new-fence"
                ? placement.CurrentFencingGeneration + 1 : placement.CurrentFencingGeneration,
            ProviderFencingGeneration = placement.ProviderFencingGeneration,
            LeaseRevision = placement.LeaseRevision,
            LeaseExpiresAt = placement.LeaseExpiresAt,
            RuntimeEndpoint = fault == "new-endpoint" ? endpoint with { PodUid = "foreign-pod" } : endpoint
        };
        replacements.Values.Add(replacement);
        var ownerContext = new RuntimeOwnerContext(1, binding.ActorIssuer, binding.ActorId,
            owner.TenantId, owner.ProjectId, owner.RunId, binding.SessionId, binding.AgentId,
            "model", binding.TurnId, 1, 1, 1, "context", binding.AcceptedSelectionHash, 1, 1, 1, 1);
        var readReplacement = () => reader.ProjectBootstrapProfileAsync(
            projection, newProfile.ProfileId, profiles, replacements, default, ownerContext: ownerContext);
        if (fault.StartsWith("new-", StringComparison.Ordinal))
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(readReplacement);
        else
            Assert.Equal(placement.Resource, (await readReplacement()).Resource);
    }

    private sealed class DefaultReplacementStore : IEnvironmentRuntimeBootstrapProfileReplacementStore
    {
        public List<EnvironmentRuntimeBootstrapProfileReplacement> Values { get; } = [];
        public Task<EnvironmentRuntimeBootstrapProfileReplacement?> ReadAsync(
            EnvironmentOwnerIdentity owner, string profileId, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(value =>
                value.Registration.Owner == owner && value.Registration.Profile.ProfileId == profileId));

        public async Task<EnvironmentRuntimeBootstrapProfileReplacement> AppendAsync(
            EnvironmentRuntimeBootstrapProfileReplacement value,
            Func<CancellationToken, Task> requireCurrentAuthority, CancellationToken cancellationToken)
        {
            await requireCurrentAuthority(cancellationToken);
            var existing = await ReadAsync(value.Registration.Owner, value.Registration.Profile.ProfileId, cancellationToken);
            if (existing is not null)
                Assert.Equal(existing, value);
            else
                Values.Add(value.Validate());
            await requireCurrentAuthority(cancellationToken);
            return existing ?? value;
        }
    }

    private sealed class DefaultClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DefaultSourceFixture : IDisposable
    {
        public EnvironmentOwnerIdentity Owner => Lease.Fence.Owner;
        public required SandboxLeaseSnapshot Lease { get; init; }
        public required DefaultSourceTransport Transport { get; init; }
        public required DefaultSandboxProvider Provider { get; init; }
        public required HttpClient Client { get; init; }
        public required EnvironmentRuntimePlacementReader Reader { get; init; }
        public DefaultClock Clock { get; init; } = new();
        public EnvironmentRuntimeBootstrapProfileRegistry Profiles { get; set; } =
            new([], "agenthost-default", new("https://orchestrator.test/"));
        public DefaultReplacementStore Replacements { get; } = new();
        public RuntimeOwnerContext OwnerContext => Transport.Context;
        public SandboxRuntimeHttpsEndpoint Endpoint => Provider.Endpoint;
        public RuntimeActorAuthorization Actor { get; } =
            new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddHours(1)), "tenant");

        public Task<RuntimeBootstrapProfileResolveReceipt> ResolveAsync(string id) =>
            Reader.ResolveCurrentProfileAsync(Actor, Owner.ProjectId, Owner.RunId, "session",
                Owner.EnvironmentId, id, Profiles, Replacements, new(Client), default);

        public Task<EnvironmentRuntimeBootstrapContext?> ReadAsync(string id) =>
            Reader.GetBootstrapContextAsync(Actor, Owner.ProjectId, Owner.RunId, "session",
                Owner.EnvironmentId, id, Profiles, default, Replacements);

        public EnvironmentSandboxPlacementProjectionV1 Projection() => new(
            1, Owner.TenantId, Owner.ProjectId, Owner.RunId, Owner.EnvironmentId, Lease.Fence.LifecycleGeneration,
            Lease.CurrentFencingGeneration, Lease.ProviderFencingGeneration, Lease.LeaseRevision,
            Lease.LeaseExpiresAt!.Value, true, SandboxLeaseState.Active,
            Lease.ProvisionedResource!.Resource, Lease.ProvisionedResource.Endpoint, Lease.ProvisionedResource.Placement)
        {
            RuntimeReadiness = new(Clock.Now, Provider.Observation(Lease.ProvisionedResource.Resource,
                Lease.ProviderFencingGeneration), new(30, 30, 30, 30, 30, 120), "/workspace/agentweaver/workspace")
        };

        public static async Task<DefaultSourceFixture> CreateAsync(
            string environmentId = "environment", SandboxLeaseState state = SandboxLeaseState.Active)
        {
            var original = RuntimeSuspendPlacementReleaseTests.Create();
            var originalOwner = original.Lease.Fence.Owner;
            var owner = new EnvironmentOwnerIdentity(
                originalOwner.TenantId, originalOwner.ProjectId, originalOwner.RunId, environmentId);
            var fence = new EnvironmentGenerationFence(owner, 1);
            var selection = EnvironmentEgressManagerTests.Selection() with { ProjectId = owner.ProjectId, RunId = owner.RunId };
            var context = new RuntimeOwnerContext(1, "https://issuer.test/", Guid.NewGuid().ToString("D"),
                owner.TenantId, owner.ProjectId, owner.RunId, "session", "agent", "model", "turn",
                1, 1, 1, "context", new string('a', 64), 1, 1, 1, 1);
            var authorization = new ProjectAuthorizationContextResponse(1, context.ActorIssuer, context.ActorId,
                owner.TenantId, 1, owner.ProjectId, owner.RunId,
                [new(ProjectAuthorityResourceType.Project, owner.ProjectId,
                    [new(ProjectAuthorizationPermission.ReadRunSelection, 1),
                     new(ProjectAuthorizationPermission.WriteProjects, 1)])]);
            var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
            var lifecycle = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
            var egress = EnvironmentEgressManagerTests.Manager(projects,
                new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(), lifecycle);
            await egress.ApplyAndVerifyAsync(new("actor-token", "tenant"), new(fence, 1, 0, "network"), default);
            var options = new AgentSandboxOptions(2, "sandbox-options", "agentweaver", "azure-files",
                "runtime@sha256:" + new string('a', 64), "linux/amd64", 1,
                "kata", "kata", "1", "1Gi", 1, 1, new(30, 30, 30, 30, 30, 120));
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            var recovery = JsonNode.Parse(original.Lease.ProvisionIntent.ProviderRequest.GetRawText())!;
            var lease = original.Lease with
            {
                Fence = fence, State = state, CurrentFencingGeneration = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1), RetirementReason = null,
                ProvisionIntent = original.Lease.ProvisionIntent with
                {
                    OptionsSchemaVersion = options.OptionsSchemaVersion,
                    OptionsSnapshot = JsonSerializer.SerializeToElement(options, json),
                    SelectionSnapshot = JsonSerializer.SerializeToElement(selection, json),
                    ProviderRequest = JsonSerializer.SerializeToElement(recovery, json)
                },
                ProvisionedResource = original.Lease.ProvisionedResource! with
                {
                    NegotiatedCapabilities = ImmutableHashSet.Create(
                        SandboxCapabilities.VmIsolation, SandboxCapabilities.WorkspacePersistentVolumeClaim),
                    ProviderBinding = new("agent-sandbox", "1.0.0", options.OptionsSchemaVersion,
                        options.OptionsRevision, JsonSerializer.SerializeToElement(options, json),
                        JsonSerializer.SerializeToElement(new { }))
                }
            };
            var transport = new DefaultSourceTransport(context);
            var client = new HttpClient(transport);
            var provider = new DefaultSandboxProvider(new(new("https://runtime.test:8443/"), "10.1.1.7", "pod", "service")
            {
                ReceiverConfigureEndpoint = new("https://runtime.test:8443/runtime/v1/configure")
            });
            var clock = new DefaultClock();
            var manager = new EnvironmentSandboxManager(projects, lifecycle, new RuntimeResumeBootstrapTests.SourceLeases(lease),
                provider, options, null!, EnvironmentEgressManagerTests.Options(), egress, timeProvider: clock);
            return new DefaultSourceFixture
            {
                Lease = lease, Transport = transport, Provider = provider, Client = client, Clock = clock,
                Reader = new(manager, new(client, new(new("https://orchestrator.test/"), new("https://broker.test/"))),
                    clock)
            };
        }

        public void Dispose()
        {
            Actor.Bearer.Invalidate();
            Client.Dispose();
        }
    }

    private sealed class DefaultSourceTransport(RuntimeOwnerContext context) : HttpMessageHandler
    {
        public RuntimeOwnerContext Context { get; set; } = context;
        public bool HealthAvailable { get; set; } = true;
        public int HealthReads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var health = request.RequestUri!.AbsolutePath == "/health/live";
            if (health)
            {
                HealthReads++;
                Assert.Null(request.Headers.Authorization);
            }
            else
            {
                Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("tenant", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            }
            Assert.True(request.Headers.CacheControl?.NoStore);
            var response = new HttpResponseMessage(health && !HealthAvailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = health
                    ? new StringContent("{\"status\":\"live\"}", Encoding.UTF8, "application/json")
                    : JsonContent.Create(Context)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class DefaultSandboxProvider(SandboxRuntimeHttpsEndpoint endpoint) : ISandboxProvider
    {
        public SandboxRuntimeHttpsEndpoint Endpoint { get; set; } = endpoint;
        public SandboxObservation Observation(ProviderResourceRef resource, long fence) =>
            new(resource, SandboxObservedState.Pending, fence, true, true, null,
                [new(SandboxStartupPhase.Scheduled, 1, DateTimeOffset.UtcNow),
                 new(SandboxStartupPhase.ImageReady, 1, DateTimeOffset.UtcNow, "sha256:" + new string('a', 64), 1),
                 new(SandboxStartupPhase.Started, 1, DateTimeOffset.UtcNow)]) { RuntimeEndpoint = Endpoint };
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(request.RequireRuntimeEndpoint);
            return Task.FromResult(Observation(request.Resource, request.FencingGeneration));
        }
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(SandboxPartialReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
