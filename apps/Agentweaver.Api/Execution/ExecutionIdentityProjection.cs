using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Execution;

public sealed record ExecutionIdentityProjection(
    [property: JsonPropertyName("evidence_state")] string EvidenceState,
    [property: JsonPropertyName("descriptor")] ExecutionDescriptorSummary? Descriptor,
    [property: JsonPropertyName("backend")] ExecutionBackendSummary? Backend,
    [property: JsonPropertyName("launch_permission_binding")] ExecutionPermissionBindingSummary? LaunchPermissionBinding,
    [property: JsonPropertyName("permission_binding")] ExecutionPermissionBindingSummary? PermissionBinding,
    [property: JsonPropertyName("decisions")] IReadOnlyList<ExecutionDecisionSummary> Decisions);

public sealed record ExecutionDescriptorSummary(
    [property: JsonPropertyName("descriptor_id")] string DescriptorId,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("principal_ref")] string PrincipalReference,
    [property: JsonPropertyName("executing_service")] string ExecutingService,
    [property: JsonPropertyName("agent_assignment_id")] string AgentAssignmentId,
    [property: JsonPropertyName("agent_role")] string? AgentRole,
    [property: JsonPropertyName("agent_display_name")] string? AgentDisplayName,
    [property: JsonPropertyName("parent_descriptor_id")] string? ParentDescriptorId,
    [property: JsonPropertyName("retry_of_descriptor_id")] string? RetryOfDescriptorId,
    [property: JsonPropertyName("workflow_run_id")] string? WorkflowRunId,
    [property: JsonPropertyName("subtask_id")] string? SubtaskId,
    [property: JsonPropertyName("approval_policy_snapshot_id")] string? ApprovalPolicySnapshotId,
    [property: JsonPropertyName("executable_workflow_digest")] string? ExecutableWorkflowDigest,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record ExecutionBackendSummary(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sandbox_ref")] string? SandboxReference,
    [property: JsonPropertyName("evidence_state")] string EvidenceState);

public sealed record ExecutionPermissionBindingSummary(
    [property: JsonPropertyName("binding_id")] string BindingId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("attempt")] int Attempt);

public sealed record ExecutionDecisionSummary(
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("gate")] string Gate,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason_code")] string? ReasonCode,
    [property: JsonPropertyName("correlation_state")] string CorrelationState,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset? TimestampUtc);

public static class ExecutionIdentityProjector
{
    private const int MaxDecisionCount = 100;

    public static ExecutionIdentityProjection Project(
        Run run,
        ExecutionIdentityDescriptor? descriptor,
        IReadOnlyList<RunEvent> events,
        EffectivePermissionBinding? currentPermissionBinding = null)
    {
        if (descriptor is null)
            return new("missing_legacy_descriptor", null, Backend(run), null, null, []);

        var backend = Backend(run);
        var attemptEvents = events
            .Where(evt => evt.TimestampUtc == default || evt.TimestampUtc >= descriptor.CreatedAt)
            .ToArray();
        var launchBinding = attemptEvents
            .Where(evt => evt.Type == EventTypes.PermissionBindingBound)
            .OrderBy(evt => evt.Sequence)
            .Select(evt => Deserialize<EffectivePermissionBinding>(evt.Payload))
            .FirstOrDefault(candidate => candidate?.Attempt == descriptor.Attempt);
        var binding = currentPermissionBinding ?? launchBinding;
        var calls = attemptEvents
            .Where(evt => evt.Type == EventTypes.ToolCall)
            .Select(evt => (Event: evt, Payload: ToJson(evt.Payload)))
            .Select(item => new ToolCallEvidence(
                item.Event.Sequence,
                SafeIdentifier(GetString(item.Payload, "callId"))
                    ?? $"event-{item.Event.Sequence}",
                SafeIdentifier(GetString(item.Payload, "toolName") ?? GetString(item.Payload, "name"))))
            .ToArray();
        var decisions = attemptEvents
            .Where(evt => evt.Type is EventTypes.ToolResult or EventTypes.ToolError
                || evt.Type == "tool.approval_resolved"
                || IsEffectivePermissionDenial(evt))
            .OrderBy(evt => evt.Sequence)
            .Select(evt => ProjectDecision(evt, calls))
            .Where(summary => summary is not null)
            .TakeLast(MaxDecisionCount)
            .Cast<ExecutionDecisionSummary>()
            .ToArray();

        return new ExecutionIdentityProjection(
            binding is null || launchBinding is null || backend.EvidenceState == "missing"
                ? "partial"
                : "complete",
            new ExecutionDescriptorSummary(
                descriptor.DescriptorId,
                descriptor.SchemaVersion,
                descriptor.RunId,
                descriptor.Attempt,
                OpaqueReference("principal", descriptor.InitiatingPrincipalId),
                descriptor.ExecutingServiceId,
                descriptor.AgentAssignmentId,
                descriptor.AgentRole,
                descriptor.AgentDisplayName,
                descriptor.ParentDescriptorId,
                descriptor.RetryOfDescriptorId,
                descriptor.WorkflowRunId,
                descriptor.SubtaskId,
                descriptor.ApprovalPolicySnapshotId,
                descriptor.ExecutableWorkflowContentDigest,
                descriptor.CreatedAt),
            backend,
            ToBindingSummary(launchBinding),
            ToBindingSummary(binding),
            decisions);
    }

    private static ExecutionPermissionBindingSummary? ToBindingSummary(EffectivePermissionBinding? binding) =>
        binding is null
            ? null
            : new ExecutionPermissionBindingSummary(
                binding.BindingId,
                binding.Version,
                binding.Source,
                binding.Attempt);

    private static bool IsEffectivePermissionDenial(RunEvent evt)
    {
        if (evt.Type != EventTypes.RunDegraded)
            return false;

        var payload = ToJson(evt.Payload);
        return GetString(payload, "reason")?.StartsWith("Operation denied", StringComparison.Ordinal) == true;
    }

    private static ExecutionBackendSummary Backend(Run run)
    {
        var kind = string.IsNullOrWhiteSpace(run.SandboxBackend) ? "local" : run.SandboxBackend;
        var sandboxIdentity = string.Join(
            "\n",
            new[] { run.SandboxClaimName, run.SandboxPodName, run.SandboxNamespace }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(
            kind,
            string.IsNullOrWhiteSpace(sandboxIdentity)
                ? null
                : OpaqueReference("sandbox", sandboxIdentity),
            string.IsNullOrWhiteSpace(sandboxIdentity) && !string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase)
                ? "missing"
                : "observed");
    }

    private static ExecutionDecisionSummary? ProjectDecision(
        RunEvent evt,
        IReadOnlyList<ToolCallEvidence> calls)
    {
        var payload = ToJson(evt.Payload);
        var explicitCallId = SafeIdentifier(
            GetString(payload, "callId") ?? GetString(payload, "requestId"));
        var directToolName = SafeIdentifier(
            GetString(payload, "toolName") ?? GetString(payload, "name"));
        var correlatedCall = explicitCallId is not null
            ? calls.LastOrDefault(call =>
                call.Sequence < evt.Sequence
                && string.Equals(call.CallId, explicitCallId, StringComparison.Ordinal))
            : calls.LastOrDefault(call =>
                call.Sequence < evt.Sequence
                && directToolName is not null
                && string.Equals(call.ToolName, directToolName, StringComparison.Ordinal));
        var callId = explicitCallId ?? correlatedCall?.CallId;
        var toolName = correlatedCall?.ToolName ?? directToolName;
        var outcome = evt.Type switch
        {
            EventTypes.ToolResult => "succeeded",
            EventTypes.ToolError => "failed",
            "tool.approval_resolved" => GetBoolean(payload, "approved") == true ? "approved" : "denied",
            EventTypes.RunDegraded => "denied",
            _ => "unknown",
        };
        var gate = evt.Type switch
        {
            "tool.approval_resolved" => "human_approval",
            EventTypes.RunDegraded => "effective_permission",
            _ => "tool_runtime",
        };
        var reason = evt.Type == EventTypes.RunDegraded
            ? "operation_not_allowed"
            : evt.Type == EventTypes.ToolError
                ? "tool_error"
                : null;
        return new(
            evt.Sequence,
            callId,
            toolName,
            gate,
            outcome,
            reason,
            correlatedCall is null ? "missing_tool_call" : "matched",
            evt.TimestampUtc == default ? null : evt.TimestampUtc);
    }

    private sealed record ToolCallEvidence(int Sequence, string CallId, string? ToolName);

    private static T? Deserialize<T>(object payload) =>
        payload switch
        {
            T value => value,
            JsonElement json => json.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            _ => JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(payload),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

    private static JsonElement ToJson(object payload) =>
        payload is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string? GetString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBoolean(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? SafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            ? value
            : null;

    private static string OpaqueReference(string prefix, string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
        return $"{prefix}-{digest}";
    }
}
