using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Persistence.Postgres;

/// <summary>Logs sanitized readiness failures at warning level with a process-local rate limit.</summary>
public sealed class ReadinessFailureLogger
{
    private static readonly TimeSpan MinimumLogInterval = TimeSpan.FromMinutes(1);
    private static readonly Regex ConnectionStringPattern = new(
        @"(?i)\b(?:connection[\s_-]*string\s*(?:[:=]\s*['""]?|['""]\s*)|(?:Host|Server|Data\s+Source|Database|Initial\s+Catalog|Username|User\s+ID|Password|Pwd|Port|Application\s+Name|SSL\s+Mode|Search\s+Path|Timeout|Command\s+Timeout)\s*=\s*)[\s\S]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)\b(password|pwd|passphrase|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|token|api[_-]?key|access[_-]?key|shared[_-]?access[_-]?key|credential)\s*[:=]\s*[\s\S]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PostgresUriPattern = new(
        @"(?i)\b(?:postgres(?:ql)?|jdbc:postgresql)://[\s\S]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerTokenPattern = new(
        @"(?i)\bBearer\s+[\s\S]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger _logger;
    private long _lastLoggedTimestamp = long.MinValue;

    public ReadinessFailureLogger(ILogger logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Writes a failure warning unless one was logged during the previous minute.</summary>
    public bool TryLog(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var now = Stopwatch.GetTimestamp();

        // Reserve the interval before writing so simultaneous probes cannot all log the same failure.
        while (true)
        {
            var previous = Volatile.Read(ref _lastLoggedTimestamp);
            if (previous != long.MinValue && Stopwatch.GetElapsedTime(previous, now) < MinimumLogInterval)
                return false;

            if (Interlocked.CompareExchange(ref _lastLoggedTimestamp, now, previous) == previous)
                break;
        }

        _logger.LogWarning(
            "Readiness verification failed with {ExceptionType}: {FailureReason}",
            exception.GetType().Name,
            SanitizeMessage(exception.Message));
        return true;
    }

    /// <summary>Removes connection strings and common secret values from exception messages.</summary>
    public static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Drop everything after a connection-string marker: quoted values may contain separators or line breaks.
        message = ConnectionStringPattern.Replace(message, "[REDACTED CONNECTION STRING]");
        // URI credentials and standalone secret fields can appear without a connection-string label.
        message = PostgresUriPattern.Replace(message, "[REDACTED CONNECTION STRING]");
        // Secret values may also be quoted or contain separators, so remove the rest of the message.
        message = SecretAssignmentPattern.Replace(message, "$1=[REDACTED]");
        return BearerTokenPattern.Replace(message, "[REDACTED BEARER TOKEN]");
    }
}
