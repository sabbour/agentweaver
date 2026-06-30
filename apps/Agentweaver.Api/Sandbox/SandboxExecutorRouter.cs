using k8s;
using Agentweaver.SandboxExec;
using Agentweaver.AgentRuntime.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Selects ISandboxExecutor based on:
///   1. <c>Sandbox:Backend</c> config override ("kubernetes" or "local").
///   2. <c>KUBERNETES_SERVICE_HOST</c> environment variable (implicit in-cluster probe).
///
/// Fail-closed: if running in-cluster and Kubernetes client initialization fails,
/// throws rather than silently falling back to a local executor.
/// </summary>
public sealed class SandboxExecutorRouter : ISandboxExecutorRouter
{
    private readonly IConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IAgentHostTurnTokenRegistry? _turnTokenRegistry;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IRunSubmittingUserResolver? _submittingUserResolver;

    public SandboxExecutorRouter(IConfiguration config, ILoggerFactory loggerFactory,
        IPodNameRegistry? podRegistry = null, IHttpClientFactory? httpClientFactory = null,
        IRunSubmittingUserResolver? submittingUserResolver = null,
        IAgentHostTurnTokenRegistry? turnTokenRegistry = null)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _podRegistry = podRegistry;
        _turnTokenRegistry = turnTokenRegistry;
        _httpClientFactory = httpClientFactory;
        _submittingUserResolver = submittingUserResolver;
    }

    public ISandboxExecutor Resolve()
    {
        var backendOverride = _config["Sandbox:Backend"]?.ToLowerInvariant();
        var isInCluster = SandboxExecutorFactory.IsInCluster;
        var logger = _loggerFactory.CreateLogger<SandboxExecutorRouter>();

        var useKubernetes = backendOverride == "kubernetes"
            || (isInCluster && backendOverride != "local");

        if (!useKubernetes)
        {
            logger.LogInformation(
                "SandboxExecutorRouter: selecting local executor (backend={Backend}, inCluster={InCluster})",
                backendOverride ?? "(none)", isInCluster);
            var localExecutor = SandboxExecutorFactory.Create(logger);
            if (!localExecutor.IsRealIsolation)
            {
                logger.LogWarning(
                    "⚠️ PassthroughExecutor selected — agent commands run directly on the host. Not for production use.");
            }
            return localExecutor;
        }

        try
        {
            var k8sConfig = KubernetesClientConfiguration.InClusterConfig();
            var k8sClient = new Kubernetes(k8sConfig);
            var sandboxOptions = new KubernetesSandboxOptions
            {
                Namespace = _config["Sandbox:Kubernetes:Namespace"] ?? "agentweaver",
                TemplateRef = _config["Sandbox:Kubernetes:TemplateRef"] ?? "agentweaver-sandbox",
                WarmPoolRef = _config["Sandbox:Kubernetes:WarmPoolRef"]
                    ?? _config["Sandbox:Kubernetes:TemplateRef"] ?? "agentweaver-sandbox",
                AgentHostWarmPoolRef = _config["Sandbox:Kubernetes:AgentHostWarmPoolRef"]
                    ?? "agentweaver-agent-host",
                WorkspaceMountPath = _config["Sandbox:Kubernetes:WorkspaceMountPath"]
                    ?? _config["Workspace:PersistentVolume:MountRoot"]
                    ?? _config["Workspace:Path"]
                    ?? "/workspace",
                TimeoutSeconds = int.TryParse(
                    _config["Sandbox:Kubernetes:TimeoutSeconds"], out int t) ? t : 600,
                ServiceCidr = _config["Sandbox:Kubernetes:ServiceCidr"]
                    ?? _config["Sandbox:Kubernetes:ClusterServiceCidr"],
                SandboxEgressCidrExclusions = ReadSandboxEgressCidrExclusions(),
                RequireMtls = !string.Equals(
                    _config["Sandbox:AgentHost:RequireMtls"], "false", StringComparison.OrdinalIgnoreCase),
                AgentHostHealthzPath = _config["Sandbox:Kubernetes:AgentHostHealthzPath"] ?? "/healthz",
                AgentHostReadyTimeoutSeconds = int.TryParse(
                    _config["Sandbox:Kubernetes:AgentHostReadyTimeoutSeconds"], out int rt) ? rt : 90,
                AgentHostReadyPollIntervalMs = int.TryParse(
                    _config["Sandbox:Kubernetes:AgentHostReadyPollIntervalMs"], out int ri) ? ri : 1000,
                // Option C warm-pool token fetch: same KV the API persists user tokens to.
                KvUri = _config["Sandbox:AgentHost:KeyVaultUri"]
                    ?? _config["Auth:TokenStore:KeyVaultUri"],
            };
            var k8sLogger = _loggerFactory.CreateLogger<KubernetesSandboxExecutor>();
            WarnIfServiceCidrNotExcluded(sandboxOptions, logger);

            // Readiness gate closes the A2A cold-start race (pod Running before Kestrel binds :8088).
            // Requires the named HttpClient that can reach the pod IP; skipped (null) only if no
            // IHttpClientFactory was injected (which would itself be a misconfiguration in-cluster).
            IAgentHostReadinessProbe? readinessProbe = null;
            if (_httpClientFactory is not null)
            {
                readinessProbe = new HttpAgentHostReadinessProbe(
                    _httpClientFactory,
                    TimeSpan.FromSeconds(sandboxOptions.AgentHostReadyTimeoutSeconds),
                    TimeSpan.FromMilliseconds(sandboxOptions.AgentHostReadyPollIntervalMs),
                    _loggerFactory.CreateLogger<HttpAgentHostReadinessProbe>());
            }
            else
            {
                logger.LogWarning(
                    "SandboxExecutorRouter: no IHttpClientFactory available — AgentHost readiness gate disabled. " +
                    "First A2A turns may race the cold-start Kestrel bind.");
            }

            logger.LogInformation(
                "SandboxExecutorRouter: selecting KubernetesSandboxExecutor (namespace={Namespace}, workspaceMountPath={WorkspaceMountPath})",
                sandboxOptions.Namespace, sandboxOptions.WorkspaceMountPath);
            return new KubernetesSandboxExecutor(
                k8sClient, sandboxOptions, k8sLogger, _podRegistry, _turnTokenRegistry, readinessProbe,
                _submittingUserResolver, _httpClientFactory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "SandboxExecutorRouter: in-cluster Kubernetes executor initialization failed. " +
                "Fail-closed: will not fall back to a local executor.", ex);
        }
    }

    private IReadOnlyList<string> ReadSandboxEgressCidrExclusions() =>
        _config.GetSection("Sandbox:Kubernetes:SandboxEgressCidrExclusions").Get<string[]>()
        ?? _config.GetSection("SandboxEgressCidrExclusions").Get<string[]>()
        ?? [];

    private static void WarnIfServiceCidrNotExcluded(
        KubernetesSandboxOptions options,
        ILogger<SandboxExecutorRouter> logger)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceCidr))
            return;

        var excluded = options.SandboxEgressCidrExclusions.Any(cidr =>
            string.Equals(cidr.Trim(), options.ServiceCidr.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!excluded)
        {
            logger.LogWarning(
                "Sandbox egress configuration warning: cluster service CIDR {ServiceCidr} is not listed in SandboxEgressCidrExclusions. Add it to keep sandbox egress from reaching in-cluster services.",
                options.ServiceCidr);
        }
    }
}
