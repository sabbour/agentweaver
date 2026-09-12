using System.Net;
using System.Text.Json;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using FluentAssertions;
using k8s;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class SandboxPreviewPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Late200AfterPublicationDeadline_IsRejectedAndRollsBack(
        bool redirected, bool delayedTimer)
    {
        var clock = new PublicationClock();
        var calls = 0;
        var publication = new PreviewPublicationHandler(async (_, ct) =>
        {
            if (redirected && Interlocked.Increment(ref calls) == 1)
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("/app", UriKind.Relative) },
                };

            if (delayedTimer)
            {
                // Elapsed time is authoritative even if the deadline timer has not run yet.
                clock.Advance(TimeSpan.FromSeconds(2));
            }
            else
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var h = new Harness(publication, timeoutSeconds: 1, clock: clock);

        var result = await h.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(409);
        h.ReadyEvents().Should().BeEmpty();
        h.AssertCleanedUp();
        publication.Requests.Should().HaveCount(redirected ? 2 : 1);
    }

    [Fact]
    public async Task DnsAnd503_WithholdReadyUntilExactHttpsUrlSucceeds()
    {
        var dns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publication = new PreviewPublicationHandler(async (_, ct) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    dns.SetResult();
                    throw new HttpRequestException(HttpRequestError.NameResolutionError);
                case 2:
                    unavailable.SetResult();
                    return new(HttpStatusCode.ServiceUnavailable);
                default:
                    await allowSuccess.Task.WaitAsync(ct);
                    return new(HttpStatusCode.OK);
            }
        });
        using var h = new Harness(publication);

        var start = h.StartAsync();
        await dns.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.IsCompleted.Should().BeFalse();
        h.ReadyEvents().Should().BeEmpty();
        await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.IsCompleted.Should().BeFalse("a configured route returning 503 is not a ready preview");
        h.ReadyEvents().Should().BeEmpty();

        allowSuccess.SetResult();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(5));
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        h.ReadyEvents().Should().HaveCount(2);
        var ready = h.Streams.Get(h.Run.Id.ToString())!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.SandboxPreviewReady);
        var url = JsonSerializer.SerializeToNode(ready.Payload)!["preview_url"]!.GetValue<string>();
        publication.Requests.Should().HaveCount(3).And.OnlyContain(u => u == new Uri(url));
        h.Kube.Requests.Should().NotContain(r => r.Method == "DELETE");
    }

    [Fact]
    public async Task DnsConvergenceWindow_AllowsNameResolutionBeyondPublicationWindow()
    {
        var calls = 0;
        var publication = new PreviewPublicationHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new HttpRequestException(HttpRequestError.NameResolutionError);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var h = new Harness(
            publication,
            timeoutSeconds: 1,
            dnsConvergenceTimeoutSeconds: 3);

        var result = await h.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        calls.Should().Be(2, "DNS convergence gets its own configured retry window");
        h.ReadyEvents().Should().HaveCount(2);
    }

    [Fact]
    public async Task ExistingDnsRecord_IsProbedAndPublishedImmediately()
    {
        var publication = new PreviewPublicationHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var h = new Harness(publication);

        var result = await h.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        publication.Requests.Should().ContainSingle();
        h.ReadyEvents().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("503")]
    [InlineData("403")]
    [InlineData("tls")]
    [InlineData("cross-origin")]
    [InlineData("stalled")]
    public async Task PermanentPublicationFailure_IsBoundedAndCleansUpWithoutReady(string failure)
    {
        var publication = new PreviewPublicationHandler(async (_, ct) =>
        {
            if (failure == "dns")
                throw new HttpRequestException(HttpRequestError.NameResolutionError);
            if (failure == "tls")
                throw new HttpRequestException(HttpRequestError.SecureConnectionError);
            if (failure == "stalled")
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (failure == "cross-origin")
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://other.example.test/") },
                };
            return new HttpResponseMessage(failure == "403" ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable);
        });
        using var h = new Harness(publication, timeoutSeconds: 1, dnsConvergenceTimeoutSeconds: 1);

        var result = await h.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(409);
        h.ReadyEvents().Should().BeEmpty();
        var failed = h.Streams.Get(h.Run.Id.ToString())!.GetSnapshotSince(0).Events
            .Should().ContainSingle(e => e.Type == EventTypes.SandboxPreviewFailed).Subject;
        var payload = JsonSerializer.SerializeToNode(failed.Payload)!;
        payload["reason"]!.GetValue<string>().Should().Be("publication_not_ready");
        payload["preview_runner_session_id"]!.GetValue<string>().Should().Be("retained-session");
        payload["message"]!.GetValue<string>().Should().Contain("within 1 seconds").And.NotContain("https://");
        h.AssertCleanedUp();
        publication.Requests.Should().OnlyContain(u => u.Host.EndsWith(".preview.example.test"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRequestOrRetry_RemovesPublication(bool stalledRequest)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = new PreviewPublicationHandler(async (_, ct) =>
        {
            entered.SetResult();
            if (stalledRequest)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var h = new Harness(publication);
        using var cancel = new CancellationTokenSource();
        var start = h.StartAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancel.Cancel();

        var act = async () => await start.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        h.ReadyEvents().Should().BeEmpty();
        h.AssertCleanedUp();
    }

    [Fact]
    public async Task SameOriginRedirect_IsCheckedBeforeReady()
    {
        var publication = new PreviewPublicationHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/"
                ? new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("/app", UriKind.Relative) },
                }
                : new HttpResponseMessage(HttpStatusCode.OK)));
        using var h = new Harness(publication);

        var result = await h.StartAsync();

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        publication.Requests.Select(u => u.AbsolutePath).Should().Equal("/", "/app");
        publication.Requests.Select(u => u.Authority).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void PublicationTransport_DoesNotRedirectShareCookiesOrBypassTls()
    {
        using var handler = SandboxPreviewService.CreatePublicationHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.UseDefaultCredentials.Should().BeFalse();
        handler.Credentials.Should().BeNull();
        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
        handler.ClientCertificates.Count.Should().Be(0);
    }

    private sealed class Harness : IDisposable
    {
        public readonly FakeKubeHandler Kube = new();
        public readonly RunStreamStore Streams = new();
        public readonly Run Run = new()
        {
            Id = RunId.New(),
            RepositoryPath = ".",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "preview publication regression",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        private readonly HttpClient _http;
        private readonly IKubernetes _client;
        private readonly SandboxPreviewService _service;

        public Harness(
            PreviewPublicationHandler handler,
            int timeoutSeconds = 90,
            int dnsConvergenceTimeoutSeconds = 600,
            TimeProvider? clock = null)
        {
            var claim = SandboxClaimConventions.DeriveAgentHostClaimName(Run.Id.ToString());
            Kube.OnGet(
                $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claim}",
                """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"preview-pod"}}}""");
            Kube.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
                """{"kind":"HTTPRouteList","items":[]}""");
            _client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, Kube);
            _http = new HttpClient(handler);
            _service = new SandboxPreviewService(_client, new SandboxPreviewOptions
            {
                Enabled = true,
                ZoneSuffix = "preview.example.test",
                PublicationTimeoutSeconds = timeoutSeconds,
                DnsConvergenceTimeoutSeconds = dnsConvergenceTimeoutSeconds,
            }, NullLogger<SandboxPreviewService>.Instance, clock: clock, publicationClient: _http);
            Streams.Create(Run.Id.ToString(), Run.SubmittingUser);
        }

        public Task<IResult> StartAsync(CancellationToken ct = default) =>
            SandboxEndpoints.StartPreviewForRunAsync(
                Run.Id.ToString(), 4632, Run, _service, null!, Streams, NullLogger.Instance, ct, "retained-session");

        public IEnumerable<string> ReadyEvents() =>
            Streams.Get(Run.Id.ToString())!.GetSnapshotSince(0).Events
                .Where(e => e.Type is EventTypes.SandboxPreviewReady or EventTypes.CoordinatorPreviewReady)
                .Select(e => e.Type);

        public void AssertCleanedUp()
        {
            var deleted = Kube.Requests.Where(r => r.Method == "DELETE").ToList();
            deleted.Should().HaveCount(2);
            deleted[0].Path.Should().Contain("/httproutes/");
            deleted[1].Path.Should().Contain("/services/");
            Kube.Requests.Should().Contain(r =>
                r.Method == "PATCH" && r.Path.EndsWith("/pods/preview-pod")
                && r.Body!.Contains("safe-to-evict") && r.Body.Contains("true"));
            using var retention = JsonDocument.Parse(Kube.Requests.Last(r =>
                r.Method == "PATCH" && r.Path.Contains("/sandboxclaims/")).Body!);
            retention.RootElement.GetProperty("spec").GetProperty("lifecycle")
                .GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        }

        public void Dispose()
        {
            _http.Dispose();
            _client.Dispose();
        }
    }

    private sealed class PublicationClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}

internal sealed class PreviewPublicationHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.RequestUri!.Scheme.Should().Be("https");
        request.Headers.Authorization.Should().BeNull();
        request.Headers.Contains("Cookie").Should().BeFalse();
        Requests.Add(request.RequestUri);
        return send?.Invoke(request, ct) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
