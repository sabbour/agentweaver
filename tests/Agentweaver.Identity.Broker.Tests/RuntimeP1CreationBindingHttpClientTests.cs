using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeP1CreationBindingHttpClientTests
{
    [Fact]
    public async Task JoinsExactOwnersWithOriginalActorAndRechecksRootAndRegistration()
    {
        var registration = Registration();
        var creation = Creation(registration);
        var paths = new List<string>();
        using var http = Client(registration, creation, paths);
        var actual = await Reader(http).ReadRootAsync(registration, default);
        Assert.Equal(creation, actual);
        Assert.Equal(7, paths.Count);
        Assert.Equal(2, paths.Count(path => path.EndsWith("/p1-invocation-binding")));
        Assert.Equal(4, paths.Count(path => path.Contains("/registrations/")));
        Assert.Equal("/internal/projects/project/runs/run/environments/environment/coordination/sessions/session/runtime-bootstrap/profiles/profile/p1-creation-binding",
            paths[3]);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("binding")]
    [InlineData("owner")]
    [InlineData("operation")]
    [InlineData("resource")]
    [InlineData("workspace")]
    [InlineData("generation")]
    [InlineData("fence")]
    [InlineData("version")]
    [InlineData("future")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("cache")]
    [InlineData("oversized")]
    [InlineData("root-changed")]
    [InlineData("registration-changed")]
    public async Task RejectsMissingForeignStaleMalformedAndChangedOwnerResults(string fault)
    {
        var registration = Registration();
        var creation = Creation(registration);
        var paths = new List<string>();
        using var http = Client(registration, creation, paths, fault);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => Reader(http).ReadRootAsync(registration, default));
        Assert.Contains(paths, path => path.EndsWith("/p1-creation-binding"));
    }

    private static RuntimeP1CreationBindingHttpClient Reader(HttpClient http) => new(
        http, new("https://orchestrator.test/"), new("https://environment.test/"),
        new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(5)), "tenant"),
        TimeProvider.System);

    private static RuntimeRegistration Registration()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        return registration with { Binding = registration.Binding with
        {
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            EnvironmentCurrentFencingGeneration = 7,
            PlacementProviderId = "sandbox"
        } };
    }

    private static HttpClient Client(
        RuntimeRegistration registration, EnvironmentP1CreationBinding original, List<string> paths,
        string? fault = null)
    {
        var rootReads = 0;
        var registrationReads = 0;
        return new(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            Assert.True(request.Headers.CacheControl?.NoStore);
            Assert.Null(request.Content);
            if (request.RequestUri.Host == "orchestrator.test")
            {
                if (request.RequestUri.AbsolutePath.Contains("/registrations/"))
                    return Response(request, ++registrationReads == 4 && fault == "registration-changed"
                        ? registration with { Revision = registration.Revision + 1 } : registration);
                return Response(request, ++rootReads == 2 && fault == "root-changed"
                    ? original.Binding with { CoreInvocationId = Guid.NewGuid() } : original.Binding);
            }
            Assert.Equal("environment.test", request.RequestUri.Host);
            var creation = fault switch
            {
                "binding" => original with { Binding = original.Binding with { SetupInvocationId = Guid.NewGuid() } },
                "owner" => original with { EnvironmentFence = new(new("foreign", "project", "run", "environment"), 1) },
                "operation" => original with { SandboxProvision = original.SandboxProvision with { OperationId = Guid.NewGuid() } },
                "resource" => original with { SandboxProvision = original.SandboxProvision with
                    { ProviderResource = original.SandboxProvision.ProviderResource! with { ResourceId = "foreign" } } },
                "workspace" => original with { WorkspaceMetadata = original.WorkspaceMetadata with { ResourceId = "foreign" } },
                "generation" => original with { WorkspaceProvision = original.WorkspaceProvision with { ResourceGeneration = 2 } },
                "fence" => original with { SandboxProvision = original.SandboxProvision with { ProviderFencingGeneration = 2 } },
                "version" => original with { ContractVersion = 2 },
                "future" => original with { Environment = original.Environment with { CompletedAt = DateTimeOffset.UtcNow.AddDays(1) } },
                _ => original
            };
            var response = Response(request, creation);
            if (fault == "missing")
                response.StatusCode = HttpStatusCode.NotFound;
            if (fault == "cache")
                response.Headers.CacheControl = null;
            if (fault is "null" or "unknown" or "duplicate")
            {
                var json = JsonSerializer.Serialize(creation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var nullEnvironment = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                nullEnvironment["environment"] = null;
                response.Content = new StringContent(fault switch
                {
                    "null" => nullEnvironment.ToJsonString(),
                    "unknown" => json.Insert(1, "\"extra\":true,"),
                    _ => json.Insert(1, "\"contractVersion\":1,")
                }, Encoding.UTF8, "application/json");
            }
            if (fault == "oversized")
                response.Content.Headers.ContentLength = 64 * 1024 + 1;
            return response;
        }));
    }

    private static EnvironmentP1CreationBinding Creation(RuntimeRegistration registration)
    {
        var b = registration.Binding;
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var root = new P1InvocationBinding(1, Guid.NewGuid(), Guid.NewGuid(), new string('c', 64),
            b.TenantId, b.ProjectId, b.RunId, b.SessionId, b.ActorIssuer, b.ActorId, Guid.NewGuid(), 1,
            b.AcceptedSelectionHash, b.ExecutionFence, 1, now);
        var fence = new EnvironmentGenerationFence(new(b.TenantId, b.ProjectId, b.RunId, b.EnvironmentId), b.EnvironmentLifecycleGeneration);
        var sandbox = new ProviderResourceRef(ProviderSeam.Sandbox, b.PlacementProviderId!, b.PlacementUid, b.PlacementGeneration);
        var operation = Guid.NewGuid();
        var pod = new SandboxPodObservation(1, now, fence, sandbox, b.EnvironmentProviderFencingGeneration,
            operation, new string('d', 64), "namespace", b.PlacementUid, "sandbox", "pod", "pod-uid", "42", 1, "Running",
            new("namespace", "pvc", "pvc-uid", "/workspace/project", false), true);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "storage", "volume-resource", 1);
        return new(1, root, fence,
            new(Guid.NewGuid(), "Environment", b.EnvironmentId, null, null, null, null, null, now, now),
            new(Guid.NewGuid(), "WorkspaceVolumeMetadata", "volume", 0, null, 1, null, null, now, now),
            new(Guid.NewGuid(), "WorkspaceVolume", "volume", 1, null, 2, null, storage, now, now),
            new(operation, "Sandbox", operation.ToString("D"), sandbox.Generation, b.EnvironmentProviderFencingGeneration,
                null, 1, sandbox, now, now), pod);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, object value) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = JsonContent.Create(value),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
