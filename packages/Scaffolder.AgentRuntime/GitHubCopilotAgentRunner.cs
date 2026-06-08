using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Safety;
using Scaffolder.Domain;
using Scaffolder.Domain.Payloads;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// The GitHub Copilot agent loop (Principle I, II). Drives a run through the
/// official <c>GitHub.Copilot.SDK</c>, which spawns the Copilot CLI subprocess
/// and exposes a session with lifecycle hooks. The runtime registers the same
/// sandboxed read/write tools the Foundry path uses, enforces run bounds through
/// the pre-tool-use hook, applies the content-safety gate inside the write tool
/// and on assistant messages, and emits the same ordered, auditable event stream
/// (Principles V, IX, X, XI).
/// </summary>
/// <remarks>
/// A "step" is one tool call (user decision), counted in the pre-tool-use hook.
/// callId correlation between the streaming <see cref="ExternalToolRequestedEvent"/>
/// and the pre/post tool-use hooks uses a per-tool-name FIFO queue inside a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for future concurrency safety.
/// Bounded abort is driven by cancelling a linked
/// <see cref="CancellationTokenSource"/> from inside the hook closures.
/// </remarks>
public sealed class GitHubCopilotAgentRunner : IAgentRunner
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<string> AllowedTools = new[] { "read_file", "write_file" };

    private readonly GitHubCopilotClientFactory _factory;
    private readonly ContentSafetyChecker _safetyChecker;
    private readonly RunBounds _bounds;

    public GitHubCopilotAgentRunner(
        GitHubCopilotClientFactory factory,
        ContentSafetyChecker safetyChecker,
        RunBounds bounds)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _safetyChecker = safetyChecker ?? throw new ArgumentNullException(nameof(safetyChecker));
        _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
    }

    public async Task ExecuteAsync(Run run, IRunEventPublisher publisher, IRunEventStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(run.WorktreePath))
        {
            throw new InvalidOperationException(
                "Run has no worktree path; the agent loop requires an isolated working directory.");
        }

        if (run.ModelSource != ModelSource.GitHubCopilot)
        {
            throw new InvalidOperationException(
                $"Runner is for GitHubCopilot; run uses {run.ModelSource}.");
        }

        // --- Per-run state (captured by the hook and event closures) ---
        var stepCount = 0;            // step = tool call; mutated via Interlocked
        var terminalEmitted = 0;      // 0 = no terminal event yet, 1 = emitted
        var wallClockDeadline = run.StartedAt.Add(_bounds.MaxDuration);

        using var boundedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // callId correlation: ExternalToolRequestedEvent enqueues the tool call id
        // per tool name; the pre-tool-use hook peeks and the post hooks dequeue.
        var pendingCallIds = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        var sandboxTools = new SandboxedFileTools(run.WorktreePath);
        var systemPrompt = BuildSystemPrompt(run);

        // Per-run random sentinel so an adversary cannot plant the sentinel string in
        // a file to suppress tool.result events via OnPostToolUse (Seraph F4).
        var contentSafetySentinel = $"[CONTENT_SAFETY_FAILED:{Guid.NewGuid():N}]";

        // Bridges the synchronous On<T> handlers to async emission.
        var channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        Task<bool> TryBeginTerminalAsync()
            => Task.FromResult(Interlocked.CompareExchange(ref terminalEmitted, 1, 0) == 0);

        // --- Real tool implementations invoked by the Copilot CLI ---
        var readTool = CopilotTool.DefineTool(
            async (string path, CancellationToken innerCt) =>
            {
                var (content, failure) = await sandboxTools.ReadFileAsync(path, innerCt);
                return failure is not null ? $"Error: {failure.Message}" : content ?? string.Empty;
            },
            new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions
            {
                Name = "read_file",
                Description = "Read the text content of a file inside the working area. Path must be relative.",
            });

        var writeTool = CopilotTool.DefineTool(
            async (string path, string content, CancellationToken innerCt) =>
            {
                // Content-safety gate on write content (FR-025, Seraph F2/B2).
                var (safe, reason) = _safetyChecker.Check(content, run.ModelSource);
                if (!safe)
                {
                    if (await TryBeginTerminalAsync())
                    {
                        await EmitEventAsync(run.Id, EventType.RunFailed,
                            new RunFailedPayload { Reason = $"Content safety check failed: {reason}" },
                            null, publisher, store, ct);
                    }

                    boundedCts.Cancel();
                    return $"{contentSafetySentinel}: {reason}";
                }

                var (bytesWritten, failure) = await sandboxTools.WriteFileAsync(path, content, innerCt);
                return failure is not null
                    ? $"Error: {failure.Message}"
                    : $"Written {bytesWritten} bytes to {path}";
            },
            new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions
            {
                Name = "write_file",
                Description = "Write text content to a file inside the working area. Creates or overwrites. Path must be relative.",
            });

        // --- Session configuration with lifecycle hooks ---
        var sessionConfig = new SessionConfig
        {
            WorkingDirectory = run.WorktreePath,
            Model = _factory.Model,
            Tools = new List<AIFunctionDeclaration> { readTool, writeTool },
            AvailableTools = new List<string>(AllowedTools),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemPrompt,
            },
            Hooks = new SessionHooks
            {
                OnSessionStart = async (input, invocation) =>
                {
                    await Task.CompletedTask;
                    return new SessionStartHookOutput { AdditionalContext = systemPrompt };
                },

                OnPreToolUse = async (input, invocation) =>
                {
                    var toolName = input.ToolName ?? string.Empty;

                    // Bounds: wall-clock.
                    if (DateTimeOffset.UtcNow >= wallClockDeadline)
                    {
                        if (await TryBeginTerminalAsync())
                        {
                            await EmitEventAsync(run.Id, EventType.RunBounded,
                                new RunBoundedPayload { LimitType = "wall-clock", StepCount = Volatile.Read(ref stepCount) },
                                null, publisher, store, ct);
                        }

                        boundedCts.Cancel();
                        return new PreToolUseHookOutput
                        {
                            PermissionDecision = "deny",
                            PermissionDecisionReason = "Wall-clock limit reached",
                        };
                    }

                    // Bounds: step count.
                    if (Volatile.Read(ref stepCount) >= _bounds.MaxSteps)
                    {
                        if (await TryBeginTerminalAsync())
                        {
                            await EmitEventAsync(run.Id, EventType.RunBounded,
                                new RunBoundedPayload { LimitType = "step-count", StepCount = Volatile.Read(ref stepCount) },
                                null, publisher, store, ct);
                        }

                        boundedCts.Cancel();
                        return new PreToolUseHookOutput
                        {
                            PermissionDecision = "deny",
                            PermissionDecisionReason = "Step limit reached",
                        };
                    }

                    var path = ExtractPath(input.ToolArgs);
                    var callId = PeekCallId(pendingCallIds, toolName);
                    var operation = toolName == "write_file" ? ToolOperation.Write : ToolOperation.Read;

                    await EmitEventAsync(run.Id, EventType.ToolCall,
                        new ToolCallPayload { Path = path, Operation = operation },
                        callId, publisher, store, ct);

                    // Defence-in-depth (Seraph F1): the enforcing boundary is
                    // SandboxedFileTools; this hook rejects obviously unsafe paths early.
                    if (!IsPathSafe(path))
                    {
                        DequeueCallId(pendingCallIds, toolName);
                        await EmitEventAsync(run.Id, EventType.ToolRejected,
                            new ToolRejectedPayload { Path = path, Reason = "Path rejected by sandbox policy" },
                            callId, publisher, store, ct);
                        return new PreToolUseHookOutput
                        {
                            PermissionDecision = "deny",
                            PermissionDecisionReason = "Path rejected by sandbox policy",
                        };
                    }

                    Interlocked.Increment(ref stepCount);
                    return new PreToolUseHookOutput { PermissionDecision = "allow" };
                },

                OnPostToolUse = async (input, invocation) =>
                {
                    var toolName = input.ToolName ?? string.Empty;
                    var path = ExtractPath(input.ToolArgs);
                    var callId = DequeueCallId(pendingCallIds, toolName);

                    // A content-safety failure already emitted run.failed inside the tool.
                    var resultText = input.ToolResult?.ToString() ?? string.Empty;
                    if (resultText.Contains(contentSafetySentinel, StringComparison.Ordinal))
                    {
                        return new PostToolUseHookOutput();
                    }

                    if (resultText.StartsWith("Error:", StringComparison.Ordinal))
                    {
                        await EmitEventAsync(run.Id, EventType.ToolError,
                            new ToolErrorPayload { Path = path, ErrorMessage = resultText[6..].Trim() },
                            callId, publisher, store, ct);
                        return new PostToolUseHookOutput();
                    }

                    // Compute byte count for audit parity with the Foundry path (RD#2).
                    // For write_file the delegate returns "Written N bytes to {path}";
                    // for read_file the result IS the file content.
                    var bytes = (input.ToolName ?? string.Empty) == "write_file"
                        ? ParseBytesWritten(resultText)
                        : Encoding.UTF8.GetByteCount(resultText);

                    await EmitEventAsync(run.Id, EventType.ToolResult,
                        new ToolResultPayload { Path = path, BytesReadOrWritten = bytes },
                        callId, publisher, store, ct);
                    return new PostToolUseHookOutput();
                },

                OnPostToolUseFailure = async (input, invocation) =>
                {
                    var toolName = input.ToolName ?? string.Empty;
                    var path = ExtractPath(input.ToolArgs);
                    var callId = DequeueCallId(pendingCallIds, toolName);

                    await EmitEventAsync(run.Id, EventType.ToolError,
                        new ToolErrorPayload { Path = path, ErrorMessage = input.Error ?? "Unknown error" },
                        callId, publisher, store, ct);
                    return new PostToolUseFailureHookOutput();
                },

                OnErrorOccurred = async (input, invocation) =>
                {
                    await Task.CompletedTask;
                    return new ErrorOccurredHookOutput();
                },
            },
        };

        // --- Start the client and session ---
        await using var client = _factory.CreateClient();
        await client.StartAsync(boundedCts.Token);

        await using var session = await client.CreateSessionAsync(sessionConfig, boundedCts.Token);

        var subscriptions = new List<IDisposable>
        {
            session.On<ExternalToolRequestedEvent>(e =>
            {
                var data = e.Data;
                if (data is null)
                {
                    return;
                }

                var toolName = data.ToolName ?? string.Empty;
                var toolCallId = string.IsNullOrEmpty(data.ToolCallId)
                    ? Guid.NewGuid().ToString("N")
                    : data.ToolCallId;
                pendingCallIds.GetOrAdd(toolName, _ => new ConcurrentQueue<string>()).Enqueue(toolCallId);
            }),
            session.On<AssistantMessageEvent>(e => channel.Writer.TryWrite(e)),
            session.On<AbortEvent>(e => channel.Writer.TryWrite(e)),
        };

        var processTask = ProcessChannelAsync(
            channel.Reader, run, publisher, store, ct, TryBeginTerminalAsync, boundedCts);

        try
        {
            var remaining = wallClockDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                if (await TryBeginTerminalAsync())
                {
                    await EmitEventAsync(run.Id, EventType.RunBounded,
                        new RunBoundedPayload { LimitType = "wall-clock", StepCount = Volatile.Read(ref stepCount) },
                        null, publisher, store, ct);
                }

                boundedCts.Cancel();
            }
            else
            {
                // Wall-clock backstop so a run that never calls a tool is still bounded.
                boundedCts.CancelAfter(remaining);

                try
                {
                    await session.SendAndWaitAsync(run.Task, remaining, boundedCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Bounded abort (wall-clock, step-count, or content-safety). If the
                    // deadline elapsed without a terminal event yet, record it now.
                    if (DateTimeOffset.UtcNow >= wallClockDeadline && await TryBeginTerminalAsync())
                    {
                        await EmitEventAsync(run.Id, EventType.RunBounded,
                            new RunBoundedPayload { LimitType = "wall-clock", StepCount = Volatile.Read(ref stepCount) },
                            null, publisher, store, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            channel.Writer.TryComplete();
            await DrainAsync(processTask);
            DisposeAll(subscriptions);

            if (await TryBeginTerminalAsync())
            {
                await EmitEventAsync(run.Id, EventType.RunFailed,
                    new RunFailedPayload { Reason = "Run was cancelled" },
                    null, publisher, store, CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete();
            await DrainAsync(processTask);
            DisposeAll(subscriptions);

            if (await TryBeginTerminalAsync())
            {
                await EmitEventAsync(run.Id, EventType.RunFailed,
                    new RunFailedPayload { Reason = $"Unexpected error: {ex.GetType().Name}" },
                    null, publisher, store, CancellationToken.None);
            }

            throw;
        }

        channel.Writer.TryComplete();
        await DrainAsync(processTask);
        DisposeAll(subscriptions);

        // A normal completion: emit run.completed only if no terminal event fired.
        if (await TryBeginTerminalAsync())
        {
            await EmitEventAsync(run.Id, EventType.RunCompleted,
                new RunCompletedPayload { StepCount = Volatile.Read(ref stepCount) },
                null, publisher, store, ct);
        }
    }

    private async Task ProcessChannelAsync(
        ChannelReader<SessionEvent> reader,
        Run run,
        IRunEventPublisher publisher,
        IRunEventStore store,
        CancellationToken ct,
        Func<Task<bool>> tryBeginTerminal,
        CancellationTokenSource boundedCts)
    {
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            switch (evt)
            {
                case AssistantMessageEvent message:
                    var text = message.Data?.Content;
                    if (string.IsNullOrEmpty(text))
                    {
                        break;
                    }

                    var (safe, reason) = _safetyChecker.Check(text, run.ModelSource);
                    if (!safe)
                    {
                        if (await tryBeginTerminal())
                        {
                            await EmitEventAsync(run.Id, EventType.RunFailed,
                                new RunFailedPayload { Reason = $"Content safety check failed: {reason}" },
                                null, publisher, store, ct);
                        }

                        boundedCts.Cancel();
                        break;
                    }

                    await EmitEventAsync(run.Id, EventType.AgentMessage,
                        new AgentMessagePayload { Text = text },
                        null, publisher, store, ct);
                    break;

                case AbortEvent abort:
                    if (await tryBeginTerminal())
                    {
                        await EmitEventAsync(run.Id, EventType.RunFailed,
                            new RunFailedPayload { Reason = $"Session aborted: {abort.Data?.Reason}" },
                            null, publisher, store, ct);
                    }

                    boundedCts.Cancel();
                    break;
            }
        }
    }

    private async Task EmitEventAsync(
        RunId runId, string type, object payload, string? callId,
        IRunEventPublisher publisher, IRunEventStore store, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), PayloadJsonOptions);
        var evt = new RunEvent
        {
            RunId = runId,
            Sequence = 0,
            Type = type,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = json,
            CallId = callId,
        };

        var stored = await store.AppendAsync(evt, ct);
        publisher.Publish(stored);
    }

    private static string PeekCallId(
        ConcurrentDictionary<string, ConcurrentQueue<string>> pending, string toolName)
    {
        if (pending.TryGetValue(toolName, out var queue) && queue.TryPeek(out var id))
        {
            return id;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string DequeueCallId(
        ConcurrentDictionary<string, ConcurrentQueue<string>> pending, string toolName)
    {
        if (pending.TryGetValue(toolName, out var queue) && queue.TryDequeue(out var id))
        {
            return id;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string ExtractPath(JsonElement? toolArgs)
    {
        if (toolArgs is null)
        {
            return string.Empty;
        }

        try
        {
            var element = toolArgs.Value;
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("path", out var pathProperty)
                && pathProperty.ValueKind == JsonValueKind.String)
            {
                return pathProperty.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Malformed or already-disposed args; treat as no path.
        }

        return string.Empty;
    }

    private static bool IsPathSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSystemPrompt(Run run)
    {
        var worktreePath = (run.WorktreePath ?? string.Empty).Replace("\r", "").Replace("\n", "");
        var repoPath = (run.RepositoryPath ?? string.Empty).Replace("\r", "").Replace("\n", "");
        var branch = (run.OriginatingBranch ?? string.Empty).Replace("\r", "").Replace("\n", "");

        return $"""
        You are a file-editing agent. Your task is to edit files inside a working directory using the read_file and write_file tools.

        [RUN CONTEXT]
        working_directory: {worktreePath}
        repository_path: {repoPath}
        branch: {branch}
        [END RUN CONTEXT]

        Rules:
        - All file paths must be relative (no absolute paths, no .. traversal)
        - Only read and write files inside the working directory
        - When you have completed the task, respond with a final message and no further tool calls
        - Do not include any emoji characters in any output
        """;
    }

    private static async Task DrainAsync(Task processTask)
    {
        try
        {
            await processTask;
        }
        catch
        {
            // The channel processor never surfaces terminal failures to the caller;
            // any fault here must not mask the run's own terminal handling.
        }
    }

    /// <summary>
    /// Parses the byte count from the write_file delegate's return string.
    /// Expected format: "Written N bytes to {path}".
    /// </summary>
    private static long ParseBytesWritten(string result)
    {
        var parts = result.Split(' ');
        return parts.Length >= 2 && long.TryParse(parts[1], out var bytes) ? bytes : 0;
    }

    private static void DisposeAll(IEnumerable<IDisposable> disposables)
    {
        foreach (var disposable in disposables)
        {
            disposable.Dispose();
        }
    }
}
