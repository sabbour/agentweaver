namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>
/// Options for the Gateway-direct browser-preview reverse-proxy (bound from the
/// <c>Sandbox:Preview</c> configuration section).
///
/// <para>
/// Architecture: Gateway(preview) → per-preview HTTPRoute → per-run ClusterIP Service →
/// the run's sandbox pod. The per-preview Kubernetes objects (Service, HTTPRoute, pod-label
/// patch) are created/deleted by the API AT RUNTIME via the in-cluster Kubernetes client.
/// </para>
///
/// <para>
/// Default behaviour is <b>disabled</b> (<see cref="Enabled"/> = false) so the feature
/// ships dark; when disabled the API retains the existing kubectl port-forward path.
/// </para>
/// </summary>
public sealed class SandboxPreviewOptions
{
    /// <summary>Master switch. When <c>false</c> (default) the Gateway preview path and reaper are no-ops.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// DNS zone suffix appended to the per-preview capability token to build the hostname,
    /// e.g. <c>preview.&lt;cluster&gt;.westus2.staging.aksapp.io</c>. Provided by deploy.
    /// The preview URL is <c>https://{token}-preview.{ZoneSuffix}</c>.
    /// </summary>
    public string ZoneSuffix { get; init; } = "";

    /// <summary>Name of the shared Gateway the per-preview HTTPRoute attaches to.</summary>
    public string GatewayName { get; init; } = "agentweaver-preview-gateway";

    /// <summary>Namespace of the shared Gateway.</summary>
    public string GatewayNamespace { get; init; } = "agentweaver";

    /// <summary>Idle timeout: a preview not kept alive within this window is reaped.</summary>
    public int IdleTimeoutMinutes { get; init; } = 30;

    /// <summary>Hard cap: a preview is always reaped after this many hours regardless of keepalive.</summary>
    public int MaxLifetimeHours { get; init; } = 8;

    /// <summary>
    /// When <c>true</c> (default), the preview is retained after the run completes or the pod is
    /// released on suspend; only the reaper (expiry) or an explicit user stop removes it.
    /// </summary>
    public bool KeepAfterRun { get; init; } = true;

    /// <summary>Kubernetes namespace where the per-preview Service / HTTPRoute / pod live.</summary>
    public string Namespace { get; init; } = "agentweaver";

    /// <summary>
    /// Lowest target port a preview may expose (inclusive). Mirrors the gateway-only ingress
    /// range allowed by <c>k8s/networkpolicy-sandbox.yaml</c>; ports outside the range are
    /// rejected by the preview endpoint so we never provision a preview the NetworkPolicy blocks.
    /// </summary>
    public int AllowedPortMin { get; init; } = 3000;

    /// <summary>Highest target port a preview may expose (inclusive). See <see cref="AllowedPortMin"/>.</summary>
    public int AllowedPortMax { get; init; } = 9000;

    /// <summary>
    /// Pure check: is <paramref name="port"/> within the inclusive preview port range
    /// [<paramref name="min"/>, <paramref name="max"/>]? Used by the preview endpoint to reject
    /// out-of-range ports the NetworkPolicy would black-hole. Kept static/pure for unit testing.
    /// </summary>
    public static bool IsPortInRange(int port, int min, int max) => port >= min && port <= max;
}
