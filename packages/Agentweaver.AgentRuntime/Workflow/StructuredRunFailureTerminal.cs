using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

internal sealed record StructuredRunFailure(string ErrorCode, string Message, bool? IsRetryable);

/// <summary>Creates and reads the structured terminal contract shared by the pod bridge and worker proxy.</summary>
public static class StructuredRunFailureTerminal
{
    internal const string InternalErrorCode = "agent_turn_internal_error";
    private const int MaxMessageLength = 512;
    private const int MaxDiagnosticLength = 2048;
    private static readonly HashSet<string> TerminalErrorCodes = new(StringComparer.Ordinal)
    {
        InternalErrorCode,
        "a2a_transport_failure",
        "agent_host_turn_incomplete",
        "coordinator_execution_failed",
        "coordinator_direct_execution_failed",
        "github_copilot_auth_required",
        "github_copilot_capability_snapshot_unavailable",
        "model_provider_snapshot_unavailable",
        "model_provider_connection_required",
        "shell_execution_timeout",
    };
    private static readonly SandboxOutputRedactor DiagnosticRedactor =
        SandboxOutputRedactor.CreateDefault(redactPii: false);

    internal static StructuredRunFailure? TryRead(RunEvent runEvent)
    {
        if (!string.Equals(runEvent.Type, EventTypes.RunFailed, StringComparison.Ordinal))
            return null;

        try
        {
            var payload = runEvent.Payload is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(runEvent.Payload);
            if (payload.ValueKind != JsonValueKind.Object)
                return null;

            string? errorCode = null;
            bool? retryable = null;
            foreach (var property in payload.EnumerateObject())
            {
                if (property.Name.Equals("errorCode", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    errorCode = property.Value.GetString();
                }
                else if (property.Name.Equals("retryable", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    retryable = property.Value.GetBoolean();
                }
            }

            if (string.IsNullOrWhiteSpace(errorCode))
                return null;

            return new StructuredRunFailure(
                NormalizeErrorCode(errorCode),
                CreateDiagnosticMessage(errorCode, retryable),
                retryable);
        }
        catch
        {
            return null;
        }
    }

    internal static RunEvent NormalizeUnstructuredFailure(RunEvent runEvent)
    {
        var normalized = NormalizeFailure(runEvent);
        if (TryRead(runEvent) is not null)
            return normalized;

        return new RunEvent(
            normalized.Sequence,
            EventTypes.RunFailed,
            new
            {
                message = CreateDiagnosticMessage(InternalErrorCode, retryable: true),
                errorCode = InternalErrorCode,
                retryable = true,
            },
            normalized.TimestampUtc);
    }

    /// <summary>
    /// Creates the only failure payload that may cross the AgentHost/A2A boundary.
    /// Input payloads are untrusted and must never be forwarded or persisted verbatim.
    /// </summary>
    public static RunEvent NormalizeFailure(RunEvent runEvent)
    {
        if (!string.Equals(runEvent.Type, EventTypes.RunFailed, StringComparison.Ordinal))
            return runEvent;

        string? errorCode = null;
        bool? retryable = null;
        try
        {
            var payload = runEvent.Payload is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(runEvent.Payload);
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.EnumerateObject())
                {
                    if (property.Name.Equals("errorCode", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                        errorCode = property.Value.GetString();
                    else if (property.Name.Equals("retryable", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        retryable = property.Value.GetBoolean();
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable inbound payload receives the same generic terminal contract.
        }

        var normalizedCode = NormalizeErrorCode(errorCode);

        return new RunEvent(
            runEvent.Sequence,
            EventTypes.RunFailed,
            new
            {
                message = CreateDiagnosticMessage(errorCode, retryable),
                errorCode = normalizedCode,
                retryable,
            },
            runEvent.TimestampUtc);
    }

    internal static RunEvent CreateInternalError(
        string message,
        string? diagnostic,
        int sequence = 0,
        DateTimeOffset timestampUtc = default) =>
        CreateFailure(
            InternalErrorCode,
            message,
            diagnostic,
            retryable: true,
            sequence,
            timestampUtc);

    internal static RunEvent CreateInternalError(
        string message,
        string? diagnostic,
        Exception exception,
        string? correlationId = null,
        int sequence = 0,
        DateTimeOffset timestampUtc = default) =>
        new(
            sequence,
            EventTypes.RunFailed,
            CreatePayload(
                InternalErrorCode,
                message,
                diagnostic,
                retryable: true,
                correlationId ?? Guid.NewGuid().ToString("n"),
                Activity.Current?.TraceId.ToHexString(),
                BuildCauseChain(exception)),
            timestampUtc);

    internal static RunEvent CreateFailure(
        string errorCode,
        string message,
        string? diagnostic,
        bool? retryable,
        int sequence = 0,
        DateTimeOffset timestampUtc = default) =>
        new(
            sequence,
            EventTypes.RunFailed,
            CreatePayload(errorCode, message, diagnostic, retryable),
            timestampUtc);

    public static string NormalizeErrorCode(string? errorCode) =>
        errorCode is not null && TerminalErrorCodes.Contains(errorCode)
            ? errorCode
            : InternalErrorCode;

    /// <summary>
    /// Produces the bounded failure summary permitted for remote A2A events. It deliberately
    /// accepts only classified protocol fields, never remote exception, prompt, or tool text.
    /// </summary>
    public static string CreateDiagnosticMessage(string? errorCode, bool? retryable)
    {
        var code = NormalizeErrorCode(errorCode);
        var retrySummary = retryable switch
        {
            true => " Retry is available.",
            false => " Retry is not available.",
            _ => " Retry availability is unknown.",
        };
        return $"Run failed with code '{code}'." + retrySummary;
    }

    private static object CreatePayload(string? errorCode, string? message, string? diagnostic, bool? retryable)
        => CreatePayload(errorCode, message, diagnostic, retryable, null, null, null);

    private static object CreatePayload(
        string? errorCode,
        string? message,
        string? diagnostic,
        bool? retryable,
        string? correlationId,
        string? traceId,
        IReadOnlyList<string>? causeChain)
    {
        var normalizedCode = NormalizeErrorCode(errorCode);
        var normalizedMessage = NormalizeTrustedMessage(message, normalizedCode);
        var payload = new Dictionary<string, object?>
        {
            ["message"] = normalizedMessage,
            ["errorCode"] = normalizedCode,
            ["retryable"] = retryable,
        };
        if (diagnostic is not null)
            payload["diagnostic"] = SanitizeDiagnostic(diagnostic);
        if (IsServerGeneratedId(correlationId))
            payload["correlationId"] = correlationId;
        if (IsServerGeneratedId(traceId))
            payload["traceId"] = traceId;
        if (causeChain is { Count: > 0 })
            payload["causeChain"] = causeChain;
        return payload;
    }

    private static IReadOnlyList<string> BuildCauseChain(Exception exception)
    {
        var causes = new List<string>(4);
        for (var current = exception; current is not null && causes.Count < 4; current = current.InnerException)
            causes.Add(current.GetType().Name);
        return causes;
    }

    private static bool IsServerGeneratedId(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static string NormalizeTrustedMessage(string? message, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(message)
            || message.Length > MaxMessageLength
            || SensitiveDataRedactor.ContainsSensitiveValue(message))
            return $"Run failed with code '{errorCode}'.";

        return message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    internal static string SanitizeDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
            return "No additional diagnostic detail was available.";

        if (SensitiveDataRedactor.ContainsAzureStorageCredential(diagnostic))
            return "Sensitive Azure Storage diagnostic detail was redacted.";

        var sanitized = DiagnosticRedactor.Redact(diagnostic)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

        return sanitized.Length <= MaxDiagnosticLength
            ? sanitized
            : sanitized[..MaxDiagnosticLength] + "…";
    }

}
