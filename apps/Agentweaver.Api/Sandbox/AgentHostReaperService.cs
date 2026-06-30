using System.Text.Json;
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
    private const string SandboxTemplatePlural = "sandboxtemplates";
    private const string SandboxWarmPoolPlural = "sandboxwarmpools";
    private const string SpcGroup = "secrets-store.csi.x-k8s.io";
    private const string SpcVersion = "v1";
    private const string SpcPlural = "secretproviderclasses";

    private readonly IKubernetes _client;
    private readonly IRunStore _runStore;
    private readonly KubernetesSandboxOptions _options;
    private readonly ILogger<AgentHostReaperService> _logger;

    public AgentHostReaperService(
        IKubernetes client,
        IRunStore runStore,
        KubernetesSandboxOptions options,
        ILogger<AgentHostReaperService> logger)
    {
        _client = client;
        _runStore = runStore;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> SweepOrphanedPodsAsync(CancellationToken ct = default)
    {
        var activeMap = await GetActiveClaimMapAsync(ct).ConfigureAwait(false);
        var claims = await ListAgentHostClaimsAsync(ct).ConfigureAwait(false);

        var reaped = 0;
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();

            // The claim belongs to a run that is still InProgress/Pending — leave it running.
            if (activeMap.ContainsKey(claim.ClaimName))
                continue;

            if (await TryDeleteClaimAsync(claim.ClaimName, ct).ConfigureAwait(false))
            {
                await DeleteRunScopedResourcesAsync(claim.ClaimName, ct).ConfigureAwait(false);
                reaped++;
            }
        }

        if (reaped > 0)
            _logger.LogInformation("AgentHostReaper: reaped {Count} orphaned claims", reaped);
        else
            _logger.LogDebug("AgentHostReaper: reaped 0 orphaned claims");

        return reaped;
    }

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
    /// Maps every AgentHost claim name that belongs to a currently active run (InProgress or Pending)
    /// to that run's id. Any <c>agent-*</c> claim whose name is not a key here is an orphan. The
    /// derivation is lossy (12-char truncation), so on the rare collision the last run wins — only
    /// the run-id label is approximate; the active/orphaned decision stays correct.
    /// </summary>
    private async Task<Dictionary<string, string>> GetActiveClaimMapAsync(CancellationToken ct)
    {
        var active = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var status in new[] { RunStatus.InProgress, RunStatus.Pending })
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

            claims.Add(new AgentHostClaimInfo(
                ClaimName: name,
                RunId: null,
                PodName: podName,
                Ready: podName is not null,
                CreatedAt: createdAt,
                Orphaned: false));
        }

        return claims;
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

    private async Task DeleteRunScopedResourcesAsync(string claimName, CancellationToken ct)
    {
        await DeleteCustomObjectAsync(
                SandboxClaimConventions.ApiGroup,
                SandboxClaimConventions.ApiVersion,
                SandboxWarmPoolPlural,
                SandboxClaimConventions.DeriveAgentHostSandboxWarmPoolName(claimName),
                "SandboxWarmPool",
                ct)
            .ConfigureAwait(false);
        await DeleteCustomObjectAsync(
                SandboxClaimConventions.ApiGroup,
                SandboxClaimConventions.ApiVersion,
                SandboxTemplatePlural,
                SandboxClaimConventions.DeriveAgentHostSandboxTemplateName(claimName),
                "SandboxTemplate",
                ct)
            .ConfigureAwait(false);
        await DeleteCustomObjectAsync(
                SpcGroup,
                SpcVersion,
                SpcPlural,
                SandboxClaimConventions.DeriveAgentHostSecretProviderClassName(claimName),
                "SecretProviderClass",
                ct)
            .ConfigureAwait(false);
    }

    private async Task DeleteCustomObjectAsync(
        string group, string version, string plural, string name, string kind, CancellationToken ct)
    {
        try
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                    group, version, _options.Namespace, plural, name, cancellationToken: ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "AgentHostReaper: deleted orphaned {Kind} {Name}", kind, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentHostReaper: failed to delete orphaned {Kind} {Name} (best-effort)", kind, name);
        }
    }
}
