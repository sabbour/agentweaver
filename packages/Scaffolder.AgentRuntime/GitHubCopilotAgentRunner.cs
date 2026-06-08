using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI.GitHub.Copilot;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Runs a single agent turn via the Microsoft Agent Framework with the GitHub
/// Copilot SDK backend.
/// </summary>
public sealed class GitHubCopilotAgentRunner : IAgentRunner
{
    private readonly GitHubCopilotClientFactory _factory;

    public GitHubCopilotAgentRunner(GitHubCopilotClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<string> ExecuteAsync(string task, string workingDirectory, ChannelWriter<RunEvent>? stream, CancellationToken ct)
    {
        await using var client = _factory.CreateClient();
        await client.StartAsync(ct);

        var seq = new[] { 0 };

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            WorkingDirectory = workingDirectory,
            OnEvent = sdkEvent =>
            {
                if (stream is null) return;
                var n = Interlocked.Increment(ref seq[0]);
                RunEvent? runEvent = sdkEvent switch
                {
                    AssistantMessageDeltaEvent e => new RunEvent(n, "agent.message.delta",
                        new { messageId = e.Data.MessageId, delta = e.Data.DeltaContent }),

                    AssistantMessageEvent e => new RunEvent(n, "agent.message",
                        new { messageId = e.Data.MessageId, content = e.Data.Content }),

                    AssistantTurnStartEvent e => new RunEvent(n, "agent.turn.start",
                        new { turnId = e.Data.TurnId }),

                    AssistantTurnEndEvent e => new RunEvent(n, "agent.turn.end",
                        new { turnId = e.Data.TurnId }),

                    ToolExecutionStartEvent e => new RunEvent(n, "tool.call",
                        new { callId = e.Data.ToolCallId, toolName = e.Data.ToolName, arguments = e.Data.Arguments }),

                    ToolExecutionCompleteEvent e when e.Data.Success => new RunEvent(n, "tool.result",
                        new { callId = e.Data.ToolCallId, content = e.Data.Result?.Content }),

                    ToolExecutionCompleteEvent e => new RunEvent(n, "tool.error",
                        new { callId = e.Data.ToolCallId, errorCode = e.Data.Error?.Code, errorMessage = e.Data.Error?.Message }),

                    SessionTaskCompleteEvent e => new RunEvent(n,
                        e.Data.Success == true ? "run.completed" : "run.failed",
                        new { summary = e.Data.Summary }),

                    SessionErrorEvent e => new RunEvent(n, "run.failed",
                        new { errorType = e.Data.ErrorType, message = e.Data.Message }),

                    _ => null,
                };
                if (runEvent is not null) stream.TryWrite(runEvent);
            },
        };

        var agent = client.AsAIAgent(sessionConfig, ownsClient: false,
            id: null, name: null, description: null);

        var session = await agent.CreateSessionAsync(ct);
        var result = await agent.RunAsync(task, session, options: null, ct);
        return result?.ToString() ?? string.Empty;
    }
}
