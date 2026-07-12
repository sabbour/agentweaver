namespace Agentweaver.AgentHost;

using Agentweaver.Domain;

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
    public AgentHostPurpose Purpose { get; private set; } = AgentHostPurpose.Default;
    public ExecutionWorkspaceMode WorkspaceMode { get; private set; } = ExecutionWorkspaceMode.Shared;
    public string? SharedWorkingDirectory { get; private set; }
    public string? SourceRepositoryPath { get; private set; }
    public string? SourceRef { get; private set; }
    public string? BaseCommitSha { get; private set; }
    public string? ExpectedTreeHash { get; private set; }
    public string? ScratchRoot { get; private set; }
    public string? CommitAuthorName { get; private set; }
    public string? CommitAuthorEmail { get; private set; }

    /// <summary>
    /// Effective repository/tool root after workspace preparation. For shared execution this is the
    /// shared worktree; for local execution it is the checkout created inside the pod.
    /// </summary>
    public string? EffectiveWorkingDirectory { get; private set; }

    /// <summary>
    /// Per-run preview-runner credential (spec-006 decouple-preview, BLOCKER A). Delivered in-memory
    /// via <c>POST /configure</c> — never in pod env/file, so it cannot be inherited by the untrusted
    /// preview process. <c>PreviewRunnerEndpointAuth</c> accepts EITHER this or
    /// <see cref="TurnBearerToken"/>; when set, preview-runner auth is fail-closed.
    /// </summary>
    public string PreviewRunnerCredential { get; private set; } = string.Empty;

    /// <summary>
    /// Key Vault secret name for the run owner's GitHub token (Option C warm-pool path).
    /// Supplied by the executor in the /configure call; consumed by
    /// <see cref="KeyVaultUserTokenProvider"/>. Null on the file-mount/shared-store paths.
    /// </summary>
    public string? KvUserSecretName { get; private set; }

    /// <summary>
    /// Pre-resolved GitHub OAuth access token supplied by the API in the /configure body.
    /// When set, <see cref="KeyVaultUserTokenProvider"/> uses this directly and skips the KV call,
    /// allowing the pod to work without outbound access to Azure AD or Key Vault.
    /// </summary>
    public string? GitHubAccessToken { get; private set; }

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
        PreviewRunnerCredential = string.Empty; // not available on env-var launch path
        KvUserSecretName = options.KvUserSecretName;
        GitHubAccessToken = null; // not available on env-var launch path
        Purpose = AgentHostPurpose.Default;
        WorkspaceMode = ExecutionWorkspaceMode.Shared;
        SharedWorkingDirectory = options.WorkingDirectory;
        SourceRepositoryPath = null;
        SourceRef = null;
        BaseCommitSha = null;
        ExpectedTreeHash = null;
        ScratchRoot = null;
        CommitAuthorName = null;
        CommitAuthorEmail = null;
        EffectiveWorkingDirectory = options.WorkingDirectory;
    }

    /// <summary>
    /// Atomically transitions the pod from standby to configured. Returns <see langword="false"/>
    /// when the pod was already configured (one-time semantics → caller returns 409).
    /// </summary>
    public bool TryConfigure(string runId, string userId, string turnBearerToken, string? kvUserSecretName, string? gitHubAccessToken, string? previewRunnerCredential = null)
        => TryConfigure(new AgentHostRunConfiguration(
            runId,
            userId,
            turnBearerToken,
            kvUserSecretName,
            gitHubAccessToken,
            previewRunnerCredential,
            SharedWorkingDirectory: null));

    /// <summary>Atomically applies the complete run-scoped warm-pod configuration.</summary>
    public bool TryConfigure(AgentHostRunConfiguration configuration)
    {
        if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
            return false;

        RunId = configuration.RunId ?? string.Empty;
        UserId = configuration.UserId ?? string.Empty;
        TurnBearerToken = configuration.TurnBearerToken ?? string.Empty;
        PreviewRunnerCredential = configuration.PreviewRunnerCredential ?? string.Empty;
        KvUserSecretName = string.IsNullOrWhiteSpace(configuration.KvUserSecretName)
            ? null
            : configuration.KvUserSecretName;
        GitHubAccessToken = string.IsNullOrWhiteSpace(configuration.GitHubAccessToken)
            ? null
            : configuration.GitHubAccessToken;
        Purpose = configuration.Purpose;
        WorkspaceMode = configuration.WorkspaceMode;
        SharedWorkingDirectory = configuration.SharedWorkingDirectory;
        SourceRepositoryPath = configuration.SourceRepositoryPath;
        SourceRef = configuration.SourceRef;
        BaseCommitSha = configuration.BaseCommitSha;
        ExpectedTreeHash = configuration.ExpectedTreeHash;
        ScratchRoot = configuration.ScratchRoot;
        CommitAuthorName = configuration.CommitAuthorName;
        CommitAuthorEmail = configuration.CommitAuthorEmail;
        EffectiveWorkingDirectory = configuration.SharedWorkingDirectory;
        return true;
    }

    public void SetEffectiveWorkingDirectory(string workingDirectory) =>
        EffectiveWorkingDirectory = workingDirectory;
}

/// <summary>Complete one-time configuration delivered to a warm AgentHost pod.</summary>
internal sealed record AgentHostRunConfiguration(
    string RunId,
    string UserId,
    string TurnBearerToken,
    string? KvUserSecretName,
    string? GitHubAccessToken,
    string? PreviewRunnerCredential,
    string? SharedWorkingDirectory,
    AgentHostPurpose Purpose = AgentHostPurpose.Default,
    string? SourceRepositoryPath = null,
    string? SourceRef = null,
    string? BaseCommitSha = null,
    string? ExpectedTreeHash = null,
    ExecutionWorkspaceMode WorkspaceMode = ExecutionWorkspaceMode.Shared,
    string? ScratchRoot = null,
    string? CommitAuthorName = null,
    string? CommitAuthorEmail = null);
