using System.Net;
using System.Net.Sockets;
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
        using var listener = StartListener(out var targetPort);

        var handler = new FakeKubeHandler();
        // GET SandboxClaim -> Ready condition True, pod resolved from status.sandbox.name.
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxClaim","metadata":{"name":"c"},"status":{"conditions":[{"type":"Ready","status":"True","reason":"Bound","message":"sandbox ready","lastTransitionTime":"2026-06-28T06:00:00Z"}],"sandbox":{"name":"agenthost-pod-zzz"}}}""");
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods/agenthost-pod-zzz",
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-zzz"},"status":{"podIP":"127.0.0.1"}}""");
        // Pod patch, Service create, HTTPRoute create all succeed (echoed).
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/", """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-zzz"}}""");
        handler.OnEcho("POST", "/api/v1/namespaces/agentweaver/services");
        handler.OnEcho("POST", "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes");

        var svc = NewService(handler);

        var session = await svc.StartPreviewAsync(runId, targetPort, "user-1");

        session.PodName.Should().Be("agenthost-pod-zzz", "pod must come from the claim status, not a registry");
        session.PreviewUrl.Should().StartWith("https://").And.Contain("-preview.");
        handler.Requests.Should().Contain(r =>
            r.Method == "GET" && r.Path.EndsWith($"/sandboxclaims/{claimName}"),
            "StartPreview must read the SandboxClaim from the cluster (replica-safe)");
    }


    [Fact]
    public async Task StartPreview_resolves_pod_from_retained_run_command_claim_status()
    {
        const string runId = "run-command-preview";
        var agentClaimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var runCommandClaimName = SandboxClaimConventions.DeriveRunCommandClaimName(runId);
        using var listener = StartListener(out var targetPort);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{runCommandClaimName}",
            """{"apiVersion":"extensions.agents.x-k8s.io/v1beta1","kind":"SandboxClaim","metadata":{"name":"c"},"status":{"conditions":[{"type":"Ready","status":"True","reason":"Bound","message":"sandbox ready","lastTransitionTime":"2026-06-28T06:00:00Z"}],"sandbox":{"name":"run-command-pod-zzz"}}}""");
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods/run-command-pod-zzz",
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"run-command-pod-zzz"},"status":{"podIP":"127.0.0.1"}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/", """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"run-command-pod-zzz"}}""");
        handler.OnEcho("POST", "/api/v1/namespaces/agentweaver/services");
        handler.OnEcho("POST", "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes");

        var svc = NewService(handler);

        var session = await svc.StartPreviewAsync(runId, targetPort, "user-1");

        session.PodName.Should().Be("run-command-pod-zzz",
            "Build/Test may start the preview server through run_command in the retained run-* sandbox claim");
        handler.Requests.Should().Contain(r =>
            r.Method == "GET" && r.Path.EndsWith($"/sandboxclaims/{agentClaimName}"),
            "the resolver checks the AgentHost claim first");
        handler.Requests.Should().Contain(r =>
            r.Method == "GET" && r.Path.EndsWith($"/sandboxclaims/{runCommandClaimName}"),
            "the resolver falls back to the retained command-sandbox claim for the same run");
    }

    [Fact]
    public async Task StartPreview_returns_not_ready_when_claim_unbound_on_every_replica()
    {
        const string runId = "run-not-bound";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"kind":"SandboxClaim","metadata":{"name":"c"},"status":{"conditions":[{"type":"Ready","status":"False","reason":"Pending","message":"provisioning","lastTransitionTime":"2026-06-28T06:00:00Z"}]}}""");

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
    public async Task ListForRun_returns_preview_sessions_from_httproute_annotations()
    {
        const string runId = "run-list-previews";
        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        const string token = "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2";
        using var listener = StartListener(out var targetPort);

        var handler = new FakeKubeHandler();
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            "{\"kind\":\"HTTPRouteList\",\"items\":[{\"metadata\":{\"name\":\"preview-x\",\"annotations\":{" +
            "\"agentweaver.dev/preview-token\":\"" + token + "\"," +
            "\"agentweaver.dev/preview-run\":\"" + sanitizedRun + "\"," +
            "\"agentweaver.dev/preview-pod\":\"agenthost-pod-1\"," +
            "\"agentweaver.dev/preview-target-port\":\"" + targetPort + "\"," +
            "\"agentweaver.dev/preview-started-at\":\"2026-06-30T23:00:00Z\"}}}]}");
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods/agenthost-pod-1",
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-1"},"status":{"podIP":"127.0.0.1"}}""");
        // Pod-existence probe (label selector) confirms the run's bound pod is still present.
        handler.OnGet("/api/v1/namespaces/agentweaver/pods",
            """{"kind":"PodList","items":[{"metadata":{"name":"agenthost-pod-1"}}]}""");

        var svc = NewService(handler);

        var sessions = await svc.ListForRunAsync(runId);

        sessions.Should().ContainSingle();
        sessions[0].Token.Should().Be(token);
        sessions[0].RunId.Should().Be(runId);
        sessions[0].PodName.Should().Be("agenthost-pod-1");
        sessions[0].TargetPort.Should().Be(targetPort);
        sessions[0].PreviewUrl.Should().Be($"https://{PreviewToken.HostLabel(token)}.6a3de4fe.westus2.staging.aksapp.io");
    }

    [Fact]
    public async Task StartPreview_does_not_tcp_probe_target_port_and_creates_route()
    {
        // Under the sandbox isolation model the API pod cannot TCP-connect to podIP:targetPort
        // (NetworkPolicy admits preview ports only from the Gateway). StartPreview must therefore
        // NOT preflight-probe the port: readiness is proven upstream by the AgentHost observe step.
        // Here nothing is listening on the target port, yet the Service + HTTPRoute must still be created.
        const string runId = "run-dead-port";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var deadPort = ReserveUnusedLocalPort();

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agenthost-pod-dead"}}}""");
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods/agenthost-pod-dead",
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-dead"},"status":{"podIP":"127.0.0.1"}}""");
        handler.OnEcho("POST", "/api/v1/namespaces/agentweaver/services");
        handler.OnEcho("POST", "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes");

        var svc = NewService(handler);

        var session = await svc.StartPreviewAsync(runId, deadPort, "user-1");

        session.PodName.Should().Be("agenthost-pod-dead");
        session.PreviewUrl.Should().StartWith("https://");
        handler.Requests.Should().Contain(r => r.Method == "POST" && r.Path.EndsWith("/services"),
            "readiness is proven by the AgentHost observe step, so the route is created without an API->podIP probe");
    }

    [Fact]
    public async Task ListForRun_filters_out_routes_when_no_bound_pod_exists_for_the_run()
    {
        // Under isolation we cannot TCP-probe podIP:targetPort from the API pod, so ListForRun uses
        // the allowed control-plane pod-existence check (label selector) as the liveness proxy.
        // When the run's bound pod is gone, its routes must not be reported as active.
        const string runId = "run-filter-dead-preview";
        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        const string token = "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2";

        var handler = new FakeKubeHandler();
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            "{\"kind\":\"HTTPRouteList\",\"items\":[" +
            "{\"metadata\":{\"name\":\"preview-x\",\"annotations\":{" +
            "\"agentweaver.dev/preview-token\":\"" + token + "\"," +
            "\"agentweaver.dev/preview-run\":\"" + sanitizedRun + "\"," +
            "\"agentweaver.dev/preview-pod\":\"agenthost-pod-gone\"," +
            "\"agentweaver.dev/preview-target-port\":\"5431\"," +
            "\"agentweaver.dev/preview-started-at\":\"2026-06-30T23:05:00Z\"}}}]}");
        // Pod-existence probe (label selector) returns an empty list -> the pod is gone.
        handler.OnGet("/api/v1/namespaces/agentweaver/pods", """{"kind":"PodList","items":[]}""");

        var svc = NewService(handler);

        var sessions = await svc.ListForRunAsync(runId);

        sessions.Should().BeEmpty("a route whose bound pod no longer exists must not be reported active");
    }

    [Fact]
    public async Task StartPreview_enforces_per_run_limit_from_httproute_state()
    {
        const string runId = "run-limit";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        using var listener = StartListener(out var targetPort);

        var handler = new FakeKubeHandler();
        handler.OnGet("/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            "{\"kind\":\"HTTPRouteList\",\"items\":[" +
            RouteJson("t1", sanitizedRun) + "," +
            RouteJson("t2", sanitizedRun) + "," +
            RouteJson("t3", sanitizedRun) + "]}");
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agenthost-pod-zzz"}}}""");
        handler.OnGet(
            "/api/v1/namespaces/agentweaver/pods/agenthost-pod-zzz",
            """{"apiVersion":"v1","kind":"Pod","metadata":{"name":"agenthost-pod-zzz"},"status":{"podIP":"127.0.0.1"}}""");

        var svc = NewService(handler);

        var act = async () => await svc.StartPreviewAsync(runId, targetPort, "user-1");
        await act.Should().ThrowAsync<PortForwardLimitExceededException>();
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

    private static string RouteJson(string token, string sanitizedRun) =>
        "{\"metadata\":{\"annotations\":{" +
        "\"agentweaver.dev/preview-token\":\"" + token + "\"," +
        "\"agentweaver.dev/preview-run\":\"" + sanitizedRun + "\"}}}";

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static int ReserveUnusedLocalPort()
    {
        using var listener = StartListener(out var port);
        return port;
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
    public sealed record Req(string Method, string Path, string? Body);

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
        var reqBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        Requests.Add(new Req(method, path, reqBody));

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
