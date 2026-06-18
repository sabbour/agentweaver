using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// A no-op <see cref="IWorkflowContext"/> used to invoke <c>RaiTurnExecutor</c>/<c>ScribeTurnExecutor</c>
/// directly from <see cref="CollectiveAssemblyPipeline"/> WITHOUT spinning up a one-node MAF workflow.
/// Both executors' <c>HandleAsync</c> implementations never touch the workflow context — they only use
/// their injected recording-writer/sub-stream seams — so calling <c>HandleAsync(input, Instance, ct)</c>
/// is a faithful reuse of the production executors. The state/message methods throw to fail loud if a
/// future executor change starts depending on the context (so we notice instead of silently misbehaving).
/// </summary>
internal sealed class NoOpWorkflowContext : IWorkflowContext
{
    public static readonly NoOpWorkflowContext Instance = new();

    private NoOpWorkflowContext() { }

    public IReadOnlyDictionary<string, string> TraceContext { get; } =
        new Dictionary<string, string>();

    public bool ConcurrentRunsEnabled => false;

    private static NotSupportedException Unsupported(string member) =>
        new($"{nameof(NoOpWorkflowContext)} does not support {member}; the collective-assembly " +
            "executors are invoked outside a MAF workflow and must not use the workflow context.");

    public ValueTask AddEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(AddEventAsync));

    public ValueTask SendMessageAsync(object message, string? targetId = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(SendMessageAsync));

    public ValueTask YieldOutputAsync(object output, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(YieldOutputAsync));

    public ValueTask RequestHaltAsync() => throw Unsupported(nameof(RequestHaltAsync));

    public ValueTask<T?> ReadStateAsync<T>(string key, string? scopeName = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ReadStateAsync));

    public ValueTask<T> ReadOrInitStateAsync<T>(string key, Func<T> initialStateFactory, string? scopeName = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ReadOrInitStateAsync));

    public ValueTask<HashSet<string>> ReadStateKeysAsync(string? scopeName = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ReadStateKeysAsync));

    public ValueTask QueueStateUpdateAsync<T>(string key, T? value, string? scopeName = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(QueueStateUpdateAsync));

    public ValueTask QueueClearScopeAsync(string? scopeName = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(QueueClearScopeAsync));
}
