using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using k8s;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Replica-safe background sweep (~2 min) that reaps orphaned AgentHost <c>SandboxClaim</c>s:
/// <c>agent-*</c> claims whose run is no longer active (Failed/Completed/terminal or gone from the
/// store). Each AgentHost pod reserves 2 CPU against the namespace quota, so claims left behind by
/// crashed or stalled coordinator runs silently exhaust the quota and make every subsequent run
/// fail with <c>ReconcilerError: exceeded quota</c>. This reaper releases that capacity.
///
/// <para>
/// The claim name is a lossy derivation of the run id
/// (<see cref="SandboxClaimConventions.DeriveAgentHostClaimName"/> strips hyphens and truncates to
/// 12 chars), so it cannot be reversed back to a run id directly. Instead the reaper computes the
/// expected claim name for every <b>active</b> run (InProgress/Pending) and treats any
/// <c>agent-*</c> claim outside that set as orphaned. Driven entirely by cluster + store state, so
/// both API replicas reconcile identically against the same data.
/// </para>
/// </summary>
public sealed class AgentHostReaperService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AgentHostReaper: starting orphaned-claim reaper (sweep every {Minutes} min, namespace {Namespace})",
            SweepInterval.TotalMinutes, _options.Namespace);

        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AgentHostReaper: sweep failed (best-effort, will retry)");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Single reconciliation pass: deletes every AgentHost claim whose run is not currently active.
    /// Returns the number of claims reaped. Public for test/diagnostic invocation.
    /// </summary>
    internal async Task<int> ReapAsync(CancellationToken ct)
    {
        var activeClaimNames = await GetActiveAgentHostClaimNamesAsync(ct).ConfigureAwait(false);
        var claimNames = await ListAgentHostClaimNamesAsync(ct).ConfigureAwait(false);

        var reaped = 0;
        foreach (var claimName in claimNames)
        {
            ct.ThrowIfCancellationRequested();

            // The claim belongs to a run that is still InProgress/Pending — leave it running.
            if (activeClaimNames.Contains(claimName))
                continue;

            if (await TryDeleteClaimAsync(claimName, ct).ConfigureAwait(false))
                reaped++;
        }

        if (reaped > 0)
            _logger.LogInformation("AgentHostReaper: reaped {Count} orphaned claims", reaped);
        else
            _logger.LogDebug("AgentHostReaper: reaped 0 orphaned claims");

        return reaped;
    }

    /// <summary>
    /// Computes the set of AgentHost claim names that map to a currently active run
    /// (InProgress or Pending). Any <c>agent-*</c> claim outside this set is an orphan.
    /// </summary>
    private async Task<HashSet<string>> GetActiveAgentHostClaimNamesAsync(CancellationToken ct)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var status in new[] { RunStatus.InProgress, RunStatus.Pending })
        {
            var runs = await _runStore.GetByStatusAsync(status, ct).ConfigureAwait(false);
            foreach (var run in runs)
                active.Add(SandboxClaimConventions.DeriveAgentHostClaimName(run.Id.ToString()));
        }

        return active;
    }

    /// <summary>
    /// Lists all <c>SandboxClaim</c> names in the namespace whose name starts with the AgentHost
    /// prefix (<c>agent-</c>), distinguishing pod-per-run claims from per-command (<c>run-</c>) claims.
    /// </summary>
    private async Task<List<string>> ListAgentHostClaimNamesAsync(CancellationToken ct)
    {
        var raw = await _client.CustomObjects.ListNamespacedCustomObjectAsync(
            SandboxClaimConventions.ApiGroup,
            SandboxClaimConventions.ApiVersion,
            _options.Namespace,
            SandboxClaimConventions.ClaimPlural,
            cancellationToken: ct).ConfigureAwait(false);

        var names = new List<string>();
        var json = JsonSerializer.Serialize(raw);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name) &&
                    name.StartsWith(SandboxClaimConventions.AgentHostClaimPrefix, StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
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
