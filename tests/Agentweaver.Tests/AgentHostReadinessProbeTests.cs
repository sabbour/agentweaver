using System.Net;
using System.Reflection;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Covers the A2A cold-start readiness gate: <see cref="HttpAgentHostReadinessProbe"/> polling
/// <c>/healthz</c> until the AgentHost serves, and <see cref="KubernetesSandboxExecutor"/> failing the
/// launch deterministically when the pod never becomes ready (instead of letting the worker send the
/// first turn into a refused connection mid-run).
/// </summary>
public sealed class AgentHostReadinessProbeTests
{
    [Fact]
    public async Task Probe_returns_once_healthz_succeeds_after_initial_failures()
    {
        // 503 twice (boot window), then 200 → probe should return without throwing.
        var handler = new SequencedHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new HttpAgentHostReadinessProbe(
            new SingleClientFactory(handler),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance);

        await probe.WaitUntilReadyAsync("http://10.0.0.7:8088/healthz", CancellationToken.None);

        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Probe_throws_timeout_when_never_ready()
    {
        var handler = new SequencedHandler(
            () => throw new HttpRequestException("Connection refused"));
        var probe = new HttpAgentHostReadinessProbe(
            new SingleClientFactory(handler),
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(10),
            NullLogger.Instance);

        var act = () => probe.WaitUntilReadyAsync("http://10.0.0.7:8088/healthz", CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Probe_honors_cancellation()
    {
        var handler = new SequencedHandler(
            () => throw new HttpRequestException("Connection refused"));
        var probe = new HttpAgentHostReadinessProbe(
            new SingleClientFactory(handler),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(10),
            NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var act = () => probe.WaitUntilReadyAsync("http://10.0.0.7:8088/healthz", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Executor_awaits_readiness_probe_before_returning_endpoint()
    {
        const string runId = "run-ready-1";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");
        StubAgentHostBaseResources(handler);

        var probe = new RecordingProbe();
        var executor = NewExecutor(handler, probe);

        var endpoint = await executor.LaunchAgentHostPodAsync(runId);

        endpoint.Should().Contain("10.0.0.7").And.Contain("8088");
        probe.LastUrl.Should().Be("http://10.0.0.7:8088/healthz",
            "the gate must probe the root /healthz path on the AgentHost port");
    }

    [Fact]
    public async Task Executor_fails_launch_when_readiness_probe_times_out()
    {
        const string runId = "run-ready-2";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");
        StubAgentHostBaseResources(handler);

        var probe = new ThrowingProbe(new TimeoutException("never ready"));
        var executor = NewExecutor(handler, probe);

        var act = () => executor.LaunchAgentHostPodAsync(runId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithInnerException<InvalidOperationException, TimeoutException>();
    }

    private static KubernetesSandboxOptions Options() => new()
    {
        Namespace = "agentweaver",
        WarmPoolRef = "agentweaver-sandbox",
        AgentHostWarmPoolRef = "agentweaver-agent-host",
        TimeoutSeconds = 600,
        RequireMtls = false,
        AgentHostPort = 8088,
        AgentHostA2APath = "/a2a/agent",
        AgentHostHealthzPath = "/healthz",
        WorkspaceMountPath = "/workspace",
    };

    private static KubernetesSandboxExecutor NewExecutor(FakeKubeHandler handler, IAgentHostReadinessProbe probe) =>
        new(new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler),
            Options(), NullLogger<KubernetesSandboxExecutor>.Instance, podRegistry: null, readinessProbe: probe,
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"));

    private static void StubAgentHostBaseResources(FakeKubeHandler handler)
    {
        handler.OnGet(
            "/apis/secrets-store.csi.x-k8s.io/v1/namespaces/agentweaver/secretproviderclasses/agentweaver-user-tokens",
            """
            {"apiVersion":"secrets-store.csi.x-k8s.io/v1","kind":"SecretProviderClass","metadata":{"name":"agentweaver-user-tokens"},"spec":{"provider":"azure","parameters":{"usePodIdentity":"false","useVMManagedIdentity":"false","clientID":"cid","keyvaultName":"kv","tenantId":"tid","objects":"array:\n  - |\n    objectName: ghtok-installation\n    objectType: secret\n"}}}
            """);
        handler.OnGet(
            "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxtemplates/agentweaver-agent-host",
            """
            {"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxTemplate","metadata":{"name":"agentweaver-agent-host","namespace":"agentweaver","resourceVersion":"1"},"spec":{"podTemplate":{"spec":{"volumes":[{"name":"csi-user-tokens","csi":{"volumeAttributes":{"secretProviderClass":"agentweaver-user-tokens"}}}]}}}}
            """);
    }

    private sealed class StubSubmittingUserResolver : IRunSubmittingUserResolver
    {
        private readonly string? _user;
        public StubSubmittingUserResolver(string? user) => _user = user;
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_user);
        public Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingProbe : IAgentHostReadinessProbe
    {
        public string? LastUrl { get; private set; }
        public Task WaitUntilReadyAsync(string readinessUrl, CancellationToken ct)
        {
            LastUrl = readinessUrl;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProbe : IAgentHostReadinessProbe
    {
        private readonly Exception _ex;
        public ThrowingProbe(Exception ex) => _ex = ex;
        public Task WaitUntilReadyAsync(string readinessUrl, CancellationToken ct) => Task.FromException(_ex);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        // Do not dispose the shared handler between attempts (the probe disposes the client each loop).
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _steps;
        public int CallCount { get; private set; }
        public SequencedHandler(params Func<HttpResponseMessage>[] steps) => _steps = steps;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = _steps[Math.Min(CallCount, _steps.Length - 1)];
            CallCount++;
            try { return Task.FromResult(step()); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
        }
    }
}
