using System.Text.Json;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Agent execution mode controlled by <c>Sandbox:AgentExecutionMode</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>InApi</b> (default): agent turns run in-process via <c>CopilotAIAgent</c> —
///     the instant rollback / today's behavior (§4.7.6).</item>
///   <item><b>PodPerRun</b>: agent turns are forwarded to a per-run sandbox pod via A2A
///     (<see cref="Agentweaver.AgentRuntime.Workflow.RemoteAgentProxy"/>). Activates the
///     OOM fix (spec-018 P1, §9). Gate conditions H1–H7 must hold before enabling in
///     production (README.md § Resolved Decisions Q1).</item>
/// </list>
/// </remarks>
public enum AgentExecutionMode
{
    /// <summary>
    /// In-process execution (default). All agent turns run in the worker process via
    /// <c>CopilotAIAgent</c>. This is the safe, production-default mode and the instant
    /// rollback for any A2A defect.
    /// </summary>
    InApi = 0,

    /// <summary>
    /// Remote execution via A2A. Each run's agent turns are forwarded to a per-run
    /// Kata-isolated sandbox pod hosting <c>CopilotAIAgent</c> via <c>MapA2A</c>.
    /// Checkpoint and run-event writes stay in the worker (Q2).
    /// </summary>
    PodPerRun = 1,
}

/// <summary>
/// Sandbox agent configuration. Bound from the <c>Sandbox</c> configuration section.
/// </summary>
public sealed class SandboxAgentOptions
{
    /// <summary>
    /// Controls whether agent turns execute in-process (<see cref="AgentExecutionMode.InApi"/>,
    /// default) or are remoted to a per-run sandbox pod via A2A
    /// (<see cref="AgentExecutionMode.PodPerRun"/>).
    ///
    /// <para>
    /// Config key: <c>Sandbox:AgentExecutionMode</c>. Supported string values:
    /// <c>"in-api"</c> (default) and <c>"pod-per-run"</c>.
    /// </para>
    /// </summary>
    public AgentExecutionMode AgentExecutionMode { get; init; } = AgentExecutionMode.InApi;

    /// <summary>
    /// A2A port on which the sandbox pod's <c>AgentHost</c> listens. Used by
    /// <see cref="KubernetesPodAgentEndpointResolver"/> when constructing the endpoint URI.
    /// Config key: <c>Sandbox:AgentHost:Port</c>. Default: <c>8088</c>.
    /// </summary>
    public int AgentHostPort { get; init; } = 8088;

    /// <summary>
    /// URI scheme for the pod A2A endpoint (<c>https</c> or <c>http</c>).
    /// MUST be <c>https</c> in production (H1: TLS required).
    /// Config key: <c>Sandbox:AgentHost:Scheme</c>. Default: <c>https</c>.
    /// </summary>
    public string AgentHostScheme { get; init; } = "https";

    /// <summary>
    /// A2A path on the pod where <c>MapA2A</c> is mounted.
    /// Config key: <c>Sandbox:AgentHost:A2APath</c>. Default: <c>/a2a/agent</c>.
    /// Must match the path in <c>Agentweaver.AgentHost</c> (Morpheus).
    /// </summary>
    public string AgentHostA2APath { get; init; } = "/a2a/agent";

    /// <summary>Parses the <c>Sandbox:AgentExecutionMode</c> string value.</summary>
    internal static AgentExecutionMode ParseMode(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "pod-per-run" or "podperrun" => AgentExecutionMode.PodPerRun,
            _ => AgentExecutionMode.InApi,
        };
}
