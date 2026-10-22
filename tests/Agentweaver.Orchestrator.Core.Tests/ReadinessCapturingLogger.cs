using Microsoft.Extensions.Logging;

namespace Agentweaver.Orchestrator.Core.Tests;

internal sealed class ReadinessCapturingLogger : ILogger
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new(logLevel, formatter(state, exception), exception));

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
