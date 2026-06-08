using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

public sealed class FoundryAgentRunner : IAgentRunner
{
    private const string SystemPrompt =
        """
        You are a file-editing assistant. Complete the given task by using the read_file and write_file tools.
        Work step by step. When you are done, produce a final message summarising what you changed and why.
        Do not ask clarifying questions — proceed with your best judgement.
        """;

    private const int MaxTurns = 30;

    private readonly FoundryClientFactory _factory;
    private readonly ILogger<FoundryAgentRunner> _logger;

    public FoundryAgentRunner(FoundryClientFactory factory, ILogger<FoundryAgentRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
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
        using var governance = SandboxGovernance.Create(workingDirectory, runId, _logger);
        var agentId = $"did:mesh:scaffolder:foundry:{runId}";

        var chatClient = _factory.CreateChatClient();
        var fileTools = new SandboxedFileTools(workingDirectory);
        var tools = BuildTools(fileTools);

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

            ChatResponse response = await chatClient.GetResponseAsync(messages, options, ct);
            var assistantMessage = response.Messages.Last();
            messages.Add(assistantMessage);

            if (!string.IsNullOrWhiteSpace(assistantMessage.Text))
            {
                sb.Clear();
                sb.Append(assistantMessage.Text);
                Emit("agent.message", new { content = assistantMessage.Text });
            }

            var calls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();

            Emit("agent.turn.end", new { turnId = turn.ToString() });

            if (calls.Count == 0)
            {
                Emit("run.completed", new { summary = assistantMessage.Text ?? string.Empty });
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
        }

        if (!completedNormally)
            Emit("run.failed", new { errorMessage = "Step limit reached." });

        return sb.ToString();
    }

    private static List<AITool> BuildTools(SandboxedFileTools fileTools) =>
    [
        AIFunctionFactory.Create(
            async ([Description("File path relative to the working directory.")] string path) =>
            {
                var (content, failure) = await fileTools.ReadFileAsync(path);
                return failure is not null ? $"Error: {failure.Message}" : content!;
            },
            "read_file", "Read the contents of a file."),

        AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Content to write.")] string content) =>
            {
                var (_, failure) = await fileTools.WriteFileAsync(path, content);
                return failure is not null ? $"Error: {failure.Message}" : "ok";
            },
            "write_file", "Write content to a file, creating it if it does not exist."),
    ];
}
