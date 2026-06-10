using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

public sealed class FoundryAgentRunner : IAgentRunner
{
    private const string SystemPrompt =
        """
        You are a file-editing assistant. Complete the given task using the available tools:
        - read_file: read a file
        - str_replace_editor: replace a unique string in a file
        - apply_patch: apply a patch in Copilot CLI patch grammar
        - create: create a new file
        - edit: write/overwrite a file
        - grep_search: search for a pattern in files
        - file_search: find files matching a glob pattern
        - run_command: run a shell command inside the sandbox (if available)
        - report_intent: report your current intent or plan step
        Work step by step. When you are done, produce a final message summarising what you changed and why.
        Do not ask clarifying questions — proceed with your best judgement.
        """;

    private const int MaxTurns = 30;

    private readonly FoundryClientFactory? _factory;
    private readonly IChatClient? _chatClient;
    private readonly ISandboxExecutor _executor;
    private readonly SandboxOptions _sandboxOptions;
    private readonly ILogger<FoundryAgentRunner> _logger;

    public FoundryAgentRunner(
        FoundryClientFactory factory,
        ISandboxExecutor executor,
        IOptions<SandboxOptions> sandboxOptions,
        ILogger<FoundryAgentRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxOptions = sandboxOptions?.Value ?? throw new ArgumentNullException(nameof(sandboxOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Internal constructor for unit tests — injects a pre-built IChatClient directly.</summary>
    internal FoundryAgentRunner(
        IChatClient chatClient,
        ISandboxExecutor executor,
        IOptions<SandboxOptions> sandboxOptions,
        ILogger<FoundryAgentRunner> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxOptions = sandboxOptions?.Value ?? throw new ArgumentNullException(nameof(sandboxOptions));
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
        using var governance = SandboxGovernance.Create(workingDirectory, runId, _executor, _sandboxOptions, _logger);
        var agentId = $"did:mesh:scaffolder:foundry:{runId}";

        // --- Emit sandbox backend selection event (T019) ---
        Emit("sandbox.selected", new { backend = _executor.BackendName, isRealIsolation = _executor.IsRealIsolation, reason = _executor.SelectionReason });

        var chatClient = _chatClient ?? _factory!.CreateChatClient();
        var fileTools = new SandboxedFileTools(workingDirectory);
        var searchTools = new SandboxedSearchTools(workingDirectory);
        var redactor = SandboxOutputRedactor.Default;
        var sandboxRoot = workingDirectory;

        var tools = BuildTools(fileTools, searchTools, _executor, redactor, workingDirectory, sandboxRoot, _sandboxOptions, ct);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, task),
        };

        var options = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto };
        var sb = new StringBuilder();
        var completedNormally = false;

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

                // Inject working directory for shell tools (SandboxPolicyBackend validates it)
                if (call.Name == "run_command" && !toolArgs.ContainsKey("directory"))
                    toolArgs["directory"] = workingDirectory;

                var (allowed, reason) = governance.EvaluateToolCall(agentId, call.Name, toolArgs, _logger);

                if (!allowed)
                {
                    var denyMsg = "Error: operation denied by sandbox policy.";
                    Emit("tool.error", new { callId = call.CallId, errorMessage = reason ?? denyMsg });
                    toolResults.Add(new FunctionResultContent(call.CallId, denyMsg));
                    continue;
                }

                // Governance passed — invoke the tool (SandboxedFileTools provides defense-in-depth)
                string resultText;
                try
                {
                    var fnArgs = new AIFunctionArguments(call.Arguments!);
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

    private static List<AITool> BuildTools(
        SandboxedFileTools fileTools,
        SandboxedSearchTools searchTools,
        ISandboxExecutor executor,
        SandboxOutputRedactor redactor,
        string workingDirectory,
        string sandboxRoot,
        SandboxOptions options,
        CancellationToken ct)
    {
        var tools = new List<AITool>();

        // --- run_command (shell, conditional) ---
        if (executor.IsRealIsolation && options.ShellEnabled)
        {
            tools.Add(AIFunctionFactory.Create(
                async (
                    [Description("Shell command to execute inside the sandbox.")] string command,
                    [Description("Timeout in milliseconds (default 30000).")] int? timeout_ms) =>
                {
                    var fsPolicy = SandboxFsPolicyBuilder.Build(sandboxRoot, options.AllowedRepositoryRoots);
                    var cmd = new SandboxCommand(command, workingDirectory, null, fsPolicy, timeout_ms ?? 30_000);
                    var result = await executor.ExecuteAsync(cmd, ct);
                    return FormatShellResult(result, redactor);
                },
                "run_command", "Run a shell command inside the sandbox."));
        }

        // --- read_file ---
        tools.Add(AIFunctionFactory.Create(
            async ([Description("File path relative to the working directory.")] string path) =>
            {
                var (content, failure) = await fileTools.ReadFileAsync(path, ct);
                return failure is not null ? $"Error: {failure.Message}" : content!;
            },
            "read_file", "Read the contents of a file."));

        // --- str_replace_editor ---
        tools.Add(AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Exact string to replace (must be unique in the file).")] string old_str,
                [Description("Replacement string.")] string new_str) =>
            {
                var (replaced, failure) = await fileTools.StrReplaceAsync(path, old_str, new_str, ct);
                return failure is not null ? $"Error: {failure.Message}" : (replaced ? "ok" : "not replaced");
            },
            "str_replace_editor", "Replace a unique string in a file with a new string."));

        // --- apply_patch ---
        tools.Add(AIFunctionFactory.Create(
            async ([Description("Patch content in the Copilot CLI patch grammar.")] string patch) =>
            {
                var result = await fileTools.ApplyPatchAsync(patch, ct);
                if (!result.Success) return $"Error: {result.Reason}";
                var summary = string.Join("; ", result.Hunks.Select(h => h.Success ? $"{h.Path}: ok" : $"{h.Path}: {h.Error}"));
                return $"Patch applied. {summary}";
            },
            "apply_patch", "Apply a patch in the Copilot CLI patch grammar."));

        // --- create ---
        tools.Add(AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Content to write to the new file.")] string file_text) =>
            {
                var (_, failure) = await fileTools.CreateFileAsync(path, file_text, ct);
                return failure is not null ? $"Error: {failure.Message}" : "ok";
            },
            "create", "Create a new file (fails if the file already exists)."));

        // --- edit (write/overwrite) ---
        tools.Add(AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Content to write.")] string content) =>
            {
                var (_, failure) = await fileTools.WriteFileAsync(path, content, ct);
                return failure is not null ? $"Error: {failure.Message}" : "ok";
            },
            "edit", "Write content to a file (creates or overwrites)."));

        // --- grep_search ---
        tools.Add(AIFunctionFactory.Create(
            async (
                [Description("Search pattern (literal string or regex).")] string pattern,
                [Description("Whether pattern is a regex. Default false.")] bool? is_regex,
                [Description("Glob pattern to filter files (e.g. '**/*.cs').")] string? include_pattern,
                [Description("Max number of results. Default 50.")] int? max_results) =>
            {
                var matches = await searchTools.GrepSearchAsync(pattern, is_regex ?? false, include_pattern, max_results ?? 50, caseSensitive: false, ct);
                if (matches.Count == 0) return "No matches found.";
                return string.Join("\n", matches.Select(m => $"{m.RelativePath}:{m.LineNumber}: {m.LineContent}"));
            },
            "grep_search", "Search for a pattern in files under the working directory."));

        // --- file_search ---
        tools.Add(AIFunctionFactory.Create(
            async (
                [Description("Glob pattern to match file paths (e.g. '**/*.cs').")] string pattern,
                [Description("Max number of results. Default 200.")] int? max_results) =>
            {
                var paths = await searchTools.FileSearchAsync(pattern, max_results ?? 200, ct);
                if (paths.Count == 0) return "No files found.";
                return string.Join("\n", paths);
            },
            "file_search", "Find files matching a glob pattern under the working directory."));

        // --- report_intent ---
        tools.Add(AIFunctionFactory.Create(
            ([Description("Brief description of the agent's current intent or plan step.")] string intent) =>
            {
                // Intentionally synchronous — just surfaces the intent; no I/O.
                _ = intent;
                return Task.FromResult<object?>("ok");
            },
            "report_intent", "Report the agent's current intent for display in the run UI."));

        return tools;
    }

    private static string FormatShellResult(SandboxExecResult result, SandboxOutputRedactor redactor)
    {
        var stdout = redactor.Redact(result.Stdout);
        var stderr = redactor.Redact(result.Stderr);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout)) parts.Add($"stdout:\n{stdout}");
        if (!string.IsNullOrWhiteSpace(stderr)) parts.Add($"stderr:\n{stderr}");
        parts.Add($"exit_code: {result.ExitCode}");
        if (result.TimedOut) parts.Add("timed_out: true");
        if (result.OutputTruncated) parts.Add("output_truncated: true");
        return string.Join("\n", parts);
    }
}

