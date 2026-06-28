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
    public const string ApiVersion = "v1alpha1";

    /// <summary>CRD plural for SandboxClaims.</summary>
    public const string ClaimPlural = "sandboxclaims";

    /// <summary>Prefix distinguishing AgentHost (pod-per-run) claims from per-command claims.</summary>
    public const string AgentHostClaimPrefix = "agent-";

    /// <summary>
    /// Derives the AgentHost <c>SandboxClaim</c> name for <paramref name="runId"/>:
    /// hyphens stripped, truncated to 12 chars, prefixed with <see cref="AgentHostClaimPrefix"/>.
    /// MUST stay identical to the name used when the claim is created/released so any replica
    /// resolves the same claim.
    /// </summary>
    public static string DeriveAgentHostClaimName(string runId)
    {
        var claimBase = (runId ?? string.Empty).Replace("-", "", StringComparison.Ordinal);
        claimBase = claimBase[..Math.Min(12, claimBase.Length)];
        return $"{AgentHostClaimPrefix}{claimBase}";
    }

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
