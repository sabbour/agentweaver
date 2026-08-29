using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Resolves whether this replica's locally held repository credentials still have a live owner.
/// </summary>
internal interface IRunRepositoryCredentialLiveness
{
    Task<IReadOnlySet<string>> GetTerminalOrGoneRunIdsAsync(
        IReadOnlyList<string> runIds,
        CancellationToken ct = default);
}

/// <summary>
/// Reads the shared run store and cluster claim inventory. No repository credentials cross this
/// boundary: only locally held run identifiers are reconciled against durable/cluster state.
/// </summary>
internal sealed class RunRepositoryCredentialLiveness : IRunRepositoryCredentialLiveness
{
    private readonly IRunStore _runStore;
    private readonly IServiceProvider _services;
    private readonly ILogger<RunRepositoryCredentialLiveness> _logger;

    public RunRepositoryCredentialLiveness(
        IRunStore runStore,
        IServiceProvider services,
        ILogger<RunRepositoryCredentialLiveness> logger)
    {
        _runStore = runStore;
        _services = services;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>> GetTerminalOrGoneRunIdsAsync(
        IReadOnlyList<string> runIds,
        CancellationToken ct = default)
    {
        var terminalOrGone = new HashSet<string>(StringComparer.Ordinal);
        var claimsToVerify = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var runId in runIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!RunId.TryParse(runId, out var parsedRunId))
            {
                // Run IDs are UUIDs by domain invariant, so an unparseable owner cannot be live.
                terminalOrGone.Add(runId);
                continue;
            }

            Run? run;
            try
            {
                run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Repository credential reconciliation could not read run {RunId}; retaining local credential until the next sweep",
                    runId);
                continue;
            }

            if (run is null || IsTerminal(run.Status))
            {
                terminalOrGone.Add(runId);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(run.SandboxClaimName))
                claimsToVerify.Add(runId, run.SandboxClaimName);
        }

        if (claimsToVerify.Count == 0)
            return terminalOrGone;

        var reaper = _services.GetService<IAgentHostReaper>();
        if (reaper is null)
            return terminalOrGone;

        IReadOnlyList<AgentHostClaimInfo> inventory;
        try
        {
            inventory = await reaper.GetClaimInventoryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Repository credential reconciliation could not read SandboxClaim inventory; retaining local credentials until the next sweep");
            return terminalOrGone;
        }

        foreach (var (runId, claimName) in claimsToVerify)
        {
            var claimIsLive = inventory.Any(claim =>
                string.Equals(claim.ClaimName, claimName, StringComparison.Ordinal) &&
                string.Equals(claim.AnnotatedRunId, runId, StringComparison.Ordinal));
            if (!claimIsLive)
                terminalOrGone.Add(runId);
        }

        return terminalOrGone;
    }

    private static bool IsTerminal(RunStatus status) => status is
        RunStatus.Completed or RunStatus.Failed or RunStatus.Merged or RunStatus.Declined or
        RunStatus.MergeFailed or RunStatus.AssembleReady;
}

/// <summary>
/// Each API replica periodically reconciles its own in-memory repository credentials against
/// shared run/claim state. This closes the release path where a different replica deletes the
/// SandboxClaim and therefore cannot see or revoke this replica's token.
/// </summary>
internal sealed class RunRepositoryCredentialReconciliationService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly RunRepositoryCredentialRegistry _registry;
    private readonly IRunRepositoryCredentialLiveness _liveness;
    private readonly ILogger<RunRepositoryCredentialReconciliationService> _logger;

    public RunRepositoryCredentialReconciliationService(
        RunRepositoryCredentialRegistry registry,
        IRunRepositoryCredentialLiveness liveness,
        ILogger<RunRepositoryCredentialReconciliationService> logger)
    {
        _registry = registry;
        _liveness = liveness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Performs one local-registry reconciliation. Extracted for focused replica-lifecycle tests.
    /// </summary>
    internal async Task ReconcileOnceAsync(CancellationToken ct = default)
    {
        IReadOnlySet<string> terminalOrGone = new HashSet<string>(StringComparer.Ordinal);
        var activeRunIds = _registry.GetActiveCredentialRunIds();

        if (activeRunIds.Count > 0)
        {
            try
            {
                terminalOrGone = await _liveness
                    .GetTerminalOrGoneRunIdsAsync(activeRunIds, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Repository credential reconciliation could not determine credential liveness; retrying next sweep");
            }
        }

        var failures = await _registry
            .ReconcileTerminalOrGoneAsync(terminalOrGone, ct)
            .ConfigureAwait(false);
        foreach (var failure in failures)
        {
            _logger.LogWarning(
                failure.Exception,
                "Repository credential reconciliation failed to revoke credential for run {RunId}; it will retry with backoff until expiry",
                failure.RunId);
        }
    }
}
