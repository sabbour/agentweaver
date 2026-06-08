using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Safety;
using Scaffolder.Domain;
using Scaffolder.Domain.Payloads;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// The single agent loop (Principle I). Drives a run through the
/// Microsoft.Extensions.AI <see cref="IChatClient"/> abstraction, dispatching
/// the sandboxed read/write tools, enforcing run bounds, applying content
/// safety, and emitting an ordered, auditable event stream (Principles V, IX,
/// X, XI).
/// </summary>
/// <remarks>
/// Tool dispatch is performed explicitly rather than via automatic function
/// invocation so the runtime can interpose the sandbox boundary, content-safety
/// gate, and ordered audit events around every tool call. The model still
/// receives the tool schemas through <see cref="ChatOptions.Tools"/>; the
/// runtime owns invocation to keep governance enforcement in one place.
/// </remarks>
public sealed class AgentRunner : IAgentRunner
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MicrosoftFoundryChatClientFactory _factory;
    private readonly ContentSafetyChecker _safetyChecker;
    private readonly RunBounds _bounds;

    public AgentRunner(
        MicrosoftFoundryChatClientFactory factory,
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

        try
        {
            if (string.IsNullOrWhiteSpace(run.WorktreePath))
            {
                throw new InvalidOperationException(
                    "Run has no worktree path; the agent loop requires an isolated working directory.");
            }

            var client = _factory.CreateForRun(run);
            var tools = new SandboxedFileTools(run.WorktreePath);

            var readTool = AIFunctionFactory.Create(
                (string path, CancellationToken innerCt) =>
                    Task.FromException<string>(new InvalidOperationException(
                        "read_file must not be invoked automatically; dispatch manually via FunctionCallContent.")),
                name: "read_file",
                description: "Read the text content of a file inside the working area. Path must be relative.");

            var writeTool = AIFunctionFactory.Create(
                (string path, string content, CancellationToken innerCt) =>
                    Task.FromException<string>(new InvalidOperationException(
                        "write_file must not be invoked automatically; dispatch manually via FunctionCallContent.")),
                name: "write_file",
                description: "Write text content to a file inside the working area. Creates or overwrites. Path must be relative.");

            var chatOptions = new ChatOptions
            {
                Tools = [readTool, writeTool],
                ToolMode = ChatToolMode.Auto,
            };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildSystemPrompt(run)),
                new(ChatRole.User, run.Task),
            };

            var stepCount = 0;
            var wallClockDeadline = run.StartedAt.Add(_bounds.MaxDuration);

            while (true)
            {
                if (stepCount >= _bounds.MaxSteps)
                {
                    await EmitEventAsync(run.Id, EventType.RunBounded,
                        new RunBoundedPayload { LimitType = "step-count", StepCount = stepCount },
                        null, publisher, store, ct);
                    return;
                }

                var remaining = wallClockDeadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await EmitEventAsync(run.Id, EventType.RunBounded,
                        new RunBoundedPayload { LimitType = "wall-clock", StepCount = stepCount },
                        null, publisher, store, ct);
                    return;
                }

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(remaining);

                ChatResponse response;
                try
                {
                    response = await client.GetResponseAsync(messages, chatOptions, stepCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await EmitEventAsync(run.Id, EventType.RunBounded,
                        new RunBoundedPayload { LimitType = "wall-clock", StepCount = stepCount },
                        null, publisher, store, ct);
                    return;
                }

                messages.AddRange(response.Messages);

                var functionCalls = new List<FunctionCallContent>();
                foreach (var message in response.Messages)
                {
                    foreach (var contentPart in message.Contents)
                    {
                        switch (contentPart)
                        {
                            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                                var (safe, reason) = _safetyChecker.Check(textContent.Text, run.ModelSource);
                                if (!safe)
                                {
                                    await EmitEventAsync(run.Id, EventType.RunFailed,
                                        new RunFailedPayload { Reason = $"Content safety check failed: {reason}" },
                                        null, publisher, store, ct);
                                    throw new ContentSafetyException(reason!);
                                }

                                await EmitEventAsync(run.Id, EventType.AgentMessage,
                                    new AgentMessagePayload { Text = textContent.Text },
                                    null, publisher, store, ct);
                                break;

                            case FunctionCallContent functionCall:
                                functionCalls.Add(functionCall);
                                break;
                        }
                    }
                }

                if (functionCalls.Count == 0)
                {
                    await EmitEventAsync(run.Id, EventType.RunCompleted,
                        new RunCompletedPayload { StepCount = stepCount },
                        null, publisher, store, ct);
                    return;
                }

                var resultContents = new List<AIContent>(functionCalls.Count);
                foreach (var functionCall in functionCalls)
                {
                    // Step = tool call (a user-visible decision). Bound the run on the
                    // number of tool calls, checked before each one executes.
                    if (stepCount >= _bounds.MaxSteps)
                    {
                        await EmitEventAsync(run.Id, EventType.RunBounded,
                            new RunBoundedPayload { LimitType = "step-count", StepCount = stepCount },
                            null, publisher, store, ct);
                        return;
                    }

                    var callId = string.IsNullOrEmpty(functionCall.CallId)
                        ? Guid.NewGuid().ToString("N")
                        : functionCall.CallId;
                    var path = GetStringArgument(functionCall, "path");
                    var operation = functionCall.Name == "write_file" ? ToolOperation.Write : ToolOperation.Read;

                    await EmitEventAsync(run.Id, EventType.ToolCall,
                        new ToolCallPayload { Path = path, Operation = operation },
                        callId, publisher, store, ct);

                    stepCount++;

                    ToolOutcome outcome;
                    try
                    {
                        if (functionCall.Name == "write_file")
                        {
                            var content = GetStringArgument(functionCall, "content");
                            outcome = await ExecuteWriteAsync(tools, path, content, run.ModelSource, stepCts.Token);
                        }
                        else
                        {
                            outcome = await ExecuteReadAsync(tools, path, stepCts.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Wall-clock deadline fired during a tool call — emit run.bounded, not run.failed.
                        await EmitEventAsync(run.Id, EventType.RunBounded,
                            new RunBoundedPayload { LimitType = "wall-clock", StepCount = stepCount },
                            null, publisher, store, ct);
                        return;
                    }

                    await EmitToolOutcomeAsync(run.Id, outcome, path, callId, publisher, store, ct);

                    if (outcome.ContentSafetyFailed)
                    {
                        await EmitEventAsync(run.Id, EventType.RunFailed,
                            new RunFailedPayload { Reason = $"Content safety check failed: {outcome.FailureMessage}" },
                            null, publisher, store, ct);
                        throw new ContentSafetyException(outcome.FailureMessage!);
                    }

                    resultContents.Add(new FunctionResultContent(callId, outcome.ResultText));
                }

                messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            }
        }
        catch (ContentSafetyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await EmitEventAsync(run.Id, EventType.RunFailed,
                new RunFailedPayload { Reason = "Run was cancelled" },
                null, publisher, store, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await EmitEventAsync(run.Id, EventType.RunFailed,
                new RunFailedPayload { Reason = $"Unexpected error: {ex.GetType().Name}" },
                null, publisher, store, CancellationToken.None);
            throw;
        }
    }

    private async Task<ToolOutcome> ExecuteReadAsync(
        SandboxedFileTools tools, string path, CancellationToken ct)
    {
        var (content, failure) = await tools.ReadFileAsync(path, ct);
        if (failure is not null)
        {
            var eventType = failure.Kind == SandboxFailureKind.Rejected
                ? EventType.ToolRejected
                : EventType.ToolError;
            return new ToolOutcome(eventType, $"Error: {failure.Message}", 0, failure.Message, false);
        }

        var bytes = Encoding.UTF8.GetByteCount(content!);
        return new ToolOutcome(EventType.ToolResult, content!, bytes, null, false);
    }

    private async Task<ToolOutcome> ExecuteWriteAsync(
        SandboxedFileTools tools, string path, string content, ModelSource modelSource, CancellationToken ct)
    {
        var (safe, reason) = _safetyChecker.Check(content, modelSource);
        if (!safe)
        {
            return new ToolOutcome(
                EventType.ToolError,
                $"Error: content safety check failed: {reason}",
                0,
                $"Content safety check failed: {reason}",
                true);
        }

        var (bytesWritten, failure) = await tools.WriteFileAsync(path, content, ct);
        if (failure is not null)
        {
            var eventType = failure.Kind == SandboxFailureKind.Rejected
                ? EventType.ToolRejected
                : EventType.ToolError;
            return new ToolOutcome(eventType, $"Error: {failure.Message}", 0, failure.Message, false);
        }

        return new ToolOutcome(
            EventType.ToolResult,
            $"Written {bytesWritten} bytes to {path}",
            bytesWritten,
            null,
            false);
    }

    private static string GetStringArgument(FunctionCallContent functionCall, string name)
    {
        if (functionCall.Arguments is not null
            && functionCall.Arguments.TryGetValue(name, out var value)
            && value is not null)
        {
            return value as string ?? value.ToString() ?? string.Empty;
        }

        return string.Empty;
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

    private Task EmitToolOutcomeAsync(
        RunId runId, ToolOutcome outcome, string path, string callId,
        IRunEventPublisher publisher, IRunEventStore store, CancellationToken ct)
    {
        object payload = outcome.EventType switch
        {
            EventType.ToolResult => new ToolResultPayload { Path = path, BytesReadOrWritten = outcome.Bytes },
            EventType.ToolRejected => new ToolRejectedPayload { Path = path, Reason = outcome.FailureMessage ?? string.Empty },
            EventType.ToolError => new ToolErrorPayload { Path = path, ErrorMessage = outcome.FailureMessage ?? string.Empty },
            _ => throw new ArgumentException($"Unknown tool outcome type: {outcome.EventType}", nameof(outcome)),
        };

        return EmitEventAsync(runId, outcome.EventType, payload, callId, publisher, store, ct);
    }

    private sealed record ToolOutcome(
        string EventType,
        string ResultText,
        long Bytes,
        string? FailureMessage,
        bool ContentSafetyFailed);
}

/// <summary>
/// Raised when content safety rejects model output or write-tool content. The
/// run is recorded as failed and the loop stops (Principle IX).
/// </summary>
public sealed class ContentSafetyException : Exception
{
    public ContentSafetyException(string reason)
        : base($"Content safety check failed: {reason}")
    {
    }
}
