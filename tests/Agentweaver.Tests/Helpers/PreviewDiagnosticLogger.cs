using Microsoft.Extensions.Logging;

namespace Agentweaver.Tests.Helpers;

internal sealed class PreviewDiagnosticLogger<T>(Exception injectedFailure) : ILogger<T>
{
    private int _reports;
    public int Reports => Volatile.Read(ref _reports);
    public TaskCompletionSource<(LogLevel Level, string Message, Exception Exception)> Reported { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!ReferenceEquals(exception, injectedFailure))
            return;
        Interlocked.Increment(ref _reports);
        Reported.TrySetResult((logLevel, formatter(state, exception), exception!));
    }
}
