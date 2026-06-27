using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Wire format for <see cref="IWorkflowTurnAgent.SetupAsync"/> parameters forwarded from the
/// worker to the sandbox pod's A2A-hosted <c>CopilotAIAgent</c>.
///
/// <para>
/// On the worker side (<see cref="RemoteAgentProxy"/>), these are encoded as a
/// <c>DataContent</c> with <see cref="MediaType"/> and sent as the first content part of the
/// initial A2A user message — immediately before the actual task text. The pod's
/// <c>AgentHost</c> decodes the setup part, calls <c>CopilotAIAgent.SetupAsync</c>, and then
/// processes the task text.
/// </para>
/// </summary>
public sealed class AgentSetupParams
{
    /// <summary>A2A DataPart media type that identifies this payload on both ends of the bridge.</summary>
    public const string MediaType = "application/x-agentweaver-agent-setup+json";

    public string WorkingDirectory { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
    public string RunId { get; init; } = "";
    public string? ModelId { get; init; }
    public string? SystemPromptContext { get; init; }
    public string? ProjectId { get; init; }
    public string? AgentName { get; init; }
    public string? ApiBaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? UserId { get; init; }
    public bool IsRevision { get; init; }
}

[JsonSerializable(typeof(AgentSetupParams))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AgentSetupParamsJsonContext : JsonSerializerContext { }
