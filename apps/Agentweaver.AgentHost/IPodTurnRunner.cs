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

    /// <summary>
    /// Applies the per-turn agent context (assembled system prompt with charter/memory/skills, plus
    /// project/agent identity) delivered by the worker in <c>AgentSetupParams</c> before the turn
    /// runs (spec-018 / #336). Returns <see langword="true"/> when the underlying agent was
    /// reconfigured. Default no-op so lightweight test doubles need not implement it.
    /// </summary>
    bool ApplyPerTurnContext(string? systemPromptContext, string? projectId, string? agentName) => false;

    /// <summary>Stops a turn that did not honor its cancellation token within the bridge drain bound.</summary>
    Task ForceStopTurnAsync() => Task.CompletedTask;
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

    public bool ApplyPerTurnContext(string? systemPromptContext, string? projectId, string? agentName) =>
        _agent.ApplyPerTurnContext(systemPromptContext, projectId, agentName);

    public Task ForceStopTurnAsync() => _agent.ForceStopCopilotProcessTreeAsync();
}
