using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.AgentRuntime.Workflow;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// <see cref="IWorkflowAgentFactory"/> that creates <see cref="RemoteAgentProxy"/> instances
/// for <c>Sandbox:AgentExecutionMode=pod-per-run</c>.
///
/// <para>
/// All agent types (worker, Rai, Rubberduck, Scribe) are remoted via the same A2A seam:
/// the pod's <c>MapA2A</c>-hosted <c>CopilotAIAgent</c> handles all roles. The MAF graph,
/// <c>WorkflowEvents</c>, <c>RequestPort</c>, and <c>CheckpointManager</c> all stay in the
/// worker — only the leaf agent turn executes in the pod (§3.1, §4.7.5).
/// </para>
///
/// <para>
/// <b>Checkpoint proxy (Q2):</b> <see cref="RemoteAgentProxy"/> carries no
/// <c>ICheckpointStore</c>. The worker's file-backed (P1) or DB-backed (P2)
/// <c>CheckpointManager</c> owns all checkpoints. The pod receives setup params over A2A
/// and has no database connection.
/// </para>
/// </summary>
internal sealed class RemoteWorkflowAgentFactory : IWorkflowAgentFactory
{
    private readonly ISandboxAgentEndpointResolver _endpointResolver;
    private readonly IAgentHostTurnTokenRegistry _turnTokenRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RemoteAgentProxyOptions _proxyOptions;
    private readonly string _remoteApiBaseUrl;

    public RemoteWorkflowAgentFactory(
        ISandboxAgentEndpointResolver endpointResolver,
        IAgentHostTurnTokenRegistry turnTokenRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IOptions<RemoteAgentProxyOptions> proxyOptions,
        IConfiguration configuration)
    {
        _endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        _turnTokenRegistry = turnTokenRegistry ?? throw new ArgumentNullException(nameof(turnTokenRegistry));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _proxyOptions = proxyOptions?.Value ?? throw new ArgumentNullException(nameof(proxyOptions));
        _remoteApiBaseUrl = ResolveRemoteApiBaseUrl(configuration);
    }

    public IWorkflowTurnAgent CreateWorkerAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateRaiAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateRubberduckAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateBuildTestAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateScribeAgent() => CreateProxy();

    private RemoteAgentProxy CreateProxy() =>
        new(
            _endpointResolver,
            _httpClientFactory,
            _loggerFactory,
            _remoteApiBaseUrl,
            _turnTokenRegistry,
            _proxyOptions);

    internal static string ResolveRemoteApiBaseUrl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["Agentweaver:RemoteApiBaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            throw new InvalidOperationException(
                "Agentweaver:RemoteApiBaseUrl is required when Sandbox:AgentExecutionMode=pod-per-run.");
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || IsLoopbackOrWildcard(uri))
        {
            throw new InvalidOperationException(
                "Agentweaver:RemoteApiBaseUrl must be an absolute HTTP(S) URL with a non-loopback, " +
                "non-wildcard host and no user information.");
        }

        return configured;
    }

    private static bool IsLoopbackOrWildcard(Uri uri)
    {
        var host = uri.Host;
        if (uri.IsLoopback
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host is "*" or "+")
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address)
            && (IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any));
    }
}
