using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

/// <summary>Safe terminal diagnostic returned by <c>run_failure_diagnostic</c>.</summary>
public sealed record RunFailureDiagnosticResult
{
    [System.Text.Json.Serialization.JsonPropertyName("code")] public required string Code { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("message")] public required string Message { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("component")] public required string Component { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("retryable")] public bool? Retryable { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("correlation_ids")] public required Dictionary<string, string> CorrelationIds { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("cause_chain")] public required List<string> CauseChain { get; init; }
}

[McpServerToolType]
public sealed class DiagnosticsTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Regex ServerGeneratedId = new(
        @"\A[a-f0-9]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SafeCauseTypes = new(StringComparer.Ordinal)
    {
        "HttpRequestException", "IOException", "OperationCanceledException", "SocketException",
        "TaskCanceledException", "TimeoutException",
    };

    [McpServerTool(Name = "diagnostics_get"), Description("Get a real-time system diagnostics snapshot: API version, process uptime, project/run counts, heartbeat state, and checkpoint GC state.")]
    public async Task<string> DiagnosticsGetAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/diagnostics", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "heartbeat_status"), Description("Get the current coordinator heartbeat service status: enabled flag, interval, last tick time, and service state (running / waiting_first_tick / disabled).")]
    public async Task<string> HeartbeatStatusAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/diagnostics/heartbeat", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_failure_diagnostic", UseStructuredContent = true),
     Description("Get the bounded, redacted terminal diagnostic for a failed run. This never returns raw logs, stacks, prompts, headers, credentials, or tool payloads.")]
    public async Task<RunFailureDiagnosticResult> RunFailureDiagnosticAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetAsync<RunFailureDiagnosticResult>(
                $"/api/runs/{Uri.EscapeDataString(run_id)}/terminal-diagnostic", ct);
            return result with
            {
                Message = CreateSafeMessage(result.Code, result.Retryable),
                CorrelationIds = result.CorrelationIds
                    .Where(pair => ServerGeneratedId.IsMatch(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                CauseChain = result.CauseChain.Where(SafeCauseTypes.Contains).ToList(),
            };
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    private static string CreateSafeMessage(string code, bool? retryable)
    {
        var safeCode = code is "agent_turn_internal_error"
            or "a2a_transport_failure"
            or "agent_host_turn_incomplete"
            or "github_copilot_auth_required"
            or "github_copilot_capability_snapshot_unavailable"
            or "model_provider_snapshot_unavailable"
            or "shell_execution_timeout"
            or "assembly_blocked"
            or "assembly_failed"
            ? code
            : "agent_turn_internal_error";
        var retrySummary = retryable switch
        {
            true => " Retry is available.",
            false => " Retry is not available.",
            _ => " Retry availability is unknown.",
        };
        return $"Run failed with code '{safeCode}'." + retrySummary;
    }
}
