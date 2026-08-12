using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using k8s;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Replica-safe reaper for orphaned AgentHost <c>SandboxClaim</c>s: <c>agent-*</c> claims whose run
/// is no longer active (Failed/Completed/terminal or gone from the store). Each AgentHost pod
/// reserves 2 CPU against the namespace quota, so claims left behind by crashed or stalled
/// coordinator runs silently exhaust the quota and make every subsequent run fail with
/// <c>ReconcilerError: exceeded quota</c>. This reaper releases that capacity.
///
/// <para>
/// The claim name is a lossy derivation of the run id
/// (<see cref="SandboxClaimConventions.DeriveAgentHostClaimName"/> strips hyphens and truncates to
/// 12 chars), so it cannot be reversed back to a run id directly. Instead the reaper computes the
/// expected claim name for every <b>active</b> run (InProgress/Pending) and treats any
/// <c>agent-*</c> claim outside that set as orphaned. Driven entirely by cluster + store state, so
/// both API replicas reconcile identically against the same data.
/// </para>
///
/// <para>
/// This is a regular singleton, NOT a <c>BackgroundService</c>: the coordinator heartbeat
/// (<c>CoordinatorHeartbeatService</c>) drives its cadence by invoking
/// <see cref="SweepOrphanedPodsAsync"/> every N ticks (<c>Coordinator:ReaperIntervalTicks</c>).
/// </para>
/// </summary>
public sealed class AgentHostReaperService : IAgentHostReaper
{
    private const int ReadinessGraceMarginSeconds = 30;

    private readonly IKubernetes _client;
    private readonly IRunStore _runStore;
    private readonly KubernetesSandboxOptions _options;
    private readonly ILogger<AgentHostReaperService> _logger;
    private readonly ISecretStore? _secretStore;
    // First-class preview lifecycle reconciler. Before reaping an orphaned claim, it derives durable
    // Previewable/PreviewActive state and atomically owns all retention or cleanup side effects.
    private readonly Preview.ISandboxPreviewService? _previewService;

    public AgentHostReaperService(
        IKubernetes client,
        IRunStore runStore,
        KubernetesSandboxOptions options,
        ILogger<AgentHostReaperService> logger,
        ISecretStore? secretStore = null,
        Preview.ISandboxPreviewService? previewService = null)
    {
        _client = client;
        _runStore = runStore;
        _options = options;
        _logger = logger;
        _secretStore = secretStore;
        _previewService = previewService;
    }

    /// <inheritdoc />
    public async Task<int> SweepOrphanedPodsAsync(CancellationToken ct = default)
    {
        var activeMap = await GetActiveClaimMapAsync(ct).ConfigureAwait(false);
        var claims = await ListAgentHostClaimsAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var creationGrace = EffectiveCreationGrace(_options);

        var reaped = 0;
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();

            var isActive = activeMap.ContainsKey(claim.ClaimName);
            if (!isActive && claim.CreatedAt is null)
            {
                _logger.LogDebug(
                    "AgentHostReaper: claim {Claim} has no parseable creationTimestamp; creation grace does not apply",
                    claim.ClaimName);
            }

            if (!IsReapable(claim, isActive, now, creationGrace))
                continue;

            // Issue #542: a completed subtask's claim is "orphan" per the active-run map, but if its
            // run still has a live preview we must NOT reap the pod — the preview URL would 404. Defer
            // until the preview idle/max-expires (then the preview reaper deletes the route and the next
            // sweep reaps this claim), so the pod cannot leak.
            if (await ReconcilePreviewLifecycleAsync(claim.AnnotatedRunId, ct).ConfigureAwait(false)
                == Preview.PreviewLifecycleState.PreviewActive)
            {
                _logger.LogInformation(
                    "AgentHostReaper: deferring reap of claim {Claim} (run {RunId}) — a live preview is " +
                    "still active; the preview idle/max expiry will release it.",
                    claim.ClaimName, claim.AnnotatedRunId);
                continue;
            }

            if (await TryDeleteClaimAsync(claim.ClaimName, ct).ConfigureAwait(false))
            {
                reaped++;
                // Belt-and-suspenders credential lifecycle: this orphaned claim's run crashed or
                // stalled, so ReleaseAgentHostPodAsync (the normal delete site) NEVER ran and the
                // per-run preview-runner credential is still in the secret store. Recover the run id
                // from the claim annotation and delete it so the credential's durable lifetime stays
                // bounded by the pod's (spec-006 decouple-preview; no-op when absent).
                await TryDeleteOrphanCredentialAsync(claim.AnnotatedRunId, ct).ConfigureAwait(false);
            }
        }

        if (reaped > 0)
            _logger.LogInformation("AgentHostReaper: reaped {Count} orphaned claims", reaped);
        else
            _logger.LogDebug("AgentHostReaper: reaped 0 orphaned claims");

        return reaped;
    }

    internal static bool IsReapable(
        AgentHostClaimInfo claim,
        bool isActive,
        DateTimeOffset now,
        TimeSpan creationGrace) =>
        !isActive &&
        (claim.CreatedAt is null || now - claim.CreatedAt.Value >= creationGrace);

    internal static TimeSpan EffectiveCreationGrace(KubernetesSandboxOptions options) =>
        TimeSpan.FromSeconds(Math.Max(
            options.AgentHostClaimCreationGraceSeconds,
            options.AgentHostReadyTimeoutSeconds + ReadinessGraceMarginSeconds));

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentHostClaimInfo>> GetClaimInventoryAsync(CancellationToken ct = default)
    {
        var activeMap = await GetActiveClaimMapAsync(ct).ConfigureAwait(false);
        var claims = await ListAgentHostClaimsAsync(ct).ConfigureAwait(false);

        var inventory = new List<AgentHostClaimInfo>(claims.Count);
        foreach (var claim in claims)
        {
            var isActive = activeMap.TryGetValue(claim.ClaimName, out var runId);
            inventory.Add(claim with { RunId = isActive ? runId : null, Orphaned = !isActive });
        }
        return inventory;
    }

    /// <summary>
    /// Maps every AgentHost claim name that belongs to a currently active run (InProgress, Pending,
    /// or AwaitingReview) 
    /// to that run's id. Any <c>agent-*</c> claim whose name is not a key here is an orphan. The
    /// derivation is lossy (12-char truncation), so on the rare collision the last run wins — only
    /// the run-id label is approximate; the active/orphaned decision stays correct.
    /// </summary>
    private async Task<Dictionary<string, string>> GetActiveClaimMapAsync(CancellationToken ct)
    {
        var active = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var status in new[] { RunStatus.InProgress, RunStatus.Pending, RunStatus.AwaitingReview })
        {
            var runs = await _runStore.GetByStatusAsync(status, ct).ConfigureAwait(false);
            foreach (var run in runs)
                active[SandboxClaimConventions.DeriveAgentHostClaimName(run.Id.ToString())] = run.Id.ToString();
        }

        return active;
    }

    /// <summary>
    /// Lists all <c>SandboxClaim</c>s in the namespace whose name starts with the AgentHost prefix
    /// (<c>agent-</c>), parsing each claim's bound pod name, readiness, and creation timestamp.
    /// RunId/Orphaned are filled in by the caller against the active-run set.
    /// </summary>
    private async Task<List<AgentHostClaimInfo>> ListAgentHostClaimsAsync(CancellationToken ct)
    {
        var raw = await _client.CustomObjects.ListNamespacedCustomObjectAsync(
            SandboxClaimConventions.ApiGroup,
            SandboxClaimConventions.ApiVersion,
            _options.Namespace,
            SandboxClaimConventions.ClaimPlural,
            cancellationToken: ct).ConfigureAwait(false);

        var claims = new List<AgentHostClaimInfo>();
        var json = JsonSerializer.Serialize(raw);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return claims;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("name", out var nameEl))
                continue;

            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name) ||
                !name.StartsWith(SandboxClaimConventions.AgentHostClaimPrefix, StringComparison.Ordinal))
                continue;

            DateTimeOffset? createdAt = null;
            if (metadata.TryGetProperty("creationTimestamp", out var ts) &&
                ts.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed))
                createdAt = parsed;

            // A bound pod name is only present once the claim is Ready (see
            // SandboxClaimConventions.TryGetBoundPodName), so it doubles as the readiness signal.
            var podName = SandboxClaimConventions.TryGetBoundPodName(item);

            // Original (un-truncated) run id stamped at claim creation; lets the orphan sweep delete
            // the per-run preview-runner credential even though the claim name is lossy.
            var annotatedRunId = SandboxClaimConventions.TryGetRunIdAnnotation(item);

            claims.Add(new AgentHostClaimInfo(
                ClaimName: name,
                RunId: null,
                PodName: podName,
                Ready: podName is not null,
                CreatedAt: createdAt,
                Orphaned: false,
                AnnotatedRunId: annotatedRunId));
        }

        return claims;
    }

    /// <summary>
    /// Deletes the per-run preview-runner credential for an orphaned (crash/stall-reaped) run. No-op
    /// when the secret store is unavailable or the run id could not be recovered from the claim
    /// annotation, and never throws — a credential-delete failure must not abort the reaper sweep.
    /// Uses the SAME <c>PreviewRunnerCredential.SecretKey(runId)</c> derivation as the mint/release
    /// paths so the delete actually matches (spec-006 decouple-preview).
    /// </summary>
    private async Task TryDeleteOrphanCredentialAsync(string? runId, CancellationToken ct)
    {
        if (_secretStore is null || string.IsNullOrEmpty(runId))
            return;

        try
        {
            await _secretStore.DeleteSecretAsync(Preview.PreviewRunnerCredential.SecretKey(runId), ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "AgentHostReaper: deleted orphaned preview-runner credential for run {RunId}", runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "AgentHostReaper: failed to delete preview-runner credential for run {RunId} (best-effort)", runId);
        }
    }

    /// <summary>
    /// Reconciles every preview-retention side effect before deciding whether to reap. A missing
    /// service/run id or reconciliation failure defaults to <c>Previewable</c> (leak-safe) rather than
    /// pinning the pod forever.
    /// </summary>
    private async Task<Preview.PreviewLifecycleState> ReconcilePreviewLifecycleAsync(
        string? runId, CancellationToken ct)
    {
        if (_previewService is null || string.IsNullOrEmpty(runId))
            return Preview.PreviewLifecycleState.Previewable;

        try
        {
            return await _previewService.ReconcilePreviewLifecycleAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "AgentHostReaper: preview lifecycle reconciliation failed for run {RunId}; using Previewable",
                runId);
            return Preview.PreviewLifecycleState.Previewable;
        }
    }

    private async Task<bool> TryDeleteClaimAsync(string claimName, CancellationToken ct)
    {
        try
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup,
                SandboxClaimConventions.ApiVersion,
                _options.Namespace,
                SandboxClaimConventions.ClaimPlural,
                claimName,
                cancellationToken: ct).ConfigureAwait(false);

            _logger.LogInformation(
                "AgentHostReaper: deleted orphaned claim {Claim}", claimName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentHostReaper: failed to delete orphaned claim {Claim} (best-effort)", claimName);
            return false;
        }
    }
}
