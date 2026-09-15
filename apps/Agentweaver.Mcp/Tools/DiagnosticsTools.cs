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
        "AgentProviderException",
        "ArgumentException",
        "DirectoryNotFoundException",
        "FileNotFoundException",
        "HttpRequestException",
        "IOException",
        "InvalidOperationException",
        "JsonException",
        "ModelProviderConnectionRequiredException",
        "NotSupportedException",
        "OperationCanceledException",
        "SocketException",
        "TaskCanceledException",
        "TimeoutException",
        "UnauthorizedAccessException",
        "WorkflowAgentInfrastructureException",
    };
    private static readonly Regex SafeCauseEntry = new(
        @"\A(?:code|phase|reason|step|tool):[A-Za-z0-9_.:-]{1,112}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SafeCodes = new(StringComparer.Ordinal)
    {
        "agent_turn_internal_error",
        "a2a_transport_failure",
        "agent_host_turn_incomplete",
        "assembly_blocked",
        "assembly_failed",
        "coordinator_execution_failed",
        "coordinator_direct_execution_failed",
        "github_copilot_auth_required",
        "github_copilot_capability_snapshot_unavailable",
        "github_copilot_model_unavailable",
        "github_copilot_models_unavailable",
        "github_copilot_provider_unavailable",
        "github_copilot_rate_limited",
        "github_copilot_runtime_not_configured",
        "github_copilot_turn_stalled",
        "github_copilot_turn_timeout",
        "model_provider_changed",
        "model_provider_connection_required",
        "model_provider_snapshot_unavailable",
        "model_provider_unavailable",
        "model_provider_validation_unavailable",
        "shell_execution_timeout",
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
                CauseChain = result.CauseChain.Where(IsSafeCause).ToList(),
            };
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    private static string CreateSafeMessage(string code, bool? retryable)
    {
        var safeCode = SafeCodes.Contains(code) ? code : "agent_turn_internal_error";
        var retrySummary = retryable switch
        {
            true => " Retry is available.",
            false => " Retry is not available.",
            _ => " Retry availability is unknown.",
        };
        return $"Run failed with code '{safeCode}'." + retrySummary;
    }

    private static bool IsSafeCause(string cause) =>
        !string.IsNullOrWhiteSpace(cause)
        && cause.Length <= 128
        && (SafeCauseTypes.Contains(cause) || SafeCauseEntry.IsMatch(cause));
}
