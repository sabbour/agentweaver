namespace Agentweaver.Projects.Config;

internal sealed class PlatformPolicyReadLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private static readonly AsyncLocal<bool> ProtectedRead = new();

    internal static IDisposable BeginProtectedRead()
    {
        var previous = ProtectedRead.Value;
        ProtectedRead.Value = true;
        return new ReadScope(previous);
    }

    public ILogger CreateLogger(string categoryName) => new SafeLogger(inner.CreateLogger(categoryName));
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() { }

    private sealed class ReadScope(bool previous) : IDisposable
    {
        public void Dispose() => ProtectedRead.Value = previous;
    }

    private sealed class SafeLogger(ILogger innerLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            ProtectedRead.Value
                ? innerLogger.BeginScope("Platform Policy read database scope.")
                : innerLogger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => innerLogger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ProtectedRead.Value)
                innerLogger.Log(logLevel, eventId, "Platform Policy read database event.", null,
                    static (message, _) => message);
            else
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
