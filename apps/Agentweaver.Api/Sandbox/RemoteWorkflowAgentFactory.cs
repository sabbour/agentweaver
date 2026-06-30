using Microsoft.Extensions.Logging;
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

    public RemoteWorkflowAgentFactory(
        ISandboxAgentEndpointResolver endpointResolver,
        IAgentHostTurnTokenRegistry turnTokenRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        _turnTokenRegistry = turnTokenRegistry ?? throw new ArgumentNullException(nameof(turnTokenRegistry));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IWorkflowTurnAgent CreateWorkerAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateRaiAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateRubberduckAgent() => CreateProxy();
    public IWorkflowTurnAgent CreateScribeAgent() => CreateProxy();

    private RemoteAgentProxy CreateProxy() =>
        new(_endpointResolver, _httpClientFactory, _loggerFactory, _turnTokenRegistry);
}
