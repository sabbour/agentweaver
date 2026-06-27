namespace Agentweaver.AgentHost;

/// <summary>
/// Configuration options for the in-pod Agentweaver AgentHost.
/// Bound from the <c>AgentHost</c> configuration section (or environment variables prefixed
/// <c>AgentHost__</c>), which are injected into the pod at claim time by the worker.
/// </summary>
public sealed class AgentHostOptions
{
    // ── A2A endpoint ──────────────────────────────────────────────────────────

    /// <summary>
    /// URL prefix at which the A2A endpoints are mounted.
    /// Card: <c>{A2APath}/v1/card</c>, stream: <c>{A2APath}/v1/message:stream</c>.
    /// Default: <c>/a2a/agent</c>.
    /// </summary>
    public string A2APath { get; init; } = "/a2a/agent";

    /// <summary>
    /// Bearer token required to access the A2A agent-card endpoint (H3 — authz-gated
    /// discovery). Callers must supply <c>Authorization: Bearer {CardBearerToken}</c>.
    /// If empty, the card endpoint is accessible without authentication (not recommended
    /// for production; use only in local/test environments).
    /// </summary>
    public string CardBearerToken { get; init; } = string.Empty;

    // ── Per-run agent context (injected at pod-launch time) ───────────────────

    /// <summary>Agentweaver run ID this pod is executing. Injected via env var.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path to the working directory (workspace PVC mount) for this run.
    /// </summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Absolute path to the git repository root inside the workspace PVC.</summary>
    public string RepositoryPath { get; init; } = string.Empty;

    /// <summary>Model identifier forwarded to the Copilot SDK (e.g. <c>gpt-4o</c>).</summary>
    public string? ModelId { get; init; }

    /// <summary>Agentweaver project ID for this run.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Agent name for identity / telemetry.</summary>
    public string? AgentName { get; init; }

    /// <summary>Agentweaver API base URL — the in-cluster loopback used by agent tools.</summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>Agentweaver API key for tool authentication against the worker-tier API.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Submitting user ID for per-user scoping.</summary>
    public string? UserId { get; init; }

    /// <summary>Optional system prompt context injected by the workflow graph.</summary>
    public string? SystemPromptContext { get; init; }
}
