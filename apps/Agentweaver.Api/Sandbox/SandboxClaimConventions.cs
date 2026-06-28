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
    /// name only when <c>status.phase == "Bound"</c> and a pod name is present at
    /// <c>status.sandbox.name</c> (primary) or <c>status.podName</c> (fallback); otherwise
    /// <see langword="null"/> (claim not yet bound). Pure — safe to unit test without a cluster.
    /// </summary>
    public static string? TryGetBoundPodName(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status))
            return null;

        var phase = status.TryGetProperty("phase", out var p) ? p.GetString() : null;
        if (phase != "Bound")
            return null;

        string? podName = null;

        // status.sandbox.name is the primary field (agent-sandbox controller shape).
        if (status.TryGetProperty("sandbox", out var sandbox) &&
            sandbox.TryGetProperty("name", out var sn))
            podName = sn.GetString();

        if (string.IsNullOrEmpty(podName) &&
            status.TryGetProperty("podName", out var pn))
            podName = pn.GetString();

        return string.IsNullOrEmpty(podName) ? null : podName;
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
