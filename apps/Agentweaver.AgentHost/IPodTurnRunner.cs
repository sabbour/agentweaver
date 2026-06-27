using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Minimal seam over the per-turn execution surface of <see cref="CopilotAIAgent"/> that the
/// pod-side <see cref="A2ATurnBridgeAgent"/> drives: attach a per-turn run-event writer, then run
/// the turn. Extracted so the bridge's setup-in / events-out behavior can be unit-tested without
/// a fully provisioned Copilot client (spec-018 P1.5).
/// </summary>
internal interface IPodTurnRunner
{
    /// <summary>Attaches (or clears, with <see langword="null"/>) the per-turn RunEvent writer.</summary>
    void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter);

    /// <summary>Runs a single agent turn, returning the accumulated assistant text.</summary>
    Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IPodTurnRunner"/> that forwards to the pod's singleton
/// <see cref="CopilotAIAgent"/>.
/// </summary>
internal sealed class CopilotPodTurnRunner : IPodTurnRunner
{
    private readonly CopilotAIAgent _agent;

    public CopilotPodTurnRunner(CopilotAIAgent agent) =>
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) =>
        _agent.SetTurnStreamWriter(streamWriter);

    public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken) =>
        _agent.RunTurnAsync(task, isRevision, cancellationToken);
}
