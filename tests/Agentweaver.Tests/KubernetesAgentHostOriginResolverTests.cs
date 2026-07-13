using System.Net;
using System.Diagnostics;
using System.Text;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class KubernetesAgentHostOriginResolverTests
{
    private const string RunId = "run-origin";
    private const string PodName = "agent-pod-1";

    [Fact]
    public async Task RetriesTwoTransientFaults_AndSucceedsOnThirdAttempt()
    {
        var handler = new SequencedPodHandler(
            static (_, ct) => WaitForCancellationAsync(ct),
            static (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("transient connection failure")),
            static (_, _) => Task.FromResult(PodResponse()));
        var resolver = CreateResolver(handler);

        var origin = await resolver.TryResolveOriginAsync(RunId, CancellationToken.None);

        handler.Calls.Should().Be(3);
        origin.Should().Be("http://10.0.0.7:8088");
    }

    [Fact]
    public async Task ExhaustsThreeInternalTimeouts_WithoutCancelingCallerToken()
    {
        var handler = new SequencedPodHandler(
            static (_, ct) => WaitForCancellationAsync(ct),
            static (_, ct) => WaitForCancellationAsync(ct),
            static (_, ct) => WaitForCancellationAsync(ct));
        var resolver = CreateResolver(handler);
        using var caller = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        var act = () => resolver.TryResolveOriginAsync(RunId, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();
        handler.Calls.Should().Be(3);
        caller.IsCancellationRequested.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PreCanceledCallerToken_ThrowsPromptly_WithoutRetry()
    {
        var handler = new SequencedPodHandler(static (_, ct) => WaitForCancellationAsync(ct));
        var resolver = CreateResolver(handler);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var sw = Stopwatch.StartNew();

        var act = () => resolver.TryResolveOriginAsync(RunId, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();
        handler.Calls.Should().BeLessThanOrEqualTo(1);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    private static KubernetesAgentHostOriginResolver CreateResolver(DelegatingHandler handler)
    {
        var registry = new PodNameRegistry();
        registry.Register(RunId, PodName);
        var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://localhost:8080" },
            handler);
        return new KubernetesAgentHostOriginResolver(
            client,
            registry,
            "agentweaver",
            new SandboxAgentOptions { RequireMtls = false, AgentHostPort = 8088 },
            NullLogger<KubernetesAgentHostOriginResolver>.Instance,
            TimeSpan.FromMilliseconds(20),
            _ => TimeSpan.Zero);
    }

    private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("unreachable");
    }

    private static HttpResponseMessage PodResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class SequencedPodHandler(
        params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
        : DelegatingHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var response = await responses[Math.Min(index, responses.Length - 1)](
                request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }
}
