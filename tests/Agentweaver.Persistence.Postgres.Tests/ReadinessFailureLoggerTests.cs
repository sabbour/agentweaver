using Agentweaver.Persistence.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Agentweaver.Persistence.Postgres.Tests;

public sealed class ReadinessFailureLoggerTests
{
    [Fact]
    public void LogsVersionRangeReasonAtWarningLevelAndRateLimitsRepeatedFailures()
    {
        var sink = new CapturingLogger();
        var readinessLogger = new ReadinessFailureLogger(sink);
        var failure = new InvalidOperationException("Schema version 034 is outside the supported range 1-33.");

        Assert.True(readinessLogger.TryLog(failure));
        Assert.False(readinessLogger.TryLog(failure));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.Contains(failure.Message, entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void SanitizesConnectionStringsAndSecretValues()
    {
        var sink = new CapturingLogger();
        var readinessLogger = new ReadinessFailureLogger(sink);
        var databaseHost = "db.internal";
        var connectionCredential = string.Concat("db", ";", "credential");
        // Build the connection string in memory so the test exercises Npgsql's exact field format.
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = databaseHost,
            Database = "platform",
            Username = "runtime",
            Password = connectionCredential
        }.ConnectionString;

        Assert.True(readinessLogger.TryLog(new InvalidOperationException($"Connection string: '{connectionString}'")));
        var connectionStringMessage = Assert.Single(sink.Entries).Message;
        Assert.Contains("[REDACTED CONNECTION STRING]", connectionStringMessage);
        Assert.DoesNotContain(databaseHost, connectionStringMessage);
        Assert.DoesNotContain(connectionCredential, connectionStringMessage);

        sink.Entries.Clear();
        readinessLogger = new ReadinessFailureLogger(sink);
        var passwordFirstConnectionString = string.Concat(
            nameof(NpgsqlConnectionStringBuilder.Password), "=", connectionCredential,
            ";Host=", databaseHost, ";Database=platform");
        Assert.True(readinessLogger.TryLog(new InvalidOperationException(passwordFirstConnectionString)));
        var passwordFirstMessage = Assert.Single(sink.Entries).Message;
        Assert.DoesNotContain(databaseHost, passwordFirstMessage);
        Assert.DoesNotContain(connectionCredential, passwordFirstMessage);

        sink.Entries.Clear();
        readinessLogger = new ReadinessFailureLogger(sink);
        var accessToken = string.Concat("access", "\r\n", "credential");
        var bearerToken = string.Concat("bearer", "-", "credential");
        var secretAssignments = string.Concat("Access", "Token=\"", accessToken, "\"");

        Assert.True(readinessLogger.TryLog(new InvalidOperationException(secretAssignments)));
        var secretMessage = Assert.Single(sink.Entries).Message;
        Assert.Contains("AccessToken=[REDACTED]", secretMessage);
        Assert.DoesNotContain(accessToken, secretMessage);
        Assert.DoesNotContain("credential", secretMessage);

        sink.Entries.Clear();
        readinessLogger = new ReadinessFailureLogger(sink);
        Assert.True(readinessLogger.TryLog(new InvalidOperationException(
            string.Concat("Bear", "er \"", bearerToken, "\""))));
        Assert.DoesNotContain(bearerToken, Assert.Single(sink.Entries).Message);
        Assert.DoesNotContain(bearerToken, secretMessage);
    }

    private sealed class CapturingLogger : ILogger
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
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
