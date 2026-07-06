using System.Text;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Production <see cref="IWorkflowSelectionModel"/>: runs a single, constrained, tool-less Copilot
/// completion to classify the best workflow for a task.
///
/// <para>
/// The selection turn is intentionally non-agentic. A <see cref="GitHub.Copilot.SessionConfig"/> with
/// an empty <c>Tools</c> list is used, which means the SDK sends no tool definitions to the model.
/// The model is physically unable to emit tool calls, so the response is always plain text — never
/// tool-call narration, reasoning scaffolding, or an empty turn caused by the model ending on a tool
/// invocation. The full <see cref="Agentweaver.AgentRuntime.CopilotAIAgent"/> machinery (sandbox,
/// permission handler, tool-approval gate, session store) is not involved at all.
/// </para>
///
/// <para>
/// Any failure is swallowed and returned as <c>null</c> so the <see cref="WorkflowSelector"/>
/// falls back to the project default — workflow selection is an optimization, never a hard gate.
/// </para>
/// </summary>
public sealed class CopilotWorkflowSelectionModel : IWorkflowSelectionModel
{
    private const string SelectionCharter =
        "You are the Coordinator selecting the single best-fit functional workflow for a task. " +
        "Choose strictly from the provided candidate workflows by matching each workflow's description " +
        "to the task and team. Respond with ONLY a single JSON object — no markdown, no code fences, " +
        "no prose, no backticks. The value of \"selected\" MUST be exactly one of the candidate ids. " +
        "Example of the exact expected format: " +
        "{\"selected\": \"bug-fix\", \"rationale\": \"A one-line null check is a targeted defect fix.\"}";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ILogger<CopilotWorkflowSelectionModel> _logger;
    private readonly string? _modelId;

    public CopilotWorkflowSelectionModel(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ILogger<CopilotWorkflowSelectionModel> logger,
        IConfiguration configuration)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _logger = logger;
        _modelId = configuration["Providers:GitHubCopilot:Model"];
    }

    public async Task<string?> CompleteAsync(
        string prompt, WorkflowSelectionContext context, CancellationToken ct)
    {
        CopilotClient? client = null;
        AIAgent? agent = null;
        try
        {
            // Use the installation scope — workflow selection is a system-level classification
            // turn, not tied to any specific user's run. CallerTokenScopeProvider falls back to
            // Installation when userId is null; FixedInstallationScopeProvider always returns it.
            var scope = _scopeProvider.Resolve(null);
            client = await _copilotClientFactory.CreateClientAsync(scope, _modelId, ct).ConfigureAwait(false);
            await client.StartAsync(ct).ConfigureAwait(false);

            // Minimal session: empty Tools list means the SDK sends no tool definitions to the
            // model. The model is physically unable to emit tool calls. No sandbox, no permission
            // handler, no session store — this is a plain grounded single completion.
            var sessionConfig = new SessionConfig
            {
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = SelectionCharter,
                },
                Tools = [],
                Model = _modelId,
                EnableConfigDiscovery = false,
                Streaming = true,
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            };

            agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);

            var result = await CaptureResponseTextAsync(
                agent.RunStreamingAsync(prompt, session, options: null, ct), ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Workflow selection model completed for project {ProjectId}: {Length} chars. Raw response (truncated): {Response}",
                context.ProjectId, result?.Length ?? 0, Truncate(result));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Workflow selection model turn failed for project {ProjectId}; selector will use the default.",
                context.ProjectId);
            return null;
        }
        finally
        {
            if (agent is IAsyncDisposable disposableAgent)
                await disposableAgent.DisposeAsync().ConfigureAwait(false);
            if (client is IAsyncDisposable disposableClient)
                await disposableClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accumulates text from a streaming response using both paths the Copilot SDK may use:
    /// <list type="bullet">
    ///   <item>Incremental delta text carried as <see cref="AgentResponseUpdate.Text"/>.</item>
    ///   <item>The consolidated final-message content delivered via an
    ///     <see cref="AssistantMessageEvent"/> in <see cref="AIContent.RawRepresentation"/> when
    ///     no delta text has been streamed yet — used only when deltas have not already covered the
    ///     content, mirroring <c>CopilotAIAgent.ExecuteStreamingLoopAsync</c>'s alreadyStreamed guard.
    ///   </item>
    /// </list>
    /// Exposed <see langword="internal"/> so the dual-path logic can be exercised in unit tests
    /// without a live Copilot SDK session.
    /// </summary>
    internal static async Task<string?> CaptureResponseTextAsync(
        IAsyncEnumerable<AgentResponseUpdate?> stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in stream.WithCancellation(ct))
        {
            if (chunk is null) continue;

            var deltaText = chunk.Text;
            if (!string.IsNullOrEmpty(deltaText))
            {
                sb.Append(deltaText);
            }
            else
            {
                var contentText = ExtractTextContent(chunk);
                if (!string.IsNullOrEmpty(contentText))
                    sb.Append(contentText);
            }

            // When no delta text has been captured yet, also check for the consolidated
            // final-message content delivered as an AssistantMessageEvent in RawRepresentation.
            // The Copilot SDK does not always stream the answer as incremental Text deltas —
            // some model configurations deliver the full response as a single non-delta event.
            if (sb.Length == 0)
            {
                var finalContent = ExtractFinalMessageContent(chunk);
                if (!string.IsNullOrEmpty(finalContent))
                    sb.Append(finalContent);
            }
        }
        return sb.Length > 0 ? sb.ToString().Trim() : null;
    }

    private static string? ExtractTextContent(AgentResponseUpdate chunk)
    {
        if (chunk.Contents is null) return null;

        StringBuilder? text = null;
        foreach (var content in chunk.Contents)
        {
            if (content is not TextContent textContent || string.IsNullOrEmpty(textContent.Text))
                continue;

            text ??= new StringBuilder();
            text.Append(textContent.Text);
        }

        return text?.ToString();
    }

    /// <summary>
    /// Extracts the full assistant message text from the SDK <see cref="AssistantMessageEvent"/>
    /// carried as the <see cref="AIContent.RawRepresentation"/> of a streaming chunk.
    /// Mirrors <c>CopilotAIAgent.ExtractFinalMessageContent</c>.
    /// </summary>
    private static string? ExtractFinalMessageContent(AgentResponseUpdate chunk)
    {
        if (chunk.Contents is null) return null;
        foreach (var content in chunk.Contents)
        {
            if (content.RawRepresentation is AssistantMessageEvent message)
                return message.Data?.Content;
        }
        return null;
    }

    private static string Truncate(string? value, int max = 500)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var collapsed = value.Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }
}
