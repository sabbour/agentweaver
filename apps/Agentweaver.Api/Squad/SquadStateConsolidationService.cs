using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Squad;

/// <summary>
/// Project-level background service that externalises Squad's canonical decision ledger from the
/// per-run branch-merge path (issue #621).
///
/// <para><b>Why this exists.</b> In a Squad-bootstrapped project, every coordinator run gets its own
/// throwaway <c>agentweaver/{runId}</c> branch and, historically, each run's embedded "Scribe" pass
/// wrote directly to the SAME canonical bookkeeping files (<c>.squad/decisions.md</c>,
/// <c>.squad/agents/*/history.md</c>, <c>.squad/identity/now.md</c>) on its own branch. When many
/// runs raced on the same lines and their branches merged back via the plumbing-level
/// <see cref="LibGit2Sharp.ObjectDatabase.MergeCommits"/> (which does NOT honour <c>.gitattributes</c>
/// <c>merge=union</c> drivers), the result was a genuine, human-resolution-required 3-way conflict —
/// a terminal <c>MergeFailed</c> state that stuck the run.</para>
///
/// <para><b>What this does.</b> On each tick it reconciles repository inbox files and any accepted
/// Markdown-only decisions into the authoritative database, promotes the inbox entries, then
/// regenerates and commits the file mirror. Scribe, API import/export, and this service all use
/// <see cref="DecisionLedgerSyncService"/>, so no path independently owns
/// <c>.squad/decisions.md</c>.</para>
///
/// <para><b>Concurrency and idempotency.</b> The shared sync service holds the repository merge lock
/// across reconciliation, export, and commit. Matching records are no-ops; conflicting records are
/// reported and neither source is overwritten.</para>
///
/// <para><b>Idempotency.</b> Each consolidated entry carries a content-addressed marker
/// (<c>&lt;!-- squad-consolidated: {blobSha} --&gt;</c>); a repeated tick over the same entry never
/// re-appends it, and processed inbox files are deleted in the same commit, so a re-tick is a no-op —
/// the same fire-at-most-once discipline <c>WorkflowScheduleTriggerService</c> uses via its synthetic
/// source-path idempotency key.</para>
///
/// <para><b>Config.</b> Master enable flag <c>Squad:StateConsolidationEnabled</c> (default true) and
/// <c>Squad:StateConsolidationIntervalSeconds</c> (default 60), mirroring the
/// <c>Workflows:ScheduleTriggerEnabled</c>/<c>...IntervalSeconds</c> convention so hermetic tests can
/// disable it deterministically.</para>
/// </summary>
public sealed class SquadStateConsolidationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SquadStateConsolidationService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public SquadStateConsolidationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SquadStateConsolidationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Master enable flag (default true), mirroring Workflows:ScheduleTriggerEnabled so hermetic
        // web tests can disable it to stay deterministic.
        _enabled = configuration.GetValue("Squad:StateConsolidationEnabled", true);

        var seconds = configuration.GetValue("Squad:StateConsolidationIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation(
                "Squad state consolidation disabled (Squad:StateConsolidationEnabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one consolidation pass over every active project. The test seam that keeps the whole
    /// service unit-testable without any wall-clock dependency or sleeping.
    /// </summary>
    public async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();

        IReadOnlyList<Project> projects = await projectStore.ListAsync(ct).ConfigureAwait(false);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.State != ProjectState.Active)
                continue;

            try
            {
                await ConsolidateProjectAsync(project, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // shutdown — stop the service cleanly
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Squad state consolidation: project {ProjectId} tick failed", project.Id);
                // Isolated; next project still processed.
            }
        }
    }

    /// <summary>
    /// Consolidates the decisions inbox on a single project's default branch. Returns the number of
    /// inbox entries newly promoted into the authoritative database, primarily for tests and
    /// diagnostics.
    /// </summary>
    public async Task<int> ConsolidateProjectAsync(Project project, CancellationToken ct)
    {
        var repoPath = project.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
            return 0;
        using var scope = _scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<DecisionLedgerSyncService>();
        try
        {
            var result = await sync.ConsolidateAsync(project, ct).ConfigureAwait(false);
            return result.Promoted;
        }
        catch (MemoryLedgerExporter.DecisionLedgerConflictException ex)
        {
            _logger.LogError(
                ex,
                "Squad state consolidation found decision ledger conflicts for project {ProjectId}",
                project.Id);
            return 0;
        }
    }

}
