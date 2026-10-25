using System.Text.RegularExpressions;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal static partial class BootstrapFailureDiagnostics
{
    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var postgres = FindPostgresException(exception);
        var detail = $"ExceptionType={exception.GetType().Name}";
        if (postgres is not null)
        {
            // Keep server-provided prose out of logs; retain only validated PostgreSQL metadata.
            if (!ReferenceEquals(postgres, exception))
                detail += $"; InnerExceptionType={postgres.GetType().Name}";

            return $"{detail}; SqlState={ValidatedValue(postgres.SqlState, SqlStateRegex())}" +
                $"; ConstraintName={ValidatedValue(postgres.ConstraintName, IdentifierRegex())}" +
                $"; TableName={ValidatedValue(postgres.TableName, IdentifierRegex())}";
        }

        if (exception is ArgumentException or InvalidOperationException)
            return $"{detail}; Category=invalid-bootstrap-configuration";

        return detail;
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        // Walk wrapped exceptions so EF database failures retain their PostgreSQL diagnostics.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        return null;
    }

    private static string ValidatedValue(string? value, Regex validator) =>
        !string.IsNullOrEmpty(value) && validator.IsMatch(value) ? value : "unknown";

    [GeneratedRegex(@"\A[0-9A-Z]{5}\z")]
    private static partial Regex SqlStateRegex();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.$-]{0,127}\z")]
    private static partial Regex IdentifierRegex();
}
