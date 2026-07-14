using System.Net;
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
