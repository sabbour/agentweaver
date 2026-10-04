using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox;

public sealed record EffectivePermissionInspection(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("binding")] PermissionBindingSummary Binding,
    [property: JsonPropertyName("configured_policy")] PermissionPolicySummary ConfiguredPolicy,
    [property: JsonPropertyName("effective_policy")] PermissionPolicySummary EffectivePolicy,
    [property: JsonPropertyName("overrides")] PermissionNarrowingSummary Overrides,
    [property: JsonPropertyName("current_revocation")] PermissionRevocationSummary CurrentRevocation,
    [property: JsonPropertyName("coverage")] IReadOnlyList<PermissionCoverageSummary> Coverage,
    [property: JsonPropertyName("latest_denial")] PermissionDenialSummary? LatestDenial);

public sealed record PermissionBindingSummary(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("binding_id")] string BindingId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("parent_binding_id")] string? ParentBindingId,
    [property: JsonPropertyName("parent_version")] string? ParentVersion,
    [property: JsonPropertyName("launch_binding_id")] string? LaunchBindingId,
    [property: JsonPropertyName("launch_version")] string? LaunchVersion);

public sealed record PermissionPolicySummary(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("shell_enabled")] bool ShellEnabled,
    [property: JsonPropertyName("direct_execution")] bool DirectExecution,
    [property: JsonPropertyName("network_enabled")] bool NetworkEnabled,
    [property: JsonPropertyName("require_approval_for_all_shell")] bool RequireApprovalForAllShell,
    [property: JsonPropertyName("redact_pii")] bool RedactPii,
    [property: JsonPropertyName("max_output_bytes")] int MaxOutputBytes,
    [property: JsonPropertyName("allowed_repository_root_count")] int AllowedRepositoryRootCount,
    [property: JsonPropertyName("destructive_command_pattern_count")] int DestructiveCommandPatternCount,
    [property: JsonPropertyName("allowed_operations")] IReadOnlyList<string> AllowedOperations);

public sealed record PermissionNarrowingSummary(
    [property: JsonPropertyName("is_narrowed")] bool IsNarrowed,
    [property: JsonPropertyName("removed_operations")] IReadOnlyList<string> RemovedOperations,
    [property: JsonPropertyName("tightened_controls")] IReadOnlyList<string> TightenedControls,
    [property: JsonPropertyName("launch_ceiling_active")] bool LaunchCeilingActive,
    [property: JsonPropertyName("parent_restriction_active")] bool ParentRestrictionActive);

public sealed record PermissionRevocationSummary(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("removed_since_launch")] IReadOnlyList<string> RemovedSinceLaunch,
    [property: JsonPropertyName("tightened_controls")] IReadOnlyList<string> TightenedControls,
    [property: JsonPropertyName("shell_revoked")] bool ShellRevoked,
    [property: JsonPropertyName("network_revoked")] bool NetworkRevoked,
    [property: JsonPropertyName("direct_execution_revoked")] bool DirectExecutionRevoked);

public sealed record PermissionCoverageSummary(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("tool_family")] string ToolFamily,
    [property: JsonPropertyName("enforcement_gate")] string EnforcementGate);

public sealed record PermissionDenialSummary(
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("operation")] string? Operation,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("binding_id")] string? BindingId,
    [property: JsonPropertyName("binding_version")] string? BindingVersion,
    [property: JsonPropertyName("binding_source")] string? BindingSource,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset? TimestampUtc);

public static class EffectivePermissionInspectionProjector
{
    private static readonly IReadOnlyDictionary<string, (string ToolFamily, string EnforcementGate)> Coverage =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [EffectivePermissionOperations.Observe] =
                ("report_intent, report_outcome", "runtime permission handler"),
            [EffectivePermissionOperations.WorkspaceRead] =
                ("file and directory reads", "binding, then path containment"),
            [EffectivePermissionOperations.WorkspaceSearch] =
                ("workspace search and glob", "binding, then bounded root enumeration"),
            [EffectivePermissionOperations.WorkspaceWrite] =
                ("create, edit, replace, and patch", "binding, then path containment and tool validation"),
            [EffectivePermissionOperations.ShellExecute] =
                ("run_command", "binding, isolation check, command policy, and executor"),
            [EffectivePermissionOperations.NetworkAccess] =
                ("web_fetch", "binding before approval or auto-approval"),
            [EffectivePermissionOperations.AgentweaverRead] =
                ("read-only Agentweaver API tools", "binding before authenticated API call"),
            [EffectivePermissionOperations.AgentweaverWrite] =
                ("mutating Agentweaver API tools", "binding before authenticated API call"),
            [EffectivePermissionOperations.PreviewManage] =
                ("preview process and session tools", "binding, then preview runner gates"),
            [EffectivePermissionOperations.HumanInteraction] =
                ("ask_question", "binding, then run-scoped question gate"),
        };

    public static EffectivePermissionInspection Project(
        string runId,
        SandboxPolicy configuredPolicy,
        EffectivePermissionBinding effective,
        IReadOnlyList<RunEvent> events)
    {
        var configured = EffectivePermissionBinding.Create(
            runId,
            effective.Attempt,
            "current-project-sandbox-policy",
            effective.Scope,
            configuredPolicy);
        var launchEvent = FindLaunchBinding(events, runId, effective.Attempt);
        var launch = launchEvent?.Binding;
        var removed = Except(configured.AllowedOperations, effective.AllowedOperations);
        var tightened = TightenedControls(configured.Policy, effective.Policy);
        var removedSinceLaunch = launch is null
            ? Array.Empty<string>()
            : Except(launch.AllowedOperations, effective.AllowedOperations);
        var tightenedSinceLaunch = launch is null
            ? Array.Empty<string>()
            : TightenedControls(launch.Policy, effective.Policy);
        var currentRevocation = removedSinceLaunch.Count > 0 || tightenedSinceLaunch.Count > 0;

        return new EffectivePermissionInspection(
            runId,
            new PermissionBindingSummary(
                effective.SchemaVersion,
                effective.BindingId,
                effective.Version,
                effective.Source,
                effective.Attempt,
                effective.Scope,
                launch?.ParentBindingId,
                launch?.ParentVersion,
                launch?.BindingId,
                launch?.Version),
            Summarize(configured),
            Summarize(effective),
            new PermissionNarrowingSummary(
                removed.Count > 0 || tightened.Count > 0,
                removed,
                tightened,
                launch is not null && IsNarrower(launch, configured),
                launch?.ParentBindingId is not null),
            new PermissionRevocationSummary(
                currentRevocation,
                removedSinceLaunch,
                tightenedSinceLaunch,
                launch?.Policy.ShellEnabled == true && !effective.Policy.ShellEnabled,
                launch?.Policy.NetworkEnabled == true && !effective.Policy.NetworkEnabled,
                launch?.Policy.Direct == true && !effective.Policy.Direct),
            [.. Coverage
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new PermissionCoverageSummary(
                    entry.Key,
                    effective.Allows(entry.Key),
                    entry.Value.ToolFamily,
                    entry.Value.EnforcementGate))],
            FindLatestDenial(
                events,
                launchEvent?.Sequence ?? 0,
                effective.Attempt,
                effective.BindingId,
                launch?.BindingId));
    }

    private static PermissionPolicySummary Summarize(EffectivePermissionBinding binding) =>
        new(
            binding.Version,
            binding.Policy.ShellEnabled,
            binding.Policy.Direct,
            binding.Policy.NetworkEnabled,
            binding.Policy.RequireApprovalForAllShell,
            binding.Policy.RedactPii,
            binding.Policy.MaxOutputBytes,
            binding.Policy.AllowedRepositoryRoots.Count,
            binding.Policy.DestructiveCommandPatterns.Count,
            binding.AllowedOperations);

    private static IReadOnlyList<string> Except(
        IReadOnlyList<string> wider,
        IReadOnlyList<string> narrower) =>
        [.. wider.Except(narrower, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static bool IsNarrower(
        EffectivePermissionBinding narrower,
        EffectivePermissionBinding wider) =>
        Except(wider.AllowedOperations, narrower.AllowedOperations).Count > 0
        || TightenedControls(wider.Policy, narrower.Policy).Count > 0;

    private static IReadOnlyList<string> TightenedControls(
        SandboxPolicy wider,
        SandboxPolicy narrower)
    {
        var controls = new List<string>();
        if (wider.ShellEnabled && !narrower.ShellEnabled)
            controls.Add("shell_disabled");
        if (wider.NetworkEnabled && !narrower.NetworkEnabled)
            controls.Add("network_disabled");
        if (wider.Direct && !narrower.Direct)
            controls.Add("direct_execution_disabled");
        if (!wider.RequireApprovalForAllShell && narrower.RequireApprovalForAllShell)
            controls.Add("shell_approval_required");
        if (!wider.RedactPii && narrower.RedactPii)
            controls.Add("pii_redaction_enabled");
        if (narrower.MaxOutputBytes < wider.MaxOutputBytes)
            controls.Add("output_limit_reduced");
        if (wider.AllowedRepositoryRoots
            .Except(narrower.AllowedRepositoryRoots, StringComparer.Ordinal)
            .Any())
        {
            controls.Add("repository_roots_reduced");
        }
        if (narrower.DestructiveCommandPatterns
            .Except(wider.DestructiveCommandPatterns, StringComparer.Ordinal)
            .Any())
        {
            controls.Add("command_approval_patterns_added");
        }
        return controls;
    }

    private static (EffectivePermissionBinding Binding, int Sequence)? FindLaunchBinding(
        IReadOnlyList<RunEvent> events,
        string runId,
        int attempt)
    {
        foreach (var evt in events
                     .Where(candidate => candidate.Type == EventTypes.PermissionBindingBound)
                     .OrderBy(candidate => candidate.Sequence))
        {
            var binding = Deserialize<EffectivePermissionBinding>(evt.Payload);
            if (binding is null || binding.Attempt != attempt)
                continue;
            binding.Validate(runId, attempt);
            return (binding, evt.Sequence);
        }

        return null;
    }

    private static PermissionDenialSummary? FindLatestDenial(
        IReadOnlyList<RunEvent> events,
        int launchSequence,
        int attempt,
        string effectiveBindingId,
        string? launchBindingId)
    {
        foreach (var evt in events
                     .Where(candidate =>
                         candidate.Type == EventTypes.RunDegraded
                         && candidate.Sequence >= launchSequence)
                     .OrderByDescending(candidate => candidate.Sequence))
        {
            var payload = ToJson(evt.Payload);
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("reason", out var reasonElement)
                || reasonElement.GetString() is not { } rawReason
                || !rawReason.StartsWith("Operation denied", StringComparison.Ordinal))
            {
                continue;
            }
            var denialAttempt = GetInt32(payload, "permissionAttempt");
            var denialBindingId = GetString(payload, "permissionBindingId");
            if (denialAttempt is not null
                    ? denialAttempt != attempt
                    : !string.Equals(denialBindingId, effectiveBindingId, StringComparison.Ordinal)
                      && !string.Equals(denialBindingId, launchBindingId, StringComparison.Ordinal))
            {
                continue;
            }

            var operation = EffectivePermissionOperations.Known
                .OrderBy(value => value, StringComparer.Ordinal)
                .FirstOrDefault(value => rawReason.Contains($"'{value}'", StringComparison.Ordinal));
            var reasonCode = rawReason.Contains("no effective permission binding", StringComparison.Ordinal)
                ? "binding_missing"
                : rawReason.Contains("unclassified", StringComparison.Ordinal)
                    ? "unclassified_operation"
                    : "operation_not_allowed";
            var safeReason = reasonCode switch
            {
                "binding_missing" => "No effective permission binding was active, so the operation was denied.",
                "unclassified_operation" => "The operation was unclassified and denied.",
                _ => $"Operation '{operation ?? "unknown"}' was not allowed by the effective binding.",
            };

            return new PermissionDenialSummary(
                reasonCode,
                safeReason,
                operation,
                SafeToolName(GetString(payload, "toolName")),
                SafeIdentifier(denialBindingId, "epb-"),
                SafeIdentifier(GetString(payload, "permissionBindingVersion"), "sha256:"),
                SafeSource(GetString(payload, "permissionSource")),
                evt.Sequence,
                evt.TimestampUtc == default ? null : evt.TimestampUtc);
        }

        return null;
    }

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
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? SafeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 128)
            return null;
        return toolName.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or ':')
            ? toolName
            : null;
    }

    private static string? SafeIdentifier(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != prefix.Length + 64
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value[prefix.Length..].Any(character =>
                !char.IsAsciiDigit(character)
                && character is not (>= 'a' and <= 'f')))
        {
            return null;
        }

        return value;
    }

    private static string? SafeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 128)
            return null;
        return source.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '+' or '.')
            ? source
            : null;
    }
}
