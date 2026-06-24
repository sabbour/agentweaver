using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Process-wide heartbeat scheduler (Feature 009, Path A). There is no per-project coordinator
/// daemon; this single <see cref="BackgroundService"/> services every eligible project on a fixed
/// cadence. Each tick reads the deterministic top-N Ready items per eligible project and hands each
/// to <see cref="CoordinatorPickupService"/> for the atomic claim+reserve + unattended coordinator
/// start.
///
/// <para>"Active coordinator for a project" (FR-011) is realized as eligibility at tick time: a
/// project is eligible when <c>State == Active</c> AND its workspace is available. Ineligible
/// projects leave their Ready tasks untouched (priority preserved) for a later tick.</para>
///
/// <para>Error isolation is two-level (per project, per task) so one bad task or project never
/// stalls the tick; only an <see cref="OperationCanceledException"/> from the stopping token
/// propagates out to stop the service cleanly. The per-heartbeat cap is enforced by reading at most
/// <c>project.MaxReadyPerHeartbeat</c> candidates per tick; exactly-once is enforced by the atomic
/// claim inside the pickup transaction, so an overlapping tick or a second instance simply loses the
/// claim.</para>
/// </summary>
public sealed class CoordinatorHeartbeatService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoordinatorHeartbeatService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly HeartbeatStatusStore _statusStore;

    public CoordinatorHeartbeatService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        HeartbeatStatusStore statusStore,
        ILogger<CoordinatorHeartbeatService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _statusStore = statusStore;

        // Master enable flag (default true). Hermetic web tests set it false to stay deterministic,
        // mirroring the existing Coordinator:AutoDispatch toggle.
        _enabled = configuration.GetValue("Coordinator:HeartbeatEnabled", true);

        // Interval read once at construction; floor of 1 second.
        var seconds = configuration.GetValue("Coordinator:HeartbeatIntervalSeconds", 10);
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Coordinator heartbeat disabled (Coordinator:HeartbeatEnabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        var tickStart = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int actedCount = 0;
        int errorCount = 0;
        string? lastError = null;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var projectStore = sp.GetRequiredService<IProjectStore>();
        var backlogStore = sp.GetRequiredService<IBacklogTaskStore>();
        var workspaceProvider = sp.GetRequiredService<IProjectWorkspaceProvider>();
        var pickupService = sp.GetRequiredService<CoordinatorPickupService>();

        IReadOnlyList<Project> projects = await projectStore.ListAsync(stoppingToken).ConfigureAwait(false);
        foreach (var project in projects)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (project.State != ProjectState.Active)
                continue;

            try
            {
                // FR-011: a project whose workspace is missing keeps its Ready tasks for a later tick.
                if (!workspaceProvider.IsAvailable(project.WorkingDirectory))
                    continue;

                // FR-008a + deterministic top-N: read at most MaxReadyPerHeartbeat candidates, which is
                // exactly how the per-heartbeat cap is enforced.
                var candidates = await backlogStore
                    .ListReadyForClaimAsync(project.Id, project.MaxReadyPerHeartbeat, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var task in candidates)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await pickupService.TryPickupAsync(project, task, stoppingToken).ConfigureAwait(false);
                        actedCount++;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;   // shutdown — stop the service cleanly
                    }
                    catch (Exception exTask)
                    {
                        errorCount++;
                        lastError = exTask.Message;
                        _logger.LogError(exTask, "Heartbeat: pickup failed for task {TaskId}", task.Id);
                        // Isolated; sibling tasks still processed.
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;   // shutdown — stop the service cleanly
            }
            catch (Exception exProject)
            {
                errorCount++;
                lastError = exProject.Message;
                _logger.LogError(exProject, "Heartbeat: project {ProjectId} tick failed", project.Id);
                // Isolated; next project still processed.
            }
        }

        sw.Stop();
        _statusStore.RecordTickOutcome(tickStart, actedCount, errorCount, sw.Elapsed.TotalMilliseconds, lastError);

        // Watchdog: recover any orphaned coordinator dispatch (a work plan still dispatching whose
        // in-memory loop died / never re-armed after a restart). Isolated so a sweep failure never
        // affects the pickup tick outcome above.
        try
        {
            var reconciler = sp.GetRequiredService<CoordinatorReconciler>();
            await reconciler.SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;   // shutdown — stop the service cleanly
        }
        catch (Exception exSweep)
        {
            _logger.LogError(exSweep, "Heartbeat: coordinator reconciler sweep failed");
        }
    }
}
