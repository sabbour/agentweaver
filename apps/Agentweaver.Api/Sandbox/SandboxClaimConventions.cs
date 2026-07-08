using System.Text.Json;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Single source of truth for the <c>SandboxClaim</c> CRD coordinates, the AgentHost claim-name
/// derivation, and the bound-pod status parsing.
///
/// <para>
/// Extracted from <see cref="KubernetesSandboxExecutor"/> so that any replica can resolve a run's
/// bound sandbox pod directly from <b>cluster state</b> (the SandboxClaim's <c>status</c>) rather
/// than an in-memory, per-process registry. The registry is only populated on the replica that
/// launched the pod, so a request landing on the other replica must read the claim instead — this
/// helper keeps that read identical to the executor's own <c>WaitForBoundAsync</c> logic.
/// </para>
/// </summary>
public static class SandboxClaimConventions
{
    /// <summary>CRD group for SandboxClaims (agent-sandbox controller).</summary>
    public const string ApiGroup = "extensions.agents.x-k8s.io";

    /// <summary>CRD version for SandboxClaims.</summary>
    public const string ApiVersion = "v1beta1";

    /// <summary>CRD plural for SandboxClaims.</summary>
    public const string ClaimPlural = "sandboxclaims";

    /// <summary>Prefix distinguishing AgentHost (pod-per-run) claims from per-command claims.</summary>
    public const string AgentHostClaimPrefix = "agent-";

    /// <summary>Prefix for retained command-sandbox claims created by KubernetesSandboxExecutor.ExecuteAsync.</summary>
    public const string RunCommandClaimPrefix = "run-";

    /// <summary>
    /// Derives the AgentHost <c>SandboxClaim</c> name for <paramref name="runId"/>:
    /// hyphens stripped, truncated to 12 chars, prefixed with <see cref="AgentHostClaimPrefix"/>.
    /// MUST stay identical to the name used when the claim is created/released so any replica
    /// resolves the same claim.
    /// </summary>
    public static string DeriveAgentHostClaimName(string runId)
    {
        var claimBase = NormalizeRunIdForClaim(runId, 12);
        return $"{AgentHostClaimPrefix}{claimBase}";
    }

    /// <summary>
    /// Derives the retained command-sandbox <c>SandboxClaim</c> name for <paramref name="runId"/>.
    /// This mirrors <see cref="KubernetesSandboxExecutor.ExecuteAsync"/> so preview activation can
    /// find a server started by an in-process <c>run_command</c> turn.
    /// </summary>
    public static string DeriveRunCommandClaimName(string runId)
    {
        var claimBase = NormalizeRunIdForClaim(runId, 16);
        return $"{RunCommandClaimPrefix}{claimBase}";
    }

    private static string NormalizeRunIdForClaim(string? runId, int maxLength)
    {
        var claimBase = (runId ?? string.Empty).Replace("-", "", StringComparison.Ordinal);
        return claimBase[..Math.Min(maxLength, claimBase.Length)];
    }

    public static string DeriveAgentHostSecretProviderClassName(string claimName) =>
        $"agentweaver-user-token-{AgentHostRunNameSuffix(claimName)}";

    public static string DeriveAgentHostSandboxTemplateName(string claimName) =>
        $"{claimName}-template";

    public static string DeriveAgentHostSandboxWarmPoolName(string claimName) =>
        $"{claimName}-pool";

    private static string AgentHostRunNameSuffix(string claimName) =>
        claimName.StartsWith(AgentHostClaimPrefix, StringComparison.Ordinal)
            ? claimName[AgentHostClaimPrefix.Length..]
            : claimName;

    /// <summary>
    /// Extracts the bound pod name from a SandboxClaim object's <c>status</c>: returns the pod
    /// name only when the claim is ready and a pod name is present at <c>status.sandbox.name</c>;
    /// otherwise <see langword="null"/> (claim not yet bound). Pure — safe to unit test without
    /// a cluster.
    ///
    /// <para>
    /// The agent-sandbox CRD (v1alpha1/v1beta1) has <b>no</b> <c>status.phase</c> field — the
    /// controller signals readiness via a <c>Ready</c> <b>condition</b>
    /// (<c>status.conditions[?(@.type=='Ready')].status == "True"</c>). The bound pod name is the
    /// Sandbox object's name at <c>status.sandbox.name</c> (Sandbox name == pod name).
    /// </para>
    /// </summary>
    public static string? TryGetBoundPodName(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status))
            return null;

        if (!IsReady(status))
            return null;

        // status.sandbox.name is the bound pod name (Sandbox object name == pod name).
        if (status.TryGetProperty("sandbox", out var sandbox) &&
            sandbox.TryGetProperty("name", out var sn))
        {
            var podName = sn.GetString();
            return string.IsNullOrEmpty(podName) ? null : podName;
        }

        return null;
    }

    /// <summary>
    /// Returns a reconciler-error message when the claim's <c>status</c> carries a condition that
    /// signals the controller failed to provision the pod — either a condition whose
    /// <c>type</c>/<c>reason</c> contains <c>ReconcilerError</c>, or any condition whose message
    /// mentions <c>exceeded quota</c>. Returns <see langword="null"/> when no such failure is
    /// present. Pure — safe to unit test without a cluster.
    /// </summary>
    public static string? TryGetReconcilerError(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var cond in conditions.EnumerateArray())
        {
            var type = cond.TryGetProperty("type", out var t) ? t.GetString() : null;
            var reason = cond.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var message = cond.TryGetProperty("message", out var m) ? m.GetString() : null;

            var isReconcilerError =
                Contains(type, "ReconcilerError") ||
                Contains(reason, "ReconcilerError") ||
                Contains(message, "exceeded quota");

            if (isReconcilerError)
                return message ?? reason ?? type ?? "reconciler error";
        }

        return null;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives a human-readable phase string from a SandboxClaim JSON element.
    /// Returns <c>"Bound"</c> when the <c>Ready</c> condition is <c>True</c>, <c>"Lost"</c>
    /// when a reconciler error is present, or <c>"Pending"</c> otherwise.
    /// </summary>
    public static string GetPhase(JsonElement root)
    {
        if (TryGetReconcilerError(root) is not null)
            return "Lost";
        if (root.TryGetProperty("status", out var status) && IsReady(status))
            return "Bound";
        return "Pending";
    }

    /// <summary>
    /// Returns <see langword="true"/> when the claim's <c>status</c> carries a <c>Ready</c>
    /// condition with <c>status == "True"</c>. This is the authoritative readiness signal for the
    /// agent-sandbox CRD (there is no <c>status.phase</c>).
    /// </summary>
    private static bool IsReady(JsonElement status)
    {
        if (!status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var cond in conditions.EnumerateArray())
        {
            if (cond.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "Ready", StringComparison.Ordinal) &&
                cond.TryGetProperty("status", out var s))
            {
                return string.Equals(s.GetString(), "True", StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Serializes the dynamic object returned by the Kubernetes custom-objects client and extracts
    /// the bound pod name via <see cref="TryGetBoundPodName(JsonElement)"/>.
    /// </summary>
    public static string? TryGetBoundPodName(object rawClaim)
    {
        var json = JsonSerializer.Serialize(rawClaim);
        using var doc = JsonDocument.Parse(json);
        return TryGetBoundPodName(doc.RootElement);
    }
}
