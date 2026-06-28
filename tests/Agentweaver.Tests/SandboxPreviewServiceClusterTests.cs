using System.Net;
using System.Text;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Cluster-backed tests for <see cref="SandboxPreviewService"/> using a fake Kubernetes API
/// (an <see cref="HttpMessageHandler"/> that canned-responds to the REST calls). These prove the
/// replica-safety fixes:
///   B1 — StartPreview resolves the bound pod from the SandboxClaim status (cluster state), with
///        NO in-memory pod registry involved (the service no longer even takes one).
///   S1 — the reaper sweeps a preview Service that has no matching HTTPRoute (orphan ClusterIP).
/// </summary>
public sealed class SandboxPreviewServiceClusterTests
{
    private static SandboxPreviewOptions EnabledOptions() => new()
    {
        Enabled = true,
        ZoneSuffix = "6a3de4fe.westus2.staging.aksapp.io",
        Namespace = "agentweaver",
        IdleTimeoutMinutes = 30,
        MaxLifetimeHours = 8,
    };

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    private static SandboxPreviewService NewService(FakeKubeHandler handler) =>
        new(ClientFor(handler), EnabledOptions(), NullLogger<SandboxPreviewService>.Instance);

    // ── B1: replica-safe pod resolution from cluster state ───────────────────────

    [Fact]
    public async Task StartPreview_resolves_pod_from_claim_status_without_in_memory_registry()
    {
        const string runId = "run-abc-123";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        // GET SandboxClaim -> Bound, pod resolved from status.sandbox.name.
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"apiVersion":"extensions.agents.x-k8s.io/v1alpha1","kind":"SandboxClaim","metadata":{"name":"c"},"status":{"phase":"Bound","sandbox":{"name":"agenthost-pod-zzz"}}}""");
        // Pod patch, Service create, HTTPRoute create all succeed (echoed).
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/", """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-zzz"}}""");
        handler.OnEcho("POST", "/api/v1/namespaces/agentweaver/services");
        handler.OnEcho("POST", "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes");

        var svc = NewService(handler);

        var session = await svc.StartPreviewAsync(runId, 3000, "user-1");

        session.PodName.Should().Be("agenthost-pod-zzz", "pod must come from the claim status, not a registry");
        session.PreviewUrl.Should().StartWith("https://").And.Contain("-preview.");
        handler.Requests.Should().Contain(r =>
            r.Method == "GET" && r.Path.EndsWith($"/sandboxclaims/{claimName}"),
            "StartPreview must read the SandboxClaim from the cluster (replica-safe)");
    }

    [Fact]
    public async Task StartPreview_returns_not_ready_when_claim_unbound_on_every_replica()
    {
        const string runId = "run-not-bound";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"kind":"SandboxClaim","metadata":{"name":"c"},"status":{"phase":"Pending"}}""");

        var svc = NewService(handler);

        var act = async () => await svc.StartPreviewAsync(runId, 3000, "user-1");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No bound sandbox pod*");
    }

    [Fact]
    public async Task StartPreview_returns_not_ready_when_claim_missing()
    {
        var handler = new FakeKubeHandler(); // no GET registered -> 404
        var svc = NewService(handler);

        var act = async () => await svc.StartPreviewAsync("run-no-claim", 3000, "user-1");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Service_no_longer_depends_on_the_in_memory_pod_registry()
    {
        // The replica-safety fix removes the IPodNameRegistry constructor dependency entirely.
        var ctor = typeof(SandboxPreviewService).GetConstructors().Single();
        ctor.GetParameters().Should().NotContain(
            p => p.ParameterType == typeof(IPodNameRegistry),
            "preview start must resolve pods from cluster state, not per-process memory");
    }

    // ── S1: orphan ClusterIP sweep ───────────────────────────────────────────────

    [Fact]
    public async Task Reap_sweeps_orphaned_service_that_has_no_matching_httproute()
    {
        var handler = new FakeKubeHandler();
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            """{"apiVersion":"gateway.networking.k8s.io/v1","kind":"HTTPRouteList","items":[]}""");
        handler.OnGet("/api/v1/namespaces/agentweaver/services",
            """{"apiVersion":"v1","kind":"ServiceList","items":[{"apiVersion":"v1","kind":"Service","metadata":{"name":"preview-orphan-xyz","namespace":"agentweaver","creationTimestamp":"2020-01-01T00:00:00Z"}}]}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/services/preview-orphan-xyz$",
            """{"apiVersion":"v1","kind":"Status","status":"Success"}""");

        var svc = NewService(handler);

        var reaped = await svc.ReapAsync();

        reaped.Should().Be(1, "the route-less preview Service is an orphan and must be swept");
        handler.Requests.Should().Contain(r =>
            r.Method == "DELETE" && r.Path.EndsWith("/services/preview-orphan-xyz"),
            "the orphaned ClusterIP Service must be deleted");
    }

    [Fact]
    public async Task Reap_keeps_service_that_still_has_its_httproute()
    {
        var handler = new FakeKubeHandler();
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            """{"kind":"HTTPRouteList","items":[{"metadata":{"name":"preview-live-abc","annotations":{"agentweaver.dev/preview-token":"live-abc","agentweaver.dev/preview-run":"run-x"}}}]}""");
        handler.OnGet("/api/v1/namespaces/agentweaver/pods",
            """{"kind":"PodList","items":[{"metadata":{"name":"p"}}]}""");
        handler.OnGet("/api/v1/namespaces/agentweaver/services",
            """{"kind":"ServiceList","items":[{"metadata":{"name":"preview-live-abc","namespace":"agentweaver","creationTimestamp":"2020-01-01T00:00:00Z"}}]}""");

        var svc = NewService(handler);

        var reaped = await svc.ReapAsync();

        reaped.Should().Be(0, "the Service still has a matching HTTPRoute — not an orphan");
        handler.Requests.Should().NotContain(r => r.Method == "DELETE",
            "a live preview's Service must never be swept");
    }

    // ── M1: run<->token binding ──────────────────────────────────────────────────

    [Fact]
    public async Task VerifyTokenForRun_true_only_for_the_owning_run()
    {
        const string token = "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2";
        const string runId = "run-owns-token";
        var routeName = PreviewReaper.ServiceName(token);
        var perRun = PreviewReaper.PerRunLabel(runId);

        var handler = new FakeKubeHandler();
        var routeJson = "{\"metadata\":{\"name\":\"r\",\"annotations\":{\"agentweaver.dev/preview-run\":\"" + perRun + "\"}}}";
        handler.OnGet(
            $"/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes/{routeName}",
            routeJson);

        var svc = NewService(handler);

        (await svc.VerifyTokenForRunAsync(token, runId)).Should().BeTrue();
        (await svc.VerifyTokenForRunAsync(token, "some-other-run")).Should().BeFalse(
            "a token bound to one run must not authorize another run");
    }

    [Fact]
    public async Task VerifyTokenForRun_false_when_route_missing()
    {
        const string token = "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2";
        var handler = new FakeKubeHandler(); // 404
        var svc = NewService(handler);

        (await svc.VerifyTokenForRunAsync(token, "run-x")).Should().BeFalse();
    }
}

/// <summary>
/// Minimal fake Kubernetes API surface: routes by HTTP method + path (query string ignored) and
/// returns canned JSON. Unmatched GET -> 404, unmatched DELETE -> success, unmatched POST/PATCH
/// echo the request body. Records every request so tests can assert which cluster calls were made.
/// </summary>
internal sealed class FakeKubeHandler : DelegatingHandler
{
    public sealed record Req(string Method, string Path);

    public List<Req> Requests { get; } = new();

    private const string EchoMarker = "\u0000ECHO";
    private readonly List<(string Method, string PathOrRegex, bool IsRegex, string Body)> _routes = new();

    public void OnGet(string path, string body) => _routes.Add(("GET", path, false, body));

    public void OnAny(string pathRegex, string body) => _routes.Add(("*", pathRegex, true, body));

    public void OnEcho(string method, string path) => _routes.Add((method, path, false, EchoMarker));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await BuildAsync(request, cancellationToken);
        response.RequestMessage = request; // client deserialization reads response.RequestMessage
        return response;
    }

    private async Task<HttpResponseMessage> BuildAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new Req(method, path));

        foreach (var (rMethod, pathOrRegex, isRegex, body) in _routes)
        {
            var methodOk = rMethod == "*" || string.Equals(rMethod, method, StringComparison.OrdinalIgnoreCase);
            if (!methodOk) continue;

            var pathOk = isRegex
                ? System.Text.RegularExpressions.Regex.IsMatch(path, pathOrRegex)
                : string.Equals(path, pathOrRegex, StringComparison.Ordinal);
            if (!pathOk) continue;

            if (body == EchoMarker)
                return Json(HttpStatusCode.OK, await EchoAsync(request, cancellationToken));

            return Json(HttpStatusCode.OK, body);
        }

        // POST/PATCH default: echo body (a create/patch the test didn't care to stub explicitly).
        if (method is "POST" or "PATCH")
            return Json(HttpStatusCode.OK, await EchoAsync(request, cancellationToken));

        // DELETE of an unstubbed object behaves as already-gone success; GET as not-found.
        return method == "DELETE"
            ? Json(HttpStatusCode.OK, """{"kind":"Status","status":"Success"}""")
            : Json(HttpStatusCode.NotFound, """{"kind":"Status","status":"Failure","code":404}""");
    }

    private static async Task<string> EchoAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var echoed = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : "{}";
        return string.IsNullOrWhiteSpace(echoed) ? "{}" : echoed;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
