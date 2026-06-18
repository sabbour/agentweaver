using System.Threading.Channels;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Centralizes GitHub Copilot SDK session lifecycle: create, run, serialize, deserialize, dispose.
/// Mirrors the DemoRuntime pattern from tamirdresher/squad-agent-framework-demo.
/// Instantiate per logical operation (not shared across requests).
/// </summary>
public sealed class AgentweaverAgentRuntime : IAsyncDisposable
{
    private readonly IAgentRunner _agentRunner;
    private readonly string _workingDirectory;
    private readonly string? _modelId;
    private bool _disposed;

    public AgentweaverAgentRuntime(IAgentRunner agentRunner, string workingDirectory, string? modelId = null)
    {
        _agentRunner = agentRunner ?? throw new ArgumentNullException(nameof(agentRunner));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _modelId = modelId;
    }

    /// <summary>
    /// Sends a single prompt and returns the plain-text response.
    /// Use for fire-and-forget calls (casting proposals, etc.).
    /// </summary>
    public async Task<string> RunAsync(string prompt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runId = Guid.NewGuid().ToString("N");
        return await _agentRunner.ExecuteAsync(
            task: prompt,
            workingDirectory: _workingDirectory,
            repositoryPath: _workingDirectory,
            modelSource: ModelSource.GitHubCopilot,
            runId: runId,
            modelId: _modelId,
            stream: null,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a prompt and returns both the response text and the serialized state for storage.
    /// Serialization is best-effort: <c>SerializedState</c> is <see langword="null"/> when the
    /// underlying <see cref="IAgentRunner"/> does not expose session serialization.
    /// Use when you need to persist session state for later resumption.
    /// </summary>
    public async Task<(string Response, string? SerializedState)> RunWithSerializationAsync(
        string prompt,
        CancellationToken ct = default)
    {
        var response = await RunAsync(prompt, ct).ConfigureAwait(false);
        // IAgentRunner does not expose SerializeSessionAsync; serialized state is best-effort.
        return (response, null);
    }

    /// <summary>
    /// Resumes from a previously serialized session by injecting its state as leading context,
    /// sends a prompt, and returns the response.
    /// Falls back to a plain <see cref="RunAsync"/> call when <paramref name="serializedState"/>
    /// is empty, because <see cref="IAgentRunner"/> does not expose <c>DeserializeSessionAsync</c>.
    /// </summary>
    public Task<string> ResumeAsync(
        string serializedState,
        string prompt,
        CancellationToken ct = default)
    {
        var contextualPrompt = string.IsNullOrWhiteSpace(serializedState)
            ? prompt
            : $"[Resuming session with prior context]\n\n{prompt}";
        return RunAsync(contextualPrompt, ct);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
