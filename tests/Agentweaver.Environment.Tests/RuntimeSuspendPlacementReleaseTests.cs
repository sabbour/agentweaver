using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed class RuntimeSuspendPlacementReleaseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void FlushDetachmentAndItsExactReplayRetainTheOriginalLeaseAndSavedEvidence()
    {
        var fixture = Create();
        var detach = EnvironmentSandboxManager.ResolveWorkspaceDetachment(
            fixture.Lease.Fence, fixture.Lease, fixture.Volume, fixture.Evidence);
        Assert.Equal(5, detach.TransitionRevision);
        Assert.Equal(8, detach.DataGeneration);
        Assert.Equal(fixture.Request, detach.SuspendRelease);
        var replay = EnvironmentSandboxManager.ResolveWorkspaceDetachment(
            fixture.Lease.Fence, fixture.Lease,
            VolumeState(fixture.Volume, revision: 6, phase: EnvironmentWorkspaceVolumeState.Bound),
            fixture.Evidence);
        Assert.Equal(detach, replay);
        Assert.Equal(7, EnvironmentSandboxManager.ReadProvisionRequest(fixture.Lease).DataGeneration);
        Assert.Equal(2, fixture.Lease.CurrentFencingGeneration);
    }

    [Fact]
    public void OrdinaryAttachmentAndItsExactDetachReplayDoNotInventASuspendReceipt()
    {
        var fixture = Create();
        var attached = VolumeState(fixture.Volume, revision: 4, data: 7);
        var detach = EnvironmentSandboxManager.ResolveWorkspaceDetachment(
            fixture.Lease.Fence, fixture.Lease, attached, null);
        Assert.Equal((4L, 7L, null), detach);
        var replay = EnvironmentSandboxManager.ResolveWorkspaceDetachment(
            fixture.Lease.Fence, fixture.Lease,
            VolumeState(attached, revision: 5, phase: EnvironmentWorkspaceVolumeState.Bound), null);
        Assert.Equal(detach, replay);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("transition")]
    [InlineData("detach-ahead")]
    [InlineData("missing-evidence")]
    [InlineData("missing-host")]
    [InlineData("placement")]
    [InlineData("provider-fence")]
    [InlineData("current-fence")]
    [InlineData("storage")]
    public void ChangedCountersOrForeignEvidenceCannotReleaseAnOldAttachment(string fault)
    {
        var fixture = Create();
        var volume = fault switch
        {
            "data" => VolumeState(fixture.Volume, data: 9),
            "transition" => VolumeState(fixture.Volume, revision: 6),
            "detach-ahead" => VolumeState(
                fixture.Volume, revision: 7, phase: EnvironmentWorkspaceVolumeState.Bound),
            "storage" => VolumeState(
                fixture.Volume, resource: fixture.Volume.Resource! with { ResourceId = "foreign-claim" }),
            _ => fixture.Volume
        };
        var host = fixture.Request.HostReceipt;
        var binding = host.Registration.Binding;
        binding = fault switch
        {
            "placement" => binding with { PlacementUid = "foreign-placement" },
            "provider-fence" => binding with { EnvironmentProviderFencingGeneration = 2 },
            "current-fence" => binding with { EnvironmentCurrentFencingGeneration = 3 },
            _ => binding
        };
        var evidence = fixture.Evidence with
        {
            RuntimeHostReceipt = fault == "missing-host" ? null :
                JsonSerializer.SerializeToElement(host with
                {
                    Registration = host.Registration with { Binding = binding }
                }, JsonOptions)
        };
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ResolveWorkspaceDetachment(
                fixture.Lease.Fence, fixture.Lease, volume,
                fault == "missing-evidence" ? null : evidence));
    }

    [Fact]
    public async Task ReleaseTransportUsesTheOriginalActorAndRequiresReleasedPlacementAndExactDetach()
    {
        var fixture = Create();
        using var handler = new ResponseHandler(Receipt(fixture));
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        var receipt = await client.ReleasePlacementAsync(fixture.Request,
            new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant"),
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, receipt.State);
        Assert.Equal(6, receipt.WorkspaceTransitionRevision);
        Assert.Equal("/internal/runtime/suspend/release-placement", handler.Path);
        Assert.Equal("actor-token", handler.Token);
        Assert.Equal("tenant", handler.Tenant);
        Assert.True(handler.NoStore);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("data")]
    [InlineData("transition")]
    [InlineData("placement")]
    [InlineData("manifest")]
    [InlineData("no-store")]
    public async Task ReleaseTransportRejectsIncompleteOrChangedPlacementEvidence(string fault)
    {
        var fixture = Create();
        var receipt = Receipt(fixture);
        receipt = fault switch
        {
            "pending" => receipt with { State = SandboxLeaseState.Releasing },
            "data" => receipt with { WorkspaceDataGeneration = 9 },
            "transition" => receipt with { WorkspaceTransitionRevision = 7 },
            "placement" => receipt with { SandboxResource = receipt.SandboxResource with { ResourceId = "foreign" } },
            "manifest" => receipt with { ManifestId = Guid.NewGuid() },
            _ => receipt
        };
        using var handler = new ResponseHandler(receipt) { NoStoreResponse = fault != "no-store" };
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.ReleasePlacementAsync(fixture.Request,
                new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant"),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("denied")]
    [InlineData("changed")]
    [InlineData("expired-before")]
    [InlineData("expired-after")]
    [InlineData("wrong-actor")]
    [InlineData("missing-expiry")]
    public async Task ReleaseAuthorityRequiresFreshAuthenticatedActorAndTheExactCoreEcho(string fault)
    {
        var fixture = Create();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer actor-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant";
        var expires = fault == "missing-expiry" ? (DateTimeOffset?)null :
            time.GetUtcNow().AddMinutes(fault == "expired-before" ? -1 : 5);
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new AuthenticationService(expires))
            .BuildServiceProvider();
        context.RequestServices = services;
        using var handler = new ResponseHandler(
            fault == "changed" ? fixture.Request with { FlushTransitionRevision = 6 } : fixture.Request)
        {
            StatusCode = fault == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
            BeforeResponse = fault == "expired-after" ? () => time.Advance(TimeSpan.FromMinutes(6)) : null
        };
        using var http = new HttpClient(handler);
        var authority = new EnvironmentRuntimeSuspendReleaseAuthority(
            http, new(new("https://core.test/"), new("https://broker.test/")),
            new HttpContextAccessor { HttpContext = context }, time);
        var caller = new CurrentCallerRequest(fault == "wrong-actor" ? "foreign-token" : "actor-token", "tenant");
        if (fault == "exact")
        {
            await authority.RequireCurrentAsync(caller, fixture.Request, CancellationToken.None);
            Assert.Equal("/internal/runtime/suspend/require-release-current", handler.Path);
            Assert.Equal("actor-token", handler.Token);
            Assert.True(handler.NoStore);
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                authority.RequireCurrentAsync(caller, fixture.Request, CancellationToken.None));
            if (fault is "expired-before" or "wrong-actor" or "missing-expiry")
                Assert.Null(handler.Path);
        }
    }

    private static RuntimeSuspendPlacementReleaseReceipt Receipt(Fixture fixture) =>
        new(1, fixture.Request.HostReceipt.OperationId, fixture.Request.HostReceipt.ManifestId,
            fixture.Lease.Fence, fixture.Lease.ProvisionedResource!.Resource,
            fixture.Lease.ProviderFencingGeneration, SandboxLeaseState.Released,
            fixture.Request.HostReceipt.WorkspaceFlush!.Volume, fixture.Volume.Resource!, 8, 6);

    private static EnvironmentWorkspaceVolumeSnapshot VolumeState(
        EnvironmentWorkspaceVolumeSnapshot volume, long? revision = null, long? data = null,
        EnvironmentWorkspaceVolumeState? phase = null, ProviderResourceRef? resource = null) =>
        new(volume.EnvironmentFence, volume.VolumeId, revision ?? volume.TransitionRevision,
            volume.ResourceGeneration, data ?? volume.DataGeneration, phase ?? volume.Phase,
            volume.LastOperation, volume.Specification, resource ?? volume.Resource, volume.ProviderBinding);

    internal static Fixture Create()
    {
        var now = DateTimeOffset.UtcNow;
        var fence = new EnvironmentGenerationFence(new("tenant", "project", "run", "environment"), 1);
        var operationId = Guid.NewGuid();
        var sandbox = SandboxResourceIdentity.CreatePlannedReference(
            "agent-sandbox", "sandbox-options", fence, 1, 1, operationId);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "claim-1", 1);
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(
            "project", "environment", "run", new("project", "workspace", 1),
            sandbox, storage, fence, 7, "/workspace/agentweaver/workspace", false,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        var attachment = new SandboxWorkspaceAttachment(negotiation, JsonSerializer.SerializeToElement(new { }));
        var recovery = JsonSerializer.SerializeToElement(new
        {
            contractVersion = 1,
            request = new SandboxProvisionApiRequest(
                "workspace", 1, 7, "/workspace/agentweaver/workspace", false, 1, "provision-once"),
            workspace = attachment,
            egressSelectorLabels = ImmutableDictionary<string, string>.Empty,
            workspaceAttachmentTransitionRevision = 4
        }, JsonOptions);
        var empty = JsonSerializer.SerializeToElement(new { });
        var providerBinding = new SandboxProviderBindingSnapshot(
            "agent-sandbox", "1.0.0", 1, "sandbox-options", empty, empty);
        var lease = new SandboxLeaseSnapshot(
            fence, 1, 1, 2, operationId, SandboxLeaseState.Releasing,
            new("agent-sandbox", "1.0.0", 1, "sandbox-options", empty, empty, recovery),
            new(sandbox, new(Guid.NewGuid()), new("placement"), ImmutableHashSet<string>.Empty, [], providerBinding),
            SandboxRetirementReason.AuthorizedAbandon, null, "actor", 1, "release-once", true, now)
        {
            LeaseRevision = 2, LeaseExpiresAt = now.AddMinutes(5)
        };
        var registration = new RuntimeRegistration(Guid.NewGuid(), 1,
            new("https://issuer.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run", "session", "agent", "turn",
                1, 1, 1, "context", new string('a', 64), 1, "environment", sandbox.ResourceId, 1, "profile",
                new("https://runtime.test/runtime/v1/configure"), new("https://core.test/observe"))
            {
                EnvironmentLifecycleGeneration = 1, EnvironmentCurrentFencingGeneration = 1,
                EnvironmentProviderFencingGeneration = 1, EnvironmentLeaseRevision = 1,
                PlacementProviderId = "agent-sandbox", ModelSelectionReference = "model:selected",
                ModelSourceMode = ModelSourceMode.HostedCopilot
            }, RuntimeRegistrationState.Active, now.AddMinutes(5));
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding), "1.0.18", "1.0.97",
            "model:selected", "controlled-model", new string('a', 64), 1m, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash, 1);
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source, Guid.NewGuid(), new string('a', 64), new string('b', 64), 2);
        var observation = new RuntimeNativeTurnObservation(
            source, Guid.NewGuid().ToString("D"), "native-turn", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now, "native-cursor", new string('c', 64), null);
        var native = new RuntimeNativeTurnRecordedReceipt(
            admission, observation, RuntimeNativeTurnContract.ObservationHash(observation), 3);
        var manifestId = Guid.NewGuid();
        var cache = new SessionMaterialAcknowledgment(1, new("project", "run", "session"), manifestId, 9,
            new(new ObjectKey(manifestId.ToString("N")), SessionMaterialValidation.Purpose(SessionMaterialKind.SdkCache), 1)
            {
                Material = new(1, SessionMaterialKind.SdkCache, "tenant", new string('d', 64),
                    registration.RuntimeInstanceId, 1, 1, registration.Binding.AcceptedSelectionHash,
                    source.SdkVersion, source.RuntimeVersion, source.ModelSelectionReference, source.ModelId)
            });
        var flush = new WorkspaceVolumeFlushReceipt(
            1, fence, negotiation.Volume, storage, 7, 8, "flush-once", new string('e', 64), null);
        var host = new RuntimeHostSuspendReceipt(
            1, Guid.NewGuid(), manifestId, registration,
            new(Guid.NewGuid(), registration.RuntimeInstanceId, 1, 1, "https://issuer.test/",
                RuntimeCredentialPurpose.Observe, registration.Binding.ObservationEndpoint,
                RuntimeCredentialState.Active, new string('f', 64), now.AddMinutes(5), now),
            native, cache) { WorkspaceFlush = flush };
        var request = new RuntimeSuspendPlacementReleaseRequest(host, 5).Validate();
        var volume = new EnvironmentWorkspaceVolumeSnapshot(
            fence, "workspace", 5, 1, 8, EnvironmentWorkspaceVolumeState.Attached,
            EnvironmentWorkspaceVolumeOperation.Flush, empty, storage,
            new("azure-files", "1.0.0", 1, "options-1", empty, empty));
        return new(request, lease, volume, new(flush, JsonSerializer.SerializeToElement(host, JsonOptions)));
    }

    internal sealed record Fixture(RuntimeSuspendPlacementReleaseRequest Request, SandboxLeaseSnapshot Lease,
        EnvironmentWorkspaceVolumeSnapshot Volume, WorkspaceVolumeFlushEvidence Evidence);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class AuthenticationService(DateTimeOffset? expiresAt) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity("test")),
                new AuthenticationProperties { ExpiresUtc = expiresAt }, "test")));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal,
            AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private sealed class ResponseHandler(object body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Token { get; private set; }
        public string? Tenant { get; private set; }
        public bool NoStore { get; private set; }
        public bool NoStoreResponse { get; init; } = true;
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public Action? BeforeResponse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.AbsolutePath;
            Token = request.Headers.Authorization?.Parameter;
            Tenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
            NoStore = request.Headers.CacheControl?.NoStore == true;
            BeforeResponse?.Invoke();
            var response = new HttpResponseMessage(StatusCode)
            {
                RequestMessage = request, Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = NoStoreResponse };
            return Task.FromResult(response);
        }
    }
}
