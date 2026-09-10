using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Runs;

/// <summary>Reads the single safe terminal-failure projection from the durable append-only event log.</summary>
public sealed class RunTerminalDiagnosticReader(MemoryDbContext db)
{
    private const int MaxCauseCount = 4;
    private static readonly Regex ServerGeneratedId = new(
        @"\A[a-f0-9]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SafeCauseTypes = new(StringComparer.Ordinal)
    {
        "HttpRequestException",
        "IOException",
        "OperationCanceledException",
        "SocketException",
        "TaskCanceledException",
        "TimeoutException",
    };

    public async Task<RunTerminalDiagnosticResponse?> GetAsync(string runId, CancellationToken ct)
    {
        var candidates = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && e.EventType == EventTypes.RunFailed)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.PayloadJson, e.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (TryRead(candidate.PayloadJson, candidate.CreatedAt, out var diagnostic))
                return diagnostic;
        }

        var assemblyState = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId
                && (e.EventType == EventTypes.CoordinatorAssemblyBlocked
                    || e.EventType == EventTypes.CoordinatorAssemblyFailed))
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.EventType, e.CreatedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (assemblyState is not null)
        {
            var code = assemblyState.EventType == EventTypes.CoordinatorAssemblyBlocked
                ? "assembly_blocked"
                : "assembly_failed";
            return new RunTerminalDiagnosticResponse
            {
                Code = code,
                Message = StructuredRunFailureTerminal.CreateDiagnosticMessage(code, retryable: null),
                Component = "coordinator",
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(assemblyState.CreatedAt, DateTimeKind.Utc)),
                Retryable = null,
                CorrelationIds = new Dictionary<string, string>(StringComparer.Ordinal),
                CauseChain = [],
            };
        }

        return null;
    }

    public static RunTerminalDiagnosticResponse CreateFallback(Run run) =>
        new()
        {
            Code = "agent_turn_internal_error",
            Message = "Run failed before a structured terminal diagnostic was recorded. Retry availability is unknown.",
            Component = "coordinator",
            Timestamp = run.EndedAt ?? run.StartedAt,
            Retryable = null,
            CorrelationIds = new Dictionary<string, string>(StringComparer.Ordinal),
            CauseChain = [],
        };

    internal static bool TryRead(
        string payloadJson,
        DateTime createdAt,
        out RunTerminalDiagnosticResponse? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var payload = doc.RootElement;
            if (payload.ValueKind != JsonValueKind.Object
                || !TryString(payload, "errorCode", out var code)
                || string.IsNullOrWhiteSpace(code))
                return false;

            code = StructuredRunFailureTerminal.NormalizeErrorCode(code);
            var retryable = TryBoolean(payload, "retryable");
            diagnostic = new RunTerminalDiagnosticResponse
            {
                Code = Bound(code, 96),
                Message = StructuredRunFailureTerminal.CreateDiagnosticMessage(code, retryable),
                Component = ComponentFor(code),
                Timestamp = ReadTimestamp(payload, createdAt),
                Retryable = retryable,
                CorrelationIds = ReadCorrelationIds(payload),
                CauseChain = ReadCauseChain(payload),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonElement payload, DateTime createdAt) =>
        TryString(payload, "timestampUtc", out var value) && DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));

    private static IReadOnlyDictionary<string, string> ReadCorrelationIds(JsonElement payload)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "correlationId", "requestId", "traceId" })
        {
            if (TryString(payload, name, out var value) && IsSafeServerGeneratedId(value))
                result[ToSnakeCase(name)] = Bound(value, 128);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadCauseChain(JsonElement payload)
    {
        if (!payload.TryGetProperty("causeChain", out var causes)
            || causes.ValueKind != JsonValueKind.Array)
            return [];

        return causes.EnumerateArray()
            .Take(MaxCauseCount)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(IsSafeCause)
            .ToArray();
    }

    private static bool IsSafeCause(string value) =>
        !SensitiveDataRedactor.ContainsSensitiveValue(value)
        && SafeCauseTypes.Contains(value);

    private static bool IsSafeServerGeneratedId(string value) =>
        !SensitiveDataRedactor.ContainsSensitiveValue(value)
        && ServerGeneratedId.IsMatch(value);

    private static string ComponentFor(string code) => code switch
    {
        var c when c.StartsWith("a2a_", StringComparison.Ordinal) => "a2a",
        var c when c.StartsWith("agent_host_", StringComparison.Ordinal) ||
                   c.StartsWith("agent_turn_", StringComparison.Ordinal) => "agent_host",
        var c when c.StartsWith("sandbox_", StringComparison.Ordinal) => "sandbox",
        var c when c.StartsWith("workflow_", StringComparison.Ordinal) => "workflow",
        _ => "run",
    };

    private static bool? TryBoolean(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static bool TryString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        return payload.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty) is not null;
    }

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string ToSnakeCase(string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
