namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Creates the per-run workflow agents (worker, Rai, Rubberduck, Scribe). Resolved from DI so the
/// production implementation builds real <see cref="CopilotAIAgent"/>-derived agents wired to
/// the GitHub Copilot SDK, while tests can substitute a fake factory. A fresh agent instance
/// is returned on every call — workflow agents are single-run and own their inner SDK client.
/// </summary>
public interface IWorkflowAgentFactory
{
    /// <summary>Creates the worker agent that performs the project run's code changes.</summary>
    IWorkflowTurnAgent CreateWorkerAgent();

    /// <summary>Creates the Responsible AI (Rai) review agent.</summary>
    IWorkflowTurnAgent CreateRaiAgent();

    /// <summary>Creates the rubber-duck critique agent.</summary>
    IWorkflowTurnAgent CreateRubberduckAgent();

    /// <summary>Creates the Scribe memory-keeper agent.</summary>
    IWorkflowTurnAgent CreateScribeAgent();
}
