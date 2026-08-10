using System.Net;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class KubernetesPodAgentEndpointResolverTests
{
    [Fact]
    public async Task ReapedNonTerminalPod_SignalsRetryableRedispatch()
    {
        const string runId = "run-reaped-pod";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new NotFoundPodHandler());
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance);

        var act = async () => await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        exception.Which.Reason.Should().Be("agenthost_pod_reaped");
        exception.Which.IsRetryable.Should().BeTrue();
        registry.TryGet(runId).Should().BeNull("the stale pod mapping must not be reused on redispatch");
    }

    [Fact]
    public async Task ReapedCoordinatorPod_RedispatchesOnceAndResolvesReplacementForAssemblyRai()
    {
        const string runId = "run-assembly-rai";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var lifecycle = new RegisteringPodLifecycle(registry, "agenthost-replacement");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReapedThenReplacementPodHandler("agenthost-reaped", "agenthost-replacement", "10.0.0.42"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var endpoint = await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        endpoint.Should().Be("http://10.0.0.42:8088/a2a/agent");
        lifecycle.LaunchCalls.Should().Be(1,
            "a reaped coordinator pod used by assembly RAI is recovered inline exactly once");
        registry.TryGet(runId).Should().Be("agenthost-replacement");
    }

    [Fact]
    public async Task ReapedReplacementPod_StopsAfterSingleRecoveryAttempt()
    {
        const string runId = "run-persistent-reap";
        var registry = new PodNameRegistry();
        registry.Register(runId, "agenthost-reaped");
        var lifecycle = new RegisteringPodLifecycle(registry, "agenthost-replacement");
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new NotFoundPodHandler());
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var act = () => resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        exception.Which.Reason.Should().Be("agenthost_pod_reaped_recovery_exhausted");
        exception.Which.IsRetryable.Should().BeFalse(
            "persistent reaping must not trigger broad blind redispatch loops");
        lifecycle.LaunchCalls.Should().Be(1);
    }

    private sealed class RegisteringPodLifecycle(IPodNameRegistry registry, string replacementPod)
        : IAgentHostPodLifecycle
    {
        public int LaunchCalls { get; private set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            LaunchCalls++;
            registry.Register(runId, replacementPod);
            return Task.FromResult("http://replacement");
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ReapedThenReplacementPodHandler(
        string reapedPod,
        string replacementPod,
        string replacementIp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith($"/pods/{reapedPod}", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"kind":"Status","code":404}"""),
                    RequestMessage = request,
                });
            }

            if (request.RequestUri?.AbsolutePath.EndsWith($"/pods/{replacementPod}", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        kind = "Pod",
                        metadata = new { name = replacementPod },
                        status = new { podIP = replacementIp },
                    })),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }

    private sealed class NotFoundPodHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"kind":"Status","code":404}"""),
                RequestMessage = request,
            });
    }
}
