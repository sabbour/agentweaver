using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed class RuntimeWorkspaceSuspendHttpClientTests
{
    [Fact]
    public async Task ResolveUsesTheOriginalActorAndReturnsTheExactOwnerDerivedNextGeneration()
    {
        var request = Request();
        using var handler = new ResponseHandler(request);
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        var resolved = await client.ResolveAsync(
            request.Suspend with { WorkspaceFlush = null }, Actor(), CancellationToken.None);
        Assert.Equal(8, resolved.Transition.NextDataGeneration);
        Assert.Equal(7, resolved.Suspend.WorkspaceFlush!.ExpectedDataGeneration);
        Assert.Equal("actor-token", handler.Bearer);
        Assert.Equal("tenant", handler.TenantSelector);
        Assert.Equal("/internal/runtime/suspend/resolve", handler.Path);
        Assert.True(handler.NoStore);
        Assert.DoesNotContain("workspaceFlush", handler.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("owner")]
    [InlineData("next-generation")]
    [InlineData("no-store")]
    public async Task ResolveRejectsChangedOwnerEvidenceOrMissingNoStore(string changed)
    {
        var request = Request();
        var response = changed switch
        {
            "manifest" => request with { Suspend = request.Suspend with { ManifestId = Guid.NewGuid() } },
            "owner" => request with
            {
                Suspend = request.Suspend with
                {
                    WorkspaceFlush = request.Suspend.WorkspaceFlush! with
                    {
                        EnvironmentFence = new(new("foreign", "project", "run", "environment"), 1)
                    }
                }
            },
            "next-generation" => request with { Transition = request.Transition with { NextDataGeneration = 9 } },
            _ => request
        };
        using var handler = new ResponseHandler(response) { NoStoreResponse = changed != "no-store" };
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.ResolveAsync(request.Suspend with { WorkspaceFlush = null }, Actor(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRejectsCallerSuppliedWorkspaceEvidenceWithoutSendingARequest()
    {
        using var handler = new ResponseHandler(Request());
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        var exception = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.ResolveAsync(Request().Suspend, Actor(), CancellationToken.None));
        Assert.Equal("runtime_suspend_resolve_evidence_not_allowed", exception.Code);
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task AnAcceptedPendingFlushIsNotReportedAsSuccessfulSuspend()
    {
        var request = Request();
        var flush = request.Suspend.WorkspaceFlush!;
        var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(
            Guid.NewGuid(), flush.EnvironmentFence, flush.Spec.VolumeId, 4, 1, 7,
            flush.Resource, flush.ProviderBinding, 5, 1, 8, EnvironmentWorkspaceVolumeState.Attached,
            EnvironmentWorkspaceVolumeOperation.Flush, EnvironmentWorkspaceVolumeTransitionState.Reserved, true);
        using var handler = new ResponseHandler(new WorkspaceVolumeLifecycleResult(reservation, null, null))
        {
            StatusCode = HttpStatusCode.Accepted
        };
        using var http = new HttpClient(handler);
        var client = new RuntimeWorkspaceSuspendHttpClient(http, new("https://environment.test/"));
        var exception = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.FlushAsync(request, Actor(), CancellationToken.None));
        Assert.Equal("runtime_workspace_flush_incomplete", exception.Code);
        Assert.Equal("/api/projects/project/runs/run/environments/environment/workspace-volumes/workspace/flush",
            handler.Path);
    }

    [Fact]
    public void MissingNativeAndCacheReceiptsCannotValidateAHostSuspend()
    {
        var request = Request();
        var host = new RuntimeHostSuspendReceipt(
            1, request.Suspend.OperationId, request.Suspend.ManifestId,
            request.Suspend.Proof.Registration, null!, null!, null!)
        {
            WorkspaceFlush = new(1, request.Suspend.WorkspaceFlush!.EnvironmentFence,
                new("project", "workspace", 1), request.Suspend.WorkspaceFlush.Resource,
                7, 8, "flush-once", new string('a', 64), null)
        };
        Assert.Equal("runtime_suspend_receipt_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeWorkspaceSuspendHttpClient.ValidateHostReceipt(
                    host, request.Suspend, DateTimeOffset.UtcNow)).Code);
    }

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant");

    private static WorkspaceVolumeApiFlushRequest Request()
    {
        var registration = new RuntimeRegistration(Guid.NewGuid(), 1,
            new("https://issuer.test/", "actor", "tenant", "project", "run", "session", "agent", "turn",
                1, 1, 1, "context", new string('a', 64), 1, "environment", "placement", 1, "profile",
                new("https://runtime.test/runtime/v1/configure"), new("https://orchestrator.test/observe"))
            {
                EnvironmentLifecycleGeneration = 1
            }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
        var spec = new WorkspaceVolumeSpec("workspace", "project", new(WorkspaceVolumeOwnerKind.Run, "run"),
            "environment", WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteMany,
            8, "azure-files", WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Retain,
            WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "claim-1", 1);
        var binding = new WorkspaceVolumeProviderBindingSnapshot(
            "azure-files", "1.0.0", 1, "options-1",
            JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));
        var flush = new WorkspaceVolumeFlushRequest(
            spec, 1, new(new("tenant", "project", "run", "environment"), 1), resource, 7, "flush-once")
        {
            ProviderBinding = binding
        };
        return new(new(4, 1, 7, "flush-once", 8),
            new(new(1, registration, Guid.NewGuid(), 1, RuntimeCredentialPurpose.Observe),
                Guid.NewGuid(), Guid.NewGuid(), 1) { WorkspaceFlush = flush });
    }

    private sealed class ResponseHandler(object body) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        public bool NoStoreResponse { get; init; } = true;
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string? Path { get; private set; }
        public string? Bearer { get; private set; }
        public string? TenantSelector { get; private set; }
        public string? Body { get; private set; }
        public bool NoStore { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Bearer = request.Headers.Authorization?.Parameter;
            TenantSelector = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            NoStore = request.Headers.CacheControl?.NoStore == true;
            var response = new HttpResponseMessage(StatusCode)
            {
                RequestMessage = request,
                Content = JsonContent.Create(body, body.GetType(), options: JsonOptions)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = NoStoreResponse };
            return response;
        }
    }
}
