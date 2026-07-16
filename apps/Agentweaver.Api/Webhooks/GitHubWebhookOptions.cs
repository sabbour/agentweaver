namespace Agentweaver.Api.Webhooks;

/// <summary>
/// Configuration for the inbound GitHub webhook receiver (issue #53 follow-up: wires a REAL external
/// event source to the existing <c>WorkflowEventTriggerService.FireEventAsync</c> mechanism). Bound
/// from the <c>GitHubWebhook</c> configuration section — see <c>appsettings.json</c>. The secret is
/// never hardcoded; it must be supplied via configuration (environment variable, Key Vault, etc.) in
/// every real deployment. When left empty, the receiver fails closed (rejects every delivery with
/// 500) rather than silently accepting unsigned/unverifiable payloads.
/// </summary>
public sealed class GitHubWebhookOptions
{
    public const string SectionName = "GitHubWebhook";

    /// <summary>
    /// The shared secret configured on the GitHub webhook (repository/org Settings → Webhooks). Used
    /// to verify the <c>X-Hub-Signature-256</c> HMAC-SHA256 signature on every delivery.
    /// </summary>
    public string? Secret { get; set; }
}
