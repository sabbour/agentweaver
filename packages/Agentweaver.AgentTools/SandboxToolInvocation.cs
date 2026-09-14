using System.Threading;

namespace Agentweaver.AgentTools;

/// <summary>
/// Async-local correlation for a sandbox tool invocation.
/// </summary>
public static class SandboxToolInvocation
{
    private static readonly AsyncLocal<string?> CurrentToolCallIdSlot = new();

    public static string? CurrentToolCallId => CurrentToolCallIdSlot.Value;

    public static IDisposable PushToolCallId(string toolCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        var prior = CurrentToolCallIdSlot.Value;
        CurrentToolCallIdSlot.Value = toolCallId;
        return new RestoreScope(prior);
    }

    private sealed class RestoreScope(string? prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentToolCallIdSlot.Value = prior;
        }
    }
}
