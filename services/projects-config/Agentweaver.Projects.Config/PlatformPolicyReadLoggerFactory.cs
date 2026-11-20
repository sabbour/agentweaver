namespace Agentweaver.Projects.Config;

internal sealed class PlatformPolicyReadLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private static readonly AsyncLocal<string?> ProtectedRead = new();

    internal static IDisposable BeginProtectedRead() => BeginProtectedRead("Platform Policy");

    internal static IDisposable BeginTargetContextRead() => BeginProtectedRead("Target-context");

    private static IDisposable BeginProtectedRead(string name)
    {
        var previous = ProtectedRead.Value;
        ProtectedRead.Value = name;
        return new ReadScope(previous);
    }

    public ILogger CreateLogger(string categoryName) => new SafeLogger(inner.CreateLogger(categoryName));
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { }

    private sealed class ReadScope(string? previous) : IDisposable
    {
        public void Dispose() => ProtectedRead.Value = previous;
    }

    private sealed class SafeLogger(ILogger innerLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            ProtectedRead.Value is { } name
                ? innerLogger.BeginScope($"{name} read database scope.")
                : innerLogger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => innerLogger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ProtectedRead.Value is { } name)
                innerLogger.Log(logLevel, eventId, $"{name} read database event.", null,
                    static (message, _) => message);
            else
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
