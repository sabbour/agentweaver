using System.Threading.Channels;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Helpers;

/// <summary>
/// Functional test IAgentRunner that performs REAL file/git operations in the
/// working directory. Behavior is configurable per test via the Mode property:
///   MakesChange  — writes/modifies a file, producing a real git diff
///   NoChange     — returns without touching any files
///   ContentSafety — throws an exception matching the production content-safety
///                   detection pattern in AgentTurnExecutor.IsContentSafetyViolation
/// </summary>
public sealed class TestFileEditAgentRunner : IAgentRunner
{
    public enum AgentMode
    {
        MakesChange,
        NoChange,
        ContentSafety
    }

    /// <summary>
    /// Controls what the next ExecuteAsync call does. Set before starting the run.
    /// Thread-safe: volatile read/write is sufficient for single-producer tests.
    /// </summary>
    public volatile AgentMode Mode = AgentMode.MakesChange;

    /// <summary>
    /// Optional file name to write. Defaults to "agent-output.txt".
    /// </summary>
    public string FileName { get; set; } = "agent-output.txt";

    /// <summary>
    /// Optional file content. Defaults to a deterministic string.
    /// </summary>
    public string FileContent { get; set; } = "deterministic agent output for testing";

    private long _invocationCount;

    public string? LastTask { get; private set; }
    public long InvocationCount => Interlocked.Read(ref _invocationCount);

    public Task<string> ExecuteAsync(
        string task,
        string workingDirectory,
        string repositoryPath,
        ModelSource modelSource,
        string runId,
        string? modelId,
        ChannelWriter<RunEvent>? stream,
        CancellationToken ct)
    {
        _ = repositoryPath;
        LastTask = task;
        Interlocked.Increment(ref _invocationCount);
        return Mode switch
        {
            AgentMode.MakesChange => ExecuteWithChangeAsync(workingDirectory, stream),
            AgentMode.NoChange => Task.FromResult("No changes needed."),
            AgentMode.ContentSafety => throw new InvalidOperationException(
                "content safety violation: test-triggered policy block"),
            _ => throw new InvalidOperationException($"Unknown test agent mode: {Mode}")
        };
    }

    private Task<string> ExecuteWithChangeAsync(string workingDirectory, ChannelWriter<RunEvent>? stream)
    {
        var filePath = Path.Combine(workingDirectory, FileName);
        File.WriteAllText(filePath, FileContent);

        // Emit a couple of representative RunEvents to the stream (tool_call + token).
        if (stream is not null)
        {
            stream.TryWrite(new RunEvent(1, EventTypes.ToolCall, new { tool = "file_write", path = FileName }));
            stream.TryWrite(new RunEvent(2, EventTypes.ToolResult, new { result = "wrote file" }));
        }

        return Task.FromResult("File written successfully.");
    }
}
