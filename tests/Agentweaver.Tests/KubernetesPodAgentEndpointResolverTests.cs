using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
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

    [Fact]
    public async Task Provider_failure_is_rethrown_unchanged_and_failed_launch_cache_is_cleared()
    {
        const string runId = "run-stale-project-credential";
        var projectId = ProjectId.New();
        var registry = new PodNameRegistry();
        var failure = new ModelProviderConnectionRequiredException(projectId);
        var lifecycle = new RecoveringProviderFailurePodLifecycle(
            registry,
            "agenthost-authorized",
            failure);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReadyPodHandler("agenthost-authorized", "10.0.0.43"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var first = () => resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        var exception = await first.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Should().BeSameAs(failure);
        exception.Which.ErrorCode.Should().Be(ModelProviderConnectionRequirement.RequirementCode);
        exception.Which.FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
        exception.Which.IsRetryable.Should().BeFalse();

        var endpoint = await resolver.TryResolveEndpointAsync(runId, CancellationToken.None);

        endpoint.Should().Be("http://10.0.0.43:8088/a2a/agent");
        lifecycle.LaunchCalls.Should().Be(2,
            "the typed provider failure must be removed from the launch cache before it propagates");
    }

    [Fact]
    public async Task Failed_launch_waiters_do_not_evict_replacement_launch()
    {
        const string runId = "run-concurrent-stale-launch";
        var projectId = ProjectId.New();
        var registry = new PodNameRegistry();
        var failure = new ModelProviderConnectionRequiredException(projectId);
        var lifecycle = new ConcurrentProviderFailurePodLifecycle(
            registry,
            "agenthost-replacement",
            failure);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            new ReadyPodHandler("agenthost-replacement", "10.0.0.44"));
        var resolver = new KubernetesPodAgentEndpointResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false },
            NullLogger<KubernetesPodAgentEndpointResolver>.Instance,
            lifecycle);

        var firstWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        var secondWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(1);

        var launches = GetLaunches(resolver);
        launches.TryGetValue(runId, out var failedLaunch).Should().BeTrue();
        var cachedFailedLaunch = failedLaunch
            ?? throw new InvalidOperationException("Failed launch was not cached.");
        launches.TryRemove(
                new KeyValuePair<string, Lazy<Task<string>>>(runId, cachedFailedLaunch))
            .Should().BeTrue("this stages the interleaving after one failed waiter removes its launch");

        var replacement = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(2);

        lifecycle.FailFirstLaunch();
        var firstWaiterFailure = async () => await firstWaiter;
        var secondWaiterFailure = async () => await secondWaiter;
        await firstWaiterFailure.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        await secondWaiterFailure.Should().ThrowAsync<ModelProviderConnectionRequiredException>();

        var replacementWaiter = resolver.TryResolveEndpointAsync(runId, CancellationToken.None);
        lifecycle.LaunchCalls.Should().Be(2,
            "stale waiters must not remove the replacement launch from the cache");

        lifecycle.CompleteReplacement(runId);
        var endpoints = await Task.WhenAll(replacement, replacementWaiter);
        endpoints.Should().OnlyContain(endpoint => endpoint == new Uri("http://10.0.0.44:8088/a2a/agent"));
    }

    private static ConcurrentDictionary<string, Lazy<Task<string>>> GetLaunches(
        KubernetesPodAgentEndpointResolver resolver)
    {
        var field = typeof(KubernetesPodAgentEndpointResolver).GetField(
            "_launches",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (ConcurrentDictionary<string, Lazy<Task<string>>>)
            (field?.GetValue(resolver) ?? throw new InvalidOperationException("Launch cache field not found."));
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

    private sealed class RecoveringProviderFailurePodLifecycle(
        IPodNameRegistry registry,
        string podName,
        AgentProviderException failure) : IAgentHostPodLifecycle
    {
        public int LaunchCalls { get; private set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            LaunchCalls++;
            if (LaunchCalls == 1)
                return Task.FromException<string>(failure);

            registry.Register(runId, podName);
            return Task.FromResult("http://authorized");
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ConcurrentProviderFailurePodLifecycle(
        IPodNameRegistry registry,
        string podName,
        AgentProviderException failure) : IAgentHostPodLifecycle
    {
        private readonly TaskCompletionSource<string> _failedLaunch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _replacementLaunch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCalls;

        public int LaunchCalls => Volatile.Read(ref _launchCalls);

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _launchCalls);
            return call == 1 ? _failedLaunch.Task : _replacementLaunch.Task;
        }

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, ct);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void FailFirstLaunch() => _failedLaunch.SetException(failure);

        public void CompleteReplacement(string runId)
        {
            registry.Register(runId, podName);
            _replacementLaunch.SetResult("http://replacement");
        }
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

    private sealed class ReadyPodHandler(string podName, string podIp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    kind = "Pod",
                    metadata = new { name = podName },
                    status = new { podIP = podIp },
                })),
                RequestMessage = request,
            });
    }
}
