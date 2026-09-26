using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agentweaver.AspNetCore;

public record RunTerminalDiagnosticResponse
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("component")] public required string Component { get; init; }
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("retryable")] public bool? Retryable { get; init; }
    [JsonPropertyName("correlation_ids")] public IReadOnlyDictionary<string, string> CorrelationIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    [JsonPropertyName("cause_chain")] public IReadOnlyList<string> CauseChain { get; init; } = [];
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 2;
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
    [JsonPropertyName("observed_at")] public DateTimeOffset? ObservedAt { get; init; }
    [JsonPropertyName("completeness")] public string Completeness { get; init; } = "partial";
    [JsonPropertyName("evidence_sources")] public IReadOnlyList<DiagnosticEvidenceSource> EvidenceSources { get; init; } = [];
    [JsonPropertyName("evidence_references")] public IReadOnlyList<DiagnosticEvidenceReference> EvidenceReferences { get; init; } = [];
    [JsonPropertyName("observed_facts")] public IReadOnlyList<DiagnosticStatement> ObservedFacts { get; init; } = [];
    [JsonPropertyName("supported_interpretations")] public IReadOnlyList<DiagnosticStatement> SupportedInterpretations { get; init; } = [];
    [JsonPropertyName("unknowns")] public IReadOnlyList<DiagnosticStatement> Unknowns { get; init; } = [];
    [JsonPropertyName("denial_gate")] public DiagnosticDenialGate? DenialGate { get; init; }
    [JsonPropertyName("next_actions")] public IReadOnlyList<DiagnosticNextAction> NextActions { get; init; } = [];
    [JsonPropertyName("execution_descriptor_id")] public string? ExecutionDescriptorId { get; init; }
    [JsonPropertyName("execution_identity_evidence_state")] public string? ExecutionIdentityEvidenceState { get; init; }
}

public sealed record DiagnosticEvidenceSource(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("completeness")] string Completeness,
    [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("detail")] string? Detail = null);

public sealed record DiagnosticEvidenceReference(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sequence")] int? Sequence,
    [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("synthetic")] bool Synthetic = false);

public sealed record DiagnosticStatement(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("evidence_reference_ids")] IReadOnlyList<string> EvidenceReferenceIds);

public sealed record DiagnosticDenialGate(
    [property: JsonPropertyName("gate")] string Gate,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason_code")] string? ReasonCode,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("capability")] string? Capability,
    [property: JsonPropertyName("permission_binding_id")] string? PermissionBindingId,
    [property: JsonPropertyName("permission_binding_version")] string? PermissionBindingVersion,
    [property: JsonPropertyName("permission_binding_source")] string? PermissionBindingSource,
    [property: JsonPropertyName("evidence_reference_id")] string EvidenceReferenceId);

public sealed record DiagnosticNextAction(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("preconditions")] IReadOnlyList<string> Preconditions,
    [property: JsonPropertyName("expected_effect")] string ExpectedEffect,
    [property: JsonPropertyName("mutating")] bool Mutating = false);

public static class RunFailureDiagnosticSanitizer
{
    private static readonly Regex ServerGeneratedId = new(
        @"\A[a-f0-9]{32}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeCauseEntry = new(
        @"\A(?:code|phase|reason|step|tool):[A-Za-z0-9_.:-]{1,112}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SafeCauseTypes = new(StringComparer.Ordinal)
    {
        "AgentProviderException", "ArgumentException", "DirectoryNotFoundException",
        "FileNotFoundException", "HttpRequestException", "IOException",
        "InvalidOperationException", "JsonException", "ModelProviderConnectionRequiredException",
        "NotSupportedException", "OperationCanceledException", "SocketException",
        "TaskCanceledException", "TimeoutException", "UnauthorizedAccessException",
        "WorkflowAgentInfrastructureException",
    };
    private static readonly HashSet<string> SafeCodes = new(StringComparer.Ordinal)
    {
        "agent_turn_internal_error", "a2a_transport_failure", "agent_host_turn_incomplete",
        "assembly_blocked", "assembly_failed", "coordinator_execution_failed",
        "coordinator_direct_execution_failed", "coordinator_outcome_spec_invalid_response",
        "coordinator_outcome_spec_model_refused", "coordinator_startup_failed",
        "github_copilot_auth_required", "github_copilot_capability_snapshot_unavailable",
        "github_copilot_model_unavailable", "github_copilot_models_unavailable",
        "github_copilot_provider_unavailable", "github_copilot_rate_limited",
        "github_copilot_runtime_not_configured", "github_copilot_turn_stalled",
        "github_copilot_turn_timeout", "model_provider_changed",
        "model_provider_connection_required", "model_provider_snapshot_unavailable",
        "model_provider_unavailable", "model_provider_validation_unavailable",
        "shell_execution_timeout",
    };

    public static RunTerminalDiagnosticResponse Sanitize(RunTerminalDiagnosticResponse result)
    {
        var code = SafeCodes.Contains(result.Code) ? result.Code : "agent_turn_internal_error";
        return result with
        {
            Code = code,
            Message = CreateSafeMessage(code, result.Retryable),
            CorrelationIds = result.CorrelationIds
                .Where(pair => ServerGeneratedId.IsMatch(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            CauseChain = result.CauseChain.Where(IsSafeCause).ToArray(),
        };
    }

    private static string CreateSafeMessage(string code, bool? retryable)
    {
        if (code == "coordinator_outcome_spec_model_refused")
            return "The model declined to draft the outcome spec after one correction attempt. Retry the run or choose another model.";
        if (code == "coordinator_outcome_spec_invalid_response")
            return "The model returned an invalid outcome-spec response after one correction attempt. Retry the run or choose another model.";
        var retrySummary = retryable switch
        {
            true => " Retry is available.",
            false => " Retry is not available.",
            _ => " Retry availability is unknown.",
        };
        return $"Run failed with code '{code}'." + retrySummary;
    }

    private static bool IsSafeCause(string cause) =>
        !string.IsNullOrWhiteSpace(cause)
        && cause.Length <= 128
        && (SafeCauseTypes.Contains(cause) || SafeCauseEntry.IsMatch(cause));
}
