namespace Agentweaver.AgentHost;

/// <summary>
/// Mutable, process-wide runtime state for the AgentHost pod. <see cref="AgentHostOptions"/> is
/// <c>init</c>-only (immutable, bound from config/env at startup); this holder carries the per-run
/// values that are delivered AFTER startup via the warm-pool <c>POST /configure</c> call.
///
/// <para>
/// Two population paths converge here:
/// <list type="bullet">
///   <item>Env-var launch (non-warm pod): <see cref="AgentHostStartupService"/> seeds this from
///   <see cref="AgentHostOptions"/> at startup via <see cref="InitializeFromOptions"/>.</item>
///   <item>Warm pool: the pod starts in standby with no run context; the executor injects RunId /
///   UserId / TurnBearerToken / KvUserSecretName at run-launch time via <see cref="TryConfigure"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// The A2A bearer-auth middleware reads <see cref="TurnBearerToken"/> from HERE (not the immutable
/// options) so the token delivered via /configure is the one enforced on <c>message:stream</c>.
/// </para>
/// </summary>
internal sealed class AgentHostRuntimeState
{
    // 0 = unconfigured, 1 = configured. One-time CompareExchange guards /configure.
    private int _configured;

    public bool IsConfigured => Volatile.Read(ref _configured) == 1;

    public string RunId { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string TurnBearerToken { get; private set; } = string.Empty;

    /// <summary>
    /// Key Vault secret name for the run owner's GitHub token (Option C warm-pool path).
    /// Supplied by the executor in the /configure call; consumed by
    /// <see cref="KeyVaultUserTokenProvider"/>. Null on the file-mount/shared-store paths.
    /// </summary>
    public string? KvUserSecretName { get; private set; }

    /// <summary>
    /// Seeds the runtime state from env-injected options (non-warm pod launched with a RunId).
    /// Marks the state configured so a later /configure is rejected (409 "Already configured via env").
    /// </summary>
    public void InitializeFromOptions(AgentHostOptions options)
    {
        Interlocked.Exchange(ref _configured, 1);
        RunId = options.RunId ?? string.Empty;
        UserId = options.UserId ?? string.Empty;
        TurnBearerToken = options.TurnBearerToken ?? string.Empty;
        KvUserSecretName = options.KvUserSecretName;
    }

    /// <summary>
    /// Atomically transitions the pod from standby to configured. Returns <see langword="false"/>
    /// when the pod was already configured (one-time semantics → caller returns 409).
    /// </summary>
    public bool TryConfigure(string runId, string userId, string turnBearerToken, string? kvUserSecretName)
    {
        if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
            return false;

        RunId = runId ?? string.Empty;
        UserId = userId ?? string.Empty;
        TurnBearerToken = turnBearerToken ?? string.Empty;
        KvUserSecretName = string.IsNullOrWhiteSpace(kvUserSecretName) ? null : kvUserSecretName;
        return true;
    }
}
