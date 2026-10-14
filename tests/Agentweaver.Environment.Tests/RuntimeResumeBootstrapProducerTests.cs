using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;

namespace Agentweaver.Environment.Tests;

public sealed partial class RuntimeResumeBootstrapTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("lost-host")]
    [InlineData("changed-live")]
    [InlineData("denied")]
    [InlineData("expired-after-probe")]
    public async Task CompleteEnvironmentProducerUsesSavedConfigurationAndFreshHostEvidenceOnReplay(string fault)
    {
        var request = Create();
        var context = request.Resume.RestoredWorkspace!;
        var fence = context.Workspace.EnvironmentFence;
        var binding = request.Registration.Binding;
        var selection = EnvironmentEgressManagerTests.Selection() with
        {
            ProjectId = binding.ProjectId, RunId = binding.RunId
        };
        var authorization = new ProjectAuthorizationContextResponse(1, binding.ActorIssuer, binding.ActorId,
            binding.TenantId, 1, binding.ProjectId, binding.RunId,
            [new(ProjectAuthorityResourceType.Project, binding.ProjectId,
                [new(ProjectAuthorizationPermission.ReadRunSelection, 1),
                 new(ProjectAuthorizationPermission.WriteProjects, 1)])]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
        var lifecycle = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var egress = EnvironmentEgressManagerTests.Manager(projects,
            new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(), lifecycle);
        var caller = new CurrentCallerRequest("actor-token", "tenant");
        await egress.ApplyAndVerifyAsync(caller, new(fence, 1, 0, "network-1"), CancellationToken.None);
        await egress.ApplyAndVerifyAsync(caller, new(fence, 2, 1, "network-2"), CancellationToken.None);
        var options = new AgentSandboxOptions(AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@" + binding.Image!.Digest, binding.Image.Platform,
            binding.Image.CompressedPullBytes, "kata-vm", "kata-qemu", "500m", "512Mi", 1, 1,
            new(30, 90, 30, 30, 30, 180));
        var optionsJson = JsonSerializer.SerializeToElement(options, JsonOptions);
        var fixture = RuntimeSuspendPlacementReleaseTests.Create();
        var recovery = JsonNode.Parse(fixture.Lease.ProvisionIntent.ProviderRequest.GetRawText())!;
        recovery["request"] = JsonSerializer.SerializeToNode(
            new SandboxProvisionApiRequest("workspace", 1, 8, context.Workspace.MountPath, false, 2, "placement"),
            JsonOptions);
        recovery["runtimeResume"] = JsonSerializer.SerializeToNode(
            request.Resume with { PhaseVersion = 2, RestoredWorkspace = null }, JsonOptions);
        var provisioned = fixture.Lease.ProvisionedResource! with
        {
            Resource = context.Placement.Resource, Endpoint = context.Placement.Endpoint,
            Placement = context.Placement.Placement,
            NegotiatedCapabilities = ImmutableHashSet.Create(
                SandboxCapabilities.VmIsolation, SandboxCapabilities.WorkspacePersistentVolumeClaim),
            ProviderBinding = new("agent-sandbox", "1.0.0", options.OptionsSchemaVersion,
                options.OptionsRevision, optionsJson, JsonSerializer.SerializeToElement(new { }))
        };
        var lease = fixture.Lease with
        {
            Fence = fence, ResourceGeneration = context.Placement.Resource.Generation,
            ProviderFencingGeneration = binding.EnvironmentProviderFencingGeneration,
            CurrentFencingGeneration = binding.EnvironmentCurrentFencingGeneration,
            OperationId = Guid.NewGuid(), State = SandboxLeaseState.Active, IsCurrent = true,
            LeaseRevision = binding.EnvironmentLeaseRevision, LeaseExpiresAt = request.Registration.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1), RetirementReason = null,
            ProvisionedResource = provisioned,
            ProvisionIntent = fixture.Lease.ProvisionIntent with
            {
                OptionsSchemaVersion = options.OptionsSchemaVersion, OptionsSnapshot = optionsJson,
                SelectionSnapshot = JsonSerializer.SerializeToElement(selection, JsonOptions),
                ProviderRequest = JsonSerializer.SerializeToElement(recovery, JsonOptions)
            }
        };
        var actor = Actor();
        var receipt = RuntimeResumeBootstrapPostgresTests.Receipt(request);
        using var handler = new ProducerHandler(request, receipt, actor) { Denied = fault == "denied" };
        using var http = new HttpClient(handler);
        var deliveryOptions = new EnvironmentRuntimeBootstrapDeliveryOptions(
            new("https://core.test/"), new("https://broker.test/")) { EventsOwnerAddress = new("https://events.test/") };
        var sandboxes = new EnvironmentSandboxManager(projects, lifecycle, new SourceLeases(lease),
            new ObservedResumeSandbox(context.Placement.RuntimeEndpoint!, binding.Image), options, null!,
            EnvironmentEgressManagerTests.Options(), egress);
        var profiles = new EnvironmentRuntimeBootstrapProfileRegistry(
        [
            new(fence.Owner, new("profile",
                request.Resume.SourceRelease.HostReceipt.Registration.Binding.ConfigureEndpoint,
                request.Resume.SourceRelease.HostReceipt.Registration.Binding.ObservationEndpoint),
                new(ProviderSeam.Sandbox, "agent-sandbox",
                    request.Resume.SourceRelease.HostReceipt.Registration.Binding.PlacementUid, 1))
        ]);
        var placements = new EnvironmentRuntimePlacementReader(sandboxes,
            new EnvironmentRuntimeOwnerContextClient(http, deliveryOptions), TimeProvider.System);
        var store = new ControlledBootstrapStore();
        var delivery = new EnvironmentRuntimeBootstrapDelivery(
            http, deliveryOptions, placements, profiles, TimeProvider.System, store);
        if (fault == "denied")
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                delivery.BootstrapResumeAsync(request, actor, CancellationToken.None));
            Assert.Equal(0, handler.BrokerCalls);
            Assert.Null(store.Saved);
            return;
        }
        var first = await delivery.BootstrapResumeAsync(request, actor, CancellationToken.None);
        Assert.True(ProducerJsonEqual(receipt, first));
        Assert.True(ProducerJsonEqual(receipt, store.Saved));
        Assert.Equal(1, handler.ConfigurationReads);
        Assert.Equal(1, handler.BrokerCalls);
        Assert.Equal(1, handler.ActivateCalls);
        handler.Missing = fault == "lost-host";
        handler.ChangedLive = fault == "changed-live";
        handler.ExpireAfterProbe = fault == "expired-after-probe";
        if (fault == "exact")
        {
            var replay = await delivery.BootstrapResumeAsync(request, actor, CancellationToken.None);
            Assert.True(ProducerJsonEqual(first, replay));
            Assert.Equal(2, handler.ActivateCalls);
        }
        else if (fault == "lost-host")
        {
            var loss = await Assert.ThrowsAsync<RuntimeResumeBootstrapStateLostException>(() =>
                delivery.BootstrapResumeAsync(request, actor, CancellationToken.None));
            loss.Receipt.ValidateFor(request);
            Assert.Equal(1, handler.ActivateCalls);
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                delivery.BootstrapResumeAsync(request, actor, CancellationToken.None));
        }
        Assert.Equal(1, handler.ConfigurationReads);
        Assert.Equal(1, handler.BrokerCalls);
        Assert.True(ProducerJsonEqual(first, store.Saved));
    }

    private static bool ProducerJsonEqual<T>(T left, T right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));

    private sealed class ControlledBootstrapStore : IEnvironmentRuntimeResumeBootstrapStore
    {
        public RuntimeResumeBootstrapReceipt? Saved { get; private set; }
        public async Task<RuntimeResumeBootstrapReceipt> ExecuteAsync(RuntimeResumeBootstrapRequest request,
            Func<CancellationToken, Task> requireCurrent,
            Func<RuntimeResumeBootstrapReceipt?, CancellationToken, Task<RuntimeResumeBootstrapReceipt>> reconcile,
            CancellationToken cancellationToken)
        {
            await requireCurrent(cancellationToken);
            var result = await reconcile(Saved, cancellationToken);
            await requireCurrent(cancellationToken);
            result.ValidateFor(request);
            Saved = result;
            await requireCurrent(cancellationToken);
            return result;
        }
    }

    private sealed class ObservedResumeSandbox(
        SandboxRuntimeHttpsEndpoint endpoint, SandboxImageIdentity image) : ISandboxProvider
    {
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxObservation(request.Resource, SandboxObservedState.Pending,
                request.FencingGeneration, true, true, null,
                [new(SandboxStartupPhase.Scheduled, 1, DateTimeOffset.UtcNow),
                 new(SandboxStartupPhase.ImageReady, 1, DateTimeOffset.UtcNow, image.Digest, image.CompressedPullBytes),
                 new(SandboxStartupPhase.Started, 1, DateTimeOffset.UtcNow)]) { RuntimeEndpoint = endpoint });
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProducerHandler(
        RuntimeResumeBootstrapRequest bootstrap, RuntimeResumeBootstrapReceipt receipt,
        RuntimeActorAuthorization actor) : HttpMessageHandler
    {
        public int BrokerCalls { get; private set; }
        public int ConfigurationReads { get; private set; }
        public int ActivateCalls { get; private set; }
        public bool Denied { get; init; }
        public bool Missing { get; set; }
        public bool ChangedLive { get; set; }
        public bool ExpireAfterProbe { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            Assert.True(request.Headers.CacheControl?.NoStore);
            object body;
            if (path == "/internal/runtime/resume/require-bootstrap-current")
            {
                body = bootstrap;
            }
            else if (path == "/internal/runtime/registrations/" + bootstrap.Registration.RuntimeInstanceId)
            {
                body = bootstrap.Registration;
            }
            else if (path.EndsWith("/runtime-owner-context", StringComparison.Ordinal))
            {
                var binding = bootstrap.Registration.Binding;
                body = new RuntimeOwnerContext(1, binding.ActorIssuer, binding.ActorId, binding.TenantId,
                    binding.ProjectId, binding.RunId, binding.SessionId, binding.AgentId,
                    binding.ModelSelectionReference!, binding.TurnId, binding.ProjectRevision,
                    binding.ProjectConfigurationRevision, binding.PlatformRuntimeRevision, binding.ContextRevision,
                    binding.AcceptedSelectionHash, binding.ExecutionFence, 1, 1, 1);
            }
            else if (path == "/internal/runtime/resume/configuration")
            {
                ConfigurationReads++;
                Assert.True(ProducerJsonEqual(bootstrap,
                    await request.Content!.ReadFromJsonAsync<RuntimeResumeBootstrapRequest>(JsonOptions, token)));
                body = new SessionMaterialReadResult(
                    bootstrap.Resume.SourceRelease.HostReceipt.ConfigurationAcknowledgment!, "{}"u8.ToArray());
            }
            else if (path == "/internal/runtime/bootstrap/request")
            {
                BrokerCalls++;
                Assert.Equal(new RuntimeBootstrapRequest(bootstrap.Registration.RuntimeInstanceId,
                    bootstrap.BootstrapOperationId, receipt.Delivery.ConfigurationHash),
                    await request.Content!.ReadFromJsonAsync<RuntimeBootstrapRequest>(JsonOptions, token));
                body = receipt.Delivery;
            }
            else if (path == "/runtime/v1/configure/resume-state")
            {
                Assert.Equal(
                    bootstrap.Resume.RestoredWorkspace!.Placement.RuntimeEndpoint!.ReceiverConfigureEndpoint!
                        .GetLeftPart(UriPartial.Authority),
                    request.RequestUri.GetLeftPart(UriPartial.Authority));
                Assert.Contains(request.Options, option =>
                    option.Value is SandboxRuntimeHttpsEndpoint endpoint &&
                    endpoint == bootstrap.Resume.RestoredWorkspace!.Placement.RuntimeEndpoint);
                body = new RuntimeResumeHostStateReceipt(1, bootstrap.Resume.OperationId,
                    bootstrap.Resume.SourceManifest.ManifestId, bootstrap.Registration, bootstrap.ResumeAttemptId,
                    Missing ? RuntimeResumeHostState.Missing :
                        ActivateCalls == 0 ? RuntimeResumeHostState.BootstrapPresent : RuntimeResumeHostState.Activated);
                if (ExpireAfterProbe)
                    actor.Bearer.Invalidate();
            }
            else if (path == "/runtime/v1/configure/activate")
            {
                ActivateCalls++;
                Assert.Equal(
                    bootstrap.Resume.RestoredWorkspace!.Placement.RuntimeEndpoint!.ReceiverConfigureEndpoint!
                        .GetLeftPart(UriPartial.Authority),
                    request.RequestUri.GetLeftPart(UriPartial.Authority));
                Assert.Contains(request.Options, option =>
                    option.Value is SandboxRuntimeHttpsEndpoint endpoint &&
                    endpoint == bootstrap.Resume.RestoredWorkspace!.Placement.RuntimeEndpoint);
                var configuration = await request.Content!.ReadFromJsonAsync<RuntimeHostConfigureRequest>(JsonOptions, token);
                Assert.NotNull(configuration);
                Assert.Equal(JsonValueKind.Null, configuration.Configuration.ValueKind);
                Assert.True(ProducerJsonEqual(bootstrap, configuration.ResumeBootstrap));
                body = ChangedLive ? receipt.HostReceipt with
                {
                    SourceGrant = receipt.HostReceipt.SourceGrant with { GrantId = Guid.NewGuid() }
                } : receipt.HostReceipt;
            }
            else
            {
                throw new InvalidOperationException("Unexpected producer request: " + path);
            }
            var response = new HttpResponseMessage(Denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                RequestMessage = request, Content = JsonContent.Create(body, options: JsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        }
    }
}
