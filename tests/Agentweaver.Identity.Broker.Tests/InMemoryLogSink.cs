using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>
/// Captures every formatted log message (and exception text) written by the broker host during
/// a test, so a test can assert that no raw token/secret value was ever written to logs. Thread
/// safe: ASP.NET Core logs from multiple concurrent request-handling threads.
/// </summary>
public sealed class InMemoryLogSink
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public void Add(string message) => _messages.Enqueue(message);
}

public sealed class InMemoryLoggerProvider(InMemoryLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class InMemoryLogger(string categoryName, InMemoryLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            sink.Add($"[{categoryName}] {message}{(exception is null ? string.Empty : " " + exception)}");
        }
    }
}
