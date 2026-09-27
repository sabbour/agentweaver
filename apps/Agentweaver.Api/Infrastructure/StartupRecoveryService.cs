using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Runs;

namespace Agentweaver.Api.Infrastructure;

/// <summary>Runs the ordered, single-leader restart sweep without delaying the HTTP listener.</summary>
public sealed class StartupRecoveryService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepTimeout = TimeSpan.FromMinutes(5);
    private readonly Func<CancellationToken, Task<(bool IsLeader, IAsyncDisposable Lease)>> _acquire;
    private readonly Func<CancellationToken, Task> _recover;
    private readonly ILogger<StartupRecoveryService> _logger;
    private readonly TimeSpan _sweepTimeout;
    private readonly TimeSpan _retryInterval;
    private readonly CancellationToken? _applicationStarted;
    private readonly bool _enabled;

    public StartupRecoveryService(
        IConfiguration configuration,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        IHostEnvironment environment,
        ILogger<StartupRecoveryService> logger)
        : this(
            ct => AcquireWithCompletionAsync(configuration, logger, ct),
            services.GetRequiredService<StartupRecoveryStages>().RunAsync,
            logger,
            applicationStarted: lifetime.ApplicationStarted,
            enabled: !(environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                || !configuration.GetValue<bool>("Testing:DisableStartupRecovery"))
    {
    }

    private static async Task<(bool IsLeader, IAsyncDisposable Lease)> AcquireWithCompletionAsync(
        IConfiguration configuration, ILogger logger, CancellationToken ct)
    {
        var leader = await StartupRecoveryLeader.AcquireAsync(configuration, logger, ct).ConfigureAwait(false);
        return (leader.IsLeader, leader);
    }

    internal StartupRecoveryService(
        Func<CancellationToken, Task<(bool IsLeader, IAsyncDisposable Lease)>> acquire,
        Func<CancellationToken, Task> recover,
        ILogger<StartupRecoveryService> logger,
        TimeSpan? sweepTimeout = null,
        TimeSpan? retryInterval = null,
        CancellationToken? applicationStarted = null,
        bool enabled = true)
    {
        _acquire = acquire;
        _recover = recover;
        _logger = logger;
        _sweepTimeout = sweepTimeout ?? SweepTimeout;
        _retryInterval = retryInterval ?? RetryInterval;
        _applicationStarted = applicationStarted;
        _enabled = enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!_enabled)
            return;
        if (_applicationStarted is { } signal)
            await HostStartup.WaitForStartAsync(signal, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(_sweepTimeout);
                var (isLeader, lease) = await _acquire(timeout.Token).ConfigureAwait(false);
                await using var _ = lease;
                if (isLeader)
                {
                    _logger.LogInformation("Startup recovery sweep started");
                    await _recover(timeout.Token).ConfigureAwait(false);
                    _logger.LogInformation("Startup recovery sweep completed");
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Startup recovery sweep exceeded {Timeout}; retrying", _sweepTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup recovery sweep failed; retrying");
            }

            try
            {
                await Task.Delay(_retryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}

public sealed class StartupRecoveryStages(IServiceProvider services)
{
    internal Func<CancellationToken, Task>? Override { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Override is { } run)
        {
            await run(ct).ConfigureAwait(false);
            return;
        }
        await services.GetRequiredService<WorkflowRestartService>().RecoverAsync(ct).ConfigureAwait(false);
        await services.GetRequiredService<CoordinatorRunService>()
            .RecoverInterruptedRunsAsync(ct).ConfigureAwait(false);
        await services.GetRequiredService<CoordinatorReconciler>()
            .SweepAsync(ct).ConfigureAwait(false);
    }
}

internal static class HostStartup
{
    internal static async Task WaitForStartAsync(CancellationToken signal, CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }
}
