using System.Text;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;

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
    private readonly GitHubCopilotClientFactory _factory;
    private readonly ILogger<GitHubCopilotAgentRunner> _logger;

    public GitHubCopilotAgentRunner(GitHubCopilotClientFactory factory, ILogger<GitHubCopilotAgentRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExecuteAsync(string task, string workingDirectory, ModelSource modelSource, ChannelWriter<RunEvent>? stream, CancellationToken ct)
    {
        _logger.LogInformation("ExecuteAsync entered — workingDirectory={WorkingDirectory}, taskLength={TaskLength}, streamIsNull={StreamIsNull}",
            workingDirectory, task.Length, stream is null);
        _logger.LogDebug("Task content preview: {TaskPreview}", task.Length > 100 ? task[..100] : task);

        await using var client = _factory.CreateClient();
        await client.StartAsync(ct);

        _logger.LogInformation("Copilot client started");

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            WorkingDirectory = workingDirectory,
            Streaming = true,
        };

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);
        var session = await agent.CreateSessionAsync(ct);

        _logger.LogInformation("MAF agent session created");

        var sb = new StringBuilder();
        var seq = 0;
        var deltaCount = 0;
        var streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var anyDeltaEmittedForNullId = false;

        void EmitDelta(string text, string? messageId)
        {
            sb.Append(text);
            if (stream is null) return;

            var n = ++seq;
            var written = stream.TryWrite(new RunEvent(n, "agent.message.delta", new { delta = text, messageId }));
            deltaCount++;
            if (messageId is null) anyDeltaEmittedForNullId = true;
            if (!written)
                _logger.LogWarning("TryWrite false for agent.message.delta");
        }

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
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunStreamingAsync threw for workingDirectory={WorkingDirectory}", workingDirectory);
            stream?.TryWrite(new RunEvent(++seq, "run.failed", new { message = "The agent encountered an internal error." }));
            throw;
        }

        stream?.TryWrite(new RunEvent(++seq, "run.completed", new { }));

        var result = sb.ToString();
        _logger.LogInformation(
            "Run complete — deltaCount={DeltaCount}, resultLength={ResultLength}",
            deltaCount, result.Length);

        return result;
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
