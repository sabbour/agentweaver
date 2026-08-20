using Agentweaver.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Proactively refreshes GitHub access tokens before they expire so coordinator runs never hit an
/// expired token at runtime. Runs every <see cref="ScanInterval"/>, refreshes any token whose
/// expiry is within <see cref="ProactiveRefreshWindow"/>.
///
/// Requires the registered <see cref="IGitHubTokenStore"/> to implement
/// <see cref="IGitHubTokenScopeEnumerable"/>; no-ops for stores that do not.
/// </summary>
public sealed class GitHubTokenProactiveRefreshService(
    IGitHubTokenStore tokenStore,
    IGitHubAccessTokenProvider tokenProvider,
    ILogger<GitHubTokenProactiveRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay          = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval          = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromHours(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger start so the pod does not hit GitHub immediately on startup.
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunScanAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proactive GitHub token refresh scan failed; will retry next cycle.");
            }

            try { await Task.Delay(ScanInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        if (tokenStore is not IGitHubTokenScopeEnumerable scopeStore)
        {
            logger.LogDebug("Token store does not support scope enumeration; skipping proactive refresh scan.");
            return;
        }

        var scopes = await scopeStore.ListScopesAsync(ct).ConfigureAwait(false);
        int refreshed = 0, skipped = 0, failed = 0;

        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();

            var token = await tokenStore.GetTokenAsync(scope, ct).ConfigureAwait(false);
            if (token is null || token.ExpiresAt is null)
            {
                skipped++;
                continue; // signed-out or non-expiring classic token
            }

            var timeUntilExpiry = token.ExpiresAt.Value - DateTimeOffset.UtcNow;

            if (timeUntilExpiry <= TimeSpan.Zero)
            {
                // Already expired — reactive path or user re-link handles this.
                logger.LogWarning(
                    "GitHub token for scope {Scope} is already expired; proactive refresh skipped (user must re-link).",
                    scope.Key);
                failed++;
                continue;
            }

            if (timeUntilExpiry > ProactiveRefreshWindow)
            {
                skipped++;
                continue; // comfortably fresh
            }

            logger.LogInformation(
                "Proactively refreshing GitHub token for scope {Scope} (expires in {Minutes:F0} min).",
                scope.Key, timeUntilExpiry.TotalMinutes);

            try
            {
                var result = await tokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
                if (result is not null)
                    refreshed++;
                else
                    failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Proactive refresh failed for scope {Scope}.", scope.Key);
                failed++;
            }
        }

        if (refreshed > 0 || failed > 0)
            logger.LogInformation(
                "Proactive GitHub token refresh scan complete: {Refreshed} refreshed, {Skipped} skipped, {Failed} failed.",
                refreshed, skipped, failed);
    }
}
