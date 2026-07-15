using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

public sealed record OperatorAssistantRequest(
    string ConversationId,
    string Message,
    string CallerUser,
    string? GitHubLogin,
    string? ProjectId,
    string? RunId,
    string? ModelId,
    string AgentDefinition,
    string CallerBearerToken,
    IReadOnlyList<ConsoleFacadeHistoryMessage> History);

public sealed record OperatorAssistantResponse(string Message, IReadOnlyList<string> ToolNamesInvoked);

public interface IOperatorAssistantAgent
{
    Task<OperatorAssistantResponse> RunTurnAsync(OperatorAssistantRequest request, CancellationToken ct);
}

/// <summary>
/// Spike prototype of the operator assistant (Morpheus design, #346). It is the same in-API MAF
/// GitHub Copilot chat loop as <see cref="CopilotConsoleFacadeAgent"/> but with two changes:
///   1. Its tool set is sourced from the REAL AgentweaverMCP server via
///      <see cref="IAgentweaverMcpToolProvider"/> (all ~91 tools) instead of 15 hand-wrapped
///      read-only tools — one source of truth, no drift.
///   2. There is no regex pre-router: the LLM routes via MCP tool descriptions.
///
/// The regex router and the existing facade are intentionally left untouched — this is an additive
/// spike that proves the MCP tool-adapter path works end to end. Per-call GitHub bearer passthrough
/// is preserved: the caller's token is forwarded to the MCP server on every tools/call.
/// </summary>
public sealed class OperatorAssistantAgent(
    GitHubCopilotClientFactory factory,
    IGitHubTokenScopeProvider scopeProvider,
    IAgentweaverMcpToolProvider mcpToolProvider,
    ILogger<OperatorAssistantAgent> logger) : IOperatorAssistantAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<OperatorAssistantResponse> RunTurnAsync(OperatorAssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CallerUser))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                "Operator assistant cannot start: no authenticated caller identity is available.",
                isRetryable: false);

        var scope = scopeProvider.Resolve(request.CallerUser);
        if (string.Equals(scope.Key, GitHubTokenScope.Installation.Key, StringComparison.Ordinal))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                "Operator assistant requires the signed-in user's Copilot-entitled token, not the installation token.",
                isRetryable: false);

        // Connect to the real MCP server as the caller and adapt its tools to AIFunctions.
        await using var mcpSession = await mcpToolProvider
            .ConnectAsync(request.CallerBearerToken, ct)
            .ConfigureAwait(false);
        var toolDeclarations = mcpSession.AsToolDeclarations();
        logger.LogInformation(
            "Operator assistant connected to MCP server: {ToolCount} tools available for conversation {ConversationId}",
            toolDeclarations.Count, request.ConversationId);

        await using var client = await factory.CreateClientAsync(scope, request.ModelId, ct).ConfigureAwait(false);
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "operator") is { } providerFailure)
        {
            logger.LogWarning(ex, "Operator assistant provider failure while starting client: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var sessionConfig = BuildSessionConfig(
            request.ConversationId,
            BuildSystemPrompt(request),
            toolDeclarations,
            request.ModelId);

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: "Agentweaver Operator", description: null);
        AgentSession session;
        try
        {
            session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "operator") is { } providerFailure)
        {
            logger.LogWarning(ex, "Operator assistant provider failure while creating session: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var messages = BuildMessages(request);
        var answer = new StringBuilder();
        var streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var anyDeltaForNullId = false;

        try
        {
            await foreach (var chunk in agent.RunStreamingAsync(messages, session, options: null, ct).WithCancellation(ct))
            {
                if (chunk is null) continue;

                var delta = chunk.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    answer.Append(delta);
                    if (chunk.MessageId is not null)
                        streamedMessageIds.Add(chunk.MessageId);
                    else
                        anyDeltaForNullId = true;
                }

                var final = ExtractFinalMessageContent(chunk);
                if (!string.IsNullOrEmpty(final))
                {
                    var alreadyStreamed = chunk.MessageId is not null
                        ? streamedMessageIds.Contains(chunk.MessageId)
                        : anyDeltaForNullId;
                    if (!alreadyStreamed)
                        answer.Append(final);
                }
            }
        }
        catch (Exception ex) when (AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "operator") is { } providerFailure)
        {
            logger.LogWarning(ex, "Operator assistant provider failure: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var text = answer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            text = "I could not produce an operator response. Try rephrasing the request with a project or run context.";

        return new OperatorAssistantResponse(text, toolDeclarations.Select(t => t.Name).ToList());
    }

    /// <summary>
    /// Builds the Copilot session config that wires the MCP tool set (as AIFunctionDeclarations) into
    /// a chat loop seeded with the Agentweaver agent definition. Exposed internally so the spike test
    /// can assert the MCP tools are carried into SessionConfig.Tools without a live Copilot session.
    /// </summary>
    internal static SessionConfig BuildSessionConfig(
        string conversationId,
        string systemPrompt,
        IReadOnlyList<AIFunctionDeclaration> tools,
        string? modelId) =>
        new()
        {
            EnableConfigDiscovery = false,
            Streaming = true,
            SessionId = $"agentweaver-operator-{conversationId}",
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Model = modelId,
            Tools = tools.ToList(),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemPrompt,
            },
        };

    internal static string BuildSystemPromptForTests(
        string agentDefinition,
        int mcpToolCount,
        string? projectId = null,
        string? runId = null) =>
        BuildSystemPrompt(new OperatorAssistantRequest(
            ConversationId: "test",
            Message: "test",
            CallerUser: "test",
            GitHubLogin: "test",
            ProjectId: projectId,
            RunId: runId,
            ModelId: null,
            AgentDefinition: agentDefinition,
            CallerBearerToken: "test",
            History: []), mcpToolCount);

    private static string BuildSystemPrompt(OperatorAssistantRequest request, int mcpToolCount = 0)
    {
        var context = JsonSerializer.Serialize(new
        {
            request.ProjectId,
            request.RunId,
            request.CallerUser,
            request.GitHubLogin,
        }, JsonOptions);

        return $"""
{request.AgentDefinition}

## Operator assistant runtime addendum

You are the Agentweaver operator assistant. You drive the platform exclusively through the
AgentweaverMCP tools that are wired into this session (the full server tool set, not a hand-picked
subset). Infer the right tool from natural language, call it directly when context is sufficient, and
ask exactly one focused clarifying question only when a project/run/tool target is genuinely ambiguous.

Current operator context:
```json
{context}
```

Operating rules:
- Prefer discovery tools to resolve unknown project or run IDs before acting; never invent IDs.
- Destructive or gated actions (start budget-consuming work, delete/archive, stop/cancel, confirm an
  outcome, approve/reject review, merge) are surfaced through per-tool approval prompts. Call the
  tool when asked; the platform enforces the human-approval gate — do not claim an action succeeded
  before approval resolves.
- Keep responses concise and include the relevant IDs you inspected.
""";
    }

    private static List<ChatMessage> BuildMessages(OperatorAssistantRequest request)
    {
        var messages = new List<ChatMessage>();
        foreach (var history in request.History.TakeLast(12))
        {
            var role = string.Equals(history.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, history.Text));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Message));
        return messages;
    }

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
}
