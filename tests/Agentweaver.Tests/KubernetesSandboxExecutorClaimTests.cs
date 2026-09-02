extern alias agenthost;

using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostCredentialProvider = agenthost::Agentweaver.AgentHost.AgentHostGitHubCapabilityCredentialProvider;
using ConfigureRequest = agenthost::ConfigureRequest;

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
        ToolApprovalApiBaseUrl = "https://agentweaver-api.internal",
    };

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    // Chains multiple handlers (KubernetesClient makes handlers[0] outermost and fast-forwards to the
    // terminal FakeKubeHandler), used to inject transient faults ahead of the fake API (issue #230).
    private static IKubernetes ClientFor(params DelegatingHandler[] handlers) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handlers);

    private static KubernetesSandboxExecutor NewExecutor(FakeKubeHandler handler) =>
        NewExecutor(handler, new StubSubmittingUserResolver("sabbour"));

    private static KubernetesSandboxExecutor NewExecutor(
        FakeKubeHandler handler, IRunSubmittingUserResolver submittingUserResolver,
        IHttpClientFactory? httpClientFactory = null, IRunOptionsStore? runOptions = null,
        IPodNameRegistry? podRegistry = null,
        Agentweaver.Api.Sandbox.Preview.ISandboxPreviewService? previewService = null,
        IGitHubCopilotCapabilityCredentialProvider? copilotCredentials = null,
        IByokProviderConfigurationProvider? byokProviderConfiguration = null,
        Func<ProjectId?, CancellationToken, Task<EffectiveModelProviderResult>>? effectiveProviderResolver = null) =>
        new(ClientFor(handler), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: podRegistry, readinessProbe: null, submittingUserResolver: submittingUserResolver,
            httpClientFactory: httpClientFactory, runOptions: runOptions,
            copilotCredentials: copilotCredentials ?? new FixedGitHubCopilotCapabilityCredentialProvider(),
            previewService: previewService,
            byokProviderConfiguration: byokProviderConfiguration,
            effectiveProviderResolver: effectiveProviderResolver);

    private sealed class StubSubmittingUserResolver : IRunSubmittingUserResolver
    {
        private readonly string? _user;
        private readonly string? _projectId;
        public StubSubmittingUserResolver(string? user, string? projectId = null)
        {
            _user = user;
            _projectId = projectId;
        }
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_user);
        public Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<(string? ProjectId, string? AgentName)> GetRunIdentityAsync(
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>((_projectId, null));
    }

    // Records the /configure POST so the warm-pool deferred-config contract can be asserted.
    private sealed class RecordingConfigureHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public RecordingConfigureHandler(
            string responseBody = """{"configured":true}""",
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    // Records turn-token registration so tests can assert a claim was treated as CREATED (which
    // registers a token) vs the silent "reuse already-configured claim" path (which does not).
    private sealed class RecordingTurnTokenRegistry : IAgentHostTurnTokenRegistry
    {
        private readonly Dictionary<string, string> _tokens = new();
        public void RegisterTurnToken(string runId, string token) => _tokens[runId] = token;
        public void UnregisterTurnToken(string runId) => _tokens.Remove(runId);
        public string? TryGetTurnToken(string runId) => _tokens.TryGetValue(runId, out var t) ? t : null;
    }

    // Fault-injecting Kubernetes handler for issue #230. Placed OUTSIDE the terminal FakeKubeHandler
    // in the client pipeline. Throws `fault()` on the first `failCount` requests matching `match`,
    // then returns `afterFault()` (when supplied) for the next matching request, otherwise delegates
    // to the inner FakeKubeHandler. Honors cancellation first so a pre-canceled token surfaces as
    // OperationCanceledException, mirroring the real transport.
    private sealed class FailFirstKubeHandler : DelegatingHandler
    {
        private readonly int _failCount;
        private readonly Func<Exception> _fault;
        private readonly Predicate<HttpRequestMessage> _match;
        private readonly Func<HttpResponseMessage>? _afterFault;

        public int MatchedRequests { get; private set; }

        public FailFirstKubeHandler(
            int failCount, Func<Exception> fault, Predicate<HttpRequestMessage> match,
            Func<HttpResponseMessage>? afterFault = null)
        {
            _failCount = failCount;
            _fault = fault;
            _match = match;
            _afterFault = afterFault;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_match(request))
            {
                MatchedRequests++;
                if (MatchedRequests <= _failCount)
                    throw _fault();
                if (_afterFault is not null)
                {
                    var resp = _afterFault();
                    resp.RequestMessage = request;
                    return Task.FromResult(resp);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ConflictFirstClaimHandler : DelegatingHandler
    {
        public int ClaimCreateRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (IsClaimPost(request) && ++ClaimCreateRequests == 1)
            {
                var response = ConflictResponse();
                response.RequestMessage = request;
                return Task.FromResult(response);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // The exact transient chain from issue #230: HttpRequestException → IOException → SocketException 104.
    private static Exception ConnectionReset() =>
        new HttpRequestException(
            "Connection reset by peer",
            new IOException(
                "Connection reset by peer",
                new SocketException((int)SocketError.ConnectionReset)));

    private static bool IsClaimPost(HttpRequestMessage r) =>
        r.Method == HttpMethod.Post && (r.RequestUri?.AbsolutePath.EndsWith("/sandboxclaims") ?? false);

    // A 409 the KubernetesClient surfaces as HttpOperationException(Response.StatusCode = Conflict).
    private static HttpResponseMessage ConflictResponse() =>
        new(System.Net.HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"AlreadyExists","code":409}"""),
        };

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
    public async Task LaunchAgentHostPod_configures_warm_pod_with_immutable_capability_credential()
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
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        await executor.LaunchAgentHostPodAsync(runId);

        configureHandler.RequestUri.Should().Be("http://10.0.0.7:8088/configure",
            "the warm pod is configured at its bound IP after readiness");
        configureHandler.Body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.GetProperty("runId").GetString().Should().Be(runId);
        body.GetProperty("userId").GetString().Should().Be("sabbour");
        body.GetProperty("turnBearerToken").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("toolApprovalApiBaseUrl").GetString().Should().Be("https://agentweaver-api.internal");
        var credential = body.GetProperty("copilotCredential");
        credential.GetProperty("snapshotReference").GetString().Should().Be("snapshot-test");
        credential.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        credential.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);

        var configuredState = new AgentHostRuntimeState();
        var configureRequest = JsonSerializer.Deserialize<ConfigureRequest>(
            configureHandler.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        configureRequest.Should().NotBeNull();
        configuredState.TryConfigure(configureRequest!.ToRunConfiguration()).Should().BeTrue();
        configuredState.ToolApprovalApiAccess.Should().NotBeNull(
            "the real warm-pool configure payload must enable lifecycle-aware policy reads");
        configuredState.ToolApprovalApiAccess!.BearerToken.Should().Be(
            body.GetProperty("turnBearerToken").GetString());
    }

    [Fact]
    public async Task LaunchAgentHostPod_with_byok_configuration_posts_provider_without_copilot_credential()
    {
        const string runId = "run-byok-configure";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var configureHandler = new RecordingConfigureHandler();
        var executor = NewExecutor(
            handler,
            new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            byokProviderConfiguration: new FixedByokProviderConfigurationProvider(
                new ByokProviderConfiguration(
                    Id: "test-provider",
                    Name: "Test Azure provider",
                    Type: "azure",
                    BaseUrl: "https://byok-resource.openai.azure.com",
                    Model: "gpt-4.1",
                    ApiKey: "test-byok-key")));

        await executor.LaunchAgentHostPodAsync(runId);

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.TryGetProperty("copilotCredential", out var copilotCredential).Should().BeTrue();
        copilotCredential.ValueKind.Should().Be(JsonValueKind.Null,
            "BYOK AgentHost launches must not require or transmit a Copilot capability snapshot");
        var byok = body.GetProperty("byokProviderConfiguration");
        byok.GetProperty("type").GetString().Should().Be("azure");
        byok.GetProperty("baseUrl").GetString().Should().Be("https://byok-resource.openai.azure.com");
        byok.GetProperty("model").GetString().Should().Be("gpt-4.1");
        byok.GetProperty("apiKey").GetString().Should().Be("test-byok-key");
    }

    [Fact]
    public async Task Router_propagates_byok_provider_to_kubernetes_executor_without_requesting_copilot()
    {
        var runId = RunId.New().ToString();
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var configureHandler = new RecordingConfigureHandler();
        var byokProvider = new FixedByokProviderConfigurationProvider(
            new ByokProviderConfiguration(
                Id: "router-provider",
                Name: "Router BYOK provider",
                Type: "openai",
                BaseUrl: "https://models.example.com",
                Model: "gpt-5",
                ApiKey: "router-byok-key"));
        var scopeProvider = new ServiceCollection().BuildServiceProvider();
        var router = new SandboxExecutorRouter(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:Backend"] = "kubernetes",
                })
                .Build(),
            NullLoggerFactory.Instance,
            byokProvider,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new UnexpectedGitHubCopilotCapabilityCredentialProvider(),
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.Byok("router-provider", "openai")),
            isInCluster: () => false,
            kubernetesClientFactory: () => ClientFor(handler));
        var executor = router.Resolve().Should().BeOfType<KubernetesSandboxExecutor>().Subject;

        await executor.LaunchAgentHostPodAsync(runId);

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.GetProperty("copilotCredential").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("byokProviderConfiguration").GetProperty("apiKey").GetString()
            .Should().Be("router-byok-key");
    }

    [Fact]
    public async Task Unavailable_project_copilot_binding_does_not_fall_back_to_active_platform_byok()
    {
        var projectId = ProjectId.New();
        var byokProvider = new FixedByokProviderConfigurationProvider(
            new ByokProviderConfiguration(
                Id: "platform-provider",
                Name: "Platform BYOK provider",
                Type: "openai",
                BaseUrl: "https://models.example.com",
                Model: "gpt-5",
                ApiKey: "platform-key"));
        var executor = NewExecutor(
            new FakeKubeHandler(),
            new StubSubmittingUserResolver("sabbour", projectId.ToString()),
            copilotCredentials: new NullGitHubCopilotCapabilityCredentialProvider(),
            byokProviderConfiguration: byokProvider,
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.Unavailable(
                    "The project's active GitHub Copilot binding credential is unavailable.")));

        var act = () => executor.LaunchAgentHostPodAsync(RunId.New().ToString());

        var exception = await act.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    [Fact]
    public void Router_requires_byok_provider_configuration_wiring()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<ISandboxExecutorRouter, SandboxExecutorRouter>()
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<ISandboxExecutorRouter>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IByokProviderConfigurationProvider*");
    }

    [Fact]
    public async Task LaunchAgentHostPod_fails_closed_without_run_capability_credential()
    {
        var executor = new KubernetesSandboxExecutor(
            ClientFor(new FakeKubeHandler()),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"));

        var act = () => executor.LaunchAgentHostPodAsync("run-missing-capability");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*live run-bound Copilot capability snapshot*");
    }

    /// <summary>
    /// Regression for the recurring "Cannot launch AgentHost pod ... without a live run-bound
    /// Copilot capability snapshot" production incident: a capability provider IS configured (the
    /// run's snapshot metadata exists and was accepted at prepare time), but the credential it
    /// resolves to could not be redeemed — e.g. the bound GitHub Copilot App connection's Key
    /// Vault secret is missing/stale (observed live via
    /// "Copilot App connection for project ... has an active binding record but its credential
    /// secret is missing."). This is a normal, user-actionable "reconnect GitHub" condition and
    /// must surface as <see cref="ModelProviderConnectionRequiredException"/> (which the frontend
    /// already renders as a "Connect GitHub" CTA), not as an opaque internal
    /// <see cref="InvalidOperationException"/> that gets wrapped into a generic 500.
    /// </summary>
    [Fact]
    public async Task LaunchAgentHostPod_surfaces_connection_required_when_configured_credential_provider_cannot_redeem()
    {
        var projectId = ProjectId.New();
        var executor = NewExecutor(
            new FakeKubeHandler(),
            new StubSubmittingUserResolver("sabbour", projectId.ToString()),
            copilotCredentials: new NullGitHubCopilotCapabilityCredentialProvider());

        var act = () => executor.LaunchAgentHostPodAsync("run-with-stale-snapshot");

        var exception = await act.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    private sealed class NullGitHubCopilotCapabilityCredentialProvider : IGitHubCopilotCapabilityCredentialProvider
    {
        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult<GitHubCapabilitySnapshotCredential?>(null);
    }

    [Fact]
    public async Task AgentHostCredentialProvider_allows_only_configured_live_run_credential()
    {
        var state = new AgentHostRuntimeState();
        var credential = new GitHubCapabilitySnapshotCredential(
            "snapshot-run-credential",
            "capability-token",
            DateTimeOffset.UtcNow.AddMinutes(5));
        state.TryConfigure(
            "run-credential",
            "sabbour",
            "turn-token",
            credential).Should().BeTrue();
        var provider = new AgentHostCredentialProvider(state);

        (await provider.GetCredentialAsync("run-credential")).Should().BeSameAs(credential);
        (await provider.GetCredentialAsync("other-run")).Should().BeNull(
            "a Host credential must remain bound to its configured run");

        var expiredState = new AgentHostRuntimeState();
        expiredState.TryConfigure(
            "run-expired",
            "sabbour",
            "turn-token",
            credential with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }).Should().BeTrue();
        var expiredProvider = new AgentHostCredentialProvider(expiredState);
        (await expiredProvider.GetCredentialAsync("run-expired")).Should().BeNull(
            "an expired capability credential must fail closed");
    }

    [Fact]
    public async Task LaunchAgentHostPod_operator_configure_carries_platform_caller_token_separately()
    {
        const string runId = "run-claim-operator-caller";
        const string callerBearerToken = "entra-platform-bearer-token";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var configureHandler = new RecordingConfigureHandler();
        var executor = NewExecutor(
            handler,
            new StubSubmittingUserResolver("entra-object-id"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        await executor.LaunchAgentHostPodAsync(
            runId,
            new AgentHostLaunchContext(
                SharedWorkingDirectory: null,
                Purpose: AgentHostPurpose.OperatorAssistant,
                CallerBearerToken: callerBearerToken));

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.GetProperty("callerBearerToken").GetString().Should().Be(callerBearerToken);
        body.GetProperty("copilotCredential").GetProperty("snapshotReference").GetString().Should().Be("snapshot-test");
        body.GetProperty("copilotCredential").GetProperty("accessToken").GetString().Should().NotBe(callerBearerToken,
            "the Entra platform credential and Copilot capability have different trust purposes");
    }

    [Fact]
    public async Task LaunchAgentHostPod_operator_recreates_existing_claim_before_sending_current_token()
    {
        const string runId = "run-claim-operator-refresh";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var fake = new FakeKubeHandler();
        fake.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-2"}}}""");
        fake.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-2$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-2"},"status":{"podIP":"10.0.0.8"}}""");

        var conflictFirst = new ConflictFirstClaimHandler();
        var configureHandler = new RecordingConfigureHandler();
        var turnTokens = new RecordingTurnTokenRegistry();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(conflictFirst, fake),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            turnTokenRegistry: turnTokens,
            readinessProbe: null,
            submittingUserResolver: new StubSubmittingUserResolver("entra-object-id"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        await executor.LaunchAgentHostPodAsync(
            runId,
            new AgentHostLaunchContext(
                SharedWorkingDirectory: null,
                Purpose: AgentHostPurpose.OperatorAssistant,
                CallerBearerToken: "current-entra-token"));

        conflictFirst.ClaimCreateRequests.Should().Be(2,
            "an existing one-shot-configured operator pod must be replaced before a refreshed bearer is delivered");
        fake.Requests.Should().Contain(request =>
            request.Method == "DELETE" && request.Path.EndsWith($"/sandboxclaims/{claimName}"));
        using var doc = JsonDocument.Parse(configureHandler.Body!);
        doc.RootElement.GetProperty("callerBearerToken").GetString().Should().Be("current-entra-token");
        turnTokens.TryGetTurnToken(runId).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LaunchAgentHostPod_configure_body_carries_assembly_purpose_and_immutable_source_refs()
    {
        const string runId = "run-claim-assembly";
        const string commitSha = "1111111111111111111111111111111111111111";
        const string treeHash = "2222222222222222222222222222222222222222";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var configureHandler = new RecordingConfigureHandler();
        var executor = NewExecutor(
            handler,
            new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        await executor.LaunchAgentHostPodAsync(
            runId,
            new AgentHostLaunchContext(
                SharedWorkingDirectory: "/workspace/reviewer",
                SourceRepositoryPath: "/workspace/repository",
                SourceRef: "agentweaver/integration/run-claim-assembly",
                BaseCommitSha: commitSha,
                ExpectedTreeHash: treeHash,
                WorkspaceMode: ExecutionWorkspaceMode.LocalReadOnly,
                Purpose: AgentHostPurpose.AssemblyBuildTest,
                ScratchRoot: PodLocalExecutionWorkspace.DefaultScratchRoot));

        using var doc = JsonDocument.Parse(configureHandler.Body!);
        var body = doc.RootElement;
        body.GetProperty("purpose").GetString().Should().Be("AssemblyBuildTest");
        body.GetProperty("workspaceMode").GetString().Should().Be("LocalReadOnly");
        body.GetProperty("sharedWorkingDirectory").GetString().Should()
            .Be(Path.GetFullPath("/workspace/reviewer"));
        body.GetProperty("sourceRepositoryPath").GetString().Should().Be("/workspace/repository");
        body.GetProperty("sourceRef").GetString().Should().Be("agentweaver/integration/run-claim-assembly");
        body.GetProperty("baseCommitSha").GetString().Should().Be(commitSha);
        body.GetProperty("expectedTreeHash").GetString().Should().Be(treeHash);
        body.GetProperty("scratchRoot").GetString().Should()
            .Be(PodLocalExecutionWorkspace.DefaultScratchRoot);
    }

    [Fact]
    public async Task LaunchAgentHostPod_persists_effective_working_directory_from_configure_success()
    {
        const string runId = "run-claim-effective-workspace";
        const string effectiveWorkingDirectory = "/local-workspace/run-claim-effective-workspace/actual-tree";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var podRegistry = new PodNameRegistry();
        var configureHandler = new RecordingConfigureHandler(
            $$"""{"configured":true,"effectiveWorkingDirectory":"{{effectiveWorkingDirectory}}"}""");
        var executor = NewExecutor(
            handler,
            new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            podRegistry: podRegistry);

        await executor.LaunchAgentHostPodAsync(
            runId,
            new AgentHostLaunchContext(
                SharedWorkingDirectory: "/workspace/reviewer",
                SourceRepositoryPath: "/workspace/repository",
                SourceRef: "agentweaver/integration/run-claim-effective-workspace",
                BaseCommitSha: new string('1', 40),
                ExpectedTreeHash: new string('2', 40),
                WorkspaceMode: ExecutionWorkspaceMode.LocalReadOnly,
                Purpose: AgentHostPurpose.AssemblyBuildTest,
                ScratchRoot: PodLocalExecutionWorkspace.DefaultScratchRoot));

        podRegistry.TryGetEffectiveWorkingDirectory(runId).Should().Be(effectiveWorkingDirectory);
    }

    [Fact]
    public async Task Launch_context_compatibility_fallback_uses_shared_working_directory_only()
    {
        var compatibilityLifecycle = new CompatibilityAgentHostLifecycle();
        IAgentHostPodLifecycle lifecycle = compatibilityLifecycle;

        await lifecycle.LaunchAgentHostPodAsync(
            "run-fallback",
            new AgentHostLaunchContext(
                SharedWorkingDirectory: "/workspace/source",
                SourceRepositoryPath: "/workspace/repository",
                SourceRef: "integration",
                BaseCommitSha: new string('1', 40),
                ExpectedTreeHash: new string('2', 40),
                WorkspaceMode: ExecutionWorkspaceMode.LocalReadOnly,
                Purpose: AgentHostPurpose.AssemblyBuildTest,
                ScratchRoot: "/local-workspace"));

        compatibilityLifecycle.CapturedWorkingDirectory.Should().Be("/workspace/source",
            "the compatibility seam must never pass a pod-internal local path as an API-visible worktree");
    }

    [Fact]
    public async Task LaunchAgentHostPod_configure_body_carries_autoApproveTools_from_run_options()
    {
        // Bug #221: the per-run AutoApproveTools flag must ride the /configure body so the warm pod
        // seeds its own IRunOptionsStore and its HITL gate can auto-approve web_fetch under autopilot.
        const string runId = "run-claim-autoapprove";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        var runOptions = new InMemoryRunOptionsStore();
        runOptions.Set(runId, new RunOptions(AutoApproveTools: true));

        var configureHandler = new RecordingConfigureHandler();
        var executor = NewExecutor(
            handler, new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler), runOptions: runOptions);

        await executor.LaunchAgentHostPodAsync(runId);

        configureHandler.Body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(configureHandler.Body!);
        doc.RootElement.GetProperty("autoApproveTools").GetBoolean().Should().BeTrue(
            "the per-run AutoApproveTools flag must be propagated to the warm pod (bug #221)");
    }

    [Fact]
    public async Task LaunchAgentHostPod_configure_body_defaults_autoApproveTools_false_without_run_options()
    {
        // No IRunOptionsStore injected (unit-test null-skip): the flag defaults false rather than
        // throwing, matching the existing optional-dependency convention in the executor.
        const string runId = "run-claim-autoapprove-default";
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
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        await executor.LaunchAgentHostPodAsync(runId);

        configureHandler.Body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(configureHandler.Body!);
        doc.RootElement.GetProperty("autoApproveTools").GetBoolean().Should().BeFalse();
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

    // =========================================================================
    // Issue #230: a transient k8s connection reset during claim create must be retried, not fail
    // the subtask fatally. The KEY regression is that a retry that observes a 409 for OUR OWN create
    // (committed server-side before the reset) is treated as CREATED — the pod is fully configured,
    // never left on the silent "reuse already-configured claim" path.
    // =========================================================================
    [Fact]
    public async Task LaunchAgentHostPod_retries_transient_reset_on_claim_create_then_succeeds()
    {
        const string runId = "run-claim-retry-ok";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var fake = new FakeKubeHandler();
        fake.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        fake.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        // First POST /sandboxclaims throws a connection reset; the retry delegates to the echo (200).
        var fault = new FailFirstKubeHandler(failCount: 1, fault: ConnectionReset, match: IsClaimPost);

        var turnTokens = new RecordingTurnTokenRegistry();
        var configureHandler = new RecordingConfigureHandler();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(fault, fake), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, turnTokenRegistry: turnTokens, readinessProbe: null,
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        var endpoint = await executor.LaunchAgentHostPodAsync(runId);

        endpoint.Should().Contain("10.0.0.7").And.Contain("8088",
            "a transient connection reset on claim create must be retried, not fail the launch (#230)");
        fault.MatchedRequests.Should().Be(2, "the create is attempted twice: initial reset + successful retry");

        configureHandler.RequestUri.Should().Be("http://10.0.0.7:8088/configure",
            "the warm pod must be configured after the retry succeeds");
        turnTokens.TryGetTurnToken(runId).Should().NotBeNullOrEmpty(
            "a successfully created claim registers the run's turn token");
    }

    [Fact]
    public async Task LaunchAgentHostPod_treats_409_after_own_create_reset_as_created_and_configures()
    {
        const string runId = "run-claim-retry-409";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var fake = new FakeKubeHandler();
        fake.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        fake.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");

        // First POST throws a reset AFTER the server committed our claim; the retry observes 409.
        var fault = new FailFirstKubeHandler(
            failCount: 1, fault: ConnectionReset, match: IsClaimPost, afterFault: ConflictResponse);

        var turnTokens = new RecordingTurnTokenRegistry();
        var configureHandler = new RecordingConfigureHandler();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(fault, fake), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, turnTokenRegistry: turnTokens, readinessProbe: null,
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"),
            httpClientFactory: new StubHttpClientFactory(configureHandler),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        var endpoint = await executor.LaunchAgentHostPodAsync(runId);

        endpoint.Should().Contain("10.0.0.7").And.Contain("8088");
        fault.MatchedRequests.Should().Be(2, "initial reset + the retry that observes the 409");

        // KEY regression (#230): a retry-409 means OUR create committed before the reset — the pod
        // must be fully configured, NOT left on the silent "reuse already-configured claim" path.
        configureHandler.RequestUri.Should().Be("http://10.0.0.7:8088/configure",
            "a retry-409 is our own create → /configure must still run (not the reuse path)");
        turnTokens.TryGetTurnToken(runId).Should().NotBeNullOrEmpty(
            "a retry-409 is our own create → the run's turn token must be registered (not the reuse path)");
    }

    [Fact]
    public async Task LaunchAgentHostPod_precanceled_token_is_not_retried_and_throws_promptly()
    {
        const string runId = "run-claim-canceled";

        var fake = new FakeKubeHandler();
        // A reset is configured, but a pre-canceled caller token must short-circuit BEFORE any retry.
        var fault = new FailFirstKubeHandler(failCount: 1, fault: ConnectionReset, match: IsClaimPost);

        var executor = new KubernetesSandboxExecutor(
            ClientFor(fault, fake), Options(), NullLogger<KubernetesSandboxExecutor>.Instance,
            podRegistry: null, turnTokenRegistry: new RecordingTurnTokenRegistry(), readinessProbe: null,
            submittingUserResolver: new StubSubmittingUserResolver("sabbour"),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await executor.Invoking(e => e.LaunchAgentHostPodAsync(runId, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>(
                "a pre-canceled caller token must never be retried and must surface promptly (#230)");
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "cancellation must abort without waiting on any backoff delay");
        fault.MatchedRequests.Should().BeLessThanOrEqualTo(1,
            "the create must not be retried once the caller token is canceled");
    }

    // =========================================================================
    // Issue #542: ReleaseAgentHostPodAsync must NOT delete the run's SandboxClaim (which reaps the
    // backing pod) while a live preview is still active — otherwise the returned preview URL 404s
    // before a human reviewer can open it. When no preview is active (or no preview service is wired),
    // the claim delete must proceed exactly as before so pods never leak.
    // =========================================================================
    [Fact]
    public async Task ReleaseAgentHostPod_defers_claim_delete_when_preview_active()
    {
        const string runId = "run-542-release-defer";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        var preview = new StubPreviewService(hasActivePreview: true);
        var executor = NewExecutor(
            handler, new StubSubmittingUserResolver("sabbour"),
            previewService: preview);

        await executor.ReleaseAgentHostPodAsync(runId);

        handler.Requests.Should().NotContain(
            r => r.Method == "DELETE" && r.Path.EndsWith($"/sandboxclaims/{claimName}"),
            "an active preview must defer the claim delete so the preview URL stays reachable (#542)");
        preview.ReconciledRunIds.Should().ContainSingle().Which.Should().Be(runId,
            "#579: the release path must invoke the single lifecycle transition that owns all preview protections");
    }

    [Fact]
    public async Task ReleaseAgentHostPod_deletes_claim_when_no_active_preview()
    {
        const string runId = "run-542-release-noactive";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        var preview = new StubPreviewService(hasActivePreview: false);
        var executor = NewExecutor(
            handler, new StubSubmittingUserResolver("sabbour"),
            previewService: preview);

        await executor.ReleaseAgentHostPodAsync(runId);

        handler.Requests.Should().Contain(
            r => r.Method == "DELETE" && r.Path.EndsWith($"/sandboxclaims/{claimName}"),
            "with no active preview the claim must be deleted exactly as before (no pod leak)");
        preview.ReconciledRunIds.Should().ContainSingle().Which.Should().Be(runId,
            "#579: normal cleanup is also governed by the same lifecycle transition");
    }

    [Fact]
    public async Task ReleaseAgentHostPod_deletes_claim_when_no_preview_service_configured()
    {
        const string runId = "run-542-release-nopreviewsvc";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        var executor = NewExecutor(handler, new StubSubmittingUserResolver("sabbour"));

        await executor.ReleaseAgentHostPodAsync(runId);

        handler.Requests.Should().Contain(
            r => r.Method == "DELETE" && r.Path.EndsWith($"/sandboxclaims/{claimName}"),
            "non-preview deployments (null preview service) must keep the original unconditional release");
    }

    // Minimal ISandboxPreviewService test double: only lifecycle reconciliation is exercised by the
    // release path; every other member throws so an unexpected call is caught loudly.
    private sealed class StubPreviewService : Agentweaver.Api.Sandbox.Preview.ISandboxPreviewService
    {
        private readonly PreviewLifecycleState _state;
        public StubPreviewService(bool hasActivePreview) =>
            _state = hasActivePreview ? PreviewLifecycleState.PreviewActive : PreviewLifecycleState.Previewable;

        public List<string> ReconciledRunIds { get; } = new();

        public Task<PreviewLifecycleState> ReconcilePreviewLifecycleAsync(
            string runId, CancellationToken ct = default)
        {
            ReconciledRunIds.Add(runId);
            return Task.FromResult(_state);
        }

        public bool Enabled => true;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;
        public Task<Agentweaver.Api.Sandbox.Preview.PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<Agentweaver.Api.Sandbox.Preview.PreviewSession>> ListForRunAsync(
            string runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task KeepAliveAsync(string token, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task StopPreviewAsync(string token, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<int> ReapAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class CompatibilityAgentHostLifecycle : IAgentHostPodLifecycle
    {
        public string? CapturedWorkingDirectory { get; private set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            LaunchAgentHostPodAsync(runId, workingDirectoryOverride: null, ct);

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default)
        {
            CapturedWorkingDirectory = workingDirectoryOverride;
            return Task.FromResult("http://agenthost");
        }

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedByokProviderConfigurationProvider(ByokProviderConfiguration configuration)
        : IByokProviderConfigurationProvider
    {
        public Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct) =>
            Task.FromResult<ByokProviderConfiguration?>(configuration);
    }

    private sealed class UnexpectedGitHubCopilotCapabilityCredentialProvider
        : IGitHubCopilotCapabilityCredentialProvider
    {
        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(string runId, CancellationToken ct) =>
            throw new InvalidOperationException("Copilot credentials must not be requested for an effective BYOK launch.");
    }

}
