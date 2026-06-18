using Microsoft.Extensions.Logging;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Minimal ILogger implementation that captures log entries into a list.
/// This is a test utility for asserting real audit output (SC-012); it is NOT a
/// mock of the system under test.
/// </summary>
public sealed class CapturingLogger : ILogger, ILogger<object>
{
    public List<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    public bool HasEntryContaining(string substring) =>
        Entries.Any(e => e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));

    public bool HasEntryMatching(LogLevel level, string substring) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
}

public sealed record LogEntry(LogLevel Level, string Message);
