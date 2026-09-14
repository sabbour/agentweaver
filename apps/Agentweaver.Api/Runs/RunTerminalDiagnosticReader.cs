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
    private const int DiagnosticLookbackEventCount = 80;
    private static readonly Regex ServerGeneratedId = new(
        @"\A[a-f0-9]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<RunTerminalDiagnosticResponse?> GetAsync(string runId, CancellationToken ct)
    {
        var candidates = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && e.EventType == EventTypes.RunFailed)
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.PayloadJson, e.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (TryRead(candidate.PayloadJson, candidate.CreatedAt, out var diagnostic))
                return await EnrichFromPriorEventsAsync(runId, candidate.Sequence, diagnostic!, ct)
                    .ConfigureAwait(false);
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
            var causeChain = ReadCauseChain(payload);
            diagnostic = new RunTerminalDiagnosticResponse
            {
                Code = Bound(code, 96),
                Message = StructuredRunFailureTerminal.CreateDiagnosticMessage(code, retryable),
                Component = ComponentFor(code, causeChain),
                Timestamp = ReadTimestamp(payload, createdAt),
                Retryable = retryable,
                CorrelationIds = ReadCorrelationIds(payload),
                CauseChain = causeChain,
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

        return StructuredRunFailureTerminal.NormalizeCauseChain(causes.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty));
    }

    private static bool IsSafeServerGeneratedId(string value) =>
        !SensitiveDataRedactor.ContainsSensitiveValue(value)
        && ServerGeneratedId.IsMatch(value);

    private async Task<RunTerminalDiagnosticResponse> EnrichFromPriorEventsAsync(
        string runId,
        int terminalSequence,
        RunTerminalDiagnosticResponse diagnostic,
        CancellationToken ct)
    {
        var candidates = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId
                && e.Sequence <= terminalSequence
                && e.Sequence >= terminalSequence - DiagnosticLookbackEventCount
                && (e.EventType == EventTypes.RunFailed
                    || e.EventType == EventTypes.ToolCall
                    || e.EventType == EventTypes.ToolError
                    || e.EventType == EventTypes.WorkflowStep))
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.EventType, e.PayloadJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var causes = new List<string>(diagnostic.CauseChain);
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolFailureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var toolFailureOrder = new List<string>();
        string? lastActiveStepCause = null;
        var foundTerminalStepCause = false;
        string? terminalReasonCause = null;
        foreach (var candidate in candidates)
        {
            if (!TryParsePayload(candidate.PayloadJson, out var payload))
                continue;

            switch (candidate.EventType)
            {
                case EventTypes.ToolCall:
                    if (TryString(payload, "callId", out var callId)
                        && TryString(payload, "toolName", out var toolName)
                        && NormalizeIdentifier(toolName) is { } safeToolName)
                        toolNamesByCallId[callId] = safeToolName;
                    break;

                case EventTypes.ToolError:
                    var failedToolName = TryString(payload, "callId", out var failedCallId)
                        && toolNamesByCallId.TryGetValue(failedCallId, out var correlatedToolName)
                            ? correlatedToolName
                            : TryString(payload, "toolName", out var directToolName)
                                ? NormalizeIdentifier(directToolName)
                                : null;
                    var toolCause = failedToolName is null
                        ? "tool:unknown:failed"
                        : $"tool:{failedToolName}:failed";
                    if (!toolFailureCounts.ContainsKey(toolCause))
                        toolFailureOrder.Add(toolCause);
                    toolFailureCounts[toolCause] = toolFailureCounts.GetValueOrDefault(toolCause) + 1;
                    break;

                case EventTypes.WorkflowStep:
                    if (TryString(payload, "step", out var step)
                        && TryString(payload, "status", out var status)
                        && NormalizeIdentifier(step) is { } safeStep
                        && NormalizeIdentifier(status) is { } safeStatus)
                    {
                        var stepCause = $"step:{safeStep}:{safeStatus}";
                        if (IsFailureStepStatus(status))
                        {
                            foundTerminalStepCause = true;
                            AddCause(causes, stepCause);
                        }
                        else if (IsActiveStepStatus(status))
                        {
                            lastActiveStepCause = stepCause;
                        }
                    }
                    break;

                case EventTypes.RunFailed when candidate.Sequence == terminalSequence:
                    if (TryString(payload, "reason", out var reason)
                        && NormalizeIdentifier(reason) is { } safeReason)
                        terminalReasonCause = $"reason:{safeReason}";
                    break;
            }
        }

        if (!foundTerminalStepCause && lastActiveStepCause is not null)
            AddCause(causes, lastActiveStepCause);
        foreach (var toolCause in toolFailureOrder)
        {
            var count = toolFailureCounts[toolCause];
            AddCause(causes, count > 1 ? $"{toolCause}:{count}" : toolCause);
        }
        if (terminalReasonCause is not null)
            AddCause(causes, terminalReasonCause);

        var normalized = StructuredRunFailureTerminal.NormalizeCauseChain(causes);
        return diagnostic with
        {
            CauseChain = normalized,
            Component = ComponentFor(diagnostic.Code, normalized),
        };
    }

    private static bool TryParsePayload(string payloadJson, out JsonElement payload)
    {
        payload = default;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            payload = doc.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddCause(List<string> causes, string cause)
    {
        if (causes.Count >= MaxCauseCount
            || !StructuredRunFailureTerminal.IsSafeCauseEntry(cause)
            || causes.Contains(cause, StringComparer.Ordinal))
            return;

        causes.Add(cause);
    }

    private static bool IsFailureStepStatus(string status) =>
        status is "failed" or "blocked" or "revise" or "declined";

    private static bool IsActiveStepStatus(string status) =>
        status is "started" or "running" or "pending";

    private static string? NormalizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = Regex.Replace(value.Trim(), @"[^A-Za-z0-9_.:-]+", "-", RegexOptions.CultureInvariant)
            .Trim('-', '.', ':', '_');
        return normalized is { Length: > 0 and <= 96 }
            && !SensitiveDataRedactor.ContainsSensitiveValue(normalized)
                ? normalized
                : null;
    }

    private static string ComponentFor(string code, IReadOnlyList<string> causeChain)
    {
        if (causeChain.Any(cause => cause.StartsWith("tool:", StringComparison.Ordinal)))
            return "agent_tool";
        if (causeChain.Any(cause => cause.StartsWith("step:preview:", StringComparison.Ordinal)
            || cause.StartsWith("tool:start_preview:", StringComparison.Ordinal)))
            return "preview";

        return code switch
    {
        var c when c.StartsWith("a2a_", StringComparison.Ordinal) => "a2a",
        var c when c.StartsWith("agent_host_", StringComparison.Ordinal) ||
                   c.StartsWith("agent_turn_", StringComparison.Ordinal) => "agent_host",
        "github_copilot_capability_snapshot_unavailable" or
        "model_provider_snapshot_unavailable" => "provider_snapshot",
        var c when c.StartsWith("coordinator_", StringComparison.Ordinal) => "coordinator",
        var c when c.StartsWith("model_provider_", StringComparison.Ordinal) ||
                   c.StartsWith("github_copilot_", StringComparison.Ordinal) => "model_provider",
        var c when c.StartsWith("sandbox_", StringComparison.Ordinal) => "sandbox",
        var c when c.StartsWith("workflow_", StringComparison.Ordinal) => "workflow",
        _ => "run",
    };
    }

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
