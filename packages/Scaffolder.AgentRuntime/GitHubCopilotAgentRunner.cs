using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentTools;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Runs a single agent turn via the GitHub Copilot SDK + MAF streaming pipeline.
/// MAF's <c>RunStreamingAsync</c> is the execution path and yields
/// <see cref="AgentResponseUpdate"/> chunks. Incremental tokens arrive as
/// <see cref="TextContent"/> (from the SDK's <c>AssistantMessageDeltaEvent</c>),
/// while the final, authoritative message arrives as a non-text
/// <see cref="AIContent"/> whose <see cref="AIContent.RawRepresentation"/> is the
/// SDK's <c>AssistantMessageEvent</c>. Because <see cref="AgentResponseUpdate.Text"/>
/// only concatenates <see cref="TextContent"/>, the final message text is NOT visible
/// through <c>chunk.Text</c> and must be read from its raw representation; otherwise
/// turns that emit no token deltas would yield no text at all.
/// </summary>
public sealed class GitHubCopilotAgentRunner : IAgentRunner
{
    /// <summary>
    /// System prompt appended as a system message via AsAIAgent(instructions:...).
    /// Tells the Claude model to use our custom tools instead of native CLI tools.
    /// </summary>
    private const string CopilotSystemPrompt =
        """
        IMPORTANT: Use ONLY the custom tools listed below. Do NOT call bash, view, glob, ls,
        grep, curl, git, gh, or any other native tool — they are disabled and will error.

        Available tools:
        - read_file(path): read a file (relative path from working directory)
        - write_file(path, content): overwrite or create a file with full content
        - create_file(path, file_text): create a new file (fails if already exists)
        - str_replace_editor(path, old_str, new_str): replace a unique string in a file
        - apply_patch(patch): apply a Copilot CLI patch
        - grep_search(pattern): search files for a pattern
        - file_search(pattern): find files by glob pattern
        - run_command(command): run a shell command
        - report_intent(intent): describe what you are about to do

        All path arguments must be RELATIVE — no absolute paths.
        Work step by step. Complete the full task including all file writes.
        """;

    /// <summary>
    /// SDK-internal tools whose lifecycle events are suppressed from the run stream.
    /// These are housekeeping operations (not sandboxed file/shell ops) that would
    /// confuse the frontend if rendered as ToolCallCards. This static allowlist is the
    /// sole suppress decision source — never driven by model-controlled strings.
    /// </summary>
    private static readonly HashSet<string> SuppressedInternalTools =
        new(StringComparer.OrdinalIgnoreCase) { "report_intent", "glob" };

    private readonly GitHubCopilotClientFactory _factory;
    private readonly ISandboxExecutor _executor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly ILogger<GitHubCopilotAgentRunner> _logger;

    public GitHubCopilotAgentRunner(
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        ILogger<GitHubCopilotAgentRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExecuteAsync(string task, string workingDirectory, string repositoryPath, ModelSource modelSource, string runId, ChannelWriter<RunEvent>? stream, CancellationToken ct)
    {
        _logger.LogInformation("ExecuteAsync entered — workingDirectory={WorkingDirectory}, taskLength={TaskLength}, runId={RunId}, streamIsNull={StreamIsNull}",
            workingDirectory, task.Length, runId, stream is null);
        _logger.LogDebug("Task content preview: {TaskPreview}", task.Length > 100 ? task[..100] : task);

        // --- Governance kernel (per-run) ---
        var sandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(repositoryPath, ct);
        var executor = sandboxPolicy.Direct
            ? new PassthroughExecutor("direct execution — sandbox disabled via settings.yml", _logger)
            : _executor;
        using var governance = SandboxGovernance.Create(workingDirectory, runId, executor, sandboxPolicy, _logger);

        await using var client = _factory.CreateClient();
        await client.StartAsync(ct);

        _logger.LogInformation("Copilot client started");

        // The deny-by-default OnPermissionRequest handler is the authoritative sandbox
        // gate: it fires for every native tool call (read/write/shell/mcp), maps it to a
        // governed tool name + path, and rejects anything outside the working directory or
        // any non-file tool. We intentionally do NOT set AvailableTools: the SDK's native
        // file tool is named "view" (not "read_file"), so an allowlist of logical names
        // would offer the model no usable tools at all — leaving file operations broken and
        // the permission gate never exercised.
        //
        // EnableConfigDiscovery is forced off so the session is hermetic: the SDK will not
        // auto-load MCP servers, skills, custom agents, or instruction files from disk. That
        // closes the one surface that could introduce tools which execute without passing
        // through OnPermissionRequest (an attacker who can write into or near the working
        // directory cannot register a config-driven server/skill). Native file/shell tools
        // remain governed by the handler above.
        //
        // The per-tool run events on this provider come from two correlated sources, both
        // keyed by the SDK's ToolCallId so each tool.call is emitted exactly once:
        //   1. OnPermissionRequest (below) — fires for EVERY native tool call before it runs.
        //      It surfaces the tool.call and, for a DENIED call, the tool.error (denied calls
        //      never execute, so they produce no execution-complete event).
        //   2. The MAF streaming loop — the SDK's ToolExecutionStart/Complete lifecycle events
        //      are delivered inline as chunk.Contents[].RawRepresentation (the standalone
        //      SessionConfig.OnEvent callback is never invoked on the RunStreamingAsync path).
        //      ToolExecutionComplete carries the approved call's real result, surfaced as
        //      tool.result (success) or tool.error (failure) — same content parity as Foundry.

        // --- Thread-safe run-event emission ---
        // The permission handler fires on SDK callback threads concurrently with the MAF
        // streaming loop, so the sequence increment and the channel write are taken under one
        // lock. This keeps event sequence numbers monotonic AND in arrival order.
        var sb = new StringBuilder();
        var seq = 0;
        var emitLock = new object();
        var deltaCount = 0;
        var streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var anyDeltaEmittedForNullId = false;

        void Emit(string type, object payload)
        {
            if (stream is null) return;
            var nonNullStream = stream;
            lock (emitLock)
            {
                if (!nonNullStream.TryWrite(new RunEvent(++seq, type, payload)))
                    _logger.LogWarning("TryWrite false for {EventType}", type);
            }
        }

        // Each native tool call carries a distinct ToolCallId. These gates ensure exactly one
        // tool.call and one terminal (tool.result OR tool.error) per call, regardless of which
        // source (permission handler or streaming lifecycle event) observes it first.
        var emittedCalls = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var emittedTerminals = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // Tracks ToolCallIds of suppressed SDK-internal tools for this run invocation.
        // Scoped to ExecuteAsync so it is GC'd on return — no static/global mutable state.
        var suppressedCallIds = new HashSet<string>(StringComparer.Ordinal);

        void EmitToolCallOnce(string callId, string toolName, object? arguments)
        {
            if (emittedCalls.TryAdd(callId, 0))
                Emit("tool.call", new { callId, toolName, arguments });
        }

        void EmitToolResultOnce(string callId, string content)
        {
            EmitToolCallOnce(callId, "unknown", null); // defensive call-before-result
            if (emittedTerminals.TryAdd(callId, 0))
                Emit("tool.result", new { callId, content });
        }

        void EmitToolErrorOnce(string callId, string errorMessage)
        {
            EmitToolCallOnce(callId, "unknown", null); // defensive call-before-error
            if (emittedTerminals.TryAdd(callId, 0))
                Emit("tool.error", new { callId, errorMessage });
        }

        void EmitDelta(string text, string? messageId)
        {
            sb.Append(text);
            if (stream is null) return;
            Emit("agent.message.delta", new { delta = text, messageId });
            deltaCount++;
            if (messageId is null) anyDeltaEmittedForNullId = true;
        }

        // Translates the SDK tool-execution lifecycle (delivered inline through the MAF stream
        // as chunk content raw representations) into individual tool.call / tool.result /
        // tool.error run events. Observe-only: it never alters execution. The result content
        // is the SDK's own execution output for an approved (in-sandbox) call — nothing is
        // fabricated. Denied calls never execute, so they never reach this path.
        void TranslateToolLifecycle(object? raw)
        {
            switch (raw)
            {
                case ToolExecutionStartEvent start when start.Data is not null:
                {
                    var callId = start.Data.ToolCallId ?? Guid.NewGuid().ToString("n");
                    if (SuppressedInternalTools.Contains(start.Data.ToolName ?? ""))
                    {
                        suppressedCallIds.Add(callId);
                        break;
                    }
                    EmitToolCallOnce(callId, start.Data.ToolName ?? "unknown", start.Data.Arguments);
                    break;
                }
                case ToolExecutionCompleteEvent complete when complete.Data is not null:
                {
                    var callId = complete.Data.ToolCallId ?? Guid.NewGuid().ToString("n");
                    if (suppressedCallIds.Contains(callId))
                        break;
                    if (complete.Data.Success)
                        EmitToolResultOnce(callId, complete.Data.Result?.Content ?? string.Empty);
                    else
                        EmitToolErrorOnce(callId, complete.Data.Error?.Message ?? "Tool execution failed.");
                    break;
                }
            }
        }

        var fileTools = new SandboxedFileTools(workingDirectory);
        var searchTools = new SandboxedSearchTools(workingDirectory);
        var redactor = SandboxOutputRedactor.Default;
        var agentId = $"did:mesh:scaffolder:copilot:{runId}";

        // --- Emit sandbox backend selection event (T019) ---
        Emit("sandbox.selected", new { backend = executor.BackendName, isRealIsolation = executor.IsRealIsolation, reason = executor.SelectionReason });

        // Emit configuration snapshot for debuggability.
        // Copilot uses native CLI tools; no custom tools or AvailableTools/ExcludedTools are set.
        Emit("agent.system_prompt", new
        {
            provider = "copilot",
            note = "Native Copilot CLI tools active — no custom tool overrides. System prompt is Copilot CLI built-in.",
            sandbox_policy = new
            {
                shell_enabled = sandboxPolicy.ShellEnabled,
                direct = sandboxPolicy.Direct,
                network_enabled = sandboxPolicy.NetworkEnabled,
            },
        });
        if (executor.HasNetworkWarning)
        {
            Emit("sandbox.warning", new { category = "network-open", message = executor.NetworkWarningMessage, backend = executor.BackendName });
        }
        if (sandboxPolicy.NetworkEnabled)
        {
            Emit("sandbox.warning", new
            {
                category = "network-open",
                message = "Sandbox is running with outbound network enabled (network_enabled: true in .scaffolder/settings.yml). " +
                          "Network access is intentional but increases the attack surface. " +
                          "Ensure this is required for the agent's task.",
                backend = executor.BackendName
            });
        }

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
            SandboxRoot: workingDirectory,
            Executor: executor,
            FileTools: fileTools,
            SearchTools: searchTools,
            Redactor: redactor,
            Options: toolOptions,
            Logger: _logger,
            EmitEvent: Emit,
            RunId: runId,
            IsCommandApproved: hash => _approvalStore.IsApproved(runId, hash));

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = BuildPermissionHandler(governance, runId, workingDirectory, EmitToolCallOnce, EmitToolErrorOnce),
            WorkingDirectory = workingDirectory,
            EnableConfigDiscovery = false,
            Streaming = true,
            // Do not register custom tools or restrict AvailableTools/ExcludedTools.
            // Let Copilot CLI use its own native tools; governance runs via OnPermissionRequest.
        };

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);
        var session = await agent.CreateSessionAsync(ct);

        _logger.LogInformation("MAF agent session created with sandbox governance — runId={RunId}", runId);

        try
        {
        try
        {
            await foreach (var chunk in agent.RunStreamingAsync(task, session, options: null, ct).WithCancellation(ct))
            {
                if (chunk is null) continue;

                var messageId = chunk.MessageId;

                // Incremental token text surfaces as TextContent (AssistantMessageDeltaEvent).
                var deltaText = chunk.Text;
                if (!string.IsNullOrEmpty(deltaText))
                {
                    EmitDelta(deltaText, messageId);
                    if (messageId is not null) streamedMessageIds.Add(messageId);
                }

                // The final, authoritative message arrives as a non-text AIContent whose
                // RawRepresentation is the SDK AssistantMessageEvent. Surface its content when
                // no token deltas were streamed for this message, so text is never lost (and is
                // not double-counted when deltas already covered it).
                var finalContent = ExtractFinalMessageContent(chunk);
                if (!string.IsNullOrEmpty(finalContent))
                {
                    var alreadyStreamed = messageId is not null
                        ? streamedMessageIds.Contains(messageId)
                        : anyDeltaEmittedForNullId;

                    if (!alreadyStreamed)
                    {
                        EmitDelta(finalContent, messageId);
                        if (messageId is not null) streamedMessageIds.Add(messageId);
                    }
                    else if (messageId is null)
                    {
                        _logger.LogWarning("Final message with null messageId skipped — delta text was already emitted");
                    }
                }

                if (string.IsNullOrEmpty(deltaText) && string.IsNullOrEmpty(finalContent))
                    _logger.LogTrace("RunStreamingAsync non-text chunk — messageId={MessageId}", messageId);

                // The SDK tool-execution lifecycle arrives inline as content raw representations.
                if (chunk.Contents is not null)
                {
                    foreach (var c in chunk.Contents)
                        TranslateToolLifecycle(c.RawRepresentation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunStreamingAsync threw for workingDirectory={WorkingDirectory}", workingDirectory);
            Emit("run.failed", new { message = "The agent encountered an internal error." });
            throw;
        }

        Emit("agent.turn.end", new { turnId = "0" });

        if (suppressedCallIds.Count > 0)
            _logger.LogInformation("Suppressed {Count} SDK-internal tool events", suppressedCallIds.Count);

        var result = sb.ToString();
        _logger.LogInformation(
            "Run complete — deltaCount={DeltaCount}, resultLength={ResultLength}",
            deltaCount, result.Length);

        return result;
        }
        finally
        {
            _approvalStore.Clear(runId);
        }
    }

    /// <summary>
    /// Builds the permission handler that enforces sandbox containment through
    /// two independent layers: AGT policy evaluation AND direct SandboxPolicyBackend check.
    /// Both must allow for the tool call to proceed. The handler is also a per-tool
    /// observability source. A denied call is surfaced here as a tool.call + tool.error pair
    /// carrying the gate reason, because denied calls never execute and so never reach the
    /// streaming tool-execution lifecycle. An approved call is surfaced from that lifecycle in
    /// the streaming loop (call + real result); the handler only co-emits its tool.call when it
    /// holds the SDK's real ToolCallId, so the two sources dedup instead of diverging.
    /// </summary>
    private PermissionRequestHandler BuildPermissionHandler(
        SandboxGovernance governance,
        string runId,
        string workingDirectory,
        Action<string, string, object?> emitToolCallOnce,
        Action<string, string> emitToolErrorOnce)
    {
        return (request, invocation) =>
        {
            // Custom external tools registered in SessionConfig.Tools fire OnPermissionRequest
            // with PermissionRequestCustomTool. Run governance against the tool name + args
            // from the request — same two-layer check as native tools — before approving.
            // The inline EvaluateToolCall inside each tool lambda is a second defense-in-depth
            // layer, not the primary gate.
            if (request is PermissionRequestCustomTool customTool)
            {
                var realCustomCallId = customTool.ToolCallId;
                var customCallId = realCustomCallId ?? Guid.NewGuid().ToString("n");
                var toolName = customTool.ToolName ?? "unknown";
                try
                {
                    // Deserialize the JSON args blob. Stamp tool_name first so it cannot be
                    // overridden by a model-supplied key (Seraph hardening).
                    var args = new Dictionary<string, object>();
                    if (customTool.Args is System.Text.Json.JsonElement argsJson &&
                        argsJson.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in argsJson.EnumerateObject())
                            args[prop.Name] = prop.Value;
                    }
                    args["tool_name"] = toolName;  // overwrite after deserialization

                    // Shell tools need "directory" for SandboxPolicyBackend to validate cwd.
                    if (toolName == "run_command" && !args.ContainsKey("directory"))
                        args["directory"] = workingDirectory;

                    // Emit tool.call only when we hold the real ToolCallId — mirrors the native
                    // path dedup logic. Approved custom tools emit their call via the SDK lifecycle
                    // (ExternalToolRequestedEvent). Denied calls never reach the lifecycle so we
                    // emit the call+error pair below regardless of whether we have a real ID.
                    if (realCustomCallId is not null)
                        emitToolCallOnce(customCallId, toolName, args);

                    var (allowed, reason) = governance.EvaluateToolCall(
                        agentId: $"did:mesh:scaffolder:copilot:{runId}",
                        toolName: toolName,
                        args: args,
                        _logger);

                    if (!allowed)
                    {
                        emitToolCallOnce(customCallId, toolName, args);
                        emitToolErrorOnce(customCallId, reason ?? "Operation denied by sandbox policy.");
                    }

                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = allowed
                            ? PermissionRequestResultKind.Approved
                            : PermissionRequestResultKind.Rejected,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Permission handler exception for custom tool (fail-closed deny) — Tool={ToolName} RunId={RunId}",
                        toolName, runId);
                    emitToolCallOnce(customCallId, toolName, null);
                    emitToolErrorOnce(customCallId, "Operation denied: internal error evaluating sandbox policy.");
                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = PermissionRequestResultKind.Rejected,
                    });
                }
            }

            // The real SDK ToolCallId correlates this handler's events with the streaming
            // tool-execution lifecycle (which carries the same id). When it can't be read, we
            // fall back to a synthetic id that is local to this handler — the lifecycle cannot
            // see it. realCallId being null therefore changes which source owns emission below.
            var realCallId = GetToolCallId(request);
            var callId = realCallId ?? Guid.NewGuid().ToString("n");
            try
            {
                var (toolName, args) = MapToToolCall(request);

                // Surface the call from this source ONLY when we hold the real ToolCallId, so it
                // dedups against the streaming lifecycle. With a synthetic id we stay silent on
                // the approved path (the lifecycle, which has the real id, emits the call and its
                // result) to avoid a divergent duplicate tool.call. Denied calls never execute,
                // so they never reach the lifecycle — those are emitted in the deny branch below.
                if (realCallId is not null)
                    emitToolCallOnce(callId, toolName, args);

                var (allowed, reason) = governance.EvaluateToolCall(
                    agentId: $"did:mesh:scaffolder:copilot:{runId}",
                    toolName: toolName,
                    args: args,
                    _logger);

                if (!allowed)
                {
                    // A denied call is terminal here and never reaches the lifecycle, so emit a
                    // self-consistent call+error pair. emitToolCallOnce is a no-op if the call was
                    // already surfaced above; for a synthetic id this is its only emission point.
                    emitToolCallOnce(callId, toolName, args);
                    emitToolErrorOnce(callId, reason ?? "Operation denied by sandbox policy.");
                }

                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = allowed
                        ? PermissionRequestResultKind.Approved
                        : PermissionRequestResultKind.Rejected,
                });
            }
            catch (Exception ex)
            {
                // Fail-closed: any failure mapping or evaluating the request denies the tool call.
                _logger.LogError(ex, "Permission handler exception (fail-closed deny) — RunId={RunId}", runId);
                emitToolCallOnce(callId, request.Kind ?? "unknown", null);
                emitToolErrorOnce(callId, "Operation denied: internal error evaluating sandbox policy.");
                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Rejected,
                });
            }
        };
    }

    /// <summary>
    /// Reads the <c>ToolCallId</c> carried by every concrete Copilot SDK
    /// <see cref="PermissionRequest"/> subtype (Read/Write/Shell/Mcp/...). The base
    /// type exposes only <see cref="PermissionRequest.Kind"/>, so the id is read
    /// reflectively to cover all subtypes uniformly. Used to correlate a denied call
    /// with its tool.error and to dedup against the execution lifecycle events.
    /// </summary>
    private static string? GetToolCallId(PermissionRequest request)
        => request.GetType().GetProperty("ToolCallId")?.GetValue(request) as string;

    /// <summary>
    /// Maps a Copilot SDK <see cref="PermissionRequest"/> to an AGT tool-call
    /// representation (tool name + arguments dictionary).
    /// </summary>
    internal static (string toolName, Dictionary<string, object> args) MapToToolCall(
        PermissionRequest request)
    {
        return request switch
        {
            PermissionRequestRead read => MapReadRequest(read),
            // SDK routes both write_file and edit_file through PermissionRequestWrite
            // (no distinct PermissionRequestEdit type exists). YAML rule "allow-file-write"
            // covers both tool names; SandboxPolicyBackend path-checks the FileName.
            PermissionRequestWrite write => ("write_file", new Dictionary<string, object>
            {
                ["path"] = write.FileName ?? "",
            }),
            PermissionRequestShell shell => ("shell", new Dictionary<string, object>
            {
                ["command"] = shell.FullCommandText ?? "",
            }),
            PermissionRequestMcp mcp => ("mcp", new Dictionary<string, object>
            {
                ["tool"] = mcp.ToolName ?? "",
            }),
            _ => (request.Kind ?? "unknown", new Dictionary<string, object>()),
        };
    }

    /// <summary>
    /// Disambiguates a read permission request into either "read_file" or "list_directory".
    /// Heuristic: trailing directory separator OR <see cref="Directory.Exists"/> → list_directory.
    /// </summary>
    internal static (string toolName, Dictionary<string, object> args) MapReadRequest(
        PermissionRequestRead request)
    {
        var path = request.Path ?? "";
        var args = new Dictionary<string, object> { ["path"] = path };

        // Heuristic: trailing separator OR existing directory → list_directory
        if (path.Length > 0 &&
            (path[^1] == Path.DirectorySeparatorChar ||
             path[^1] == Path.AltDirectorySeparatorChar ||
             Directory.Exists(path)))
        {
            return ("list_directory", args);
        }

        return ("read_file", args);
    }

    /// <summary>
    /// Builds the tool list for the Copilot SDK session, stamping
    /// <see cref="CopilotTool.OverridesBuiltInToolKey"/> on tools whose names match a
    /// native Copilot built-in. Without this flag the SDK rejects them with
    /// "conflicts with a built-in tool of the same name".
    /// <c>run_command</c> is excluded from the override set — no native tool has that name.
    /// </summary>
    private static IList<AIFunction> BuildCopilotTools(SandboxToolContext context)
    {
        // Tools that share a name with a Copilot CLI native built-in must be marked
        // as intentional overrides. run_command has no native equivalent — leave it unmarked.
        var nativeOverrides = new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file", "str_replace_editor", "apply_patch",
            "create_file", "write_file", "grep_search", "file_search", "report_intent",
        };

        return SandboxToolRegistry.Build(context)
            .Select(f => nativeOverrides.Contains(f.Name)
                ? new CopilotOverrideAIFunction(f)
                : f)
            .ToList();
    }

    /// <summary>
    /// Wraps an <see cref="AIFunction"/> and injects
    /// <see cref="CopilotTool.OverridesBuiltInToolKey"/> into <see cref="AITool.AdditionalProperties"/>
    /// so the Copilot SDK accepts tools whose names match a native built-in.
    /// </summary>
    private sealed class CopilotOverrideAIFunction(AIFunction inner) : AIFunction
    {
        // The key expected by the Copilot SDK in AITool.AdditionalProperties.
        // CopilotTool.OverridesBuiltInToolKey is internal in the SDK package;
        // the string value is confirmed by the SDK's own error message:
        // "Set overridesBuiltInTool: true to explicitly override it."
        private const string OverridesBuiltInToolKey = "overridesBuiltInTool";

        private readonly IReadOnlyDictionary<string, object?> _additionalProperties =
            new Dictionary<string, object?>(inner.AdditionalProperties)
            {
                [OverridesBuiltInToolKey] = true,
            };

        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _additionalProperties;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            inner.InvokeAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Extracts the full assistant message text from the SDK <see cref="AssistantMessageEvent"/>
    /// carried as the <see cref="AIContent.RawRepresentation"/> of a chunk. MAF wraps this final
    /// message in a plain <see cref="AIContent"/> (not <see cref="TextContent"/>), so the text is
    /// invisible to <see cref="AgentResponseUpdate.Text"/> and must be read directly.
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
}
