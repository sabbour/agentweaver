using k8s;
using Agentweaver.SandboxExec;
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

    public SandboxExecutorRouter(IConfiguration config, ILoggerFactory loggerFactory,
        IPodNameRegistry? podRegistry = null)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _podRegistry = podRegistry;
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
            return SandboxExecutorFactory.Create(logger);
        }

        try
        {
            var k8sConfig = KubernetesClientConfiguration.InClusterConfig();
            var k8sClient = new Kubernetes(k8sConfig);
            var sandboxOptions = new KubernetesSandboxOptions
            {
                Namespace = _config["Sandbox:Kubernetes:Namespace"] ?? "agentweaver",
                TemplateRef = _config["Sandbox:Kubernetes:TemplateRef"] ?? "agentweaver-sandbox",
                TimeoutSeconds = int.TryParse(
                    _config["Sandbox:Kubernetes:TimeoutSeconds"], out int t) ? t : 600,
            };
            var k8sLogger = _loggerFactory.CreateLogger<KubernetesSandboxExecutor>();
            logger.LogInformation(
                "SandboxExecutorRouter: selecting KubernetesSandboxExecutor (namespace={Namespace})",
                sandboxOptions.Namespace);
            return new KubernetesSandboxExecutor(k8sClient, sandboxOptions, k8sLogger, _podRegistry);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "SandboxExecutorRouter: in-cluster Kubernetes executor initialization failed. " +
                "Fail-closed: will not fall back to a local executor.", ex);
        }
    }
}
