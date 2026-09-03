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

/// <summary>A single prior turn in an operator/console conversation history, replayed as chat
/// context for the next turn.</summary>
public sealed record ConsoleFacadeHistoryMessage(string Role, string Text);

public sealed record OperatorAssistantRequest(
    string ConversationId,
    string Message,
    string CallerUser,
    string? GitHubLogin,
    string? ProjectId,
    string? RunId,
    string? ModelId,
    string AgentDefinition,
    string McpBrokerToken,
    IReadOnlyList<ConsoleFacadeHistoryMessage> History,
    Func<CancellationToken, Task<string>>? RenewMcpBrokerTokenAsync = null);

public sealed record OperatorAssistantResponse(string Message, IReadOnlyList<string> ToolNamesInvoked);

/// <summary>
/// Wire envelope for a single operator turn forwarded to the sandbox AgentHost pod (narrow AgentHost
/// cutover, #346/#347). The A2A transport's <c>AgentSetupParams</c> already carries
/// <c>ModelId</c>/<c>ProjectId</c>/<c>UserId</c> plus a single free-text task string — the remaining
/// <see cref="OperatorAssistantRequest"/> fields specific to the operator chat loop (the message
/// itself, replay history, the assembled agent definition, the caller's GitHub login, and the
/// optional cross-referenced run id) are packed into that task string as JSON using this envelope,
/// so no shared workflow contract needs to grow an operator-specific field.
/// </summary>
public sealed record OperatorAssistantTurnEnvelope(
    string Message,
    string AgentDefinition,
    string? GitHubLogin,
    string? ContextRunId,
    IReadOnlyList<ConsoleFacadeHistoryMessage> History);

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

    /// <summary>
    /// Renews the server-held MCP broker credential immediately before a tool invocation. The token
    /// itself never crosses this callback or the run event stream.
    /// </summary>
    ValueTask OnMcpBrokerTokenRefreshRequiredAsync(CancellationToken ct);
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
/// GitHub Copilot chat loop as the retired legacy Console facade agent but with two changes:
///   1. Its tool set is sourced from the REAL AgentweaverMCP server via
///      <see cref="IAgentweaverMcpToolProvider"/> (all ~91 tools) instead of 15 hand-wrapped
///      read-only tools — one source of truth, no drift.
///   2. There is no regex pre-router: the LLM routes via MCP tool descriptions.
///
/// The regex router and the existing facade are intentionally left untouched — this is an additive
/// spike that proves the MCP tool-adapter path works end to end. The API-issued, short-lived broker
/// token is forwarded to the MCP server on every tools/call.
/// </summary>
public sealed class OperatorAssistantAgent(
    GitHubCopilotClientFactory factory,
    IAgentweaverMcpToolProvider mcpToolProvider,
    ILogger<OperatorAssistantAgent> logger,
    IByokProviderConfigurationProvider? byokProviderConfiguration = null) : IOperatorAssistantAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Hard per-invocation deadline for a single MCP tool call. The Copilot SDK auto-invokes tools,
    /// and each MCP tool call is a <c>tools/call</c> HTTP round-trip to the AgentweaverMCP server that
    /// can fan out into further backend work (e.g. <c>coordinator_steer stop</c> deletes an AgentHost
    /// pod via the Kubernetes API). None of that had a bound: when a downstream dependency hung (a
    /// degraded K8s API was observed), the tool call never returned, so the turn's streaming loop
    /// never completed, the run's turn semaphore was never released, and no <c>tool.result</c> was
    /// ever written — the whole operator conversation wedged until the 30-minute idle sweep force-
    /// closed it. This deadline is a runtime backstop that guarantees no single tool call can block a
    /// turn forever; it is deliberately generous (legitimate MCP calls return in seconds) and only
    /// trips on a genuinely stuck dependency.
    /// </summary>
    internal static readonly TimeSpan ToolInvocationTimeout = TimeSpan.FromMinutes(3);

    public async Task<OperatorAssistantResponse> RunTurnAsync(
        OperatorAssistantRequest request,
        IOperatorAssistantTurnSink? sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var byokProvider = byokProviderConfiguration is null
            ? null
            : await byokProviderConfiguration.GetAsync(ct).ConfigureAwait(false);
        var modelSource = byokProvider is null ? ModelSource.GitHubCopilot : ModelSource.Byok;

        if (byokProvider is null && string.IsNullOrWhiteSpace(request.RunId))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                "Operator assistant cannot start without a run-bound Copilot capability snapshot.",
                isRetryable: false);

        // Connect to the real MCP server as the caller and adapt its tools to AIFunctions.
        await using var mcpSession = await mcpToolProvider
            .ConnectAsync(request.McpBrokerToken, ct)
            .ConfigureAwait(false);
        var toolDeclarations = BuildToolDeclarations(mcpSession, sink, ct);
        logger.LogInformation(
            "Operator assistant connected to MCP server: {ToolCount} tools available for conversation {ConversationId}",
            toolDeclarations.Count, request.ConversationId);

        await using var client = byokProvider is null
            ? await factory.CreateClientAsync(request.RunId!, request.ModelId, ct).ConfigureAwait(false)
            : factory.CreateByokClient();
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(modelSource, ex, "operator") is { } providerFailure)
        {
            logger.LogWarning(ex, "Operator assistant provider failure while starting client: {Code}", providerFailure.ErrorCode);
            throw providerFailure;
        }

        var sessionConfig = BuildSessionConfig(
            request.ConversationId,
            BuildSystemPrompt(request),
            toolDeclarations,
            request.ModelId,
            byokProvider);

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: "Agentweaver Operator", description: null);
        AgentSession session;
        try
        {
            session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentProviderException.Classify(modelSource, ex, "operator") is { } providerFailure)
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
        catch (Exception ex) when (AgentProviderException.Classify(modelSource, ex, "operator") is { } providerFailure)
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
            // Every tool invocation is bounded by a hard deadline (ToolInvocationTimeout) so a single
            // stuck tool call can never wedge the whole turn. Consequential tools are additionally
            // wrapped in the human-approval gate; the deadline applies to the actual tool call, not to
            // the (separately time-boxed) approval wait.
            declarations.Add(WrapTool(
                tool,
                sink,
                OperatorToolApprovalPolicy.RequiresApproval(tool.Name),
                ToolInvocationTimeout,
                ct));
        }
        return declarations;
    }

    /// <summary>Test seam: wraps <paramref name="inner"/> in the per-invocation deadline wrapper the
    /// production tool set uses, so the timeout backstop can be exercised without a live model/MCP
    /// session. Not for production call sites.</summary>
    internal static AIFunction CreateDeadlineToolForTests(AIFunction inner, TimeSpan timeout) =>
        new DeadlineAIFunction(inner, timeout);

    internal static AIFunction CreateRenewableToolForTests(
        AIFunction inner,
        IOperatorAssistantTurnSink sink,
        bool requiresApproval,
        CancellationToken ct) =>
        WrapTool(inner, sink, requiresApproval, TimeSpan.FromMinutes(1), ct);

    private static AIFunction WrapTool(
        AIFunction tool,
        IOperatorAssistantTurnSink? sink,
        bool requiresApproval,
        TimeSpan timeout,
        CancellationToken ct)
    {
        AIFunction wrapped = new DeadlineAIFunction(tool, timeout);
        if (sink is not null)
            wrapped = new BrokerTokenRefreshingAIFunction(wrapped, sink, ct);
        return sink is not null && requiresApproval
            ? new ApprovalGatingAIFunction(wrapped, sink, ct)
            : wrapped;
    }

    /// <summary>
    /// Wraps an MCP <see cref="AIFunction"/> so its invocation is abandoned once
    /// <see cref="ToolInvocationTimeout"/> elapses, returning a clear timeout result to the model so
    /// the turn completes gracefully instead of hanging forever on an unbounded downstream dependency.
    /// A genuine turn cancellation (client disconnect / host shutdown) is propagated unchanged — only
    /// the deadline-expiry case is converted into a model-visible result. All declaration metadata is
    /// forwarded verbatim so the model still sees the tool's real name, description, and schema.
    /// </summary>
    private sealed class DeadlineAIFunction(AIFunction inner, TimeSpan timeout) : AIFunction
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
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineCts.CancelAfter(timeout);
            try
            {
                return await inner.InvokeAsync(arguments, deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return $"The '{inner.Name}' action did not complete within {timeout.TotalSeconds:0} seconds and was " +
                       "aborted to keep the conversation responsive. It may still be taking effect in the background — " +
                       "do not assume it failed; tell the user it timed out and offer to check its status.";
            }
        }
    }

    /// <summary>
    /// Refreshes the short-lived MCP credential immediately before every call. For consequential
    /// tools this wrapper sits inside the approval wrapper, so renewal happens after approval and
    /// directly before the post-approval request.
    /// </summary>
    private sealed class BrokerTokenRefreshingAIFunction(
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
            await sink.OnMcpBrokerTokenRefreshRequiredAsync(linked.Token).ConfigureAwait(false);
            return await inner.InvokeAsync(arguments, linked.Token).ConfigureAwait(false);
        }
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
        string? modelId,
        ByokProviderConfiguration? byokProviderConfiguration = null) =>
        new()
        {
            EnableConfigDiscovery = false,
            Streaming = true,
            SessionId = $"agentweaver-operator-{conversationId}",
            // #1814 / v0.9.68 REGRESSION (reverted): EnableSessionStore/InfiniteSessions were briefly
            // flipped to true here on the theory that #1814's "database is locked" only affects
            // one-shot/ephemeral sandbox workloads, not a long-lived in-process agent. That theory was
            // wrong for THIS agent: RunTurnAsync (below) creates a brand-new SDK session on EVERY turn
            // (it never resumes one — see the CreateSessionAsync call in this file), so enabling the
            // store means every turn, across every concurrent conversation in this pod, hammers the
            // SAME pod-local SQLite session file. That is exactly the concurrent-write contention
            // #1814 describes — it was reproduced live in staging within minutes of deploy (every new
            // operator run failed with "Error: database is locked"). Reverted to false/disabled.
            // Re-enabling this safely would require first switching RunTurnAsync to actually resume
            // the deterministic SessionId across turns (like CopilotAIAgent.ResumeSessionAsync) so the
            // SDK does one session's-worth of I/O per conversation instead of a fresh one every turn —
            // out of scope for this hotfix. Durable rehydration in AssistantRunService (from the
            // persisted RunEvents log) is unaffected and remains the correct fix for cross-pod/idle-
            // timeout/restart continuity.
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Model = byokProviderConfiguration?.Model ?? modelId,
            Provider = byokProviderConfiguration is null ? null : new GitHub.Copilot.ProviderConfig
            {
                Type = byokProviderConfiguration.Type,
                BaseUrl = byokProviderConfiguration.BaseUrl,
                ApiKey = byokProviderConfiguration.ApiKey,
                WireApi = byokProviderConfiguration.WireApi ?? "responses",
                Headers = ByokProviderConfigMapper.ToHeaderDictionary(byokProviderConfiguration.Headers),
                Azure = ByokProviderConfigMapper.ToAzureOptions(byokProviderConfiguration),
            },
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
            McpBrokerToken: "test",
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
