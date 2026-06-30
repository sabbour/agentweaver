using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.AgentRuntime;

public sealed class FoundryAgentRunner : IAgentRunner
{
    private const string SystemPrompt =
        """
        You are a coding and file editing assistant. Complete the given task using the available tools.

        Call report_intent(intent) before each major step to describe what you are about to do.
        report_intent does NOT write files — always follow it with the actual tool call in the same response.

        Work step by step. Do not produce a final summary until ALL writes are done.

        Prefer to proceed using the task and the workspace. If you hit a genuine blocker — a
        material decision you cannot infer, or an action that needs the user's permission — do
        NOT silently guess and do NOT stop without surfacing it. Call ask_question(question) to
        bubble the question or permission request up to the coordinator (which may answer on
        your behalf when Autopilot is on) or the user, then continue once you receive the answer.
        """;

    private const int MaxTurns = 30;

    private readonly FoundryClientFactory? _factory;
    private readonly IChatClient? _chatClient;
    private readonly ISandboxExecutor _executor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IQuestionGate? _questionGate;
    private readonly ILogger<FoundryAgentRunner> _logger;

    public FoundryAgentRunner(
        FoundryClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        ILogger<FoundryAgentRunner> logger,
        IQuestionGate? questionGate = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _questionGate = questionGate;
    }

    /// <summary>Internal constructor for unit tests — injects a pre-built IChatClient directly.</summary>
    internal FoundryAgentRunner(
        IChatClient chatClient,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        ILogger<FoundryAgentRunner> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExecuteAsync(
        string task,
        string workingDirectory,
        string repositoryPath,
        ModelSource modelSource,
        string runId,
        string? modelId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct,
        string? systemPromptContext = null,
        string? userId = null)
    {
        var seq = new[] { 0 };
        void Emit(string type, object payload)
        {
            if (stream is null) return;
            stream.TryWrite(new RunEvent(Interlocked.Increment(ref seq[0]), type, payload));
        }

        // --- Per-run governance kernel (shared mechanism — FR-032) ---
        var sandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(repositoryPath, ct);
        // direct: true in settings.yml → bypass all sandbox machinery, run commands on host shell.
        var executor = sandboxPolicy.Direct
            ? new PassthroughExecutor("direct execution — sandbox disabled via settings.yml", _logger)
            : _executor;
        using var governance = SandboxGovernance.Create(workingDirectory, runId, executor, sandboxPolicy, _logger);
        var agentId = $"did:mesh:agentweaver:foundry:{runId}";

        // Emit sandbox + debug info — visible in the run stream and UI.
        Emit("sandbox.selected", new { backend = executor.BackendName, isRealIsolation = executor.IsRealIsolation, reason = executor.SelectionReason });

        Emit("agent.system_prompt", new { provider = "foundry", prompt = SystemPrompt });
        var shellIncluded = (executor.IsRealIsolation || executor.BackendName == "direct") && sandboxPolicy.ShellEnabled;
        Emit("agent.tools", new { provider = "foundry", tools = SandboxToolRegistry.GetToolNames(shellIncluded) });
        if (executor.HasNetworkWarning)
        {
            Emit("sandbox.warning", new { category = "network-open", message = executor.NetworkWarningMessage, backend = executor.BackendName });
        }
        if (sandboxPolicy.NetworkEnabled)
        {
            Emit("sandbox.warning", new
            {
                category = "network-open",
                message = "Sandbox is running with outbound network enabled (network_enabled: true in .agentweaver/settings.yml). " +
                          "Network access is intentional but increases the attack surface. " +
                          "Ensure this is required for the agent's task.",
                backend = executor.BackendName
            });
        }

        var chatClient = _chatClient ?? _factory!.CreateChatClient(modelId);
        var fileTools = new SandboxedFileTools(workingDirectory, sandboxPolicy.MaxOutputBytes);
        var searchTools = new SandboxedSearchTools(workingDirectory, sandboxPolicy.MaxOutputBytes);
        var redactor = SandboxOutputRedactor.Default;
        var sandboxRoot = workingDirectory;

        var toolOptions = new SandboxToolOptions(
            ShellEnabled: sandboxPolicy.ShellEnabled)
        {
            AllowedRepositoryRoots = [.. sandboxPolicy.AllowedRepositoryRoots],
            DestructiveCommandPatterns = [.. sandboxPolicy.DestructiveCommandPatterns],
            RequireApprovalForAllShell = sandboxPolicy.RequireApprovalForAllShell,
            NetworkEnabled = sandboxPolicy.NetworkEnabled,
        };
        var toolContext = new SandboxToolContext(
            AgentId: agentId,
            WorkingDirectory: workingDirectory,
            SandboxRoot: sandboxRoot,
            Executor: executor,
            FileTools: fileTools,
            SearchTools: searchTools,
            Redactor: redactor,
            Options: toolOptions,
            Logger: _logger,
            EmitEvent: Emit,
            RunId: runId,
            IsCommandApproved: hash => _approvalStore.IsApproved(runId, hash),
            IsCommandDenied: hash => _approvalStore.IsDenied(runId, hash),
            QuestionGate: _questionGate);
        var toolFunctions = SandboxToolRegistry.Build(toolContext);
        var tools = toolFunctions.Cast<AITool>().ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, string.IsNullOrEmpty(systemPromptContext)
                ? SystemPrompt
                : SystemPrompt + "\n\n" + systemPromptContext),
            new(ChatRole.User, task),
        };

        var options = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto };
        var sb = new StringBuilder();
        var completedNormally = false;

        try
        {
        for (var turn = 0; turn < MaxTurns; turn++)
        {
            Emit("agent.turn.start", new { turnId = turn.ToString() });

            // MF1 + SF3: reset per-turn state before the streaming loop so only
            // the current turn's text accumulates and the delta/messageId flags are clean.
            sb.Clear();
            var hadTextDelta = false;
            string? turnMessageId = null;
            var updates = new List<ChatResponseUpdate>();

            try
            {
                await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct)
                                                       .WithCancellation(ct))
                {
                    updates.Add(update);

                    // Pin the stable messageId for this turn: first non-null value wins.
                    if (turnMessageId is null && update.MessageId is not null)
                        turnMessageId = update.MessageId;

                    var deltaText = update.Text;
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        sb.Append(deltaText);
                        Emit("agent.message.delta", new { delta = deltaText, messageId = turnMessageId });
                        hadTextDelta = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // MF2: cancellation is not a failure — propagate cleanly without run.failed.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetStreamingResponseAsync threw — turn={Turn}, workingDirectory={WorkingDirectory}",
                    turn, workingDirectory);
                Emit("run.failed", new { errorMessage = "The agent encountered an internal error." });
                throw;
            }

            // Reconstruct the full ChatMessage (including FunctionCallContent) from updates.
            // ToChatResponse merges incremental function-call argument fragments correctly.
            ChatResponse response = updates.ToChatResponse();
            var assistantMessage = response.Messages.LastOrDefault()
                ?? new ChatMessage(ChatRole.Assistant, "");
            messages.Add(assistantMessage);

            // Fallback: emit agent.message only when no text deltas were produced this turn
            // (tool-call-only turn, empty stream, etc.) — never double-emit alongside deltas.
            if (!hadTextDelta && !string.IsNullOrWhiteSpace(assistantMessage.Text))
            {
                sb.Clear();
                sb.Append(assistantMessage.Text);
                Emit("agent.message", new { content = assistantMessage.Text });
            }

            var calls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();

            if (calls.Count == 0)
            {
                Emit("agent.turn.end", new { turnId = turn.ToString() });
                completedNormally = true;
                break;
            }

            var toolResults = new List<AIContent>();
            foreach (var call in calls)
            {
                Emit("tool.call", new { callId = call.CallId, toolName = call.Name, arguments = call.Arguments });

                // Backward-compat aliases: GPT models have strong training on Copilot CLI's
                // built-in tool names and may hallucinate them even when the schema says otherwise.
                var resolvedName = call.Name switch
                {
                    "edit"        => "write_file",
                    "create"      => "create_file",
                    "view"        => "read_file",
                    "write"       => "write_file",
                    _ => call.Name,
                };

                var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == resolvedName);
                if (tool is null)
                {
                    var err = $"Unknown tool: {call.Name}. Available tools: {string.Join(", ", tools.OfType<AIFunction>().Select(t => t.Name))}";
                    Emit("tool.error", new { callId = call.CallId, errorMessage = err });
                    toolResults.Add(new FunctionResultContent(call.CallId, err));
                    continue;
                }

                // --- Dual-layer governance check BEFORE execution ---
                var toolArgs = new Dictionary<string, object>();
                if (call.Arguments is not null)
                {
                    foreach (var kvp in call.Arguments)
                        toolArgs[kvp.Key] = kvp.Value ?? "";
                }

                // Inject working directory unconditionally so governance always sees our value,
                // not a model-supplied one (prevents policy/execution mismatch).
                if (resolvedName == "run_command")
                    toolArgs["directory"] = workingDirectory;

                if (resolvedName is "write_file" or "create_file" or "str_replace_editor" or "read_file")
                {
                    foreach (var alias in new[] { "file_path", "filename", "target", "file" })
                    {
                        if (!toolArgs.ContainsKey("path") &&
                            toolArgs.TryGetValue(alias, out var aliasVal))
                        {
                            toolArgs["path"] = aliasVal;
                            break;
                        }
                    }
                }

                var (allowed, reason) = governance.EvaluateToolCall(agentId, resolvedName, toolArgs, _logger);

                if (!allowed)
                {
                    _logger.LogWarning(
                        "Governance DENIED for tool {ToolName} (called as {CalledName}). ReceivedArgKeys=[{Keys}] Reason={Reason}",
                        resolvedName,
                        call.Name,
                        string.Join(", ", toolArgs.Keys),
                        reason);
                    var denyMsg = "Error: operation denied by sandbox policy.";
                    var modelReason = reason ?? denyMsg;
                    Emit("tool.error", new { callId = call.CallId, errorMessage = modelReason });
                    // Pass the actual reason to the model so it can correct its arguments and retry.
                    toolResults.Add(new FunctionResultContent(call.CallId, modelReason));
                    continue;
                }

                // Governance passed — invoke the tool (SandboxedFileTools provides defense-in-depth)
                // Governance passed — invoke with original call.Arguments (not the governance-enriched toolArgs)
                string resultText;
                try
                {
                    // Use toolArgs (which contains path-alias normalization and governance
                    // injections) rather than call.Arguments (which has the original un-normalized
                    // keys from the model), so read_file with file_path=X resolves correctly.
                    var fnArgs = new AIFunctionArguments(
                        toolArgs.ToDictionary(k => k.Key, k => (object?)k.Value));
                    var raw = await tool.InvokeAsync(fnArgs, ct);
                    resultText = raw?.ToString() ?? string.Empty;
                    Emit("tool.result", new { callId = call.CallId, content = resultText });
                }
                catch (Exception ex)
                {
                    resultText = $"Error: {ex.Message}";
                    Emit("tool.error", new { callId = call.CallId, errorMessage = ex.Message });
                }

                toolResults.Add(new FunctionResultContent(call.CallId, resultText));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, toolResults));
            Emit("agent.turn.end", new { turnId = turn.ToString() });
        }

        if (!completedNormally)
            Emit("run.failed", new { errorMessage = "Step limit reached." });

        return sb.ToString();
        }
        finally
        {
            _approvalStore.Clear(runId);
            _questionGate?.Clear(runId);
        }
    }

}
