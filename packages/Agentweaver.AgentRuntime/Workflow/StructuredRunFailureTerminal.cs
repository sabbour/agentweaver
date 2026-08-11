using System.Text.Json;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

internal sealed record StructuredRunFailure(string ErrorCode, string Message, bool? IsRetryable);

/// <summary>Creates and reads the structured terminal contract shared by the pod bridge and worker proxy.</summary>
internal static class StructuredRunFailureTerminal
{
    internal const string InternalErrorCode = "agent_turn_internal_error";
    private const int MaxDiagnosticLength = 2048;

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
            string? message = null;
            bool? retryable = null;
            foreach (var property in payload.EnumerateObject())
            {
                if (property.Name.Equals("errorCode", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    errorCode = property.Value.GetString();
                }
                else if (property.Name.Equals("message", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind == JsonValueKind.String)
                {
                    message = property.Value.GetString();
                }
                else if (property.Name.Equals("retryable", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    retryable = property.Value.GetBoolean();
                }
            }

            return string.IsNullOrWhiteSpace(errorCode)
                ? null
                : new StructuredRunFailure(
                    errorCode,
                    string.IsNullOrWhiteSpace(message) ? errorCode : message,
                    retryable);
        }
        catch
        {
            return null;
        }
    }

    internal static RunEvent NormalizeUnstructuredFailure(RunEvent runEvent) =>
        CreateInternalError(
            "Agent turn failed without a structured error code.",
            DescribeUnstructuredPayload(runEvent.Payload),
            runEvent.Sequence,
            runEvent.TimestampUtc);

    internal static RunEvent CreateInternalError(
        string message,
        string? diagnostic,
        int sequence = 0,
        DateTimeOffset timestampUtc = default) =>
        new(
            sequence,
            EventTypes.RunFailed,
            new
            {
                message,
                errorCode = InternalErrorCode,
                retryable = true,
                diagnostic = SanitizeDiagnostic(diagnostic),
            },
            timestampUtc);

    internal static string SanitizeDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
            return "No additional diagnostic detail was available.";

        var sanitized = DiagnosticRedactor.Redact(diagnostic)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

        return sanitized.Length <= MaxDiagnosticLength
            ? sanitized
            : sanitized[..MaxDiagnosticLength] + "…";
    }

    private static string DescribeUnstructuredPayload(object payload)
    {
        try
        {
            var element = payload is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(payload);
            if (element.ValueKind != JsonValueKind.Object)
                return $"Unstructured run.failed payload kind: {element.ValueKind}.";

            var details = new List<string>();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;

                if (property.Name.Equals("message", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("detail", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"{property.Name}={property.Value.GetString()}");
                }
            }

            return details.Count == 0
                ? "The pod emitted run.failed without an errorCode or recognized diagnostic fields."
                : string.Join("; ", details);
        }
        catch
        {
            return "The pod emitted an unreadable run.failed payload without an errorCode.";
        }
    }
}
