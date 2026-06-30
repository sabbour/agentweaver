using System.Reflection;
using System.Text.Json;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies <see cref="KubernetesSandboxExecutor"/> emits SandboxClaim bodies that match the
/// installed agent-sandbox <b>v0.5.0 v1beta1</b> CRD schema:
///   • <c>apiVersion: extensions.agents.x-k8s.io/v1beta1</c>;
///   • <c>spec.warmPoolRef.name</c> (the claim binds to a SandboxWarmPool);
///   • <c>spec.lifecycle.ttlSecondsAfterFinished</c> (integer);
///   • NO <c>spec.sandboxTemplateRef</c> and NO <c>spec.warmpool</c> (the v0.4.x/v1alpha1
///     deprecated fields — pruned by the v1beta1 API server, which would leave spec without the
///     required warmPoolRef → 422 "spec.warmPoolRef: Required value").
/// </summary>
public sealed class KubernetesSandboxExecutorClaimTests
{
    private static KubernetesSandboxOptions Options() => new()
    {
        Namespace = "agentweaver",
        WarmPoolRef = "agentweaver-sandbox",
        AgentHostWarmPoolRef = "agentweaver-agent-host",
        TimeoutSeconds = 600,
        RequireMtls = false,
        AgentHostPort = 8088,
        AgentHostA2APath = "/a2a/agent",
        WorkspaceMountPath = "/workspace",
    };

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    private static KubernetesSandboxExecutor NewExecutor(FakeKubeHandler handler) =>
        NewExecutor(handler, new StubSubmittingUserResolver("sabbour"));

    private static KubernetesSandboxExecutor NewExecutor(
        FakeKubeHandler handler, IRunSubmittingUserResolver submittingUserResolver,
        IHttpClientFactory? httpClientFactory = null) =>
        new(ClientFor(handler), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, readinessProbe: null, submittingUserResolver: submittingUserResolver,
            httpClientFactory: httpClientFactory);

    private sealed class StubSubmittingUserResolver : IRunSubmittingUserResolver
    {
        private readonly string? _user;
        public StubSubmittingUserResolver(string? user) => _user = user;
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_user);
    }

    // Records the /configure POST so the warm-pool deferred-config contract can be asserted.
    private sealed class RecordingConfigureHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"configured\":true}"),
            };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static JsonElement SpecOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("spec").Clone();
    }

    [Fact]
    public async Task LaunchAgentHostPod_posts_v1beta1_claim_bound_to_shared_warmpool_with_no_per_run_context()
    {
        const string runId = "run-claim-1";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        // GET claim -> Ready condition True with bound pod name.
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        // GET pod -> has an IP so GetPodIpAsync returns.
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");
        // POST claim is left to the default echo so we can read the body back.

        var executor = NewExecutor(handler);

        var endpoint = await executor.LaunchAgentHostPodAsync(runId);

        endpoint.Should().Contain("10.0.0.7").And.Contain("8088");

        var post = handler.Requests.Should().ContainSingle(r =>
            r.Method == "POST" && r.Path.EndsWith("/sandboxclaims")).Subject;

        post.Path.Should().Contain("/v1beta1/", "claims must target the native v1beta1 version");
        post.Body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(post.Body!);
        var root = doc.RootElement;
        root.GetProperty("apiVersion").GetString().Should().Be("extensions.agents.x-k8s.io/v1beta1");

        var spec = root.GetProperty("spec");
        spec.GetProperty("warmPoolRef").GetProperty("name").GetString()
            .Should().Be("agentweaver-agent-host",
                "agent-host claims bind to the SHARED pre-warmed SandboxWarmPool via spec.warmPoolRef.name (v0.5.0 v1beta1)");
        spec.GetProperty("lifecycle").GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        spec.GetProperty("lifecycle").GetProperty("shutdownPolicy").GetString().Should().Be("Delete");
        spec.TryGetProperty("sandboxTemplateRef", out _).Should()
            .BeFalse("sandboxTemplateRef is a deprecated v0.4.x/v1alpha1 field — must be gone");
        spec.TryGetProperty("warmpool", out _).Should()
            .BeFalse("warmpool is a deprecated v0.4.x/v1alpha1 field — must be gone");
        spec.TryGetProperty("templateRef", out _).Should().BeFalse("the deprecated templateRef key must be gone");

        // v0.5.0: spec.env must NOT be present — the controller bypasses warm pool adoption
        // whenever spec.env or spec.volumeClaimTemplates are set. All static config lives in the
        // SandboxTemplate / agenthost-config ConfigMap. Per-run context arrives via POST /configure.
        spec.TryGetProperty("env", out _).Should()
            .BeFalse("spec.env must be absent so the v0.5.0 controller can assign a pre-warmed pool pod");

        // No per-run SecretProviderClass is created any more (token fetched from KV at /configure).
        handler.Requests.Should().NotContain(r =>
            r.Method == "POST" && r.Path.EndsWith("/secretproviderclasses"),
            "the per-run CSI SecretProviderClass is replaced by runtime KV fetch");
    }

    [Fact]
    public async Task CreateClaim_generic_posts_v1beta1_warmPoolRef_body()
    {
        // Drive the private generic claim path directly (the public ExecuteAsync path also needs a
        // websocket exec, out of scope here). Asserts the same v1beta1 contract.
        var handler = new FakeKubeHandler();
        var executor = NewExecutor(handler);

        var create = typeof(KubernetesSandboxExecutor).GetMethod(
            "CreateClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)create.Invoke(executor, new object[] { "run-generic-1", CancellationToken.None })!;

        var post = handler.Requests.Should().ContainSingle(r =>
            r.Method == "POST" && r.Path.EndsWith("/sandboxclaims")).Subject;

        post.Path.Should().Contain("/v1beta1/");
        var spec = SpecOf(post.Body!);
        spec.GetProperty("warmPoolRef").GetProperty("name").GetString().Should().Be("agentweaver-sandbox");
        spec.TryGetProperty("sandboxTemplateRef", out _).Should()
            .BeFalse("sandboxTemplateRef is a deprecated v0.4.x/v1alpha1 field — must be gone");
        spec.TryGetProperty("warmpool", out _).Should()
            .BeFalse("warmpool is a deprecated v0.4.x/v1alpha1 field — must be gone");
        spec.GetProperty("lifecycle").GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        spec.GetProperty("lifecycle").GetProperty("shutdownPolicy").GetString().Should().Be("Delete");
        spec.TryGetProperty("templateRef", out _).Should().BeFalse();
        spec.TryGetProperty("ttl", out _).Should().BeFalse();
    }

    [Fact]
    public async Task LaunchAgentHostPod_configures_warm_pod_with_run_owner_kv_secret()
    {
        const string runId = "run-claim-user";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var configureHandler = new RecordingConfigureHandler();
        var executor = NewExecutor(
            handler, new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler));

        await executor.LaunchAgentHostPodAsync(runId);

        configureHandler.RequestUri.Should().Be("http://10.0.0.7:8088/configure",
            "the warm pod is configured at its bound IP after readiness");
        configureHandler.Body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.GetProperty("runId").GetString().Should().Be(runId);
        body.GetProperty("userId").GetString().Should().Be("sabbour");
        body.GetProperty("turnBearerToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("kvUserSecretName").GetString().Should()
            .StartWith("ghtok-user--",
                "the pod must fetch ONLY the run owner's KV secret (base32-encoded user id)");
    }

    [Fact]
    public async Task LaunchAgentHostPod_fails_when_no_submitting_user()
    {
        const string runId = "run-claim-nouser";

        var handler = new FakeKubeHandler();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(handler), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, readinessProbe: null, submittingUserResolver: new StubSubmittingUserResolver(null));

        await executor.Invoking(e => e.LaunchAgentHostPodAsync(runId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*without a submitting user*");
        handler.Requests.Should().NotContain(r => r.Method == "POST" && r.Path.EndsWith("/sandboxclaims"),
            "no pod should be claimed without a resolved run owner to scope the KV token to");
    }
}
