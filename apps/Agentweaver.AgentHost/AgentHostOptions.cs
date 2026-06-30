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
    /// When <see langword="true"/> (default, production) the pod's A2A listener requires TLS and a
    /// client certificate (mTLS, H1) via the mounted <c>appsettings.k8s.json</c> Kestrel endpoint.
    /// When <see langword="false"/> (PoC only) the listener serves plain HTTP on <see cref="Port"/>
    /// with no server/client certificate. Config key: <c>AgentHost:RequireMtls</c>.
    /// MUST be <see langword="true"/> in production.
    /// </summary>
    public bool RequireMtls { get; init; } = true;

    /// <summary>
    /// Port the Kestrel A2A listener binds to when <see cref="RequireMtls"/> is <see langword="false"/>
    /// (the PoC plain-HTTP path). In the mTLS path the port comes from the Kestrel endpoint config.
    /// Config key: <c>AgentHost:Port</c>. Default: <c>8088</c>.
    /// </summary>
    public int Port { get; init; } = 8088;

    /// <summary>
    /// Bearer token required to access the A2A agent-card endpoint (H3 — authz-gated
    /// discovery). Callers must supply <c>Authorization: Bearer {CardBearerToken}</c>.
    /// If empty, the card endpoint is accessible without authentication (not recommended
    /// for production; use only in local/test environments).
    /// </summary>
    public string CardBearerToken { get; init; } = string.Empty;

    /// <summary>
    /// Bearer token required to submit A2A turns to <c>{A2APath}/v1/message:stream</c>.
    /// Callers must supply <c>Authorization: Bearer {TurnBearerToken}</c>. If empty,
    /// the turn endpoint is accessible without authentication (local/test only).
    /// </summary>
    public string TurnBearerToken { get; init; } = string.Empty;

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

    // ── Token store selection ─────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/>, the agent-host reads GitHub tokens from the shared RWX
    /// filesystem store written by the API/worker tier (spec-018 P1.5). See
    /// <see cref="SharedTokenStorePath"/>. Takes effect only when
    /// <see cref="KvTokenMountPath"/> is not set.
    /// Config key: <c>AgentHost:UseSharedTokenStore</c>.
    /// </summary>
    public bool UseSharedTokenStore { get; init; }

    /// <summary>
    /// Root path of the shared RWX auth directory, used with <see cref="UseSharedTokenStore"/>.
    /// Passed to <see cref="SharedTokenStorePaths.ResolveAuthDir"/>.
    /// Config key: <c>AgentHost:SharedTokenStorePath</c>.
    /// </summary>
    public string? SharedTokenStorePath { get; init; }

    /// <summary>
    /// When set, GitHub user tokens are read from CSI-mounted files at this path (Option B).
    /// The CSI driver mounts per-user token files from Key Vault as
    /// <c>{KvTokenMountPath}/user_{userId}.json</c>.
    /// Config key: <c>AgentHost:KvTokenMountPath</c>.
    /// When set, takes precedence over <see cref="UseSharedTokenStore"/>.
    /// </summary>
    public string? KvTokenMountPath { get; init; }

    /// <summary>Azure Key Vault URI for runtime token fetch (Option C warm-pool path).
    /// When set, overrides KvTokenMountPath — token is fetched via workload identity at configure-time.
    /// Config key: AgentHost:KeyVaultUri
    /// </summary>
    public string? KeyVaultUri { get; init; }

    /// <summary>Key Vault secret name for the run owner's GitHub token.
    /// Passed in the /configure call. Format: ghtok-user--{base32(userId)}.
    /// Config key: AgentHost:KvUserSecretName
    /// </summary>
    public string? KvUserSecretName { get; init; }
}
