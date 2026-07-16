using System.Reflection;
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

/// <summary>
/// Real-time sink for a single operator turn. The assistant invokes these callbacks as the turn
/// streams so a caller (e.g. AssistantRunService) can project each assistant/tool step onto the run
/// event stream in order. All members are optional to implement; a null sink disables streaming
/// projection (the turn still returns its final <see cref="OperatorAssistantResponse"/>).
/// </summary>
public interface IOperatorAssistantTurnSink
{
    /// <summary>A streamed slice of the assistant's textual answer.</summary>
    ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken ct);

    /// <summary>The assistant asked to call an MCP tool. <paramref name="argumentsJson"/> may be null.</summary>
    ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken ct);

    /// <summary>A tool call completed. <paramref name="success"/> reflects whether the tool returned an error.</summary>
    ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken ct);

    /// <summary>
    /// A gated MCP tool call is about to run and requires an operator approval decision first. The
    /// sink must project a <c>tool.approval_required</c> event carrying <paramref name="requestId"/>
    /// and <paramref name="toolName"/> onto the run stream (the shape the frontend approval UI
    /// consumes) and block until the operator grants or denies via the existing
    /// <c>/api/runs/{id}/tool-approvals</c> / <c>tool-denials</c> endpoints, or the wait times out.
    /// Returns <see langword="true"/> when approved (the tool then runs) or <see langword="false"/>
    /// when denied or timed out (a "denied by operator" result is returned to the model instead).
    /// A null sink cannot gate, so gated tools run ungated only in the no-sink (non-projected) case.
    /// </summary>
    ValueTask<bool> OnApprovalRequiredAsync(string requestId, string toolName, string? argumentsJson, CancellationToken ct);
}

public interface IOperatorAssistantAgent
{
    Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct);
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

    public async Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
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
        var toolDeclarations = BuildToolDeclarations(mcpSession, sink, ct);
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
        var invokedTools = new List<string>();

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

                    if (sink is not null)
                        await sink.OnAssistantTextDeltaAsync(delta, ct).ConfigureAwait(false);
                }

                // Surface the actual tool activity (not the whole tool catalog) so callers can
                // project a faithful per-step transcript onto the run event stream.
                if (chunk.Contents is not null)
                {
                    foreach (var content in chunk.Contents)
                    {
                        switch (content)
                        {
                            case FunctionCallContent call:
                                invokedTools.Add(call.Name);
                                if (sink is not null)
                                {
                                    var argsJson = call.Arguments is null
                                        ? null
                                        : JsonSerializer.Serialize(call.Arguments, JsonOptions);
                                    await sink.OnToolCallAsync(call.Name, argsJson, ct).ConfigureAwait(false);
                                }
                                break;
                            case FunctionResultContent result:
                                if (sink is not null)
                                {
                                    var toolName = ResolveToolName(result, invokedTools);
                                    var success = result.Exception is null;
                                    await sink.OnToolResultAsync(toolName, success, ct).ConfigureAwait(false);
                                }
                                break;
                        }
                    }
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

        return new OperatorAssistantResponse(text, invokedTools);
    }

    private static string ResolveToolName(FunctionResultContent result, IReadOnlyList<string> invokedTools) =>
        !string.IsNullOrEmpty(result.CallId)
            ? invokedTools.LastOrDefault() ?? result.CallId
            : invokedTools.LastOrDefault() ?? "tool";

    /// <summary>
    /// Adapts every MCP tool to the <see cref="AIFunctionDeclaration"/> form used by SessionConfig.Tools,
    /// wrapping the consequential ones (see <see cref="OperatorToolApprovalPolicy"/>) in an approval
    /// gate so the SDK's auto-invocation blocks on an operator decision before the tool actually runs.
    /// Read/discovery tools and low-consequence writes pass through unwrapped. When <paramref name="sink"/>
    /// is null there is no run stream to raise the approval on, so gating is skipped (the tool runs).
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> BuildToolDeclarations(
        AgentweaverMcpToolSession session,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
    {
        var declarations = new List<AIFunctionDeclaration>(session.Tools.Count);
        foreach (var tool in session.Tools)
        {
            if (sink is not null && OperatorToolApprovalPolicy.RequiresApproval(tool.Name))
                declarations.Add(new ApprovalGatingAIFunction(tool, sink, ct));
            else
                declarations.Add(tool);
        }
        return declarations;
    }

    /// <summary>
    /// Wraps a gated MCP <see cref="AIFunction"/> so that, when the Copilot SDK auto-invokes it, the
    /// call is first surfaced to the operator for approval via <see cref="IOperatorAssistantTurnSink"/>.
    /// On approval the inner tool runs unchanged; on denial (or timeout) a clear "denied by operator"
    /// result is returned to the model so the conversation continues sensibly instead of the model
    /// seeing a generic permission-denied error. All declaration metadata is forwarded verbatim so the
    /// model still sees the tool's real name, description, and parameter schema.
    /// </summary>
    private sealed class ApprovalGatingAIFunction(
        AIFunction inner,
        IOperatorAssistantTurnSink sink,
        CancellationToken turnCt) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;
        public override JsonElement JsonSchema => inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
        public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;
        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(turnCt, cancellationToken);
            var effectiveCt = linked.Token;

            var requestId = Guid.NewGuid().ToString("N");
            string? argumentsJson = null;
            try
            {
                argumentsJson = JsonSerializer.Serialize(
                    (IReadOnlyDictionary<string, object?>)arguments, JsonOptions);
            }
            catch
            {
                // Arguments are informational on the approval prompt; a serialization failure must
                // not block the gate.
            }

            var approved = await sink
                .OnApprovalRequiredAsync(requestId, inner.Name, argumentsJson, effectiveCt)
                .ConfigureAwait(false);

            if (!approved)
                return $"The operator denied the '{inner.Name}' action, so it did not run. " +
                       "Do not retry it; tell the user it was declined and ask how they would like to proceed.";

            return await inner.InvokeAsync(arguments, effectiveCt).ConfigureAwait(false);
        }
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
            // SECURITY (assistant sandbox, #346): the operator assistant runs IN-PROCESS in the API
            // pod with NO OS-level sandbox (unlike sandboxed agent runs, which are contained by the
            // linux-bwrap boundary AND a deny-by-default OnPermissionRequest gate — see
            // GitHubCopilotAgentRunner/CopilotAIAgent). The Copilot SDK ships built-in native tools
            // (bash/shell, view/read, write, str_replace_editor, grep, web_fetch, …) that are present
            // by DEFAULT and, with no permission handler, would auto-run — giving arbitrary host
            // shell/filesystem access from the chat surface. Constrain the session to ONLY the MCP
            // tool declarations via the SDK allowlist ("only these tools will be available when
            // specified"), so every SDK built-in is removed from the model's tool surface. Any file
            // system / shell / code-execution work must be redirected to an orchestrator/sandboxed
            // run through the MCP run tools (coordinator_start / run_submit / run_task).
            AvailableTools = tools.Select(t => t.Name).ToList(),
            // Defense in depth: even if a native built-in somehow reached the permission layer, fail
            // closed — reject every native shell/read/write/URL request. MCP/custom tool requests are
            // approved (their consequential subset is already human-gated by ApprovalGatingAIFunction
            // and the MCP server governs execution).
            OnPermissionRequest = RejectNativeToolPermissionHandler,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemPrompt,
            },
        };

    /// <summary>
    /// Deny-by-default backstop for the operator assistant's in-API session: rejects every SDK
    /// built-in native tool request (shell/read/write/URL) so the assistant can never touch the host
    /// file system or shell directly, and approves MCP/custom tool requests (which are separately
    /// gated by <see cref="ApprovalGatingAIFunction"/> and enforced by the MCP server). This is a
    /// backstop to <see cref="SessionConfig.AvailableTools"/>, which already removes the built-ins
    /// from the model's tool surface.
    /// </summary>
    private static Task<PermissionDecision> RejectNativeToolPermissionHandler(
        PermissionRequest request, PermissionInvocation invocation)
    {
        if (request is PermissionRequestShell
                or PermissionRequestRead
                or PermissionRequestWrite
                or PermissionRequestUrl)
        {
            return Task.FromResult<PermissionDecision>(PermissionDecision.Reject(
                "Native shell/file tools are disabled for the operator assistant. Use the Agentweaver " +
                "MCP tools; run any file/shell/code work through an orchestrator run (e.g. coordinator_start)."));
        }

        return Task.FromResult<PermissionDecision>(PermissionDecision.ApproveOnce());
    }

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
- You have NO direct file system, shell, or code-execution capability of your own — only the
  AgentweaverMCP tools above. Any request that needs files edited, commands run, code changed, or work
  executed must be carried out by starting/steering an orchestrator run (e.g. coordinator_start /
  run_submit / run_task), never by attempting to run it yourself.
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
