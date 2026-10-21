namespace Agentweaver.Orchestrator;

internal sealed class IssueSyncWorker(IServiceScopeFactory scopes, ILogger<IssueSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IssueSyncStore>();
                var engine = scope.ServiceProvider.GetRequiredService<IssueSyncEngine>();
                foreach (var project in await store.ReadDueAsync(stoppingToken))
                    await engine.CycleAsync(project, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Npgsql.NpgsqlException error)
            {
                logger.LogError(error, "Issue sync persistence unavailable; retrying the next polling cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
