using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentTools;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

public sealed class FoundryAgentRunner : IAgentRunner
{
    private const string SystemPrompt =
        """
        You are a file-editing assistant. Complete the given task using the available tools.

        Prefer file tools over shell for all file operations — they are faster, safer, and
        always available regardless of sandbox configuration:
        - read_file: read a file
        - str_replace_editor: replace a unique string in a file (preferred for edits)
        - apply_patch: apply a patch in Copilot CLI patch grammar
        - create: create a new file (fails if it already exists)
        - edit: write/overwrite a file
        - grep_search: search for a pattern across files
        - file_search: find files matching a glob pattern

        Only use run_command for operations that genuinely require a shell (building,
        running tests, etc.). Do not use run_command to read, list, copy, or delete files —
        use the file tools instead.

        - report_intent: call this before each major step to describe what you are about to do,
          AND immediately after each run_command to give a one-sentence plain-English interpretation
          of the output (what happened, whether it succeeded or failed, and why — max 15 words).

        Work step by step. When you are done, produce a final message summarising what you
        changed and why. Do not ask clarifying questions — proceed with your best judgement.
        """;

    private const int MaxTurns = 30;

    private readonly FoundryClientFactory? _factory;
    private readonly IChatClient? _chatClient;
    private readonly ISandboxExecutor _executor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly ILogger<FoundryAgentRunner> _logger;

    public FoundryAgentRunner(
        FoundryClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        ILogger<FoundryAgentRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        ModelSource modelSource,
        string runId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct)
    {
        var seq = new[] { 0 };
        void Emit(string type, object payload)
        {
            if (stream is null) return;
            stream.TryWrite(new RunEvent(Interlocked.Increment(ref seq[0]), type, payload));
        }

        // --- Per-run governance kernel (shared mechanism — FR-032) ---
        var sandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(workingDirectory, ct);
        using var governance = SandboxGovernance.Create(workingDirectory, runId, _executor, sandboxPolicy, _logger);
        var agentId = $"did:mesh:scaffolder:foundry:{runId}";

        // --- Emit sandbox backend selection event (T019) ---
        Emit("sandbox.selected", new { backend = _executor.BackendName, isRealIsolation = _executor.IsRealIsolation, reason = _executor.SelectionReason });
        if (_executor.HasNetworkWarning)
        {
            Emit("sandbox.warning", new { category = "network-open", message = _executor.NetworkWarningMessage, backend = _executor.BackendName });
        }

        var chatClient = _chatClient ?? _factory!.CreateChatClient();
        var fileTools = new SandboxedFileTools(workingDirectory);
        var searchTools = new SandboxedSearchTools(workingDirectory);
        var redactor = SandboxOutputRedactor.Default;
        var sandboxRoot = workingDirectory;

        var toolOptions = new SandboxToolOptions(
            ShellEnabled: sandboxPolicy.ShellEnabled)
        {
            AllowedRepositoryRoots = [.. sandboxPolicy.AllowedRepositoryRoots],
            DestructiveCommandPatterns = [.. sandboxPolicy.DestructiveCommandPatterns],
            RequireApprovalForAllShell = sandboxPolicy.RequireApprovalForAllShell,
        };
        var toolContext = new SandboxToolContext(
            AgentId: agentId,
            WorkingDirectory: workingDirectory,
            SandboxRoot: sandboxRoot,
            Executor: _executor,
            FileTools: fileTools,
            SearchTools: searchTools,
            Redactor: redactor,
            Options: toolOptions,
            EvaluateToolCall: (toolName, args) => governance.EvaluateToolCall(agentId, toolName, new Dictionary<string, object>(args), _logger),
            Logger: _logger,
            EmitEvent: Emit,
            RunId: runId,
            IsCommandApproved: hash => _approvalStore.IsApproved(runId, hash));
        var toolFunctions = SandboxToolRegistry.Build(toolContext);
        var tools = toolFunctions.Cast<AITool>().ToList();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
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

                var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name);
                if (tool is null)
                {
                    var err = $"Unknown tool: {call.Name}";
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
                if (call.Name == "run_command")
                    toolArgs["directory"] = workingDirectory;

                if (call.Name is "edit" or "create" or "str_replace_editor" or "read_file")
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

                var (allowed, reason) = governance.EvaluateToolCall(agentId, call.Name, toolArgs, _logger);

                if (!allowed)
                {
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
        }
    }

}
