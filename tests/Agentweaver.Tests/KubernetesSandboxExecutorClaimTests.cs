using System.Reflection;
using System.Text.Json;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies <see cref="KubernetesSandboxExecutor"/> emits SandboxClaim bodies that match the
/// installed agent-sandbox <b>v1beta1</b> CRD schema (confirmed via
/// <c>kubectl explain sandboxclaims.spec --api-version=extensions.agents.x-k8s.io/v1beta1</c>):
///   • <c>apiVersion: extensions.agents.x-k8s.io/v1beta1</c>;
///   • <c>spec.warmPoolRef.name</c> (the claim binds to a SandboxWarmPool);
///   • <c>spec.lifecycle.ttlSecondsAfterFinished</c> (integer);
///   • NO <c>spec.templateRef</c> and NO <c>spec.ttl</c> (both were pruned by the API server in the
///     old v1alpha1 <c>templateRef</c>/<c>ttl</c> body, leaving an empty spec → 422 / no Sandbox).
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
        new(ClientFor(handler), Options(), NullLogger<KubernetesSandboxExecutor>.Instance, podRegistry: null);

    private static KubernetesSandboxExecutor NewExecutor(
        FakeKubeHandler handler, IRunSubmittingUserResolver submittingUserResolver) =>
        new(ClientFor(handler), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, readinessProbe: null, submittingUserResolver: submittingUserResolver);

    private sealed class StubSubmittingUserResolver : IRunSubmittingUserResolver
    {
        private readonly string? _user;
        public StubSubmittingUserResolver(string? user) => _user = user;
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_user);
    }

    private static JsonElement SpecOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("spec").Clone();
    }

    [Fact]
    public async Task LaunchAgentHostPod_posts_v1beta1_claim_with_warmPoolRef_and_no_templateRef()
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
            .Should().Be("agentweaver-agent-host", "agent-host claims bind to the agent-host warm pool");
        spec.GetProperty("lifecycle").GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        spec.GetProperty("lifecycle").GetProperty("shutdownPolicy").GetString().Should().Be("Delete");
        spec.TryGetProperty("templateRef", out _).Should().BeFalse("the deprecated templateRef key must be gone");
        spec.TryGetProperty("sandboxTemplateRef", out _).Should().BeFalse("claims reference a warm pool, not a template");
        spec.TryGetProperty("ttl", out _).Should().BeFalse("the non-existent ttl field must be gone");
        // Per-run env must still be injected.
        spec.GetProperty("env").EnumerateArray()
            .Should().Contain(e => e.GetProperty("name").GetString() == "AgentHost__RunId");
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
        spec.GetProperty("lifecycle").GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        spec.GetProperty("lifecycle").GetProperty("shutdownPolicy").GetString().Should().Be("Delete");
        spec.TryGetProperty("templateRef", out _).Should().BeFalse();
        spec.TryGetProperty("ttl", out _).Should().BeFalse();
    }

    [Fact]
    public async Task LaunchAgentHostPod_injects_AgentHost__UserId_from_submitting_user()
    {
        const string runId = "run-claim-user";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var executor = NewExecutor(handler, new StubSubmittingUserResolver("sabbour"));

        await executor.LaunchAgentHostPodAsync(runId);

        var post = handler.Requests.Should().ContainSingle(r =>
            r.Method == "POST" && r.Path.EndsWith("/sandboxclaims")).Subject;

        var env = SpecOf(post.Body!).GetProperty("env");
        env.EnumerateArray().Should().Contain(e =>
            e.GetProperty("name").GetString() == "AgentHost__UserId" &&
            e.GetProperty("value").GetString() == "sabbour",
            "the run's submitting user must be injected so the pod loads the user's Copilot token");
    }

    [Fact]
    public async Task LaunchAgentHostPod_omits_AgentHost__UserId_when_no_submitting_user()
    {
        const string runId = "run-claim-nouser";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        // No resolver configured at all → no AgentHost__UserId.
        var executor = NewExecutor(handler);

        await executor.LaunchAgentHostPodAsync(runId);

        var post = handler.Requests.Should().ContainSingle(r =>
            r.Method == "POST" && r.Path.EndsWith("/sandboxclaims")).Subject;

        var env = SpecOf(post.Body!).GetProperty("env");
        env.EnumerateArray().Should().NotContain(e =>
            e.GetProperty("name").GetString() == "AgentHost__UserId",
            "without a resolved submitting user, AgentHost__UserId must be omitted (not blank)");
    }
}
