using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed class RuntimeWorkspaceResumeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task TwoStageTransportRetainsTheActorAndExactNewPlacementWithoutRuntimeRegistration()
    {
        var request = Request();
        var workspace = Workspace(request);
        using var handler = new ResponseHandler(Placement(request, workspace));
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceResumeHttpClient(http, new("https://environment.test/"));
        var actor = Actor();
        var prepared = await client.PrepareAsync(request, actor, CancellationToken.None);
        Assert.Equal(2, prepared.Workspace.Placement.LifecycleGeneration);
        Assert.Equal(request.SourceManifest.WorkspaceResource, prepared.Workspace.Workspace.StorageResource);
        Assert.Equal(8, prepared.Workspace.Workspace.DataGeneration);
        Assert.Equal("/internal/runtime/resume/prepare", handler.Path);
        Assert.Equal("actor-token", handler.Token);
        Assert.Equal("tenant", handler.Tenant);
        Assert.True(handler.NoStore);
        Assert.Null(prepared.Workspace.Placement.RuntimeOwnerContext);
        var verification = request with { PhaseVersion = 3, RestoredWorkspace = prepared.Workspace };
        handler.Body = Verified(verification);
        var verified = await client.VerifyAsync(verification, actor, CancellationToken.None);
        Assert.Equal(2, verified.NetworkIntentGeneration);
        Assert.Equal(request.SourceManifest.WorkspaceTreeSha256, verified.HostReceipt.ActualTreeSha256);
        Assert.Equal("/internal/runtime/resume/verify", handler.Path);
        Assert.Equal(3, handler.RequestBody!.RootElement.GetProperty("phaseVersion").GetInt64());
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("hash")]
    [InlineData("data")]
    [InlineData("cache")]
    public void ChangedSourceEvidenceIsRejectedBeforeAnOwnerCall(string fault)
    {
        var request = Request();
        var manifest = request.SourceManifest;
        manifest = fault switch
        {
            "manifest" => manifest with { ManifestId = Guid.NewGuid() },
            "hash" => manifest with { WorkspaceTreeSha256 = new string('a', 64) },
            "data" => manifest with { WorkspaceDataGeneration = 9 },
            _ => manifest with { CacheAcknowledgment = manifest.CacheAcknowledgment! with { Position = 10 } }
        };
        Assert.Equal("runtime_resume_request_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                (request with { SourceManifest = manifest }).Validate()).Code);
    }

    [Theory]
    [InlineData("fence")]
    [InlineData("storage")]
    [InlineData("data")]
    [InlineData("old-placement")]
    [InlineData("transition")]
    [InlineData("read-only")]
    public void ANewLifecycleCounterCannotStandInForAnExactRetainedRemount(string fault)
    {
        var request = Request();
        var context = Workspace(request);
        var workspace = context.Workspace;
        var sourceBinding = request.SourceRelease.HostReceipt.Registration.Binding;
        workspace = fault switch
        {
            "fence" => workspace with { EnvironmentFence = request.SourceManifest.EnvironmentFence! },
            "storage" => workspace with { StorageResource = workspace.StorageResource with { ResourceId = "foreign" } },
            "data" => workspace with { DataGeneration = 9 },
            "old-placement" => workspace with
            {
                SandboxResource = new(ProviderSeam.Sandbox, sourceBinding.PlacementProviderId!,
                    sourceBinding.PlacementUid, sourceBinding.PlacementGeneration)
            },
            "read-only" => workspace with { ReadOnly = true },
            _ => workspace
        };
        context = context with { Workspace = workspace, TransitionRevision = fault == "transition" ? 6 : 7 };
        Assert.Throws<RuntimeAuthorizationException>(() => Placement(request, context).ValidateFor(request));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("manifest")]
    [InlineData("network")]
    [InlineData("missing-host")]
    [InlineData("no-store")]
    public async Task VerifyRejectsChangedNativeProofAndUnverifiedNetwork(string fault)
    {
        var source = Request();
        var request = source with { PhaseVersion = 3, RestoredWorkspace = Workspace(source) };
        var response = Verified(request);
        response = fault switch
        {
            "hash" => response with { HostReceipt = response.HostReceipt with { ActualTreeSha256 = new string('b', 64) } },
            "manifest" => response with { HostReceipt = response.HostReceipt with { SourceManifestId = Guid.NewGuid() } },
            "network" => response with { NetworkIntentGeneration = 1 },
            "missing-host" => response with { HostReceipt = null! },
            _ => response
        };
        using var handler = new ResponseHandler(response) { NoStoreResponse = fault != "no-store" };
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceResumeHttpClient(http, new("https://environment.test/"));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.VerifyAsync(request, Actor(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWrongStageIsRejectedWithoutSendingARequest(bool prepareWithWorkspace)
    {
        var source = Request();
        var request = prepareWithWorkspace ? source with { RestoredWorkspace = Workspace(source) } : source;
        using var handler = new ResponseHandler(new { });
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceResumeHttpClient(http, new("https://environment.test/"));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => prepareWithWorkspace
            ? client.PrepareAsync(request, Actor(), CancellationToken.None)
            : (Task)client.VerifyAsync(request, Actor(), CancellationToken.None));
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task ActorExpiryDuringAnOwnerWaitRejectsAnOtherwiseExactReceipt()
    {
        var request = Request();
        var actor = Actor();
        using var handler = new ResponseHandler(Placement(request, Workspace(request)))
        {
            BeforeResponse = actor.Bearer.Invalidate
        };
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceResumeHttpClient(http, new("https://environment.test/"));
        Assert.Equal("runtime_resume_actor_expired",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                client.PrepareAsync(request, actor, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("position")]
    public async Task MissingOrChangedConfigurationIsRejectedBeforeAnyResumeOwnerOrEnvironmentEffect(string fault)
    {
        var source = Request();
        var host = source.SourceRelease.HostReceipt;
        var acknowledgment = host.ConfigurationAcknowledgment!;
        host = host with
        {
            ConfigurationAcknowledgment = fault == "missing" ? null : acknowledgment with
            {
                Position = fault == "position" ? host.CacheAcknowledgment.Position : acknowledgment.Position,
                Reference = fault == "hash" ? acknowledgment.Reference with
                {
                    Material = acknowledgment.Reference.Material! with { Sha256 = new string('a', 64) }
                } : acknowledgment.Reference
            }
        };
        var request = source with { SourceRelease = source.SourceRelease with { HostReceipt = host } };
        using var handler = new ResponseHandler(new { });
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceResumeHttpClient(http, new("https://environment.test/"));
        var manager = new EnvironmentRuntimeWorkspaceResumeManager(null!, null!, null!, null!, null!, null!, null!);
        var expected = fault == "missing" ? "runtime_resume_configuration_missing" : "runtime_resume_configuration_changed";
        Assert.Equal(expected, (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.PrepareAsync(request, Actor(), CancellationToken.None))).Code);
        Assert.Equal(expected, (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            manager.PrepareAsync(request, Actor(), CancellationToken.None))).Code);
        Assert.Null(handler.Path);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("denied")]
    [InlineData("changed-echo")]
    [InlineData("changed-hash")]
    [InlineData("expired-after-host")]
    public async Task NativeVerificationRequiresCurrentCoreAuthorityBeforeAndAfterTheRead(string fault)
    {
        var source = Request();
        var request = source with { PhaseVersion = 3, RestoredWorkspace = Workspace(source) };
        var actor = Actor();
        using var handler = new ResumeDeliveryHandler(request, fault, actor);
        using var http = new HttpClient(handler);
        var delivery = new EnvironmentRuntimeBootstrapDelivery(http,
            new(new("https://core.test/"), new("https://broker.test/")), null!,
            new EnvironmentRuntimeBootstrapProfileRegistry([]), TimeProvider.System);
        if (fault == "exact")
        {
            var receipt = await delivery.VerifyResumeWorkspaceAsync(request, actor, CancellationToken.None);
            Assert.Equal(request.SourceManifest.WorkspaceTreeSha256, receipt.ActualTreeSha256);
            Assert.Equal(2, handler.AuthorityCalls);
            Assert.Equal(1, handler.HostCalls);
            Assert.Equal("https://sandbox-2.agentweaver.svc.cluster.local:8443/runtime/v1/workspace/resume",
                handler.HostAddress);
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                delivery.VerifyResumeWorkspaceAsync(request, actor, CancellationToken.None));
            if (fault is "denied" or "changed-echo")
                Assert.Equal(0, handler.HostCalls);
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.1.1.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    [InlineData("not-an-ip")]
    public void RuntimeEndpointRejectsNonPodDestinations(string address) =>
        Assert.Throws<ArgumentException>(() =>
            (Workspace(Request()).Placement.RuntimeEndpoint! with { PodAddress = address }).Validate());

    internal static RuntimeWorkspaceResumeRequest Request()
    {
        var fixture = RuntimeSuspendPlacementReleaseTests.Create();
        var host = WithConfigurationAcknowledgment(fixture.Request.HostReceipt);
        var manifest = new SessionSuspendResumeManifest(1, host.ManifestId, new("project", "run", "session"),
            SessionSuspendResumeManifestState.Suspended, host.WorkspaceFlush!.EnvironmentFence, 1,
            host.CacheAcknowledgment, new(new("project", "run", "session"), "coordinator-execution",
                "checkpoint", 1, "plan", 1), 9, host.WorkspaceFlush.Volume, host.WorkspaceFlush.Resource,
            8, host.WorkspaceFlush.TreeSha256, null, null, null, 1, []);
        return new RuntimeWorkspaceResumeRequest(1, Guid.NewGuid(), 2, manifest,
            fixture.Request with { HostReceipt = host }).Validate();
    }

    internal static RuntimeHostSuspendReceipt WithConfigurationAcknowledgment(RuntimeHostSuspendReceipt host)
    {
        var hash = RuntimeContractValidation.Hash("{}"u8);
        var reference = new SessionObjectReference(
            new ObjectKey("private/runtime/configuration"),
            SessionMaterialValidation.Purpose(SessionMaterialKind.RuntimeConfiguration), 2)
        {
            Material = host.CacheAcknowledgment.Reference.Material! with
            {
                Kind = SessionMaterialKind.RuntimeConfiguration, Sha256 = hash
            }
        };
        return host with
        {
            SourceGrant = host.SourceGrant with { ConfigurationHash = hash },
            ConfigurationAcknowledgment = new(1, host.CacheAcknowledgment.Identity, Guid.NewGuid(), 8, reference)
        };
    }

    internal static EnvironmentRuntimeWorkspaceContext Workspace(RuntimeWorkspaceResumeRequest request)
    {
        var fence = new EnvironmentGenerationFence(request.SourceManifest.EnvironmentFence!.Owner, 2);
        var resource = new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", "claim-2", 2);
        var endpoint = new SandboxRuntimeHttpsEndpoint(
            new("https://sandbox-2.agentweaver.svc.cluster.local:8443/"), "10.2.3.4", "pod-2", "service-2");
        var placement = new EnvironmentRuntimeBootstrapContext(
            1, "tenant", "project", "run", "environment", 2, 3, 3, 1, DateTimeOffset.UtcNow.AddMinutes(5),
            resource, new(Guid.NewGuid()), new("cluster"), "profile",
            new(endpoint.Address, "runtime/v1/configure"), new("https://core.test/observe"))
        {
            RuntimeEndpoint = endpoint
        };
        return new(1, placement, new("project", "environment", "run", request.SourceManifest.WorkspaceVolume!,
            resource, request.SourceManifest.WorkspaceResource!, fence, 8, "/workspace/agentweaver/workspace",
            false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim), 7);
    }

    private static RuntimeWorkspaceResumePlacementReceipt Placement(
        RuntimeWorkspaceResumeRequest request, EnvironmentRuntimeWorkspaceContext workspace) =>
        new(1, request.OperationId, request.SourceManifest.ManifestId, workspace);

    private static RuntimeWorkspaceResumeVerificationReceipt Verified(RuntimeWorkspaceResumeRequest request) =>
        new(1, request.OperationId, request.SourceManifest.ManifestId, request.RestoredWorkspace!,
            Host(request), 2);

    private static RuntimeHostWorkspaceResumeReceipt Host(RuntimeWorkspaceResumeRequest request) =>
        new(1, request.OperationId, request.SourceManifest.ManifestId, request.RestoredWorkspace!,
            request.SourceManifest.WorkspaceTreeSha256!);

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant");

    private sealed class ResumeDeliveryHandler(
        RuntimeWorkspaceResumeRequest request, string fault, RuntimeActorAuthorization actor) : HttpMessageHandler
    {
        public int AuthorityCalls { get; private set; }
        public int HostCalls { get; private set; }
        public string? HostAddress { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Assert.Equal("actor-token", message.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", message.Headers.GetValues("X-Agentweaver-Tenant").Single());
            Assert.True(message.Headers.CacheControl?.NoStore);
            var host = message.RequestUri!.AbsolutePath == "/runtime/v1/workspace/resume";
            object body;
            if (host)
            {
                HostCalls++;
                HostAddress = message.RequestUri.AbsoluteUri;
                body = fault == "changed-hash" ? Host(request) with { ActualTreeSha256 = new string('b', 64) } :
                    Host(request);
                if (fault == "expired-after-host")
                    actor.Bearer.Invalidate();
            }
            else
            {
                Assert.Equal("/internal/runtime/resume/require-current", message.RequestUri.AbsolutePath);
                AuthorityCalls++;
                body = fault == "changed-echo" ? request with { PhaseVersion = 4 } : request;
            }
            var response = new HttpResponseMessage(fault == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                RequestMessage = message, Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class ResponseHandler(object body) : HttpMessageHandler
    {
        public object Body { get; set; } = body;
        public bool NoStoreResponse { get; init; } = true;
        public Action? BeforeResponse { get; init; }
        public string? Path { get; private set; }
        public string? Token { get; private set; }
        public string? Tenant { get; private set; }
        public bool NoStore { get; private set; }
        public JsonDocument? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Token = request.Headers.Authorization?.Parameter;
            Tenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
            NoStore = request.Headers.CacheControl?.NoStore == true;
            RequestBody?.Dispose();
            RequestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            BeforeResponse?.Invoke();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = JsonContent.Create(Body, Body.GetType(), options: JsonOptions)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = NoStoreResponse };
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                RequestBody?.Dispose();
            base.Dispose(disposing);
        }
    }
}
